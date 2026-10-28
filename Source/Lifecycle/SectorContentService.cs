using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Grammar;

namespace DeltaV
{
    /// <summary>
    /// 星区内容生成/记账服务（星区架构 §4/§9）。
    ///
    /// 星区（worldtile）是 SpaceObject 的管理/生成记账分组，不是坐标维度：
    /// - 「该星区已触发生成」是持久事实（universe.generatedSectorTiles），防止重复
    ///   刷对象，且不可从现存对象推导（对象会被销毁）；
    /// - 「该星区当前有哪些对象」由 objects + TileFromDirection 查询时重建，不序列化
    ///   （可重建缓存不是存档事实）；
    /// - 密度规则 = 类型树每个叶子「保底 + 分段概率额外量」（Mod 设置「生成」页，
    ///   SectorTypeRule；类型树 SectorContentTypes 提供分类与默认值，分支只是分组）。
    ///   设置是全局的，只影响尚未首次生成的星区；
    /// - 触发时机（§4）：玩家参考对象 tile 地址变化进入新星区；逆重战略飞行
    ///   到达链在生成降落图之前；开局出生星区在世界生成时预生成（StartedNewGame）。
    ///
    /// 生成内容只有 Delta-V 自建对象（类型树叶子的 SnapshotPocket）；原版 orbit 站点本来就
    /// 按 tile 生成，天然已按星区分布，本服务不触碰。对象被摧毁后的补充策略按类型
    /// 区分——现阶段不做补充（接口占位，星区架构 §4）。
    /// </summary>
    public static class SectorContentService
    {
        /// <summary>现阶段不做销毁补充（接口占位，星区架构 §4）。</summary>
        public const bool SuppressReplenishment = true;

        /// <summary>自建对象远离星区边缘的最小留白（格）：避免贴脸生成与跨星区激活。</summary>
        public const float EdgeMarginCells = 1000f;

        /// <summary>同星区内自建对象相互之间的最小间距（格）。</summary>
        public const float MinSpacingCells = 300f;

        /// <summary>落点重试次数上限（间距/留白约束满足不了时放弃精化，不阻塞生成）。</summary>
        private const int PlacementAttempts = 24;

        // ---- 记账 ----

        private static bool WasGenerated(UniverseManager universe, PlanetTile tile)
        {
            return universe.generatedSectorTiles != null
                && universe.generatedSectorTiles.Contains(tile.ToString());
        }

        private static void MarkGenerated(UniverseManager universe, PlanetTile tile)
        {
            if (universe.generatedSectorTiles == null)
            {
                universe.generatedSectorTiles = new List<string>();
            }
            string key = tile.ToString();
            if (!universe.generatedSectorTiles.Contains(key))
            {
                universe.generatedSectorTiles.Add(key);
            }
        }

        /// <summary>该星区当前有哪些对象（查询时由 TileFromDirection 重建，不序列化；弹体不进星区记账，§3.1）。</summary>
        public static List<SpaceObject> ObjectsInSector(UniverseManager universe, PlanetTile tile)
        {
            var result = new List<SpaceObject>();
            if (universe == null || !tile.Valid) return result;
            foreach (SpaceObject obj in universe.objects)
            {
                if (!obj.ParticipatesInOverlays) continue;
                if (OrbitSphere.TileFromDirection(tile.Layer, obj.pose.position) == tile)
                {
                    result.Add(obj);
                }
            }
            return result;
        }

        /// <summary>星区内现存的指定类型自建 marker 数（SnapshotPocket 口径）。</summary>
        private static int CountTypedMarkers(UniverseManager universe, PlanetTile tile, string typeId)
        {
            return ObjectsInSector(universe, tile).OfType<PoiState>()
                .Count(p => p.kind == PoiKind.SnapshotPocket && p.contentTypeId == typeId);
        }

        // ---- 生成 ----

        /// <summary>
        /// 确保星区已按规则完成首次生成（幂等；持久标记在生成成功后写入）。
        /// 只对轨道层（isSpace）生效：地表层 tile（如 RimExodus 无缝世界）不是星区，
        /// 记账与内容生成都不得触碰（AGENTS §16 边界）。
        /// 遍历类型树的全部叶子逐类型执行「保底 + 分段概率额外量」；规则读全局
        /// Mod 设置，缺条目用注册表默认。
        /// </summary>
        public static void EnsureSectorGenerated(UniverseManager universe, PlanetTile tile)
        {
            if (universe == null || !tile.Valid || !tile.LayerDef.isSpace
                || !OrbitSphere.LayerUsable(tile.Layer)) return;
            if (WasGenerated(universe, tile)) return;

            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            int totalSpawned = 0;
            foreach (SectorContentType type in SectorContentTypes.AllLeaves())
            {
                SectorTypeRule rule = settings != null
                    ? settings.EffectiveSectorRule(type)
                    : new SectorTypeRule { typeId = type.id, minCount = type.defaultMin };
                int existing = CountTypedMarkers(universe, tile, type.id);
                int target = Mathf.Max(rule.minCount, existing);
                foreach (SegmentRule segment in rule.segments)
                {
                    if (Rand.Chance(segment.chance01))
                    {
                        target += Rand.RangeInclusive(1, Mathf.Max(1, segment.maxCount));
                    }
                }
                // 物化内容链是 prefab-only（2026-09-30 定案）：池空的类型无法物化，
                // 生成期即拦截——报错并跳过本类型（物化侧对存量 marker 同判兜底）。
                // ErrorOnce 防逐星区刷屏。
                if (target > existing && rule.prefabPaths.NullOrEmpty())
                {
                    Log.ErrorOnce($"[DeltaV] Content type '{type.id}' has no prefab pool; skipped " +
                        $"{target - existing} spawn(s) in sector {tile}. Materialization is prefab-only; " +
                        "configure the pool in Mod settings (生成 tab).",
                        ("DeltaVSectorTypePoolEmpty:" + type.id).GetHashCode());
                    continue;
                }
                int spawned = 0;
                for (int i = existing; i < target; i++)
                {
                    SpawnSectorObject(universe, tile, type);
                    spawned++;
                    totalSpawned++;
                }
                if (spawned > 0)
                {
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"sector {tile}: first-entry generation spawned {spawned} {type.id}(s) (existing {existing}, target {target})");
                }
            }
            MarkGenerated(universe, tile);
            if (totalSpawned == 0)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"sector {tile}: first-entry generation spawned nothing (all minimums met)");
            }
        }

        private static void SpawnSectorObject(UniverseManager universe, PlanetTile tile, SectorContentType type)
        {
            Vector3 center = OrbitSphere.TileCenterDirection(tile);
            // 星区可用半径 ≈ CellsPerTile/2（六边形内切近似）；远离边缘放置。
            float usableRadius = Mathf.Max(150f, OrbitSphere.CellsPerTile * 0.5f - EdgeMarginCells);
            Vector2 bestOffset = Vector2.zero;
            bool found = false;
            float bestSpacing = -1f;
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                float angle = Rand.Range(0f, Mathf.PI * 2f);
                float radius = Mathf.Sqrt(Rand.Value) * usableRadius;
                if (radius < 60f) radius = 60f + Rand.Value * 100f;
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                float spacing = MinSpacingToObjects(universe, tile, offset);
                if (spacing >= MinSpacingCells)
                {
                    bestOffset = offset;
                    found = true;
                    break;
                }
                if (spacing > bestSpacing)
                {
                    bestSpacing = spacing;
                    bestOffset = offset;
                }
            }
            if (!found)
            {
                // 重试未满足间距就取最疏的一次：生成不因布局精化失败而阻塞。
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"sector {tile}: {type.id} placement relaxed (best spacing {bestSpacing:F0} < {MinSpacingCells})");
            }
            // 身份在生成时一次性决定（2026-09-30 定案）：随机派系 + 完整名
            //（RulePack 词干 + 类型名 + (派系名) + #ID），烘焙进 envLabel/faction 随档
            // 持久；物化时直接应用（容器派系、prefab 建筑与 pawn 派系、归属名），
            // 之后不改不变。envLabel 是持久事实，不随语言切换。
            int poiId = universe.AllocateOwnerId();
            Faction faction = RandomSpaceFaction();
            string label = NameGenerator.GenerateName(DeltaVDefOf.DeltaV_NamerSpacePoi,
                extraRules: new List<Rule> { new Rule_String("poi_type", type.Label) });
            if (faction != null)
            {
                label += $"({faction.Name})";
            }
            label += $" #{poiId}";
            var poi = new PoiState(poiId, label,
                OrbitSphere.Move(center, bestOffset), Rand.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int, type.id)
            {
                faction = faction,
            };
            DeltaVSettings currentSettings = DeltaVMod.CurrentSettings;
            SectorTypeRule currentRule = currentSettings?.EffectiveSectorRule(type);
            poi.sourcePrefabPath = MapPrefab.ResolvePoolPath(currentRule?.prefabPaths, poi.seed);
            universe.RegisterObject(poi);
        }

        /// <summary>
        /// POI 派系在生成时随机落定（2026-09-30 定案，不做类型→派系映射）：候选 =
        /// 可见、非玩家、未灭亡的派系——敌友皆有可能（敌方贸易站可劫、友方战机只是
        /// 路过），与原版地表对象随机派系同思路。无候选（极端孤派系世界）返回 null
        ///（POI 无派系，名称省略派系段）。
        /// </summary>
        private static Faction RandomSpaceFaction()
        {
            Faction player = Faction.OfPlayer;
            var candidates = new List<Faction>();
            foreach (Faction faction in Find.FactionManager.AllFactionsVisible)
            {
                if (faction == null || faction == player || faction.defeated) continue;
                candidates.Add(faction);
            }
            return candidates.Count > 0 ? candidates.RandomElement() : null;
        }

        private static float MinSpacingToObjects(UniverseManager universe, PlanetTile tile, Vector2 offset)
        {
            Vector3 center = OrbitSphere.TileCenterDirection(tile);
            Vector3 candidate = OrbitSphere.Move(center, offset);
            float best = float.MaxValue;
            foreach (SpaceObject obj in ObjectsInSector(universe, tile))
            {
                float spacing = OrbitSphere.ArcCells(candidate, obj.pose.position);
                if (spacing < best) best = spacing;
            }
            return best;
        }

        // ---- 触发点：玩家参考对象星区跟踪 ----

        /// <summary>
        /// 玩家参考对象进入新星区时触发首次生成，缓存按 objectId 由 UniverseManager 持有。
        /// 先快照对象列表，生成期间注册 marker 不影响本轮遍历；同图多船仅有一个参考点。
        /// </summary>
        public static void TickSectorTracking(UniverseManager universe)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            List<SpaceObject> references = universe.PlayerReferenceObjects();
            Dictionary<int, PlanetTile> hints = universe.sectorReferenceTiles;
            var activeIds = new HashSet<int>(references.Select(obj => obj.objectId));
            foreach (int id in hints.Keys.ToList())
                if (!activeIds.Contains(id)) hints.Remove(id);
            foreach (SpaceObject reference in references)
            {
                SpacePose pose = reference.pose;
                if (!hints.TryGetValue(reference.objectId, out PlanetTile hint)) hint = PlanetTile.Invalid;
                PlanetTile tile = hint.Valid
                    ? OrbitSphere.TileFromDirection(pose.position, hint)
                    : OrbitSphere.TileFromDirection(pose.position);
                if (!tile.Valid) continue;
                if (!hint.Valid || tile != hint)
                {
                    hints[reference.objectId] = tile;
                    EnsureSectorGenerated(universe, tile);
                }
            }
        }
    }
}

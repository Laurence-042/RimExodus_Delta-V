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
    /// - 密度规则 = 生成树每个叶子的标准面积密度 × 全局档位倍率；全部叶子共用
    ///   同一面积公式，分支只是分组。设置是全局的，
    ///   只影响尚未首次生成的星区；
    /// - 触发时机（§4）：玩家参考对象 tile 地址变化进入新星区；逆重战略飞行
    ///   到达链在生成降落图之前；开局出生星区在世界生成时预生成（StartedNewGame）。
    ///
    /// 生成内容只有 Delta-V 自建结构 marker（生成树叶子的 SnapshotPocket）；原版 orbit 站点本来就
    /// 按 tile 生成，天然已按星区分布，本服务不触碰。对象被摧毁后现阶段一律不补充
    /// （接口占位，星区架构 §4）。
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

        // ---- 生成 ----

        /// <summary>
        /// 确保星区已按规则完成首次生成（幂等；持久标记在生成成功后写入）。
        /// 只对轨道层（isSpace）生效：地表层 tile（如 RimExodus 无缝世界）不是星区，
        /// 记账与内容生成都不得触碰（AGENTS §16 边界）。
        /// 遍历生成树全部叶子执行「面积 × 标准密度 × 档位倍率」；小数部分用星区/生成项
        /// 确定性种子概率取整。规则读全局 Mod 设置；没有设置表时使用内置标准表。
        /// </summary>
        public static void EnsureSectorGenerated(UniverseManager universe, PlanetTile tile)
        {
            if (universe == null || !tile.Valid || !tile.LayerDef.isSpace
                || !OrbitSphere.LayerUsable(tile.Layer)) return;
            if (WasGenerated(universe, tile)) return;

            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            settings?.EnsureSectorGenerationTable();
            double sectorAreaCellsSquared = OrbitSphere.TileAreaCellsSquared(tile);
            if (sectorAreaCellsSquared <= 0d) return;
            int totalSpawned = 0;
            List<SectorGenerationNode> roots = settings != null
                ? settings.sectorGenerationRoots : SectorGenerationTable.Defaults();
            foreach (var source in SectorGenerationTable.LeavesWithPaths(roots))
            {
                SectorGenerationNode entry = source.node;
                float density = settings != null
                    ? entry.standardDensityPer100MCells2 * SectorGenerationPresets.Scale(settings.sectorGenerationPreset)
                    : entry.standardDensityPer100MCells2;
                double expected = Mathf.Max(0f, density)
                    * sectorAreaCellsSquared / SectorGenerationPresets.DensityAreaUnitCellsSquared;
                int target = StochasticRound(expected, SectorEntrySeed(tile, entry.id));
                // 物化内容链是 prefab-only（2026-09-30 定案）：池空的生成项无法物化，
                // 生成期即拦截——报错并跳过该项（物化侧对存量 marker 同判兜底）。
                // ErrorOnce 防逐星区刷屏。
                if (target > 0 && entry.prefabPaths.NullOrEmpty())
                {
                    Log.ErrorOnce($"[DeltaV] Generation entry '{entry.id}' has no prefab pool; skipped " +
                        $"{target} spawn(s) in sector {tile}. Materialization is prefab-only; " +
                        "configure the pool in Mod settings (生成 tab).",
                        ("DeltaVSectorEntryPoolEmpty:" + entry.id).GetHashCode());
                    continue;
                }
                int spawned = 0;
                for (int i = 0; i < target; i++)
                {
                    if (SpawnSectorObject(universe, tile, roots, entry, source.path, i))
                    { spawned++; totalSpawned++; }
                }
                if (spawned > 0)
                {
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"sector {tile}: first-entry generation spawned {spawned} {entry.id}(s) " +
                        $"(area {sectorAreaCellsSquared / SectorGenerationPresets.DensityAreaUnitCellsSquared:F2}e8 cells^2, " +
                        $"density {density:F3}, expected {expected:F2}, target {target})");
                }
            }
            MarkGenerated(universe, tile);
            if (totalSpawned == 0)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"sector {tile}: first-entry generation spawned nothing (rounded to zero or entries skipped)");
            }
        }

        private static int StochasticRound(double expected, int seed)
        {
            if (expected <= 0d) return 0;
            if (expected >= int.MaxValue) return int.MaxValue;
            int whole = (int)System.Math.Floor(expected);
            return whole + (Rand.ValueSeeded(seed) < expected - whole ? 1 : 0);
        }

        private static int SectorEntrySeed(PlanetTile tile, string entryId)
        {
            int worldSeed = Find.World?.info?.Seed ?? 0;
            int sectorSeed = Gen.HashCombineInt(worldSeed, StableHash(tile.ToString()));
            return Gen.HashCombineInt(sectorSeed, StableHash(entryId));
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                if (value != null)
                {
                    for (int i = 0; i < value.Length; i++) hash = hash * 31 + value[i];
                }
                return hash;
            }
        }

        private static bool SpawnSectorObject(UniverseManager universe, PlanetTile tile,
            List<SectorGenerationNode> roots, SectorGenerationNode entry,
            IReadOnlyList<string> categoryPath, int spawnIndex)
        {
            Vector3 center = OrbitSphere.TileCenterDirection(tile);
            // 先在星区内切圆近似中取样，再用原版 tile 地址函数复核；小尺度下留白
            // 自动按半径收缩，避免固定 1000 格留白吞掉整个星区。
            float halfSpan = OrbitSphere.CellsPerTile * 0.5f;
            float effectiveMargin = Mathf.Min(EdgeMarginCells, halfSpan * 0.5f);
            float usableRadius = Mathf.Max(1f, halfSpan - effectiveMargin);
            Vector2 bestOffset = Vector2.zero;
            bool found = false;
            float bestSpacing = -1f;
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                float angle = Rand.Range(0f, Mathf.PI * 2f);
                float radius = Mathf.Sqrt(Rand.Value) * usableRadius;
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                Vector3 candidate = OrbitSphere.Move(center, offset);
                if (OrbitSphere.TileFromDirection(tile.Layer, candidate) != tile) continue;
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
                    $"sector {tile}: {entry.id} placement relaxed (best spacing {bestSpacing:F0} < {MinSpacingCells})");
            }
            // 名称与派系在生成时一次性决定（2026-09-30 定案）：随机派系 + 完整名
            //（RulePack 词干 + 生成项名称后缀 + (派系名) + #ID），烘焙进 envLabel/faction 随档
            // 持久；物化时直接应用（容器派系、prefab 建筑与 pawn 派系、归属名），
            // 之后不改不变。envLabel 是持久事实，不随语言切换。
            int poiId = universe.AllocateOwnerId();
            SpaceFactionCandidates().TryRandomElement(out Faction faction);
            int seed = Rand.Int;
            string preferred = MapPrefab.ResolvePoolPath(entry.prefabPaths, seed);
            string prefabPath = SectorGenerationTable.ResolveFrozenPrefab(
                roots, preferred, categoryPath, seed);
            if (prefabPath.NullOrEmpty()) return false;
            MapPrefabDocument prefab;
            try { prefab = MapPrefab.LoadRelative(prefabPath); }
            catch (System.Exception ex)
            {
                Log.ErrorOnce($"[DeltaV] Generation entry '{entry.id}' cannot load prefab '{prefabPath}': {ex.Message}",
                    ("DeltaVSectorEntryPrefab:" + entry.id + ":" + prefabPath).GetHashCode());
                return false;
            }
            string label = NameGenerator.GenerateName(DeltaVDefOf.DeltaV_NamerSpacePoi,
                extraRules: new List<Rule> { new Rule_String("generation_suffix", entry.label) });
            if (faction != null)
            {
                label += $"({faction.Name})";
            }
            label += $" #{poiId}";
            var poi = new PoiState(poiId, label,
                OrbitSphere.Move(center, bestOffset), Rand.Range(0f, 360f) * Mathf.Deg2Rad, seed)
            {
                faction = faction,
                sourcePrefabPath = prefabPath.Replace('\\', '/'),
                sourcePrefabCategoryPath = new List<string>(categoryPath),
                sourcePrefabSeed = seed,
                externalPrefabGenerationCompleted = false
            };
            if (PrefabHasNaturalBody(prefab)) poi.baseTag |= SpaceObjectTag.GravityRegion;
            universe.RegisterObject(poi);
            if (universe.ObjectById(poiId) == poi) return true;
            universe.UnregisterObject(poi);
            return false;
        }

        internal static bool PrefabHasNaturalBody(MapPrefabDocument prefab)
            => prefab != null && (prefab.thingEntries.Any(entry => GlobeDisplayRules.IsNaturalRock(entry.def))
                || prefab.terrain.Any(entry => entry.def?.natural == true));

        internal static IEnumerable<Faction> SpaceFactionCandidates()
            => Find.FactionManager.GetFactions(minTechLevel: TechLevel.Industrial, allowTemporary: true);

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

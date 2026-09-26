using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

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
    /// - 密度规则 = 保底 + 分段概率额外量（均可设置）。数值待定（§12.1），
    ///   现阶段提供接口与可运行的保守默认值（残骸保底 1、无额外量）；
    /// - 触发时机（§4）：自由飞行中玩家船 tile 地址变化进入新星区；逆重战略飞行
    ///   到达链在生成降落图之前；开局出生星区在世界生成时预生成（StartedNewGame）。
    ///
    /// 生成内容只有 Delta-V 自建对象（残骸等 SnapshotPocket）；原版 orbit 站点本来就
    /// 按 tile 生成，天然已按星区分布，本服务不触碰。对象被摧毁后的补充策略按类型
    /// 区分——现阶段只留接口（SuppressReplenishment 默认开），不做补充。
    /// </summary>
    public static class SectorContentService
    {
        /// <summary>分段概率额外量：以 chance 概率追加 1..maxCount 个（各段独立掷骰）。</summary>
        public struct ExtraSegment
        {
            public float chance;
            public int maxCount;

            public ExtraSegment(float chance, int maxCount)
            {
                this.chance = chance;
                this.maxCount = maxCount;
            }
        }

        /// <summary>
        /// 星区内容密度规则（保底 + 分段概率额外量）。数值待定（§12.1）：
        /// 默认每星区残骸保底 1、无额外量；规则可整体替换（设置接口的载体）。
        /// </summary>
        public sealed class SectorContentRules
        {
            /// <summary>每星区残骸保底数量（不足则生成补齐）。</summary>
            public int minDerelicts = 1;

            /// <summary>残骸的额外生成分段（默认空 = 不额外生成）。</summary>
            public List<ExtraSegment> derelictExtras = new List<ExtraSegment>();

            /// <summary>现阶段不做销毁补充（接口占位，星区架构 §4）。</summary>
            public bool suppressReplenishment = true;
        }

        /// <summary>当前规则；接口载体，后续可由设置/蓝图替换。</summary>
        public static SectorContentRules Rules { get; set; } = new SectorContentRules();

        /// <summary>自建对象远离星区边缘的最小留白（格）：避免贴脸生成与跨星区激活。</summary>
        public const float EdgeMarginCells = 1000f;

        /// <summary>同星区内自建对象相互之间的最小间距（格）。</summary>
        public const float MinSpacingCells = 300f;

        /// <summary>落点重试次数上限（间距/留白约束满足不了时放弃精化，不阻塞生成）。</summary>
        private const int PlacementAttempts = 24;

        // 自由飞行星区跟踪的 per-ship tile hint（运行时态，不存档）。
        private static readonly Dictionary<int, PlanetTile> SectorHintByShip = new Dictionary<int, PlanetTile>();

        /// <summary>读档 / 场景重置时清空运行时跟踪。</summary>
        public static void Reset()
        {
            SectorHintByShip.Clear();
        }

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

        /// <summary>星区内现存的 Delta-V 自建残骸数（SnapshotPocket 口径）。</summary>
        private static int CountDerelicts(UniverseManager universe, PlanetTile tile)
        {
            return ObjectsInSector(universe, tile).OfType<PoiState>()
                .Count(p => p.kind == PoiKind.SnapshotPocket);
        }

        // ---- 生成 ----

        /// <summary>
        /// 确保星区已按规则完成首次生成（幂等；持久标记在生成成功后写入）。
        /// 只对轨道层（isSpace）生效：地表层 tile（如 RimExodus 无缝世界）不是星区，
        /// 记账与残骸生成都不得触碰（AGENTS §16 边界）。
        /// </summary>
        public static void EnsureSectorGenerated(UniverseManager universe, PlanetTile tile)
        {
            if (universe == null || !tile.Valid || !tile.LayerDef.isSpace
                || !OrbitSphere.LayerUsable(tile.Layer)) return;
            if (WasGenerated(universe, tile)) return;

            SectorContentRules rules = Rules ?? new SectorContentRules();
            int existing = CountDerelicts(universe, tile);
            int target = Mathf.Max(rules.minDerelicts, existing);
            foreach (ExtraSegment segment in rules.derelictExtras)
            {
                if (Rand.Chance(segment.chance))
                {
                    target += Rand.RangeInclusive(1, Mathf.Max(1, segment.maxCount));
                }
            }
            int spawned = 0;
            for (int i = existing; i < target; i++)
            {
                SpawnDerelict(universe, tile);
                spawned++;
            }
            MarkGenerated(universe, tile);
            if (spawned > 0)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"sector {tile}: first-entry generation spawned {spawned} derelict(s) (existing {existing}, target {target})");
            }
        }

        private static void SpawnDerelict(UniverseManager universe, PlanetTile tile)
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
                    $"sector {tile}: derelict placement relaxed (best spacing {bestSpacing:F0} < {MinSpacingCells})");
            }
            var poi = new PoiState(universe.AllocateOwnerId(), "Derelict wreck",
                OrbitSphere.Move(center, bestOffset), Rand.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int);
            universe.RegisterObject(poi);
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

        // ---- 触发点：自由飞行星区跟踪 ----

        /// <summary>
        /// 每 60 tick 由 UniverseManager 调用：自由飞行的玩家船 tile 地址变化进入
        /// 新星区时触发首次生成。hint 爬坡避免每次全量扫描。
        /// 必须先快照船列表：EnsureSectorGenerated 会向 universe.objects 添加残骸，
        /// 惰性枚举边走边 Add 会抛 InvalidOperationException（2026-09-17 实测事故）。
        /// </summary>
        public static void TickSectorTracking(UniverseManager universe)
        {
            if (universe == null || !OrbitSphere.Valid) return;
            List<Ship> ships = universe.PlayerShips();
            foreach (Ship ship in ships)
            {
                if (!ShipRegistry.IsFreeFlight(ship)
                    || universe.ShipTransitions.IsDockingFlowShip(ship) || ship.map == null) continue;
                SpacePose pose = ShipRegistry.ShipPose(ship);
                SectorHintByShip.TryGetValue(ship.ownerId, out PlanetTile hint);
                PlanetTile tile = hint.Valid
                    ? OrbitSphere.TileFromDirection(pose.position, hint)
                    : OrbitSphere.TileFromDirection(pose.position);
                if (!tile.Valid) continue;
                if (!hint.Valid || tile != hint)
                {
                    SectorHintByShip[ship.ownerId] = tile;
                    EnsureSectorGenerated(universe, tile);
                }
            }
        }
    }
}

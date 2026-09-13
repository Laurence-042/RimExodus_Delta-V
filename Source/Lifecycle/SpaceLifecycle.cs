using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// POI 地图生命周期的单一管理入口（AGENTS §11：加载/卸载必须有单一管理入口，
    /// POI、战斗、UI 与调试操作都只经本类触碰 POI 图生命周期）。
    ///
    /// 三段模型（阶段3 用户决策，按 universe 距离而非 tile 距离）：
    /// - 活跃（≤ poiActiveRadius）：图存在且正常 tick；无图则生成（快照优先恢复，
    ///   否则模板 + seed 重生成），生成经 LongEventHandler 异步入队；
    /// - 休眠（activeRadius &lt; d ≤ deleteRadius）：图保留但冻结 tick
    ///   （Harmony Prefix 早退，Patch_PoiDormancy）；当前聚焦图永不休眠；
    /// - 删除/Packed（> deleteRadius）：图上无 pawn 时收结构级快照销毁口袋图
    ///   （有 pawn 只休眠——原版 AnyPawnBlockingMapRemoval 同语义）。
    ///
    /// 休眠集合是运行时状态、不存档：读档后全部先活跃，下一次扫描重新分段。
    /// 距离参考 = 全部玩家阵营飞船的最小 universe 距离；无玩家船时不做任何迁移。
    /// </summary>
    public static class SpaceLifecycle
    {
        /// <summary>POI 口袋图边长（模板规模，小于飞船测试图）。</summary>
        public const int PoiMapSize = 90;

        private static readonly HashSet<Map> DormantMaps = new HashSet<Map>();

        /// <summary>生成中标记：LongEventHandler 入队到落成之间的重入防护。</summary>
        private static readonly HashSet<PoiState> PendingGeneration = new HashSet<PoiState>();

        public static bool IsDormant(Map map)
        {
            return map != null && DormantMaps.Contains(map);
        }

        /// <summary>唤醒一张休眠 POI 图（Game.CurrentMap setter 的同步唤醒入口）。</summary>
        public static void Wake(Map map, string reason)
        {
            if (map != null && DormantMaps.Remove(map))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{map.Parent?.Label ?? map.ToString()}: woken ({reason})");
            }
        }

        /// <summary>UniverseManager 每 60 tick 调用：对全部 POI 做距离分段与状态迁移。</summary>
        public static void Tick(UniverseManager universe)
        {
            List<PoiState> pois = universe.objects.OfType<PoiState>().ToList();
            if (pois.Count == 0)
            {
                return;
            }
            Map currentMap = Find.CurrentMap;
            float activeRadius = DeltaVMod.Settings.poiActiveRadius;
            float deleteRadius = Mathf.Max(DeltaVMod.Settings.poiDeleteRadius, activeRadius + 1f);
            foreach (PoiState poi in pois)
            {
                if (PendingGeneration.Contains(poi))
                {
                    continue;
                }
                if (poi.map == currentMap)
                {
                    // 聚焦中的 POI 图强制活跃（玩家正看着/走在里面的图不能冻结）。
                    if (DormantMaps.Remove(poi.map))
                    {
                        DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{poi.label}: woken (focused)");
                    }
                    continue;
                }
                float dist = MinPlayerDistance(universe, poi);
                if (dist <= activeRadius)
                {
                    EnsureActive(universe, poi);
                }
                else if (dist <= deleteRadius)
                {
                    EnsureDormant(poi);
                }
                else
                {
                    TryPack(poi);
                }
            }
        }

        /// <summary>玩家阵营船到 POI 的最小 universe 距离；无玩家船返回正无穷（不做迁移）。</summary>
        public static float MinPlayerDistance(UniverseManager universe, PoiState poi)
        {
            float best = float.PositiveInfinity;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj is ShipState ship && ship.isPlayerShip)
                {
                    best = Mathf.Min(best, Vector2.Distance(ship.pose.position, poi.pose.position));
                }
            }
            return best;
        }

        private static void EnsureActive(UniverseManager universe, PoiState poi)
        {
            if (poi.map != null)
            {
                if (DormantMaps.Remove(poi.map))
                {
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{poi.label}: woken (distance)");
                }
                return;
            }
            // Packed → 生成/恢复。地图生成耗时不定，走 LongEventHandler（同步、带加载提示）。
            PendingGeneration.Add(poi);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{poi.label}: generating map (distance)");
            LongEventHandler.QueueLongEvent(delegate
            {
                try
                {
                    GeneratePoiMap(universe, poi);
                }
                finally
                {
                    PendingGeneration.Remove(poi);
                }
            }, "GeneratingMap", false, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        private static void EnsureDormant(PoiState poi)
        {
            if (poi.map == null || !DormantMaps.Add(poi.map))
            {
                return;
            }
            DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{poi.label}: dormant (map kept, ticking frozen)");
        }

        private static void TryPack(PoiState poi)
        {
            if (poi.map == null)
            {
                return; // 已是 Packed
            }
            if (poi.map == Find.CurrentMap)
            {
                return; // 聚焦图不删
            }
            if (poi.map.mapPawns.AllPawnsSpawned.Any())
            {
                EnsureDormant(poi); // 有 pawn 只休眠（不删，原版同语义）
                return;
            }
            Map map = poi.map;
            poi.snapshot = PoiSnapshot.Capture(map);
            poi.map = null;
            DormantMaps.Remove(map);
            PocketMapUtility.DestroyPocketMap(map);
            Messages.Message("DeltaV_PoiPacked".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{poi.label}: map destroyed and packed into snapshot ({poi.snapshot.ThingCount} things)");
        }

        /// <summary>生成（或从快照恢复）POI 口袋图并挂到 poi.map。LongEventHandler 内执行。</summary>
        private static void GeneratePoiMap(UniverseManager universe, PoiState poi)
        {
            Map hostMap = universe.hostMap;
            if (hostMap == null || !hostMap.Tile.Valid)
            {
                Log.Error($"[DeltaV] Cannot generate POI '{poi.label}': no valid space host map.");
                return;
            }
            PlanetLayer layer = hostMap.Tile.Layer;
            PlanetTile projected = UniverseLayerMapping.ProjectToTile(layer, hostMap.Tile, poi.pose.position, hostMap.Tile);
            PlanetTile tile = UniverseLayerMapping.FindFreeTileNear(layer, projected);
            if (!tile.Valid)
            {
                Log.Error($"[DeltaV] No free orbit tile for POI '{poi.label}'; skipping generation.");
                return;
            }
            var parent = (ShipPocketParent_DeltaV)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.Tile = tile;
            parent.sourceMap = hostMap;
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            Map map = MapGenerator.GenerateMap(new IntVec3(PoiMapSize, 1, PoiMapSize), parent,
                DeltaVDefOf.DeltaV_ShipPocket, null, null, isPocketMap: true);
            Find.World.pocketMaps.Add(parent);
            if (poi.snapshot != null)
            {
                poi.snapshot.ApplyTo(map);
                poi.snapshot = null;
                Messages.Message("DeltaV_PoiRestored".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                PoiTemplate.Stamp(map, poi.seed);
                Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
            }
            poi.map = map;
        }

        // ---- 调试/外部操作入口（DebugActions 只经这些方法触碰 POI 生命周期）----

        /// <summary>强制 POI 立即生成图（忽略距离；等待中的重复调用会被 PendingGeneration 挡住）。</summary>
        public static void ForceActivate(UniverseManager universe, PoiState poi)
        {
            EnsureActive(universe, poi);
        }

        /// <summary>强制休眠（聚焦图除外）。</summary>
        public static void ForceDormant(PoiState poi)
        {
            if (poi.map != null && poi.map != Find.CurrentMap)
            {
                DormantMaps.Add(poi.map);
            }
        }

        /// <summary>强制删除收快照（聚焦图/有 pawn 时拒绝）。</summary>
        public static void ForcePack(PoiState poi)
        {
            TryPack(poi);
        }

        public static bool IsPendingGeneration(PoiState poi)
        {
            return PendingGeneration.Contains(poi);
        }

        /// <summary>场景销毁/读档清理：全部休眠记账清空（图本身由调用方销毁）。</summary>
        public static void Reset()
        {
            DormantMaps.Clear();
            PendingGeneration.Clear();
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// TerrainGrid 私有 map 字段的统一读取（归属 patch 族共用，不各建 FieldInfo）。
    /// </summary>
    internal static class TerrainGridOwnershipAccess
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(TerrainGrid), "map");

        internal static Map MapOf(TerrainGrid grid)
        {
            return (Map)MapField?.GetValue(grid);
        }
    }

    /// <summary>
    /// 基架归属的唯一写入口（2026-09-18 设计）。基架写入全部经 TerrainGrid.SetFoundation
    /// （SetTerrain 对 isFoundation 地形的分支路由；玩家建造、生成盖章、降落放置、快照恢复
    /// 四条路径共用），此处统一登记归属：
    /// 1. 显式盖章（SubstructureOwnership.SetPendingStamp 登记的降落/生成管线）优先；
    /// 2. 其余仅 Delta-V 参考系图按邻接规则（四邻一致继承 / 孤立新建 / 冲突弃写为 Unassigned）。
    /// RimExodus/原版地表图不参与（FrameOf 门禁，AGENTS §3/§16）。
    /// 基架移除的归属同步见 Patch_TerrainGrid_RemoveFoundation_Ownership（两方向闭环）。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetFoundation))]
    public static class Patch_TerrainGrid_SetFoundation_Ownership
    {
        private static void Prefix(TerrainGrid __instance, IntVec3 c, out TerrainDef __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            __state = SubstructureOwnership.FoundationAt(map, c);
        }

        public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr, TerrainDef __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            TerrainDef next = SubstructureOwnership.FoundationAt(map, c);
            if (next == null || next != newTerr) return;
            var universe = Current.Game?.GetComponent<UniverseManager>();
            var ownership = SubstructureOwnership.For(map);
            using var changes = universe?.ShipStructure.BeginChanges();
            int beforeOwner = ownership.OwnerAt(c);
            if (ownership.TryGetStamp(c, out int stamped)) ownership.Assign(c, stamped);
            else if (universe?.FrameOf(map) != null) ownership.ResolveBuilt(c);
            universe?.NotifyFoundationChanged(map, c);
            if (universe?.SpaceObjectOnMap(map) != null || universe?.ShipStructure.Tracks(map) == true)
                universe.ShipStructure.NotifyCell(map, c, ShipStructureAspect.Foundation, __state, next, beforeOwner, ownership.OwnerAt(c));
        }
    }

    /// <summary>
    /// 基架移除的归属同步（2026-09-27 用户决策）：RemoveFoundation 是一切「单格移除」
    /// 的漏斗（玩家手拆 JobDriver/Designator、火焚 Notify_FoundationDestroyed、战斗剥离
    /// SpaceImpactEffects、降落放置前的旧层清理），RemoveGravshipTerrainUnsafe 覆盖起飞
    /// 批量剥离（原版战略起飞 + GravshipInterop.StripDomain）。两者都经
    /// NotifyFoundationRemoved 清该格归属（内含版本 bump 与引擎/剪影通知）；
    /// 搬运逐格清除源域，保留同归属未搬走的断片。地表图沿用原版流程。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveFoundation))]
    public static class Patch_TerrainGrid_RemoveFoundation_Ownership
    {
        private static void Prefix(TerrainGrid __instance, IntVec3 c, out (TerrainDef Foundation, int Owner) __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            __state = (SubstructureOwnership.FoundationAt(map, c), SubstructureOwnership.TryGet(map)?.OwnerAt(c) ?? 0);
        }

        public static void Postfix(TerrainGrid __instance, IntVec3 c, (TerrainDef Foundation, int Owner) __state) =>
            NotifyRemoval(TerrainGridOwnershipAccess.MapOf(__instance), c, __state);

        internal static void NotifyRemoval(Map map, IntVec3 cell, (TerrainDef Foundation, int Owner) previous)
        {
            if (map == null) return;
            var universe = Current.Game?.GetComponent<UniverseManager>();
            using var changes = universe?.ShipStructure.BeginChanges();
            var ownership = SubstructureOwnership.TryGet(map);
            ownership?.UpdateSupportCell(cell);
            TerrainDef next = SubstructureOwnership.FoundationAt(map, cell);
            if (next == null && previous.Foundation != null) ownership?.NotifyFoundationRemoved(cell);
            if (universe?.SpaceObjectOnMap(map) == null && universe?.ShipStructure.Tracks(map) != true) return;
            universe.NotifyFoundationChanged(map, cell);
            universe.NotifySupportChanged(map, cell);
            universe.ShipStructure.NotifyCell(map, cell, ShipStructureAspect.Foundation, previous.Foundation, next, previous.Owner);
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveGravshipTerrainUnsafe))]
    public static class Patch_TerrainGrid_RemoveGravshipTerrainUnsafe_Ownership
    {
        // 参数名按目标方法真实签名绑定（RemoveGravshipTerrainUnsafe(IntVec3 cell, int index)，
        // 与 RemoveFoundation 的 c 不同；名字不匹配会中断整个 PatchAll）。
        private static void Prefix(TerrainGrid __instance, IntVec3 cell, out (TerrainDef Foundation, int Owner) __state)
        {
            Map map = TerrainGridOwnershipAccess.MapOf(__instance);
            __state = (SubstructureOwnership.FoundationAt(map, cell), SubstructureOwnership.TryGet(map)?.OwnerAt(cell) ?? 0);
        }

        public static void Postfix(TerrainGrid __instance, IntVec3 cell, (TerrainDef Foundation, int Owner) __state) =>
            Patch_TerrainGrid_RemoveFoundation_Ownership.NotifyRemoval(TerrainGridOwnershipAccess.MapOf(__instance), cell, __state);
    }

    /// <summary>
    /// 原版 Gravship 批量剥离在 RemoveGravshipTerrainUnsafe 返回后才调用
    /// RemoveRoofUnsafe。前一段基架通知会在屋顶仍存在时把该格记为 loose terrain；
    /// raw 屋顶写入本身不触发 SetRoof、MapEvents 或结构版本，因此必须在最终清除后
    /// 补发同一套屋顶缓存通知。原版战略起飞与 Delta-V StripDomain 共用此入口。
    /// </summary>
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.RemoveRoofUnsafe))]
    public static class Patch_RoofGrid_RemoveRoofUnsafe_Structure
    {
        private static void Prefix(Map ___map, int index, out RoofDef __state)
        {
            __state = ___map?.roofGrid?.RoofAt(index);
        }

        public static void Postfix(Map ___map, int index, RoofDef __state)
        {
            if (___map == null || __state == null) return;
            IntVec3 cell = ___map.cellIndices.IndexToCell(index);
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.NotifyRoof(
                ___map, cell, __state, ___map.roofGrid.RoofAt(index));
        }
    }

        /// <summary>
        /// 引擎基架连通域的归属过滤收口：引擎的「自己的 ID」= 其中心格基架归属。
        /// 命中时以同构的归属过滤 flood-fill 替换原版遍历（镜像 GravshipUtility.
        /// GetConnectedSubstructure，仅增加 owner 谓词），ValidSubstructure /
        /// AllConnectedSubstructure(+NoRegen) 四个 getter、原版 overlay 与一切捕获管线
        /// 读者自动获得新口径：
        /// - 逆重战略发射（ValidSubstructure：力场内限量域）只算/只带走自己 ID 的基架；
        /// - 自由飞行捕获（AllConnectedSubstructure）只含自己 ID 的连通域，停靠图上的
        ///   wreck 基架不再被误并入。
        /// 未占有（0）是真实归属域：其上的引擎只连通同为未占有的基架（孤立平台可
        /// bootstrap 成船）。仅组件缺失（迁移前防御态）走原版口径；仅 Delta-V 参考
        /// 系图生效。
        /// </summary>
        [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GetConnectedSubstructure))]
        public static class Patch_GravshipUtility_GetConnectedSubstructure_Ownership
        {
            public static bool Prefix(Building_GravEngine engine, HashSet<IntVec3> cells, int maxCells,
                bool requireInsideFootprint)
            {
                Map map = engine?.Map;
                if (map == null || cells == null)
                {
                    return true;
                }
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                if (universe?.FrameOf(map) == null)
                {
                    return true;
                }
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
                if (ownership == null)
                {
                    return true;
                }
                FillOwnedConnectedSubstructure(engine, cells, maxCells, requireInsideFootprint,
                    ownership, ownership.OwnerAt(engine.Position));
                return false;
            }

        /// <summary>镜像原版 flood-fill：从引擎出发，仅通行「基架 ∧ 归属一致」的格子。</summary>
        private static void FillOwnedConnectedSubstructure(Building_GravEngine engine,
            HashSet<IntVec3> cells, int maxCells, bool requireInsideFootprint,
            SubstructureOwnership ownership, int ownerId)
        {
            cells.Clear();
            if (!engine.Spawned)
            {
                return;
            }
            Map map = engine.Map;
            List<Thing> footprintMakers = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint);
            SubstructureOwnership.CollectConnected(map, engine.Position, cells, delegate (IntVec3 x)
            {
                if (ownership.OwnerAt(x) != ownerId)
                {
                    return false;
                }
                return !requireInsideFootprint || GravshipUtility.InsideFootprint(x, map, footprintMakers);
            }, maxCells);
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架归属的唯一写入口（2026-09-18 设计）。基架写入全部经 TerrainGrid.SetFoundation
    /// （SetTerrain 对 isFoundation 地形的分支路由；玩家建造、生成盖章、降落放置、快照恢复
    /// 四条路径共用），此处统一登记归属：
    /// 1. 显式盖章（SubstructureOwnership.SetPendingStamp 登记的降落/生成管线）优先；
    /// 2. 其余仅 Delta-V 参考系图按邻接规则（四邻一致继承 / 孤立新建 / 冲突弃写为 Unassigned）。
    /// RimExodus/原版地表图不参与（FrameOf 门禁，AGENTS §3/§16）。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetFoundation))]
    public static class Patch_TerrainGrid_SetFoundation_Ownership
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(TerrainGrid), "map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr)
        {
            if (newTerr != DeltaVDefOf.Substructure)
            {
                return; // 目前唯一基架地形；未来其他 foundation 不自动纳入归属体系
            }
            Map map = (Map)MapField?.GetValue(__instance);
            if (map == null)
            {
                return;
            }
            if (SubstructureOwnership.TryGetStamp(map, c, out int stamped))
            {
                SubstructureOwnership.For(map).Assign(c, stamped);
                return;
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) == null)
            {
                return;
            }
            // 「未占有」语义（2026-09-18 用户决策）：继承唯一有效邻接归属并整域吸收
            // 相连未占有区域；孤立建造保持未占有（不分配新 ID），等待后续相连吸收
            // 或经驾驶台 bootstrap 认领。多归属冲突同样保持未占有（PlaceWorker 已拒）。
            SubstructureOwnership.For(map).ResolveBuilt(c);
        }
    }

        /// <summary>
        /// 引擎基架连通域的归属过滤收口：引擎的「自己的 ID」= 其中心格基架归属。
        /// 命中时以同构的归属过滤 flood-fill 替换原版遍历（镜像 GravshipUtility.
        /// GetConnectedSubstructure，仅增加 owner 谓词），ValidSubstructure /
        /// AllConnectedSubstructure(+NoRegen) 四个 getter、原版 overlay 与一切捕获管线
        /// 读者自动获得新口径：
        /// - 逆重战略发射（ValidSubstructure：力场内限量域）只算/只带走自己 ID 的基架；
        /// - 移动模式捕获（AllConnectedSubstructure）只含自己 ID 的连通域，停靠图上的
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
            map.floodFiller.FloodFill(engine.Position, delegate (IntVec3 x)
            {
                if (!x.InBounds(map))
                {
                    return false;
                }
                TerrainDef foundation = map.terrainGrid.FoundationAt(x);
                if (foundation == null || !foundation.IsSubstructure)
                {
                    return false;
                }
                if (ownership.OwnerAt(x) != ownerId)
                {
                    return false;
                }
                return !requireInsideFootprint || GravshipUtility.InsideFootprint(x, map, footprintMakers);
            }, delegate (IntVec3 x)
            {
                cells.Add(x);
                return false;
            }, maxCells);
        }
    }
}

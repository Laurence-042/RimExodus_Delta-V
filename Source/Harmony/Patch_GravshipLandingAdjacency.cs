using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 降落选点规则（2026-09-18 基架归属设计定稿）：
    /// - 撤销原版「脚印须与地面重合」（GravshipMustBeConnectedToLand）——Delta-V 空间图
    ///   上允许降落到纯太空（宿主图内的空 Space 区域），不再要求接触坚固表面；
    /// - 新增禁触规则：脚印任一格不得与**异主**基架重合或四邻接（不得紧贴 wreck 基架；
    ///   跨船硬连接只能经对接口的对接通道）。同主基架（如自己战略发射的遗留）不受限。
    /// 与异主基架直接重合的情形由原版逐格检查自行拒绝（基架不可叠建基架），此处只拦
    /// 「邻接」与重合兜底。仅 Delta-V 参考系图生效（FrameOf 判定）：RimExodus/原版地表
    /// 的逆重移动不受影响。逐格合法性（越界/阻挡/禁建区等）仍由原版裁决。
    /// </summary>
    [HarmonyPatch(typeof(Designator_MoveGravship), "ValidGravshipLocation")]
    public static class Patch_DesignatorMoveGravship_AdjacentLanding
    {
        private static readonly MethodInfo IsValidCell =
            AccessTools.Method(typeof(Designator_MoveGravship), "IsValidCell");

        public static void Postfix(Designator_MoveGravship __instance, IntVec3 loc,
            List<IntVec3> validCells, List<IntVec3> invalidCells, ref AcceptanceReport __result)
        {
            if (__result.Accepted
                || __result.Reason != "GravshipMustBeConnectedToLand".Translate())
            {
                return;
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(Find.CurrentMap) == null) return;
            GravshipLandingMarker marker = __instance.marker;
            if (marker?.gravship == null) return;

            Map map = Find.CurrentMap;
            // 停放锚点唯一入口：原版判定链的显式镜像（对 root 锚恒等；loc 即 root 锚格）。
            IntVec3 root = SpaceTransform.PlacementAnchorRoot(loc,
                marker.gravship.Bounds.Size, marker.GravshipRotation);

            if (TouchesForeignSubstructure(root, marker, map, LandingOwnerId(marker)))
            {
                // 异主基架重合/邻接：与建造归属规则同构的禁触（不能紧贴 wreck 基架）。
                __result = "DeltaV_LandingBlockedForeignSubstructure".Translate();
                invalidCells?.AddRange(validCells);
                validCells?.Clear();
                return;
            }

            if (validCells == null || invalidCells == null)
            {
                // 点击门禁路径（CanDesignateCell）：走到重合检查失败时所有格已逐格合法，
                // 纯太空停靠放行。
                __result = true;
                return;
            }
            // 预览路径（SelectedUpdate）：原版在重合失败时把全部格并入 invalid——放宽后按
            // 逐格合法性重算红绿分布。反射入口缺失时退化为整体放行（点击路径本就如此）。
            if (IsValidCell == null)
            {
                validCells.AddRange(invalidCells);
                invalidCells.Clear();
                __result = true;
                return;
            }
            validCells.Clear();
            invalidCells.Clear();
            AcceptanceReport result = true;
            foreach (IntVec3 gravshipCell in marker.GravshipCells)
            {
                IntVec3 cell = root + gravshipCell;
                var cellReport = (AcceptanceReport)IsValidCell.Invoke(null, new object[] { cell, map });
                if (cellReport.Accepted)
                {
                    validCells.Add(cell);
                }
                else
                {
                    invalidCells.Add(cell);
                    result = cellReport;
                }
            }
            __result = result;
        }

        /// <summary>
        /// 降落船的归属 ID：战术选点阶段引擎仍在口袋图（或同图旧位）上，读其当前格归属；
        /// 停靠流程船（PendingDockingShip）优先。无归属数据时返回 Unassigned——禁触检查
        /// 会把一切基架视为异主（保守：迁移后不应出现）。
        /// </summary>
        private static int LandingOwnerId(GravshipLandingMarker marker)
        {
            ShipState pending = DockController.PendingDockingShip;
            if (pending != null)
            {
                return pending.objectId;
            }
            Building_GravEngine engine = marker.gravship?.Engine;
            if (engine != null && engine.Spawned)
            {
                return SubstructureOwnership.TryGet(engine.Map)?.OwnerAt(engine.Position)
                    ?? SubstructureOwnership.Unassigned;
            }
            return SubstructureOwnership.Unassigned;
        }

        /// <summary>脚印任一格自身或四邻触碰异主基架。</summary>
        private static bool TouchesForeignSubstructure(IntVec3 root, GravshipLandingMarker marker,
            Map map, int ownerId)
        {
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            foreach (IntVec3 gravshipCell in marker.GravshipCells)
            {
                IntVec3 cell = root + gravshipCell;
                if (IsForeignSubstructure(cell, map, ownership, ownerId)
                    || IsForeignSubstructure(cell + IntVec3.East, map, ownership, ownerId)
                    || IsForeignSubstructure(cell + IntVec3.West, map, ownership, ownerId)
                    || IsForeignSubstructure(cell + IntVec3.North, map, ownership, ownerId)
                    || IsForeignSubstructure(cell + IntVec3.South, map, ownership, ownerId))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsForeignSubstructure(IntVec3 cell, Map map,
            SubstructureOwnership ownership, int ownerId)
        {
            return cell.InBounds(map)
                && map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure
                && (ownership?.OwnerAt(cell) ?? SubstructureOwnership.Unassigned) != ownerId;
        }
    }
}

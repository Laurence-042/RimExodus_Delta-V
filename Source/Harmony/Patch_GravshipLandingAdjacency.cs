using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 降落选点规则（2026-09-18 基架归属设计定稿；2026-09-24 解耦架构 §4 修订）：
    /// - 撤销原版「脚印须与地面重合」（GravshipMustBeConnectedToLand）——Delta-V 空间图
    ///   上允许降落到纯太空（宿主图内的空 Space 区域），不再要求接触坚固表面；
    /// - **禁止脚印与任何归属的既有基架重叠**（含自己 ID 的遗留——旧的「自己 ID 可
    ///   重叠」例外已按解耦架构 §4 取消：基架不重叠，跨船连接走对接或贴边）；
    /// - **允许异主基架四邻接**（对接允许异主基架相邻；几何相邻不授予跨归属控制权）。
    /// 与基架直接重合不再区分归属，统一由本补丁拦截。仅 Delta-V 参考系图生效
    /// （FrameOf 判定）：RimExodus/原版地表的逆重移动不受影响。小行星天然地面不是
    /// 基架，不触发本门禁（允许飞船停在其地面上）。逐格合法性（越界/阻挡/禁建区等）
    /// 仍由原版裁决。
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
            // loc 是原版 designator 输入格；与原版 ValidGravshipLocation 同一入口求 root。
            IntVec3 root = GravshipInterop.DesignatorInputToRoot(loc,
                marker.gravship.Bounds.Size, marker.GravshipRotation);

            if (OverlapsAnySubstructure(root, marker, map, out _, out _))
            {
                // 脚印与任何归属的既有基架重叠：拒绝（异主邻接本身合法）。
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
        /// 只在确认落点与完成复核两个事件调用。复用实际禁触判定，记录两次检查
        /// 使用的 root、脚印与第一处基架重叠，避免另造一份近似几何诊断。
        /// </summary>
        internal static string DescribeFootprint(GravshipLandingMarker marker, Map map, IntVec3 root)
        {
            if (marker?.gravship == null || map == null) return "marker/target map missing";
            int count = 0;
            int minX = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxZ = int.MinValue;
            int hash = 0;
            foreach (IntVec3 cell in marker.GravshipCells)
            {
                count++;
                if (cell.x < minX) minX = cell.x;
                if (cell.x > maxX) maxX = cell.x;
                if (cell.z < minZ) minZ = cell.z;
                if (cell.z > maxZ) maxZ = cell.z;
                unchecked { hash += cell.x * 73856093 ^ cell.z * 19349663; }
            }
            bool touches = OverlapsAnySubstructure(root, marker, map, out IntVec3 footprintCell, out IntVec3 substructureCell);
            return $"root={root} marker={marker.Position} rot={marker.GravshipRotation.AsInt} "
                + $"bounds={marker.gravship.Bounds.Size} footprint={count} "
                + (count == 0 ? "range=empty " : $"range=({minX},{minZ})..({maxX},{maxZ}) ")
                + $"hash={hash:X8} "
                + (touches ? $"substructureOverlap={footprintCell}->{substructureCell}" : "substructureOverlap=none");
        }

        /// <summary>脚印任一格与任何归属的既有基架重叠。</summary>
        private static bool OverlapsAnySubstructure(IntVec3 root, GravshipLandingMarker marker,
            Map map, out IntVec3 footprintCell, out IntVec3 substructureCell)
        {
            foreach (IntVec3 gravshipCell in marker.GravshipCells)
            {
                IntVec3 cell = root + gravshipCell;
                if (cell.InBounds(map) && IsSubstructure(cell, map))
                {
                    footprintCell = cell;
                    substructureCell = cell;
                    return true;
                }
            }
            footprintCell = IntVec3.Invalid;
            substructureCell = IntVec3.Invalid;
            return false;
        }

        /// <summary>该格是否有基架（foundation 层语义；天然地面不是基架，不触发）。</summary>
        private static bool IsSubstructure(IntVec3 cell, Map map)
        {
            return map.terrainGrid.FoundationAt(cell)?.IsSubstructure == true;
        }
    }
}

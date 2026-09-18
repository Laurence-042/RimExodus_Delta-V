using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版降落选点要求飞船脚印与地面至少一格重合（Designator_MoveGravship.
    /// ValidGravshipLocation 的 GravshipMustBeConnectedToLand）。Delta-V 空间图上
    /// 放宽为「重合或上下左右相邻」，让残骸/既有结构旁的空位也可停靠。
    /// 仅 Delta-V 参考系图生效（FrameOf 判定）：RimExodus/原版地表的逆重移动不受影响。
    /// 放宽只在原版重合判定失败时介入；逐格合法性（越界/阻挡/禁建区等）仍由原版裁决。
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
            // 停放锚点唯一入口（取代对 designator 私有 GetSizeRotAdjustedCell 的反射）：
            // 悬停格 → 中心锚格 → 放置 root 格。
            IntVec3 root = SpaceTransform.AdjustedCellToPlacementRoot(
                SpaceTransform.PlacementRootToAdjustedCell(loc, marker.gravship.Bounds.Size, marker.GravshipRotation),
                marker.gravship.Bounds.Size, marker.GravshipRotation);
            if (!AnyCellConnectedToLand(root, marker, map)) return;

            if (validCells == null || invalidCells == null)
            {
                // 点击门禁路径（CanDesignateCell）：走到重合检查失败时所有格已逐格合法，直接放行。
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

        /// <summary>脚印内任一格自身或其上下左右四邻存在界内非 Space 地形。</summary>
        private static bool AnyCellConnectedToLand(IntVec3 root, GravshipLandingMarker marker, Map map)
        {
            foreach (IntVec3 gravshipCell in marker.GravshipCells)
            {
                IntVec3 cell = root + gravshipCell;
                if (ConnectedToLand(cell, map))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ConnectedToLand(IntVec3 cell, Map map)
        {
            return IsLand(cell, map)
                || IsLand(cell + IntVec3.East, map)
                || IsLand(cell + IntVec3.West, map)
                || IsLand(cell + IntVec3.North, map)
                || IsLand(cell + IntVec3.South, map);
        }

        private static bool IsLand(IntVec3 cell, Map map)
        {
            return cell.InBounds(map) && cell.GetTerrain(map) != TerrainDefOf.Space;
        }
    }
}

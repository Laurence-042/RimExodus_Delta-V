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
            // loc 是原版 designator 输入格；与原版 ValidGravshipLocation 同一入口求 root。
            IntVec3 root = GravshipInterop.DesignatorInputToRoot(loc,
                marker.gravship.Bounds.Size, marker.GravshipRotation);

            if (TouchesForeignSubstructure(root, marker, map, LandingOwnerId(marker),
                out _, out _))
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
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            ShipState pending = universe?.ShipTransitions.PendingDockingShip(universe);
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

        /// <summary>
        /// 只在确认落点与完成复核两个事件调用。复用实际禁触判定，记录两次检查
        /// 使用的 root、脚印与第一处异主接触，避免另造一份近似几何诊断。
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
            int ownerId = LandingOwnerId(marker);
            bool touches = TouchesForeignSubstructure(root, marker, map, ownerId,
                out IntVec3 footprintCell, out IntVec3 foreignCell);
            Building_GravEngine engine = marker.gravship.Engine;
            return $"root={root} marker={marker.Position} rot={marker.GravshipRotation.AsInt} "
                + $"bounds={marker.gravship.Bounds.Size} footprint={count} "
                + (count == 0 ? "range=empty " : $"range=({minX},{minZ})..({maxX},{maxZ}) ")
                + $"hash={hash:X8} owner={ownerId} engine={engine?.Position} "
                + (touches ? $"foreignTouch={footprintCell}->{foreignCell}" : "foreignTouch=none");
        }

        /// <summary>脚印任一格自身或四邻触碰异主基架。</summary>
        private static bool TouchesForeignSubstructure(IntVec3 root, GravshipLandingMarker marker,
            Map map, int ownerId, out IntVec3 footprintCell, out IntVec3 foreignCell)
        {
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            foreach (IntVec3 gravshipCell in marker.GravshipCells)
            {
                IntVec3 cell = root + gravshipCell;
                IntVec3 neighbor;
                if (IsForeignSubstructure(cell, map, ownership, ownerId)) neighbor = cell;
                else if (IsForeignSubstructure(cell + IntVec3.East, map, ownership, ownerId))
                    neighbor = cell + IntVec3.East;
                else if (IsForeignSubstructure(cell + IntVec3.West, map, ownership, ownerId))
                    neighbor = cell + IntVec3.West;
                else if (IsForeignSubstructure(cell + IntVec3.North, map, ownership, ownerId))
                    neighbor = cell + IntVec3.North;
                else if (IsForeignSubstructure(cell + IntVec3.South, map, ownership, ownerId))
                    neighbor = cell + IntVec3.South;
                else continue;
                footprintCell = cell;
                foreignCell = neighbor;
                return true;
            }
            footprintCell = IntVec3.Invalid;
            foreignCell = IntVec3.Invalid;
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

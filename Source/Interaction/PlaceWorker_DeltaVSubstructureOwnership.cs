using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架建造的归属门禁（经 XML patch 挂在原版 Substructure def 上）：新基架不得同时
    /// 邻接多个不同归属的基架（2026-09-18 设计——否则该格归属无法判定）。
    /// 仅 Delta-V 参考系图生效；RimExodus/原版地表的基架建造不受影响。
    /// 实际归属写入在 Patch_TerrainGrid_SetFoundation_Ownership（建造完成时）。
    /// </summary>
    public class PlaceWorker_DeltaVSubstructureOwnership : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc,
            Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) == null)
            {
                return AcceptanceReport.WasAccepted;
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null)
            {
                return AcceptanceReport.WasAccepted;
            }
            if (ownership.DistinctNeighborOwners(loc, out _) >= 2)
            {
                return "DeltaV_SubstructureOwnerConflict".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol,
            Thing thing = null)
        {
            // 邻接归属高亮：悬停格四邻基架按归属着色（归属过滤口径的可视化提示）。
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (map == null || universe?.FrameOf(map) == null)
            {
                return;
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null)
            {
                return;
            }
            foreach (IntVec3 delta in GenAdj.CardinalDirectionsAround)
            {
                IntVec3 cell = center + delta;
                if (!cell.InBounds(map) || map.terrainGrid.TerrainAt(cell) != DeltaVDefOf.Substructure)
                {
                    continue;
                }
                GenDraw.DrawFieldEdges(new List<Verse.IntVec3> { cell },
                    SubstructureOwnership.OwnerColor(ownership.OwnerAt(cell)));
            }
        }
    }
}

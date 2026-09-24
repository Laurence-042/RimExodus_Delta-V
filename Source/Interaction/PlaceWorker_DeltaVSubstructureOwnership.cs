using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 基架建造的归属可视化（经 XML patch 挂在原版 Substructure def 上；解耦架构 §4
    /// 修订）：**多归属邻接不再拒建**——新格保持未占有，交由玩家经「修改基架归属」
    /// 划分（实际写入在 Patch_TerrainGrid_SetFoundation_Ownership 的 ResolveBuilt）。
    /// 放行前不再拦截；本 Worker 只保留邻接归属高亮提示。
    /// 仅 Delta-V 参考系图生效；RimExodus/原版地表的基架建造不受影响。
    /// </summary>
    public class PlaceWorker_DeltaVSubstructureOwnership : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc,
            Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // 多归属邻接合法（保持未占有）；无其他门禁。
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

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>降落选点的双方通道提示；只读原版已旋转的放置数据，不修改地图或端口。</summary>
    internal static class DockingChannelPreview
    {
        private static readonly Color ArrivingColor = new Color(0.2f, 0.85f, 1f);
        private static readonly Color HostColor = new Color(0.95f, 0.4f, 0.9f);
        private static readonly Color ContactColor = new Color(0.35f, 1f, 0.35f);

        internal static void Draw(Designator_MoveGravship designator)
        {
            Map map = designator.map;
            GravshipLandingMarker marker = designator.marker;
            if (map == null || map != Find.CurrentMap || marker?.gravship == null) return;

            var placements = marker.gravship.ThingPlacements;
            List<Thing> hostPorts = map.listerThings.ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort);
            bool hasArrivingPort = false;
            foreach (Thing thing in placements.Keys)
                if (thing is Building_DockingPort) { hasArrivingPort = true; break; }
            if (!hasArrivingPort && hostPorts.Count == 0) return;

            IntVec3 root = GravshipInterop.DesignatorInputToRoot(UI.MouseCell(),
                marker.gravship.Bounds.Size, marker.GravshipRotation);
            var landingStructure = new HashSet<IntVec3>();
            foreach (IntVec3 cell in marker.GravshipCells)
                landingStructure.Add(root + cell);
            // shuttle 没有实际基架；战略降落也可有伸出基架脚印的建筑。
            foreach (var entry in placements)
                if (entry.Key is Building)
                    foreach (IntVec3 cell in GenAdj.OccupiedRect(root + entry.Value.local,
                        entry.Value.rotation, entry.Key.def.Size))
                        landingStructure.Add(cell);

            var cells = new List<IntVec3>();
            var endpoint = new List<IntVec3>(1);
            foreach (var entry in placements)
                if (entry.Key is Building_DockingPort port)
                    DrawPort(map, root + entry.Value.local, entry.Value.rotation, port.def,
                        ArrivingColor, landingStructure, cells, endpoint);

            foreach (Thing thing in hostPorts)
                if (thing is Building_DockingPort port && !placements.ContainsKey(port))
                    DrawPort(map, port.Position, port.Rotation, port.def,
                        HostColor, landingStructure, cells, endpoint, port);
        }

        private static void DrawPort(Map map, IntVec3 position, Rot4 rotation, ThingDef def,
            Color color, ISet<IntVec3> landingStructure, List<IntVec3> cells,
            List<IntVec3> endpoint, Building_DockingPort existing = null)
        {
            int range = def.GetModExtension<DefModExtension_DockingPort>()?.channelRange ?? 8;
            bool connects = Building_DockingPort.TryFindChannel(map, position, rotation, range,
                cells, out IntVec3 face, existing, landingStructure);
            // 无接触面时仍画完整搜索带，便于把另一船移入范围；越界和已有通道仍截断。
            GenDraw.DrawFieldEdges(cells, color);
            endpoint.Clear();
            if (position.InBounds(map)) endpoint.Add(position);
            GenDraw.DrawFieldEdges(endpoint, color);
            if (!connects) return;
            endpoint.Clear();
            endpoint.Add(face);
            GenDraw.DrawFieldEdges(endpoint, ContactColor);
        }
    }
}

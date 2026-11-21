using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class Patch_ShipHullRendering
    {
        private static readonly AccessTools.FieldRef<MapDrawLayer, Map> LayerMap =
            AccessTools.FieldRefAccess<MapDrawLayer, Map>("map");
        private static int strategicRegeneration;
        internal static bool MarkingFocusedSections { get; private set; }

        private static bool OwnsDrawing(Map map)
        {
            if (map == null || map.Disposed || Current.Game?.GetComponent<UniverseManager>() is not { } universe)
                return false;
            return universe.SpaceObjectOnMap(map) != null || SpaceMapGovernance.IsGoverned(map, universe);
        }

        private static bool DrawNormally(Map map) => strategicRegeneration == 0
            && !WorldComponent_GravshipController.GravshipRenderInProgess && OwnsDrawing(map);

        internal static void Watch(ShipStructureIndex index, Map map)
        {
            index.WatchMap(map, ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                | ShipStructureAspect.Membership | ShipStructureAspect.Footprint | ShipStructureAspect.HullBoundary,
                batch => Invalidate(map, batch));
        }

        private static void Invalidate(Map map, ShipStructureBatch batch)
        {
            if (map.mapDrawer == null || !OwnsDrawing(map)) return;
            var sections = new HashSet<IntVec3>();
            foreach (ShipStructureChange change in batch.Changes)
            {
                if (change.Before is CellRect previous) Collect(previous);
                if (change.After is CellRect current) Collect(current);
                if (change.Cell is IntVec3 cell) Collect(CellRect.SingleCell(cell));
            }
            ulong flags = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Things;
            foreach (IntVec3 section in sections)
            {
                MapOverlayRenderer.MarkHullSectionDirty(map, section);
                const int size = 17;
                var marker = new IntVec3(System.Math.Min(section.x * size + size / 2, map.Size.x - 1), 0,
                    System.Math.Min(section.z * size + size / 2, map.Size.z - 1));
                MarkingFocusedSections = true;
                try { map.mapDrawer.MapMeshDirty(marker, flags, regenAdjacentCells: false, regenAdjacentSections: false); }
                finally { MarkingFocusedSections = false; }
            }

            void Collect(CellRect rect)
            {
                foreach (IntVec3 section in ShellHullVisuals.BoundarySections(map, rect, 2)) sections.Add(section);
            }
        }

        private static bool PrintLayer(SectionLayer layer, bool angles)
        {
            Map map = LayerMap(layer);
            if (!ModsConfig.OdysseyActive || !DrawNormally(map)) return true;
            foreach (LayerSubMesh mesh in layer.subMeshes) mesh.Clear(MeshParts.All);
            CellRect rect = layer.GetBoundaryRect();
            if (angles) ShellHullVisuals.PrintAngles(map, rect, layer);
            else ShellHullVisuals.PrintFoundationEdges(map, rect, layer);
            foreach (LayerSubMesh mesh in layer.subMeshes)
                if (mesh.verts.Count != 0) mesh.FinalizeMesh(MeshParts.All);
            return false;
        }

        [HarmonyPatch(typeof(SectionLayer_GravshipHull), nameof(SectionLayer_GravshipHull.Regenerate))]
        internal static class Angles
        {
            private static bool Prefix(SectionLayer_GravshipHull __instance) => PrintLayer(__instance, true);
        }

        [HarmonyPatch(typeof(SectionLayer_SubstructureProps), nameof(SectionLayer_SubstructureProps.Regenerate))]
        internal static class FoundationEdges
        {
            private static bool Prefix(SectionLayer_SubstructureProps __instance) => PrintLayer(__instance, false);
        }

        [HarmonyPatch(typeof(Graphic_LinkedCornerFiller), nameof(Graphic_LinkedCornerFiller.Print))]
        internal static class Wall
        {
            private static bool Prefix(SectionLayer layer, Thing thing)
            {
                if (thing is not Building building || building.def != ThingDefOf.GravshipHull
                    || !DrawNormally(building.Map)) return true;
                Map map = building.Map;
                var angles = ShellHullVisuals.FindAngledCorners(map, CellRect.CenteredOn(building.Position, 1).ClipInsideMap(map));
                var adjacency = ShellHullVisuals.Analyze(map, building.Position, building,
                    ShellHullVisuals.AngledSidesAt(building.Position, angles));
                var material = ShellHullVisuals.WallMaterial(building, adjacency);
                if (material == null) return true;
                ShellHullVisuals.PrintWall(layer, building, adjacency, material);
                return false;
            }
        }


        [HarmonyPatch(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginGravshipRender))]
        internal static class CaptureWalls
        {
            private static void Prefix(Building_GravEngine engine)
            {
                Map map = engine?.Map;
                if (!OwnsDrawing(map)) return;
                strategicRegeneration++;
                try { map.mapDrawer.RegenerateLayerNow(typeof(SectionLayer_ThingsGeneral)); }
                finally { strategicRegeneration--; }
            }
        }

        [HarmonyPatch(typeof(WorldComponent_GravshipController), "ResetCutscene")]
        internal static class RestoreOwnedLayers
        {
            private static void Prefix(out Map __state) => __state = WorldComponent_GravshipController.CutsceneInProgress
                ? SectionLayer_GravshipMask.Engine?.Map ?? Find.CurrentMap : null;

            private static void Postfix(Map __state)
            {
                if (!OwnsDrawing(__state)) return;
                MapDrawer drawer = __state.mapDrawer;
                drawer.RegenerateLayerNow(typeof(SectionLayer_ThingsGeneral));
                drawer.RegenerateLayerNow(typeof(SectionLayer_GravshipHull));
                drawer.RegenerateLayerNow(typeof(SectionLayer_SubstructureProps));
            }
        }

        [HarmonyPatch(typeof(WorldComponent_GravshipController), "RegenerateGravshipMask")]
        internal static class StrategicCapture
        {
            private static void Prefix() => strategicRegeneration++;
            private static void Finalizer() => strategicRegeneration--;
        }
    }
}

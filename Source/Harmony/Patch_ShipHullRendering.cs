using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using CornerType = RimWorld.SectionLayer_GravshipHull.CornerType;

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

        private static bool DrawNormally(Map map) => strategicRegeneration == 0 && OwnsDrawing(map)
            && !WorldComponent_GravshipController.GravshipRenderInProgess;

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

        private static bool WallsShareBoundary(Map map, IntVec3 first, IntVec3 second) =>
            ShellHullVisuals.SharesDomain(map, first, second)
            || (!SubstructureOwnership.HasFoundation(map, first)
                && !SubstructureOwnership.HasFoundation(map, second));

        [HarmonyPatch(typeof(Graphic_Linked), nameof(Graphic_Linked.ShouldLinkWith))]
        internal static class WallLinks
        {
            private static void Postfix(IntVec3 c, Thing parent, ref bool __result)
            {
                if (!__result || (parent.def != ThingDefOf.GravshipHull && !parent.def.IsWall)) return;
                Map map = parent.Map;
                if (c.InBounds(map) && DrawNormally(map))
                    __result = WallsShareBoundary(map, parent.Position, c);
            }
        }

        [HarmonyPatch(typeof(SectionLayer_GravshipHull), nameof(SectionLayer_GravshipHull.ShouldDrawCornerPiece))]
        internal static class Angles
        {
            private static void Postfix(IntVec3 pos, Map map, ref CornerType cornerType, ref bool __result)
            {
                if (!__result || !DrawNormally(map)) return;
                var (across, along, partial) = cornerType switch
                {
                    CornerType.Corner_NW or CornerType.Diagonal_NW => (IntVec3.West, IntVec3.North, CornerType.Diagonal_NW),
                    CornerType.Corner_NE or CornerType.Diagonal_NE => (IntVec3.East, IntVec3.North, CornerType.Diagonal_NE),
                    CornerType.Corner_SW or CornerType.Diagonal_SW => (IntVec3.West, IntVec3.South, CornerType.Diagonal_SW),
                    CornerType.Corner_SE or CornerType.Diagonal_SE => (IntVec3.East, IntVec3.South, CornerType.Diagonal_SE),
                    _ => (IntVec3.Zero, IntVec3.Zero, CornerType.None)
                };
                if (partial == CornerType.None) return;

                IntVec3 first = pos + across;
                if (!WallsShareBoundary(map, first, pos + along))
                {
                    cornerType = CornerType.None;
                    __result = false;
                }
                else if (cornerType != partial && !WallsShareBoundary(map, first, first + along))
                    cornerType = partial;
            }
        }

        [HarmonyPatch(typeof(SectionLayer_SubstructureProps), "ShouldDrawPropsOn")]
        internal static class FoundationEdges
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var foundationAt = AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.FoundationAt),
                    new[] { typeof(IntVec3) });
                var lookups = code.FindAll(instruction => instruction.Calls(foundationAt));
                int anchor = lookups.Count == 3 ? code.IndexOf(lookups[0]) : -1;
                if (anchor <= 0 || code[anchor - 1].opcode != OpCodes.Ldarg_1)
                {
                    Log.Error("[DeltaV] Could not identify the native substructure border's neighboring foundation lookups.");
                    return code;
                }

                var readNeighbor = AccessTools.Method(typeof(FoundationEdges), nameof(ReadNeighbor));
                // Keep the anchor lookup. Only the cardinal and diagonal neighbors depend on its domain.
                foreach (var call in lookups.Skip(1))
                {
                    code.InsertRange(code.IndexOf(call), new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call).MoveBlocksFrom(call),
                        new CodeInstruction(OpCodes.Ldarg_1)
                    });
                    call.opcode = OpCodes.Call;
                    call.operand = readNeighbor;
                }
                return code;
            }

            private static TerrainDef ReadNeighbor(TerrainGrid grid, IntVec3 neighbor,
                SectionLayer_SubstructureProps layer, IntVec3 anchor)
            {
                TerrainDef terrain = grid.FoundationAt(neighbor);
                if (terrain?.IsSubstructure != true) return terrain;
                Map map = LayerMap(layer);
                return !DrawNormally(map) || ShellHullVisuals.SharesDomain(map, anchor, neighbor) ? terrain : null;
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

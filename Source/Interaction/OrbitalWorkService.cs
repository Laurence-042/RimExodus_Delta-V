using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>User-command orchestration. Discovery and content scans run only on Start/selection.</summary>
    public sealed class OrbitalWorkService
    {
        private readonly UniverseManager universe;
        // Retain captured objects if even rollback fails; transfer guards prevent saving a partial graph.
        private List<ContentMove> failedTransfers;
        public OrbitalWorkService(UniverseManager universe) { this.universe = universe; }
        private static void Reject(string key) => Messages.Message(key.Translate(), MessageTypeDefOf.RejectInput, false);

        private bool CanStart(PilotTarget target) => failedTransfers == null && target.IsValid && target.Vessel.State == ShipDerivedState.FreeFlight
            && universe.FrameOf(target.Map) is SpaceObject own && !own.projectileRoleOverride && !own.IsWorksite
            && !universe.ShipTransitions.FreeFlightTransitionPending() && Current.Game.Gravship == null
            && Find.GravshipController?.IsGravshipTravelling != true;

        private List<SpaceObject> Nearby(PilotTarget target)
        {
            SpaceObject own = universe.FrameOf(target.Map);
            return universe.objects.Where(o => o != own && o.map != null && !o.map.Disposed && !o.IsScattered
                && !o.HasTag(SpaceObjectTag.Projectile)
                && OrbitSphere.ArcCells(own.pose.position, o.pose.position) <= DeltaVMod.CurrentSettings.dockingCancelDistance)
                .OrderBy(o => OrbitSphere.ArcCells(own.pose.position, o.pose.position)).ToList();
        }

        public void SelectSyncTarget(PilotTarget target)
        {
            if (!CanStart(target)) return;
            Choose(Nearby(target), host =>
            {
                if (CanStart(target) && Nearby(target).Contains(host)) universe.Approach.BeginSync(universe, target, host);
            }, false);
        }

        private static void Choose(List<SpaceObject> candidates, Action<SpaceObject> action, bool allowEmpty)
        {
            if (candidates.Count == 0) { if (allowEmpty) action(null); else Reject("DeltaV_DockFailNoTarget"); return; }
            if (candidates.Count == 1) { action(candidates[0]); return; }
            Find.WindowStack.Add(new FloatMenu(candidates.Select(o => new FloatMenuOption(o.label, () => action(o))).ToList()));
        }

        private bool MatchesMotion(PilotTarget target, SpaceObject host)
            => LandingApproachController.MatchesMotion(universe.FrameOf(target.Map).pose, host.pose, target.Vessel);

        public void Start(PilotTarget target)
        {
            if (!CanStart(target)) return;
            var active = universe.Approach.ActivePlan;
            if (active?.Target.Identity == target.Identity)
            {
                if (!LandingApproachController.IsValid(universe, active))
                {
                    universe.Approach.CancelSelectionOrPlan(universe, silent: true);
                    Reject("DeltaV_EVANotSynchronized");
                    return;
                }
                if (!universe.Approach.Ready)
                { Reject("DeltaV_EVANotSynchronized"); return; }
                Execute(target, active.host);
                return;
            }
            Choose(Nearby(target).Where(o => MatchesMotion(target, o)).ToList(), host => Execute(target, host), true);
        }

        private void Execute(PilotTarget target, SpaceObject host)
        {
            if (!CanStart(target)) return;
            SpaceObject own = universe.FrameOf(target.Map);
            if (host != null && (!Nearby(target).Contains(host) || !MatchesMotion(target, host)))
            { Reject("DeltaV_EVANotSynchronized"); return; }
            // One command-time nearest-grid placement; no navigation or ghost phase.
            var placement = host == null ? null : LandingApproachController.NearestPlacement(universe, target, host);
            if (host != null && placement == null) { Reject("DeltaV_EVANotSynchronized"); return; }
            bool docking = host != null && LandingApproachController.LandingSiteStillValid(placement);
            // Every gravity rectangle participates, including sources other than the selected target.
            foreach (SpaceObject gravity in universe.objects.Where(o => o != own && o.HasGravityRegion))
            {
                if (!IntersectsGravity(target, gravity)) continue;
                if (!docking || gravity != host) { Reject("DeltaV_EVAGravity"); return; }
            }
            if (docking) Dock(target, placement);
            else if (host != null && !host.CanRelocateMap) Reject("DeltaV_EVAGravity");
            else CreateWorksite(target, host, placement);
        }

        private bool IntersectsGravity(PilotTarget target, SpaceObject gravity)
            => IntersectsGravity(universe.FrameOf(target.Map), target.Vessel.HullCells, gravity);

        internal static bool IntersectsGravity(SpaceObject own, IEnumerable<IntVec3> footprint, SpaceObject gravity)
            => footprint.Any(cell => IntersectsGravity(own, new CellRect(cell.x, cell.z, 1, 1), gravity));

        // The same rectangle test serves live worksite gates and authored course clearance.
        internal static bool IntersectsGravity(SpaceObject own, CellRect footprint, SpaceObject gravity)
        {
            var rectangle = new List<Vector2> { Vector2.zero, new Vector2(gravity.map.Size.x, 0),
                new Vector2(gravity.map.Size.x, gravity.map.Size.z), new Vector2(0, gravity.map.Size.z) };
            var corners = new List<Vector2>(4);
            foreach (Vector2 corner in new[] { new Vector2(footprint.minX, footprint.minZ), new Vector2(footprint.maxX + 1, footprint.minZ),
                new Vector2(footprint.maxX + 1, footprint.maxZ + 1), new Vector2(footprint.minX, footprint.maxZ + 1) })
                corners.Add(SpaceTransform.UniverseToMapPoint(gravity, gravity.map,
                    SpaceTransform.MapPointToUniverse(own, own.map, corner)));
            return PlanarGeometry.HullDistance(PlanarGeometry.ConvexHull(corners), rectangle) <= 0.001f;
        }

        private void StopControls(IEnumerable<ShipBase> ships)
        {
            universe.Approach.CancelSelectionOrPlan(universe, silent: true);
            foreach (ShipBase ship in ships)
            {
                universe.Controls.PlayerFor(ship)?.ReplaceAutoTask(PlayerAutoTaskKind.Drift);
                universe.Autopilot.Detach(ship.Identity, "EVA started");
            }
            universe.Pilot.Exit(universe);
        }

        // The caller validates the frozen placement with LandingSiteStillValid before entering this transfer.
        internal bool Dock(PilotTarget target, LandingApproachController.DockingPlan placement)
        {
            Action dockCommitted = universe.Missions.BeginDock(target.Identity, placement.targetMap);
            StopControls(new[] { target.Vessel });
            var handoff = universe.ShipTransitions.CaptureDockingFocusHandoff(universe, target.Map, placement.targetMap);
            Gravship captured = universe.ShipTransitions.CaptureDockingTargetForCompletion(target, out var cells, out bool terrain);
            if (captured == null) return false;
            captured.Rotation = placement.landingRotation;
            if (!universe.ShipTransitions.CompleteTransferIntoHost(universe, target, captured, cells, terrain,
                placement.targetMap, placement.landingCell, placement.landingRotation, placement.structureId, out Map source)) return false;
            var completion = new DockingCompletion(universe, target.Vessel, placement.targetMap, source, handoff);
            completion.OnTransferedIntoHost();
            universe.QueueFrameAction(() =>
            {
                completion.Finish();
                dockCommitted?.Invoke();
            });
            return true;
        }

        private sealed class ContentMove
        {
            internal SpaceObject source;
            internal HashSet<IntVec3> cells;
            internal IntVec3 origin, root;
            internal Rot4 rotation;
            internal Dictionary<IntVec3, int> owners;
            internal Dictionary<IntVec3, int> sourceOwners;
            internal Gravship captured;
            internal List<ShipBase> ships;
        }

        // One explicit content snapshot per operation, including dock placeholder buildings.
        private static HashSet<IntVec3> ContentCells(Map map)
        {
            var cells = new HashSet<IntVec3>();
            foreach (IntVec3 cell in CellRect.WholeMap(map))
                if (map.terrainGrid.FoundationAt(cell) != null || map.roofGrid.RoofAt(cell) != null
                    || map.terrainGrid.TerrainAt(cell) != TerrainDefOf.Space) cells.Add(cell);
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Mote || thing is Gas || thing is Filth) continue;
                if (thing is Skyfaller || !thing.def.bringAlongOnGravship)
                    throw new InvalidOperationException($"Cannot transfer {thing.def.defName} with the Gravship chain.");
                foreach (IntVec3 cell in thing.OccupiedRect())
                    if (cell.InBounds(map)) cells.Add(cell);
            }
            return cells;
        }

        private void CreateWorksite(PilotTarget target, SpaceObject host, LandingApproachController.DockingPlan placement)
        {
            Action dockCommitted = universe.Missions.BeginDock(target.Identity, host?.map);
            SpaceObject own = universe.FrameOf(target.Map);
            SpaceObject basis = own;
            var moves = new List<ContentMove>();
            var occupied = new HashSet<IntVec3>();
            try
            {
                foreach (SpaceObject source in host == null ? new[] { own } : new[] { host, own })
                {
                    var cells = ContentCells(source.map);
                    if (cells.Count == 0) { Reject("DeltaV_DockFailNoTarget"); return; }
                    if (universe.objects.Any(g => g != source && g.HasGravityRegion && IntersectsGravity(source, cells, g)))
                    { Reject("DeltaV_EVAGravity"); return; }
                    CellRect bounds = CellRect.FromCellList(cells.ToList());
                    var move = new ContentMove { source = source, cells = cells,
                        origin = new IntVec3(bounds.minX, 0, bounds.minZ),
                        rotation = source == basis ? Rot4.North : placement.InverseRotation,
                        ships = source.Ships.ToList(), owners = new Dictionary<IntVec3, int>(),
                        sourceOwners = cells.ToDictionary(c => c, c => SubstructureOwnership.TryGet(source.map)?.OwnerAt(c) ?? 0) };
                    move.root = source == basis ? move.origin : placement.InversePlacementRootFor(move.origin);
                    foreach (IntVec3 cell in cells)
                    {
                        IntVec3 placed = move.root + PrefabUtility.GetAdjustedLocalPosition(cell - move.origin, move.rotation);
                        if (!occupied.Add(placed)) { Reject("DeltaV_EVAOverlap"); return; }
                        move.owners[placed] = move.sourceOwners[cell];
                    }
                    moves.Add(move);
                }
            }
            catch (InvalidOperationException ex) { Log.Warning($"[DeltaV] EVA preflight: {ex.Message}"); Reject("DeltaV_EVAUnsupportedContent"); return; }
            CellRect union = host == null ? CellRect.WholeMap(own.map) : CellRect.FromCellList(occupied.ToList()).ExpandedBy(ShipPocketMapFactory.StructureMargin);
            int margin = Mathf.Max(1, DeltaVMod.CurrentSettings.worksiteMargin);
            IntVec3 shift = new IntVec3(margin - union.minX, 0, margin - union.minZ);
            Map workmap = ShipPocketMapFactory.Generate(new IntVec3(union.Width + margin * 2, 1, union.Height + margin * 2), universe);
            if (workmap == null) return;
            workmap.GetComponent<DeltaVPocketMapMetadata>().worksite = true;
            Vector2 newOriginInBasis = SpaceTransform.ShipLocalOrigin(workmap) - new Vector2(shift.x, shift.z);
            SpacePose pose = SpaceTransform.RigidPointPose(basis.pose, SpaceTransform.MapToShipLocal(basis.map, newOriginInBasis), 0f);
            var work = new MapSpaceObject(universe.AllocateOwnerId(), workmap, pose) { faction = own.faction };
            universe.RegisterObject(work);
            var handoff = universe.ShipTransitions.CaptureDockingFocusHandoff(universe, Find.CurrentMap, workmap);
            StopControls(moves.SelectMany(m => m.ships));
            universe.ShipTransitions.RegisterDockingCleanup(workmap);
            foreach (ContentMove move in moves) universe.ShipTransitions.RegisterDockingCleanup(move.source.map);
            try
            {
                foreach (ContentMove move in moves)
                {
                    move.captured = GravshipInterop.CaptureDomain(move.source.map, move.cells, null, null);
                    if (move.captured == null) throw new InvalidOperationException("Whole-map capture returned null.");
                    move.captured.Rotation = move.rotation;
                    GravshipInterop.StripDomain(move.captured, move.source.map, move.cells);
                    GravshipInterop.PlaceIntoMap(move.captured, move.root + shift, workmap, 0,
                        move.owners.ToDictionary(p => p.Key + shift, p => p.Value));
                    foreach (ShipBase ship in move.ships) ShipRegistry.ShipByIdentity(ship.Identity).NotifyLanded();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] EVA transfer interrupted; restoring captured domains: {ex}");
                failedTransfers = moves;
                try
                {
                    foreach (ContentMove move in moves.AsEnumerable().Reverse())
                    {
                        if (move.captured == null) continue;
                        var placedCells = new HashSet<IntVec3>(move.owners.Keys.Select(c => c + shift));
                        GravshipInterop.StripDomain(move.captured, workmap, placedCells);
                        move.captured.Rotation = Rot4.North;
                        GravshipInterop.PlaceIntoMap(move.captured, move.origin, move.source.map, 0, move.sourceOwners);
                    }
                    failedTransfers = null;
                    foreach (ContentMove move in moves) universe.ShipTransitions.FinishDockingCleanup(move.source.map);
                    universe.ShipTransitions.FinishDockingCleanup(workmap);
                    universe.ShipTransitions.HandleDepartedSourceMap(universe, workmap);
                }
                catch (Exception restoreError)
                {
                    Log.Error($"[DeltaV] EVA rollback failed; captured objects retained and saving blocked. Reload the last save: {restoreError}");
                }
                Reject("DeltaV_EVATransferFailed");
                return;
            }
            universe.ShipTransitions.FinishDockingCleanup(workmap);
            foreach (ContentMove move in moves) universe.ShipTransitions.FinishDockingCleanup(move.source.map);
            universe.ShipTransitions.ApplyDockingFocusHandoff(universe, handoff);
            foreach (ContentMove move in moves)
                universe.ShipTransitions.HandleDepartedSourceMap(universe, move.source.map);
            Building_DockingPort.NotifyLandingSettled(workmap);
            dockCommitted?.Invoke();
            ShipPhysics.ClearCache();
            HostColonistBarVisibility.NotifyDockingStateChanged();
            Messages.Message("DeltaV_EVAStarted".Translate(), MessageTypeDefOf.PositiveEvent, false);
        }
    }
}

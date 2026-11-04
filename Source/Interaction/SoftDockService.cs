using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>Indexes ports and connects their local docking state to universe services.</summary>
    public sealed partial class SoftDockService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<Building_DockingPort> ports = new HashSet<Building_DockingPort>();
        private DriveSubject controlled;
        private int nextSearch;
        private CompSoftDocking steering;
        private CompSoftDocking suppressedSteering;
        private readonly HashSet<CompSoftDocking> restoredSuppressed = new HashSet<CompSoftDocking>();
        private bool suppressRestoredSteering;
        private DriveSubject steeringShip;
        private System.Func<SpacePose, SpacePose> steeringReference;
        public bool Enabled => universe.SoftDockEnabled;

        public SoftDockService(UniverseManager universe) { this.universe = universe; }
        internal void Register(Building_DockingPort port) { if (Enabled) ports.Add(port); }

        internal void Remove(Building_DockingPort port)
        {
            CancelTransfersAt(port);
            if (steering?.Port == port || steering?.Peer == port) StopSteering();
            port.Docking?.Clear();
            ports.Remove(port);
        }

        internal void Forget(Map map)
        { foreach (Building_DockingPort port in ports.Where(p => p.Map == map).ToList()) Remove(port); }

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            CancelAllTransfers();
            StopSteering();
            suppressedSteering = null;
            restoredSuppressed.Clear();
            if (!enabled)
                foreach (Building_DockingPort port in ports) port.Docking?.Clear();
            universe.SoftDockEnabled = enabled;
            ports.Clear(); controlled = default; nextSearch = 0;
            if (!enabled) return;
            RestoreEnabled(suppressRestored: false);
        }

        internal void RestoreEnabled(bool suppressRestored = true)
        {
            if (!Enabled) return;
            suppressRestoredSteering = suppressRestored;
            restoredSuppressed.Clear();
            ports.Clear(); controlled = default; nextSearch = 0;
            // Explicit enable and load restore build the index once; no periodic map scan.
            foreach (Map map in Find.Maps)
                foreach (Building_DockingPort port in map.listerThings
                    .ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort).OfType<Building_DockingPort>())
                    ports.Add(port);
            SelectControl();
        }

        private void SelectControl()
        {
            ShipBase ship = universe.Pilot.SelectedShipOnMap(Find.CurrentMap);
            if (ship?.IsValid == true && !ship.Identity.shuttle && ship.PlayerMayOperate)
                controlled = ship.Identity;
        }

        internal bool Live(Building_DockingPort port) => port?.Spawned == true && !port.Destroyed
            && universe.FrameOf(port.Map) != null && !universe.Lifecycle.IsFrozen(port.Map)
            && !universe.ShipTransitions.IsAwaitingDockingCleanup(port.Map);

        internal void ApplyAlignmentAssist()
        {
            if (!Enabled) return;
            // Pair alignment remains active after manual takeover, task replacement or load.
            // Steering is only the optional thruster task, not the docking state owner.
            foreach (Building_DockingPort port in ports)
                if (port.Docking?.IsActiveOwner == true)
                    port.Docking.ApplyAlignmentAssist(universe, this, controlled);
        }

        private bool SteeringValid(CompSoftDocking dock)
            => dock != suppressedSteering && !restoredSuppressed.Contains(dock)
                && dock?.CanSteer(universe, this, controlled) == true;

        private void StartSteering(CompSoftDocking dock)
        {
            StopSteering();
            ShipBase ship = ShipRegistry.ShipOf(dock.Port);
            if (ship?.IsValid != true) return;
            steering = dock; steeringShip = ship.Identity;
            if (universe.Approach.ActivePlan?.Target.Identity == ship.Identity)
                universe.Approach.CancelSelectionOrPlan(universe, silent: true);
            universe.Controls.PlayerFor(ship)?.ReplaceAutoTask(PlayerAutoTaskKind.SoftDock,
                activate: false);
            // Manual flight retains control; the post-integration assist remains available.
            if (universe.Pilot.PilotedTarget(universe).Identity == ship.Identity) return;
            ResumeSteeringFor(ship.Identity);
        }

        internal bool ResumeSteeringFor(DriveSubject identity)
        {
            if (steeringShip != identity || !SteeringValid(steering)) return false;
            CompSoftDocking dock = steering;
            steeringReference = _ => dock.AlignmentReference(universe);
            universe.Autopilot.FollowReference(steeringShip, steeringReference, false,
                () => SteeringValid(dock), () => StopSteering());
            return universe.Autopilot.IsFollowing(steeringShip, steeringReference);
        }

        internal void PauseSteeringFor(DriveSubject identity)
        {
            if (steeringShip != identity || steeringReference == null) return;
            universe.Autopilot.DetachReference(identity, steeringReference);
            steeringReference = null;
        }

        internal void StopSteeringFor(DriveSubject identity)
        {
            if (steeringShip != identity) return;
            suppressedSteering = steering;
            StopSteering();
        }

        private void StopSteering()
        {
            DriveSubject oldShip = steeringShip;
            if (steeringReference != null)
                universe.Autopilot.DetachReference(oldShip, steeringReference);
            steering = null; steeringReference = null; steeringShip = default;
            if (oldShip.key > 0)
                universe.Controls.PlayerFor(ShipRegistry.ShipByIdentity(oldShip))
                    ?.ClearAutoTask(PlayerAutoTaskKind.SoftDock);
        }

        internal void Tick()
        {
            if (!Enabled) return;
            if (suppressedSteering?.IsActiveOwner != true) suppressedSteering = null;
            restoredSuppressed.RemoveWhere(dock => dock?.IsActiveOwner != true);
            PilotTarget piloted = universe.Pilot.PilotedTarget(universe);
            if (piloted.IsValid && !piloted.Identity.shuttle && piloted.Identity != controlled)
            {
                CancelAllTransfers(); StopSteering();
                foreach (Building_DockingPort port in ports) port.Docking?.Clear();
                controlled = piloted.Identity;
            }
            foreach (Building_DockingPort port in ports)
            {
                CompSoftDocking dock = port.Docking;
                if (dock?.IsActiveOwner != true) continue;
                dock.TickConnection(universe, this, controlled);
                if (!dock.Connected) CancelTransfersAt(port);
                if (steering == dock && !SteeringValid(dock)) StopSteering();
                if (steering == null && SteeringValid(dock)) StartSteering(dock);
            }
            int now = Find.TickManager.TicksGame;
            if (now < nextSearch) return;
            nextSearch = now + 15;
            if (ShipRegistry.ShipByIdentity(controlled)?.IsValid != true) SelectControl();
            Building_DockingPort[] ordered = ports.OrderBy(p => p.thingIDNumber).ToArray();
            // Existing saved pairs claim their original peer before new candidate searches.
            foreach (Building_DockingPort port in ordered)
                if (port.Docking?.TryRestore(universe, this, controlled) == true && steering == null)
                {
                    if (suppressRestoredSteering)
                    {
                        restoredSuppressed.Add(port.Docking);
                    }
                    else StartSteering(port.Docking);
                }
            foreach (Building_DockingPort port in ordered)
                if (port.Docking?.TryFindCapture(universe, this, controlled, ordered) == true && steering == null)
                {
                    suppressRestoredSteering = false;
                    StartSteering(port.Docking);
                }
        }

        internal void Draw()
        {
            if (!Enabled || Find.CurrentMap == null) return;
            SpaceObject focus = universe.FrameOf(Find.CurrentMap);
            if (focus == null) return;
            foreach (Building_DockingPort port in ports)
                if (Live(port)) port.Docking?.DrawGuide(universe, focus, Find.CurrentMap);
            foreach (Building_DockingPort port in ports)
                if (port.Docking?.IsActiveOwner == true)
                    port.Docking.DrawPassage(universe, focus, Find.CurrentMap);
        }
    }
}

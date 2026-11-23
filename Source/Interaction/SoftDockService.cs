using System;
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
        private readonly Dictionary<DriveSubject, (CompSoftDocking source, Building_DockingPort target,
            Func<SpacePose, SpacePose> reference)> orders = new();
        private int nextSearch;
        public bool Enabled => universe.SoftDockEnabled;

        public SoftDockService(UniverseManager universe) { this.universe = universe; }
        internal void Register(Building_DockingPort port) { if (Enabled) ports.Add(port); }

        internal void Remove(Building_DockingPort port)
        {
            CancelTransfersAt(port);
            foreach (var order in orders.Where(entry => entry.Value.source.Port == port
                || entry.Value.target == port).ToArray())
                StopSteeringFor(order.Key);
            port.Docking?.Clear();
            ports.Remove(port);
        }

        internal void Forget(Map map)
        { foreach (Building_DockingPort port in ports.Where(p => p.Map == map).ToList()) Remove(port); }

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            CancelAllTransfers();
            foreach (DriveSubject subject in orders.Keys.ToArray()) StopSteeringFor(subject);
            if (!enabled)
                foreach (Building_DockingPort port in ports) port.Docking?.Clear();
            universe.SoftDockEnabled = enabled;
            ports.Clear();
            nextSearch = 0;
            if (enabled) RestoreEnabled();
        }

        internal void RestoreEnabled()
        {
            if (!Enabled) return;
            ports.Clear();
            nextSearch = 0;
            // Explicit enable and load restore build the index once; no periodic map scan.
            foreach (Map map in Find.Maps)
                foreach (Building_DockingPort port in map.listerThings
                    .ThingsOfDef(DeltaVDefOf.DeltaV_DockingPort).OfType<Building_DockingPort>())
                    ports.Add(port);
        }

        internal bool Live(Building_DockingPort port) => port?.Spawned == true && !port.Destroyed
            && universe.FrameOf(port.Map) != null && !universe.MapStates.IsFrozen(port.Map)
            && !universe.ShipTransitions.IsAwaitingDockingCleanup(port.Map);

        internal bool Reserved(Building_DockingPort port, DriveSubject except = default)
            => orders.Any(entry => entry.Key != except
                && (entry.Value.source.Port == port || entry.Value.target == port));

        internal bool UsesMap(Map map) => Enabled && (ports.Any(port => port.Map == map && port.Docking?.Owner != null)
            || orders.Values.Any(order => order.source.Port.Map == map || order.target.Map == map));

        private static bool CanOrder(ShipBase ship)
            => ship?.IsValid == true && !ship.Identity.shuttle && ship.PlayerMayOperate
                && ship.State == ShipDerivedState.FreeFlight;

        private bool Available(Building_DockingPort port, DriveSubject subject)
            => Live(port) && port.Docking?.IsFree == true && !Reserved(port, subject);

        internal void SelectTarget(PilotTarget pilot)
        {
            ShipBase ship = pilot.Vessel;
            if (!CanOrder(ship)) return;
            if (!Enabled) { Reject("DeltaV_AutoSoftDockEnable"); return; }
            if (!ports.Any(port => port.Map == ship.Map && Available(port, ship.Identity)
                && ShipRegistry.ShipOf(port)?.Identity == ship.Identity))
            { Reject("DeltaV_AutoSoftDockNoOwnPort"); return; }

            SpaceObject own = universe.FrameOf(ship.Map);
            var options = ports.Where(port => port.Map != ship.Map && Available(port, ship.Identity))
                .Select(port => universe.FrameOf(port.Map)).Distinct()
                .OrderBy(host => OrbitSphere.ArcCells(own.pose.position, host.pose.position))
                .ThenBy(host => host.objectId)
                .Select(host => new FloatMenuOption(host.label, () => ConfirmTarget(ship, host)))
                .ToList();
            if (options.Count == 0) { Reject("DeltaV_AutoSoftDockNoTarget"); return; }
            Find.WindowStack.Add(new FloatMenu(options, "DeltaV_AutoSoftDockChoose".Translate()));
        }

        private void ConfirmTarget(ShipBase ship, SpaceObject host)
        {
            if (!Enabled) { Reject("DeltaV_AutoSoftDockEnable"); return; }
            if (!CanOrder(ship)) return;
            DriveSubject subject = ship.Identity;
            var pair = (from source in ports
                        where source.Map == ship.Map && Available(source, subject)
                            && ShipRegistry.ShipOf(source)?.Identity == subject
                        from target in ports
                        where target.Map != ship.Map && Available(target, subject)
                            && universe.FrameOf(target.Map) == host
                        orderby OrbitSphere.ArcCells(CompSoftDocking.Point(universe, source, 0f),
                            CompSoftDocking.Point(universe, target, 0f)), target.thingIDNumber, source.thingIDNumber
                        select (source: source.Docking, target)).FirstOrDefault();
            if (pair.source == null) { Reject("DeltaV_AutoSoftDockNoTarget"); return; }
            if (!universe.Controls.SelectPlayerAutoTask(ship, PlayerAutoTaskKind.SoftDock,
                activate: false)) return;
            orders.Add(subject, (pair.source, pair.target,
                _ => pair.source.AlignmentReference(universe, pair.target)));
            ResumeSteeringFor(subject);
        }

        private static void Reject(string key) =>
            Messages.Message(key.Translate(), MessageTypeDefOf.RejectInput, false);

        private bool PairValid(DriveSubject subject, CompSoftDocking source, Building_DockingPort target)
            => Enabled && Live(source.Port) && Live(target) && source.Port.Map != target.Map
                && !source.Port.ChannelExtended && !target.ChannelExtended
                && ShipRegistry.ShipOf(source.Port) is ShipBase ship && ship.Identity == subject
                && ship.PlayerMayOperate
                && (source.Peer == target || source.IsFree && target.Docking?.IsFree == true);

        internal void ResumeSteeringFor(DriveSubject subject)
        {
            if (!orders.TryGetValue(subject, out var order)) return;
            bool Valid() => PairValid(subject, order.source, order.target);
            if (!Valid()) { StopSteeringFor(subject); return; }
            universe.Autopilot.FollowReference(subject, order.reference, false, Valid,
                () => StopSteeringFor(subject));
        }

        internal void StopSteeringFor(DriveSubject subject)
        {
            if (!orders.TryGetValue(subject, out var order)) return;
            orders.Remove(subject);
            universe.Autopilot.DetachReference(subject, order.reference);
        }

        internal string SteeringStatus(DriveSubject subject)
        {
            if (!orders.TryGetValue(subject, out var order)
                || !universe.Autopilot.IsFollowing(subject, order.reference))
                return "DeltaV_AutoSoftDockInactive".Translate();
            return (order.source.Peer == order.target && order.source.Connected
                ? "DeltaV_AutoSoftDockConnected" : "DeltaV_AutoRunning").Translate();
        }

        internal void ApplyAlignmentAssist()
        {
            if (!Enabled) return;
            foreach (Building_DockingPort port in ports)
                if (port.Docking?.IsActiveOwner == true && Live(port) && Live(port.Docking.Peer))
                    port.Docking.ApplyAlignmentAssist(universe);
        }

        internal void Tick()
        {
            if (!Enabled) return;
            foreach (Building_DockingPort port in ports)
            {
                CompSoftDocking dock = port.Docking;
                if (dock?.IsActiveOwner != true) continue;
                dock.TickConnection(universe, this);
                if (!dock.Connected) CancelTransfersAt(port);
            }

            int now = Find.TickManager.TicksGame;
            if (now < nextSearch) return;
            nextSearch = now + 15;
            PilotTarget piloted = universe.Pilot.PilotedTarget(universe);
            Building_DockingPort[] ordered = ports.OrderBy(p => p.thingIDNumber).ToArray();
            // Existing saved pairs claim their original peer before new candidate searches.
            foreach (Building_DockingPort port in ordered)
                port.Docking?.TryRestore(universe, this);
            foreach (var entry in orders.ToArray())
            {
                var order = entry.Value;
                if (!PairValid(entry.Key, order.source, order.target)
                    || entry.Key != piloted.Identity && !universe.Autopilot.IsFollowing(entry.Key, order.reference))
                    StopSteeringFor(entry.Key);
                else
                    order.source.TryCaptureOrdered(universe, this, order.target);
            }

            ShipBase ship = piloted.IsValid ? piloted.Vessel : universe.Pilot.SelectedShipOnMap(Find.CurrentMap);
            if (ship?.IsValid != true || ship.Identity.shuttle || !ship.PlayerMayOperate
                || orders.ContainsKey(ship.Identity)) return;
            foreach (Building_DockingPort port in ordered)
                port.Docking?.TryFindCapture(universe, this, ship.Identity, ordered);
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

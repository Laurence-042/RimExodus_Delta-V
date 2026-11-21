using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal sealed partial class ShipFlightTransitionService
    {
        private readonly HashSet<SpaceObject> pendingExternalForceSplits = new HashSet<SpaceObject>();

        private static bool ExternalForceEmitterReady(CompSpaceWeapon weapon) => weapon?.parent?.Spawned == true
            && !weapon.parent.Destroyed && weapon.SpaceModeEnabled && weapon.GroupActive
            && weapon.OwnerShip.EffectiveFireControlMode != ShipFireControlMode.Disabled
            && weapon.aimMode == SpaceWeaponAimMode.Volley
            && weapon.Component<CompPowerTrader>()?.PowerOn == true
            && (!weapon.WeaponProps.tractor || SpaceTractor.CanOperate(weapon));

        // The same domain and building membership travel from selection through placement.
        private sealed class ForceDomain
        {
            internal readonly Map map;
            internal readonly HashSet<IntVec3> cells;
            internal readonly HashSet<Building> buildings;
            internal readonly ShipBase independent;
            internal readonly int owner;
            private readonly int revision, membershipRevision;
            private readonly Rot4 facing;

            internal ForceDomain(Map map, HashSet<IntVec3> cells, HashSet<Building> buildings, ShipBase independent, int owner, int revision, int membershipRevision)
            {
                this.map = map;
                this.cells = cells;
                this.buildings = buildings;
                this.independent = independent;
                this.owner = owner;
                this.revision = revision;
                this.membershipRevision = membershipRevision;
                facing = independent?.Anchor.Rotation ?? Rot4.Invalid;
            }

            internal bool Matches(ForceDomain current) => current != null && current.map == map
                && current.independent?.Identity == independent?.Identity && current.owner == owner
                && revision == current.revision && membershipRevision == current.membershipRevision
                && (independent == null ? ReferenceEquals(cells, current.cells)
                    : current.facing == facing && current.cells.SetEquals(cells) && current.buildings.SetEquals(buildings));
        }

        private HashSet<IntVec3> ForceCells(ShipBase ship) => ship.Transfer.body is Building body
            ? new HashSet<IntVec3>(body.OccupiedRect())
            : universe.FoundationTopology.DomainAt(ship.Map, ship.Anchor.Position);

        private ForceDomain PlanForceDomain(Map map, HashSet<IntVec3> cells, ShipBase independent = null)
        {
            if (independent != null)
                return new ForceDomain(map, ForceCells(independent),
                    new HashSet<Building> { independent.Transfer.body }, independent, 0,
                    independent.StructuralState?.TransferRevision ?? -1, -1);
            return cells?.Count > 0 ? new ForceDomain(map, cells,
                universe.FoundationTopology.BuildingsIn(map, cells), null,
                SubstructureOwnership.For(map).OwnerAt(cells.First()), universe.FoundationTopology.DomainRevision(map, cells),
                universe.FoundationTopology.MembershipRevision(map, cells)) : null;
        }

        private ForceDomain ResolveForceDomain(Map map, IntVec3 cell, Building building)
        {
            var topology = universe.FoundationTopology;
            if (building == null) return PlanForceDomain(map, topology.DomainAt(map, cell));
            var domain = topology.BuildingDomain(map, building);
            if (domain != null) return PlanForceDomain(map, domain);
            if (!building.Spawned || building.Map != map) return null;
            ShipBase ship = ShipRegistry.ShipOf(building);
            return ship.IsValid && ship.Transfer.body == building ? PlanForceDomain(map, null, ship) : null;
        }

        private bool IsOnlyForceDomain(SpaceObject frame, ForceDomain plan) =>
            universe.FoundationTopology.DomainCount(frame.map) == (plan.independent == null ? 1 : 0)
            && frame.Ships.All(ship => ship.Transfer.body == null || plan.buildings.Contains(ship.Transfer.body));

        private SpaceObject MoveForceDomain(SpaceObject frame, ForceDomain plan, ShipBase pilot = null, SplitFocus focus = null)
        {
            if (plan == null) return null;
            ShipBase ship = plan.independent ?? pilot;
            if (ship != null)
            {
                if (plan.independent == null)
                    Building_DockingPort.RetractChannelsFor(plan.map, plan.cells);
                return TransferShipToPocket(universe, ship, plan.map, frame, plan.cells,
                    ship.Anchor.Rotation, plan.buildings);
            }
            return TransferForceDomain(frame, plan, focus);
        }

        internal bool PrepareExternalForceTarget(SpaceObject target, IntVec3 cell, CompSpaceWeapon weapon,
            Building building = null)
        {
            if (target?.map == null || !target.CanRelocateMap || !ExternalForceEmitterReady(weapon)) return false;
            Thing launcher = weapon.parent;
            Map map = target.map, sourceMap = launcher.Map;
            SpaceObject source = universe.FrameOf(sourceMap);
            if (source == null || source == target) return false;
            var plan = ResolveForceDomain(map, cell, building);
            if (plan == null) return false;
            if (IsOnlyForceDomain(target, plan)) return true;
            if (!pendingExternalForceSplits.Add(target)) return false;
            int version = target.RepresentationVersion, sourceVersion = source.RepresentationVersion;
            IntVec3 sourceCell = launcher.Position;
            universe.QueueFrameAction(() =>
            {
                if (!pendingExternalForceSplits.Remove(target) || target.map != map || map.Disposed
                    || target.RepresentationVersion != version || universe.FrameOf(map) != target
                    || !target.CanRelocateMap || !ExternalForceEmitterReady(weapon)
                    || launcher.Map != sourceMap || launcher.Position != sourceCell
                    || source.RepresentationVersion != sourceVersion || universe.FrameOf(sourceMap) != source) return;
                var current = ResolveForceDomain(map, cell, building);
                if (plan.Matches(current)) MoveForceDomain(target, current);
            });
            return false;
        }

        private SpaceObject TransferForceDomain(SpaceObject target, ForceDomain plan, SplitFocus focus)
        {
            Map map = plan.map;
            var domain = plan.cells;
            var members = plan.buildings;
            int old = plan.owner;
            var main = ShipRegistry.MainConsoleForOwner(map, old);
            int owner = main?.parent is Building console && members.Contains(console)
                ? old : universe.AllocateOwnerId();
            if (owner != old) universe.NoteStructureName(owner, universe.NewWreckName(owner));
            var owners = domain.ToDictionary(cell => cell, _ => owner);
            bool finish = focus == null;
            focus ??= SplitFocus.Capture(universe, map);
            Building_DockingPort.RetractChannelsFor(map, domain);
            SpaceObject moved = TransferDomainToNewPocket(universe, map, target, domain, null,
                retainedOwnerId: owner, targetConsoleFacing: Rot4.North,
                focus: focus, sourceOwners: owners, buildings: members);
            if (moved == null) return null;
            if (finish) focus?.Complete(universe);
            QueueEmptyDepartedMapCleanup(universe, map);
            return moved;
        }
    }
}

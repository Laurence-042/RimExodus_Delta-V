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

        // External force acts on the physical component under the beam, without a target engine gate.
        internal bool PrepareExternalForceTarget(SpaceObject target, IntVec3 cell, CompSpaceWeapon weapon)
        {
            Thing launcher = weapon?.parent;
            if (target?.map == null || !target.CanRelocateMap || !ExternalForceEmitterReady(weapon)) return false;
            Map sourceMap = launcher.Map;
            SpaceObject source = universe.FrameOf(sourceMap);
            if (source == null || source == target) return false;
            int sourceVersion = source.RepresentationVersion;
            IntVec3 sourceCell = launcher.Position;
            var blocks = universe.FoundationTopology.PhysicalBlocks(target.map);
            if (blocks.Count <= 1) return true;
            HashSet<IntVec3> block = blocks.FirstOrDefault(b => b.Contains(cell));
            if (block == null) return false;
            if (!pendingExternalForceSplits.Add(target)) return false;
            Map map = target.map;
            int version = target.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                if (!pendingExternalForceSplits.Remove(target) || target.map != map || map.Disposed
                    || target.RepresentationVersion != version || universe.FrameOf(map) != target
                    || !target.CanRelocateMap || !ExternalForceEmitterReady(weapon)
                    || launcher.Map != sourceMap || launcher.Position != sourceCell
                    || source.RepresentationVersion != sourceVersion || universe.FrameOf(sourceMap) != source) return;
                // Revalidate the same physical domain; never transfer a different fragment after damage.
                var current = universe.FoundationTopology.PhysicalBlocks(map);
                if (current.Count <= 1 || !current.Any(b => b.SetEquals(block))) return;
                var ownership = SubstructureOwnership.For(map);
                var owners = new Dictionary<IntVec3, int>();
                var remap = new Dictionary<int, int>();
                var retained = new HashSet<int>(target.Ships.Where(s => s.IsValid && !s.Identity.shuttle
                    && block.Contains(s.Anchor.Position)).Select(s => s.Identity.key));
                foreach (IntVec3 at in block)
                {
                    int old = ownership.OwnerAt(at);
                    if (!remap.TryGetValue(old, out int id))
                    {
                        id = retained.Contains(old) ? old : universe.AllocateOwnerId();
                        remap.Add(old, id);
                        if (id != old) universe.NoteStructureName(id, universe.NewWreckName(id));
                    }
                    owners.Add(at, id);
                }
                SplitFocus focus = SplitFocus.Capture(universe, map);
                int selectedOwner = focus != null && remap.TryGetValue(focus.selection.identity.key, out int selected)
                    ? selected : owners[cell];
                Building_DockingPort.RetractChannelsFor(map, block);
                SpaceObject moved = TransferDomainToNewPocket(universe, map, target, block, null,
                    wreckOwnership: false, retainedOwnerId: selectedOwner, targetConsoleFacing: Rot4.North,
                    focus: focus, sourceOwners: owners);
                if (moved == null) return;
                focus?.Complete(universe);
                QueueEmptyDepartedMapCleanup(universe, map);
            });
            return false;
        }
    }
}

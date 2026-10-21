using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Content handoff driven by destruction, transfer and despawn events; no scan schedule.</summary>
    public sealed class SpaceScatterService
    {
        private readonly UniverseManager universe;
        private readonly HashSet<SpaceObject> candidates = new HashSet<SpaceObject>();
        internal IEnumerable<SpaceObject> Candidates => candidates;
        private readonly Dictionary<Map, HashSet<IntVec3>> damaged = new Dictionary<Map, HashSet<IntVec3>>();
        private readonly HashSet<Map> pendingEmpty = new HashSet<Map>();
        private readonly Dictionary<Map, int> pulledAt = new Dictionary<Map, int>();
        private readonly HashSet<Map> collecting = new HashSet<Map>();
        private readonly HashSet<Map> retiring = new HashSet<Map>();
        public SpaceScatterService(UniverseManager universe)
        {
            this.universe = universe;
            foreach (SpaceObject obj in universe.objects) Register(obj);
        }
        internal void Register(SpaceObject obj) { if (obj.IsScattered && obj.map != null) candidates.Add(obj); }
        internal void Remove(SpaceObject obj) { candidates.Remove(obj); }
        internal void Forget(Map map)
        {
            candidates.RemoveWhere(o => o.map == map);
            damaged.Remove(map); pendingEmpty.Remove(map); pulledAt.Remove(map); collecting.Remove(map); retiring.Remove(map);
        }

        // Called by the existing distance lifecycle or explicit sector departure, never a new scan.
        internal void RetireLost(SpaceObject owner, bool departing = false)
        {
            Map map = owner?.map;
            if (owner?.IsScattered != true || map == null || map.Disposed || !retiring.Add(map)) return;
            int version = owner.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                if (!retiring.Remove(map) || map.Disposed || universe.FrameOf(map) != owner
                    || owner.RepresentationVersion != version) return;
                float limit = Mathf.Max(DeltaVMod.CurrentSettings.poiDeleteRadius,
                    DeltaVMod.CurrentSettings.poiActiveRadius + 1f);
                if (!departing && universe.Lifecycle.MinPlayerDistance(owner) <= limit) return;
                if (Find.CurrentMap == map)
                {
                    SpaceObject focus = universe.PlayerReferenceObjects().FirstOrDefault(o => o.map != null && o.map != map);
                    if (focus == null) return;
                    universe.SetFocused(focus);
                }
                // Loss is content removal, including otherwise indestructible quest cargo.
                // Use vanilla's scoped destruction override and restore it even on failure.
                bool previous = Thing.allowDestroyNonDestroyable;
                try
                {
                    Thing.allowDestroyNonDestroyable = true;
                    foreach (Thing thing in map.listerThings.AllThings.ToList())
                        if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                }
                finally { Thing.allowDestroyNonDestroyable = previous; }
                if (map.listerThings.AllThings.Count != 0) return;
                if (map.lordManager != null)
                    foreach (var lord in map.lordManager.lords.ToList())
                        if (lord.ownedPawns.Count == 0) map.lordManager.RemoveLord(lord);
                universe.UnregisterObject(owner);
                universe.MapPool.TryReturn(universe.MapPool.Restore(map));
            });
        }

        internal static bool IsCargo(Thing thing) => thing is Pawn || thing.def.category == ThingCategory.Item;
        internal static bool Supported(Map map, IntVec3 cell) => SubstructureOwnership.HasFoundation(map, cell)
            || map.terrainGrid.TerrainAt(cell) != TerrainDefOf.Space;

        internal void NotifyDestroyedCell(Map map, IntVec3 cell)
        {
            if (universe.FrameOf(map) == null || universe.IsResettingScene) return;
            if (!damaged.TryGetValue(map, out HashSet<IntVec3> cells))
            {
                cells = new HashSet<IntVec3>();
                damaged.Add(map, cells);
                universe.QueueFrameAction(() =>
                {
                    if (!damaged.TryGetValue(map, out HashSet<IntVec3> current)) return;
                    damaged.Remove(map);
                    if (map.Disposed || universe.FrameOf(map) == null) return;
                    foreach (IntVec3 lost in current)
                        if (!Supported(map, lost))
                            foreach (Thing thing in lost.GetThingList(map).Where(IsCargo).ToList()) TryScatter(thing);
                });
            }
            cells.Add(cell);
        }

        internal bool ScatterLoose(Map map)
        {
            foreach (Thing thing in map.listerThings.AllThings.Where(IsCargo).ToList())
                if (thing.Spawned && !Supported(map, thing.Position) && !TryScatter(thing)) return false;
            return !map.listerThings.AllThings.Any(t => IsCargo(t) && !Supported(map, t.Position));
        }

        internal bool TryScatter(Thing thing)
        {
            Map source = thing?.Map;
            SpaceObject owner = universe.FrameOf(source);
            if (owner == null || owner.IsScattered || !thing.Spawned) return false;
            IntVec3 origin = thing.Position;
            Rot4 facing = thing.Rotation;
            SpacePose pose = SpaceTransform.RigidPointPose(owner.pose,
                SpaceTransform.MapToShipLocal(source, new Vector2(origin.x + .5f, origin.z + .5f)), 0f);
            bool success = universe.MapPool.TryRent(SpaceObjectTag.Scattered, new IntVec2(1, 1), destination =>
            {
                thing.DeSpawn();
                try
                {
                    GenSpawn.Spawn(thing, IntVec3.Zero, destination, facing);
                    thing.PostSwapMap();
                }
                catch
                {
                    if (thing.Spawned) thing.DeSpawn();
                    GenSpawn.Spawn(thing, origin, source, facing);
                    throw;
                }
            }, out SpaceMapLease lease);
            if (!success)
            {
                if (lease != null) universe.MapPool.TryReturn(lease);
                return false;
            }
            var scattered = new MapSpaceObject(universe.AllocateOwnerId(), lease.Map, pose)
            {
                baseTag = SpaceObjectTag.Scattered, faction = thing.Faction, ambientLabel = thing.LabelCap
            };
            universe.RegisterObject(scattered);
            return true;
        }

        internal void NotifyContentRemoved(Map map)
        {
            if (map != null && universe.FrameOf(map)?.IsScattered == true) SpaceCollision.Forget(map);
            if (map == null || universe.FrameOf(map)?.IsScattered != true || !pendingEmpty.Add(map)) return;
            universe.QueueFrameAction(() =>
            {
                if (!pendingEmpty.Remove(map) || map.Disposed) return;
                SpaceObject owner = universe.FrameOf(map);
                if (owner?.IsScattered != true || map.listerThings.AllThings.Any(IsCargo)) return;
                foreach (Thing residue in map.listerThings.AllThings.Where(t => t is Filth || t is Mote || t is Gas).ToList())
                    residue.Destroy(DestroyMode.Vanish);
                // Death can spawn a corpse in this same frame. Only genuinely empty maps retire.
                if (map.listerThings.AllThings.Count != 0 || Find.CurrentMap == map) return;
                universe.UnregisterObject(owner);
                universe.MapPool.TryReturn(universe.MapPool.Restore(map));
            });
        }

        internal void Pull(SpaceObject target, Thing launcher)
        {
            var weapon = launcher?.TryGetComp<CompSpaceWeapon>();
            if (target?.IsScattered != true || target.map == null || target.map.Disposed
                || !SpaceTractor.CanOperate(weapon)) return;
            int now = Find.TickManager.TicksGame;
            if (pulledAt.TryGetValue(target.map, out int last) && last == now) return;
            pulledAt[target.map] = now;
            SpaceObject receiver = universe.FrameOf(launcher.Map);
            if (receiver == null) return;
            IntVec3 cell = launcher.Position;
            SpacePose destination = SpaceTransform.RigidPointPose(receiver.pose,
                SpaceTransform.MapToShipLocal(receiver.map, new Vector2(cell.x + .5f, cell.z + .5f)), 0f);
            ShipPhysics.AttractPoint(target, Vector2.zero, destination, 2.5f);
            if (OrbitSphere.ArcCells(target.pose.direction, destination.direction) > .4f
                || (SpaceTransform.TransportVelocity(target.pose, destination.position) - destination.velocity).magnitude > .5f
                || !collecting.Add(target.map)) return;
            Map source = target.map, receivingMap = launcher.Map;
            int sourceVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                collecting.Remove(source);
                if (target.map != source || universe.FrameOf(source) != target || target.RepresentationVersion != sourceVersion
                    || receiver.map != receivingMap || receiver.RepresentationVersion != receiverVersion
                    || launcher.Map != receivingMap || launcher.Position != cell || !SpaceTractor.CanOperate(weapon)) return;
                SpacePose at = SpaceTransform.RigidPointPose(receiver.pose,
                    SpaceTransform.MapToShipLocal(receivingMap, new Vector2(cell.x + .5f, cell.z + .5f)), 0f);
                if (OrbitSphere.ArcCells(target.pose.direction, at.direction) <= .5f && !TryReceive(target, receivingMap, cell))
                    launcher.TryGetComp<CompSpaceTractor>()?.NotifyBlocked();
            });
        }

        internal bool TryReceive(SpaceObject scattered, Map destination, IntVec3 cell)
        {
            if (scattered?.IsScattered != true || scattered.map == null || scattered.map.Disposed
                || !MoonPoolUtility.IsMoonPoolAt(destination, cell) || !cell.Standable(destination)) return false;
            Map source = scattered.map;
            foreach (Thing thing in source.listerThings.AllThings.Where(IsCargo).ToList())
            {
                if (thing is Pawn)
                {
                    if (cell.GetThingList(destination).Any(t => t is Pawn)) return false;
                    thing.DeSpawn();
                    try { GenSpawn.Spawn(thing, cell, destination, thing.Rotation); thing.PostSwapMap(); }
                    catch
                    {
                        if (thing.Spawned) thing.DeSpawn();
                        GenSpawn.Spawn(thing, IntVec3.Zero, source);
                        throw;
                    }
                }
                else
                {
                    thing.DeSpawn();
                    try
                    {
                        if (!GenPlace.TryPlaceThing(thing, cell, destination, ThingPlaceMode.Direct))
                        { GenSpawn.Spawn(thing, IntVec3.Zero, source); return false; }
                    }
                    catch { if (!thing.Spawned && !thing.Destroyed) GenSpawn.Spawn(thing, IntVec3.Zero, source); throw; }
                }
            }
            if (Find.CurrentMap == source) universe.SetFocused(universe.FrameOf(destination));
            NotifyContentRemoved(source);
            return true;
        }
    }
}

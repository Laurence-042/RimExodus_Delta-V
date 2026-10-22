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
        internal const float CollectionRadius = 2f;
        private readonly UniverseManager universe;
        private readonly HashSet<SpaceObject> candidates = new HashSet<SpaceObject>();
        internal IEnumerable<SpaceObject> Candidates => candidates;
        private readonly Dictionary<Map, HashSet<IntVec3>> damaged = new Dictionary<Map, HashSet<IntVec3>>();
        private readonly HashSet<Map> pendingEmpty = new HashSet<Map>();
        private sealed class PullRequest
        {
            internal SpaceObject target, receiver;
            internal Thing launcher;
            internal Map source, destination;
            internal int targetVersion, receiverVersion, tick;
            internal IntVec3 cell;
            internal Vector2 local, aim;
            internal bool hasAim;
            internal float angle;
        }
        private readonly List<PullRequest> pulls = new List<PullRequest>();
        internal const float PullFraction = 0.025f;
        private readonly HashSet<Map> collecting = new HashSet<Map>();
        private readonly HashSet<Map> retiring = new HashSet<Map>();
        public SpaceScatterService(UniverseManager universe)
        {
            this.universe = universe;
            foreach (SpaceObject obj in universe.objects) Register(obj);
        }
        internal void Register(SpaceObject obj) { if (obj.IsScattered && obj.map != null) candidates.Add(obj); }
        internal void Remove(SpaceObject obj)
        {
            candidates.Remove(obj);
            pulls.RemoveAll(p => p.target == obj || p.receiver == obj);
        }
        internal void Forget(Map map)
        {
            candidates.RemoveWhere(o => o.map == map);
            damaged.Remove(map); pendingEmpty.Remove(map); collecting.Remove(map); retiring.Remove(map);
            pulls.RemoveAll(p => p.source == map || p.destination == map);
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

        internal bool TryLaunch(SpaceWeaponShot shot)
        {
            SpaceObject target = shot.target;
            Vector2 aim = SpaceTransform.UniverseToMapPoint(shot.container, shot.weapon.parent.Map, shot.aimPoint);
            if (shot.weapon.aimMode != SpaceWeaponAimMode.PointDefense)
            {
                PullRequest held = pulls.Find(p => p.launcher == shot.weapon.parent);
                target = held != null && (held.aim - aim).sqrMagnitude <= 4f && Valid(held)
                    ? held.target : null;
                if (target == null)
                {
                    float best = 4f;
                    foreach (SpaceObject candidate in candidates)
                    {
                        if (candidate.map == null || candidate.map.Disposed) continue;
                        float distance = (SpaceTransform.UniverseToMapPoint(shot.container,
                            shot.weapon.parent.Map, candidate.pose.position) - aim).sqrMagnitude;
                        if (distance <= best) { best = distance; target = candidate; }
                    }
                }
            }
            if (!Pull(target, shot.weapon.parent)) return false;
            PullRequest request = pulls.Find(p => p.launcher == shot.weapon.parent && p.target == target);
            // Keep the initial cursor anchor: moving the mouse deliberately selects another object.
            if (!request.hasAim) { request.aim = aim; request.hasAim = true; }
            return true;
        }

        internal bool Pull(SpaceObject target, Thing launcher)
        {
            var weapon = launcher?.TryGetComp<CompSpaceWeapon>();
            SpaceObject receiver = universe.FrameOf(launcher?.Map);
            if (target?.IsScattered != true || target.map == null || target.map.Disposed
                || receiver == null || !SpaceTractor.CanOperate(weapon)) return false;
            Vector2 receiverLocal = SpaceTransform.MapToShipLocal(receiver.map,
                new Vector2(launcher.Position.x + .5f, launcher.Position.z + .5f));
            SpacePose emitter = SpaceTransform.RigidPointPose(receiver.pose, receiverLocal, 0f);
            if (OrbitSphere.ArcCells(emitter.direction, target.pose.direction) > weapon.WeaponProps.range) return false;
            int now = Find.TickManager.TicksGame;
            PullRequest request = pulls.Find(p => p.target == target);
            if (request != null && request.launcher != launcher && request.tick >= now - 1 && Valid(request)) return false;
            if (request == null || request.launcher != launcher || request.tick < now - 1 || !Valid(request))
            {
                pulls.RemoveAll(p => p.target == target || p.launcher == launcher);
                request = new PullRequest
                {
                    target = target, receiver = receiver, launcher = launcher,
                    source = target.map, destination = launcher.Map, cell = launcher.Position,
                    targetVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion,
                    local = SpaceTransform.UniverseToShipLocal(receiver.pose, target.pose.direction),
                    angle = SpaceTransform.WrapAngle(target.pose.angle - receiver.pose.angle)
                };
                pulls.Add(request);
            }
            request.tick = now;
            return true;
        }

        private bool Valid(PullRequest p) => p.target.map == p.source && !p.source.Disposed
            && universe.FrameOf(p.source) == p.target && p.target.RepresentationVersion == p.targetVersion
            && p.receiver.map == p.destination && universe.FrameOf(p.destination) == p.receiver
            && p.receiver.RepresentationVersion == p.receiverVersion && p.launcher.Map == p.destination
            && p.launcher.Position == p.cell && SpaceTractor.CanOperate(p.launcher.TryGetComp<CompSpaceWeapon>());

        // Run after every receiver has drifted. Only refreshed, paid requests move; no candidate scan.
        internal void Tick()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = pulls.Count - 1; i >= 0; i--)
            {
                PullRequest p = pulls[i];
                if (p.tick != now || !Valid(p)) { pulls.RemoveAt(i); continue; }
                Vector2 destination = SpaceTransform.MapToShipLocal(p.destination,
                    new Vector2(p.cell.x + .5f, p.cell.z + .5f));
                p.local = Vector2.Lerp(p.local, destination, PullFraction);
                // Release inherits the receiver's rigid-point motion, never the old runaway velocity.
                p.target.pose = SpaceTransform.RigidPointPose(p.receiver.pose, p.local, p.angle);
                universe.Projectiles.RecordTractorBeam(p.launcher, p.receiver, p.target);
                QueueCollection(p.target, p.launcher, p.receiver);
            }
        }

        private void QueueCollection(SpaceObject target, Thing launcher, SpaceObject receiver)
        {
            var weapon = launcher.TryGetComp<CompSpaceWeapon>();
            IntVec3 cell = launcher.Position;
            SpacePose destination = SpaceTransform.RigidPointPose(receiver.pose,
                SpaceTransform.MapToShipLocal(receiver.map, new Vector2(cell.x + .5f, cell.z + .5f)), 0f);
            if (OrbitSphere.ArcCells(target.pose.direction, destination.direction) > CollectionRadius
                || !collecting.Add(target.map)) return;
            Map source = target.map, receivingMap = launcher.Map;
            int sourceVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion;
            universe.QueueFrameAction(() =>
            {
                collecting.Remove(source);
                if (target.map != source || universe.FrameOf(source) != target || target.RepresentationVersion != sourceVersion
                    || receiver.map != receivingMap || receiver.RepresentationVersion != receiverVersion
                    || launcher.Map != receivingMap || launcher.Position != cell || !SpaceTractor.CanOperate(weapon)
                    || !pulls.Exists(p => p.target == target && p.launcher == launcher
                        && p.tick == Find.TickManager.TicksGame && Valid(p))) return;
                SpacePose at = SpaceTransform.RigidPointPose(receiver.pose,
                    SpaceTransform.MapToShipLocal(receivingMap, new Vector2(cell.x + .5f, cell.z + .5f)), 0f);
                if (OrbitSphere.ArcCells(target.pose.direction, at.direction) <= CollectionRadius && !TryReceive(target, launcher))
                    launcher.TryGetComp<CompSpaceTractor>()?.NotifyBlocked();
            });
        }

        internal bool TryReceive(SpaceObject scattered, Thing launcher)
        {
            Map destination = launcher.Map;
            IntVec3 cell = launcher.Position;
            if (scattered?.IsScattered != true || scattered.map == null || scattered.map.Disposed
                || destination == null || !MoonPoolUtility.IsMoonPoolAt(destination, cell)) return false;
            Map source = scattered.map;
            IntVec3 output = launcher.InteractionCell;
            foreach (Thing thing in source.listerThings.AllThings.Where(IsCargo).ToList())
            {
                thing.DeSpawn();
                try
                {
                    // CompDeepDrill's output path owns nearby placement and stacking.
                    if (!GenPlace.TryPlaceThing(thing, output, destination, ThingPlaceMode.Near,
                        null, p => p != cell && p != output))
                    {
                        if (!thing.Spawned && !thing.Destroyed) GenSpawn.Spawn(thing, IntVec3.Zero, source);
                        return false;
                    }
                    thing.PostSwapMap();
                }
                catch { if (!thing.Spawned && !thing.Destroyed) GenSpawn.Spawn(thing, IntVec3.Zero, source); throw; }
            }
            if (Find.CurrentMap == source) universe.SetFocused(universe.FrameOf(destination));
            NotifyContentRemoved(source);
            return true;
        }
    }
}

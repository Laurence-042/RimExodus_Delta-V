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
        private readonly Dictionary<Map, HashSet<Thing>> looseCargoByMap =
            new Dictionary<Map, HashSet<Thing>>();
        internal IEnumerable<SpaceObject> Candidates => candidates;
        internal bool HasCandidates => candidates.Count > 0;
        private readonly Dictionary<Map, HashSet<IntVec3>> damaged = new Dictionary<Map, HashSet<IntVec3>>();
        private readonly HashSet<Map> pendingEmpty = new HashSet<Map>();
        private readonly HashSet<Map> awaitingFocusLoss = new HashSet<Map>();
        private sealed class PullRequest
        {
            internal SpaceObject target, receiver;
            internal Thing launcher;
            internal Map source, destination;
            internal int targetVersion, receiverVersion, tick, paidTick = -1;
            internal IntVec3 cell, targetCell;
            internal Vector2 local;
            internal Vector2 targetLocal;
            internal bool started, manual;
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
            if (obj.map != null) awaitingFocusLoss.Remove(obj.map);
            pulls.RemoveAll(p => p.target == obj || p.receiver == obj);
        }
        internal void Forget(Map map)
        {
            candidates.RemoveWhere(o => o.map == map);
            looseCargoByMap.Remove(map);
            damaged.Remove(map); pendingEmpty.Remove(map); awaitingFocusLoss.Remove(map);
            collecting.Remove(map); retiring.Remove(map);
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
            || map.terrainGrid.TerrainAt(cell) is TerrainDef terrain
                && terrain != TerrainDefOf.Space && terrain != DeltaVDefOf.DeltaV_Void;

        internal IEnumerable<Thing> LooseCargo(Map map)
        {
            if (map == null || map.Disposed) return Array.Empty<Thing>();
            if (!looseCargoByMap.TryGetValue(map, out HashSet<Thing> loose))
            {
                loose = new HashSet<Thing>();
                foreach (Thing thing in map.listerThings.AllThings)
                    if (thing.Spawned && !thing.Destroyed && IsCargo(thing)
                        && !Supported(map, thing.Position)) loose.Add(thing);
                looseCargoByMap.Add(map, loose);
            }
            return loose;
        }

        internal void NotifyCargoRegistered(Map map, Thing thing, IntVec3 cell)
        {
            if (map == null || thing == null || cell != thing.Position
                || !looseCargoByMap.TryGetValue(map, out HashSet<Thing> loose)
                || !thing.Spawned || thing.Destroyed || !IsCargo(thing)) return;
            if (Supported(map, cell)) loose.Remove(thing);
            else loose.Add(thing);
        }

        internal void NotifyCargoDeregistered(Map map, Thing thing, IntVec3 cell)
        {
            if (map != null && thing != null && cell == thing.Position
                && looseCargoByMap.TryGetValue(map, out HashSet<Thing> loose)) loose.Remove(thing);
        }

        internal void NotifySupportChanged(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map)
                || !looseCargoByMap.TryGetValue(map, out HashSet<Thing> loose)) return;
            bool supported = Supported(map, cell);
            foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
            {
                if (thing.Position != cell || !thing.Spawned || thing.Destroyed || !IsCargo(thing)) continue;
                if (supported) loose.Remove(thing);
                else loose.Add(thing);
            }
        }

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
                    {
                        SubstructureDamageUtility.CleanInvalidFilth(map, lost);
                        if (!Supported(map, lost))
                            foreach (Thing thing in lost.GetThingList(map).Where(IsCargo).ToList()) TryScatter(thing);
                    }
                });
            }
            cells.Add(cell);
        }

        internal bool ScatterLoose(Map map)
        {
            foreach (Thing thing in LooseCargo(map).ToList())
                if (!TryScatter(thing)) return false;
            return !LooseCargo(map).Any();
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
                if (map.listerThings.AllThings.Count != 0) return;
                if (Find.CurrentMap == map)
                {
                    awaitingFocusLoss.Add(map);
                    return;
                }
                awaitingFocusLoss.Remove(map);
                universe.UnregisterObject(owner);
                universe.MapPool.TryReturn(universe.MapPool.Restore(map));
            });
        }

        internal void NotifyFocusChanged(Map focused)
        {
            if (awaitingFocusLoss.Count == 0) return;
            foreach (Map map in awaitingFocusLoss.ToList())
            {
                if (map == focused) continue;
                awaitingFocusLoss.Remove(map);
                if (!map.Disposed) NotifyContentRemoved(map);
            }
        }

        internal bool TryLaunch(SpaceWeaponShot shot)
        {
            if (shot.weapon.aimMode != SpaceWeaponAimMode.PointDefense)
                return PullStructure(shot);
            return Pull(shot.target, shot.weapon.parent);
        }

        private bool PullStructure(SpaceWeaponShot shot)
        {
            Thing launcher = shot.weapon.parent;
            SpaceObject receiver = universe.FrameOf(launcher?.Map);
            if (receiver == null || !SpaceTractor.CanOperate(shot.weapon)) return false;
            if (!TryStructureAt(shot.aimPoint, receiver, out SpaceObject target, out IntVec3 hitCell)) return false;
            Vector2 launcherLocal = SpaceTransform.MapToShipLocal(receiver.map,
                new Vector2(launcher.Position.x + .5f, launcher.Position.z + .5f));
            SpacePose emitter = SpaceTransform.RigidPointPose(receiver.pose, launcherLocal, 0f);
            if (OrbitSphere.ArcCells(emitter.position, shot.aimPoint) > shot.weapon.EffectiveRange)
                return false;
            ShipBase ship = ShipRegistry.ShipOf(launcher);
            if (ship != null && !universe.ShipTransitions.RequestThrust(universe, PilotTarget.Of(ship),
                    playerInitiated: true)) return false;
            if (!universe.ShipTransitions.PrepareTractorTarget(target, hitCell, launcher)) return false;
            int now = Find.TickManager.TicksGame;
            PullRequest request = pulls.Find(p => p.launcher == launcher && p.target == target
                && p.targetCell == hitCell && p.manual && Valid(p));
            if (request == null)
            {
                // Manual beams each own their force and attachment point; never replace another emitter.
                pulls.RemoveAll(p => p.launcher == launcher);
                request = new PullRequest
                {
                    manual = true, target = target, receiver = receiver, launcher = launcher,
                    source = target.map, destination = launcher.Map, cell = launcher.Position, targetCell = hitCell,
                    targetVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion,
                    targetLocal = SpaceTransform.UniverseToShipLocal(target.pose, shot.aimPoint)
                };
                pulls.Add(request);
            }
            request.tick = now;
            request.paidTick = now;
            return true;
        }

        internal bool TryStructureAt(Vector3 point, SpaceObject receiver, out SpaceObject target, out IntVec3 cell)
        {
            target = null; cell = IntVec3.Invalid;
            foreach (SpaceObject candidate in universe.objects)
            {
                if (!StructuralTarget(candidate, receiver)) continue;
                Vector2 local = SpaceTransform.UniverseToMapPoint(candidate, candidate.map, point);
                IntVec3 at = new IntVec3(Mathf.FloorToInt(local.x), 0, Mathf.FloorToInt(local.y));
                if (SpaceStructureRules.ClassifyCell(candidate.map, at, out _) == SpaceCellKind.Empty) continue;
                if (target != null && candidate.objectId > target.objectId) continue;
                target = candidate; cell = at;
            }
            return target != null;
        }

        private static bool StructuralTarget(SpaceObject target, SpaceObject receiver) => target != null
            && target != receiver && target.CanRelocateMap && target.map != null && !target.map.Disposed;

        internal void ReleaseAutomatic(Thing launcher) => pulls.RemoveAll(p => !p.manual && p.launcher == launcher);

        private static bool AllowedCargo(SpaceObject target, Thing launcher)
        {
            if (target?.IsScattered != true || target.map == null || target.map.Disposed) return false;
            CompSpaceTractor tractor = launcher.TryGetComp<CompSpaceTractor>();
            if (tractor == null) return false;
            bool any = false;
            foreach (Thing thing in target.map.listerThings.AllThings)
                if (IsCargo(thing))
                {
                    if (!tractor.Allows(thing)) return false;
                    any = true;
                }
            return any;
        }

        internal bool AutoAvailable(SpaceObject target, CompSpaceWeapon weapon)
        {
            if (!AllowedCargo(target, weapon.parent)) return false;
            int now = Find.TickManager.TicksGame;
            return !pulls.Exists(p => !p.manual && p.target == target && p.launcher != weapon.parent
                && p.tick >= now - 1 && Valid(p));
        }

        internal bool ClaimAutomatic(SpaceObject target, CompSpaceWeapon weapon)
        {
            if (!SpaceTractor.CanOperate(weapon) || weapon.aimMode != SpaceWeaponAimMode.PointDefense
                || !weapon.GroupActive || !AutoAvailable(target, weapon)) return false;
            Thing launcher = weapon.parent;
            SpaceObject receiver = universe.FrameOf(launcher.Map);
            if (receiver == null) return false;
            int now = Find.TickManager.TicksGame;
            PullRequest request = pulls.Find(p => !p.manual && p.target == target && p.launcher == launcher
                && p.tick >= now - 1 && Valid(p));
            if (request == null)
            {
                pulls.RemoveAll(p => (!p.manual && p.target == target) || p.launcher == launcher);
                request = new PullRequest
                {
                    target = target, receiver = receiver, launcher = launcher,
                    source = target.map, destination = launcher.Map, cell = launcher.Position,
                    targetVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion
                };
                pulls.Add(request);
            }
            request.tick = now;
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
            if (!ClaimAutomatic(target, weapon)) return false;
            PullRequest request = pulls.Find(p => !p.manual && p.target == target && p.launcher == launcher);
            if (!request.started)
            {
                request.local = SpaceTransform.UniverseToShipLocal(receiver.pose, target.pose.direction);
                request.angle = SpaceTransform.WrapAngle(target.pose.angle - receiver.pose.angle);
                request.started = true;
            }
            request.tick = now;
            request.paidTick = now;
            return true;
        }

        private bool Valid(PullRequest p) => p.target.map == p.source && !p.source.Disposed
            && universe.FrameOf(p.source) == p.target && p.target.RepresentationVersion == p.targetVersion
            && p.receiver.map == p.destination && universe.FrameOf(p.destination) == p.receiver
            && p.receiver.RepresentationVersion == p.receiverVersion && p.launcher.Map == p.destination
            && p.launcher.Position == p.cell && SpaceTractor.CanOperate(p.launcher.TryGetComp<CompSpaceWeapon>())
            && p.launcher.TryGetComp<CompSpaceWeapon>().GroupActive
            && (p.manual ? StructuralTarget(p.target, p.receiver) : AllowedCargo(p.target, p.launcher))
            && (p.launcher.TryGetComp<CompSpaceWeapon>().aimMode == SpaceWeaponAimMode.PointDefense) != p.manual;

        // Run after every receiver has drifted. Only refreshed, paid requests move; no candidate scan.
        internal void Tick()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = pulls.Count - 1; i >= 0; i--)
            {
                PullRequest p = pulls[i];
                if (p.tick != now || !Valid(p)) { pulls.RemoveAt(i); continue; }
                if (p.paidTick != now) continue; // Aiming claims exclude competitors but never move unpaid cargo.
                if (p.manual)
                {
                    Vector2 emitter = SpaceTransform.MapToShipLocal(p.destination,
                        new Vector2(p.cell.x + .5f, p.cell.z + .5f));
                    ShipPhysics.ApplyTractorImpulse(p.receiver, emitter, p.target, p.targetLocal,
                        ShipPhysics.ThrusterThrust / 60f);
                    Vector3 hit = SpaceTransform.RigidPointPose(p.target.pose, p.targetLocal, 0f).position;
                    universe.Projectiles.RecordTractorBeam(p.launcher, p.receiver, p.target, hit);
                    continue;
                }
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
                        && p.paidTick == Find.TickManager.TicksGame && Valid(p))) return;
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

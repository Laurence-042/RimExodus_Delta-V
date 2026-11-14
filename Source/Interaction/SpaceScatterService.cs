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
            internal CompSpaceWeapon weapon;
            internal Thing launcher => weapon.parent;
            internal Map source, destination;
            internal int targetVersion, receiverVersion, tick, paidTick = -1;
            internal IntVec3 cell;
            internal Vector2 local;
            internal bool started;
            internal float angle;
        }
        private readonly List<PullRequest> pulls = new List<PullRequest>();
        internal const float PullFraction = 0.025f;
        private readonly HashSet<PullRequest> collecting = new HashSet<PullRequest>();
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
            collecting.RemoveWhere(p => p.source == map || p.destination == map); retiring.Remove(map);
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
                RemoveLostContents(owner, map);
                universe.MapPool.TryReturn(universe.MapPool.Restore(map));
            });
        }

        internal void TryRetireCargoForCapacity(SpaceObject owner)
        {
            Map map = owner.map;
            if (map.mapPawns.AllPawnsCount != 0 || pulls.Any(pull => pull.source == map)
                || map.listerThings.AllThings.Any(thing => thing is IThingHolder || !thing.questTags.NullOrEmpty()
                    || (!IsCargo(thing) && thing is not Filth && thing is not Mote && thing is not Gas))) return;
            RemoveLostContents(owner, map);
            if (map.listerThings.AllThings.Count == 0 && universe.SpaceObjectOnMap(map) == null)
                universe.Lifecycle.DestroyPocketMap(map);
        }

        private void RemoveLostContents(SpaceObject owner, Map map)
        {
            // Use vanilla's scoped destruction override for otherwise indestructible cargo.
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
        }

        internal static bool IsCargo(Thing thing) => thing is Pawn || thing.def.category == ThingCategory.Item;
        internal static bool Supported(Map map, IntVec3 cell) => SubstructureOwnership.SupportsAt(map, cell);

        internal static void ClearPawnTransferJob(Thing thing)
        {
            if (thing is not Pawn pawn) return;
            if (pawn.mindState != null) pawn.mindState.knownExploder = null;
            pawn.jobs?.StopAll();
        }

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
                            foreach (Thing thing in lost.GetThingList(map).Where(IsCargo).ToList())
                            {
                                if (TryScatter(thing) || !thing.Spawned || thing.Map != map) continue;
                                ClearPawnTransferJob(thing);
                                if (!universe.MapPool.HasRoom)
                                    universe.QueueFrameAction(() =>
                                    {
                                        if (universe.MapPool.HasRoom && thing.Spawned && thing.Map == map
                                            && thing.Position == lost && !Supported(map, lost)) TryScatter(thing);
                                    });
                            }
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

        internal static SpaceScatterService ForCargoMap(Map map)
        {
            if (map == null || map.Disposed) return null;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe != null && !universe.IsResettingScene
                && universe.SpaceObjectOnMap(map) is SpaceObject owner && !owner.IsScattered
                ? universe.Scatter
                : null;
        }

        internal void PlaceCargoOverflow(Thing item, Map map, IntVec3 cell, bool fromDestruction = false)
        {
            // GenSpawn displaces existing items at capacity. Keep this stack on the source
            // map until the frame handoff so a failed rental leaves real, recoverable cargo.
            item.holdingOwner?.Remove(item);
            SpawnItemAt(item, map, cell);
            QueueCargoScatter(item, map, cell, fromDestruction);
        }

        internal void QueueDestroyedStorageItem(Thing item, Map source)
        {
            SpaceObject owner = universe.SpaceObjectOnMap(source);
            int version = owner.RepresentationVersion;
            IntVec3 origin = item.Position;
            Rot4 facing = item.Rotation;
            Vector2 localPoint = SpaceTransform.MapToShipLocal(source, new Vector2(origin.x + .5f, origin.z + .5f));
            Vector2 launchVelocity = SpaceTransform.Rotate(Vector2.up, Rand.Range(0f, Mathf.PI * 2f))
                * Rand.Range(0f, 4f);
            SpacePose pose = SpaceTransform.RigidPointPose(owner.pose, localPoint, 0f);
            pose.velocity = SpaceTransform.VelocityAtMovingLocalPoint(owner.pose, localPoint, launchVelocity, pose.position);

            // Building.DeSpawn redistributes surplus stacks when the storage capacity disappears.
            item.DeSpawn();
            QueueDetachedCargo(item, owner, source, version, origin, facing, pose);
        }

        internal void QueueMiningEjection(Thing ore, SpaceObject asteroid, IntVec3 origin,
            Vector2 hitPoint, Vector2 outward)
        {
            Map map = asteroid.map;
            Vector2 localPoint = SpaceTransform.MapToShipLocal(map, hitPoint) + outward * 0.5f;
            SpacePose pose = SpaceTransform.RigidPointPose(asteroid.pose, localPoint, 0f);
            pose.velocity = SpaceTransform.VelocityAtMovingLocalPoint(asteroid.pose, localPoint,
                outward * Rand.Range(2f, 4f), pose.position);
            QueueDetachedCargo(ore, asteroid, map, asteroid.RepresentationVersion, origin, Rot4.North, pose);
        }

        private void QueueDetachedCargo(Thing item, SpaceObject owner, Map source, int version,
            IntVec3 origin, Rot4 facing, SpacePose pose)
        {
            universe.QueueFrameAction(Transfer);

            void Transfer()
            {
                if (TryCreateScatter(item, pose, destination => SpawnScatteredItem(item, destination, facing))) return;

                if (!source.Disposed && Find.Maps.Contains(source) && universe.SpaceObjectOnMap(source) == owner
                    && owner.RepresentationVersion == version)
                {
                    item.Rotation = facing;
                    SpawnItemAt(item, source, origin);
                }
                else universe.QueueFrameAction(Transfer);
            }
        }

        private void QueueCargoScatter(Thing item, Map map, IntVec3 cell, bool fromDestruction = false)
        {
            SpaceObject owner = universe.SpaceObjectOnMap(map);
            if (owner == null) return;
            int version = owner.RepresentationVersion;
            Vector2 launchDirection = fromDestruction
                ? SpaceTransform.Rotate(Vector2.up, Rand.Range(0f, Mathf.PI * 2f))
                : Vector2.zero;
            Vector2 launchOffset = launchDirection * 1.5f;
            Vector2 launchVelocity = fromDestruction ? launchDirection * Rand.Range(0f, 4f) : Vector2.zero;
            universe.QueueFrameAction(() =>
            {
                if (!map.Disposed && item.Spawned && item.Map == map && item.Position == cell
                    && universe.SpaceObjectOnMap(map) == owner && owner.RepresentationVersion == version)
                    TryScatter(item, map, cell, null, launchOffset, launchVelocity);
            });
        }

        internal void QueueCrateEjection(Building_SealedSpaceCargoContainer crate)
        {
            Map map = crate.Map;
            IntVec3 cell = crate.Position;
            SpaceObject owner = universe.SpaceObjectOnMap(map);
            int version = owner.RepresentationVersion;
            IReadOnlyList<Thing> goods = crate.Contents;
            universe.QueueFrameAction(() =>
            {
                if (map.Disposed || !crate.Spawned || crate.Map != map || crate.Position != cell || !crate.CanOpen
                    || universe.SpaceObjectOnMap(map) != owner || owner.RepresentationVersion != version) return;
                ThingOwner holder = crate.GetDirectlyHeldThings();
                bool released = false;
                foreach (Thing item in goods)
                    if (item.holdingOwner == holder && TryScatter(item, map, cell, holder))
                        released = true;
                if (released) crate.BroadcastCompSignal(Building_Crate.CrateContentsChanged);
            });
        }

        private static void SpawnItemAt(Thing item, Map map, IntVec3 cell)
        {
            item.Position = cell;
            item.SpawnSetup(map, respawningAfterLoad: false);
        }

        internal bool TryScatter(Thing thing) => thing?.Spawned == true
            && TryScatter(thing, thing.Map, thing.Position, null);

        private bool TryScatter(Thing thing, Map source, IntVec3 origin, ThingOwner holder,
            Vector2 launchOffset = default, Vector2 launchVelocity = default)
        {
            SpaceObject owner = universe.FrameOf(source);
            if (owner == null || owner.IsScattered) return false;
            Rot4 facing = thing.Rotation;
            Vector2 localPoint = SpaceTransform.MapToShipLocal(source, new Vector2(origin.x + .5f, origin.z + .5f))
                + launchOffset;
            SpacePose pose = SpaceTransform.RigidPointPose(owner.pose, localPoint, 0f);
            if (launchVelocity != Vector2.zero)
                pose.velocity = SpaceTransform.VelocityAtMovingLocalPoint(owner.pose, localPoint, launchVelocity, pose.position);
            return TryCreateScatter(thing, pose, destination =>
            {
                ClearPawnTransferJob(thing);
                if (holder == null) thing.DeSpawn();
                else if (!holder.Remove(thing))
                    throw new InvalidOperationException("Scatter cargo is no longer in its container.");
                try
                {
                    SpawnScatteredItem(thing, destination, facing);
                }
                catch
                {
                    if (thing.Spawned) thing.DeSpawn();
                    if (holder?.TryAdd(thing, canMergeWithExistingStacks: false) == true) throw;
                    if (thing.def.category == ThingCategory.Item)
                    {
                        thing.Rotation = facing;
                        SpawnItemAt(thing, source, origin);
                    }
                    else GenSpawn.Spawn(thing, origin, source, facing);
                    throw;
                }
            });
        }

        private static void SpawnScatteredItem(Thing thing, Map destination, Rot4 facing)
        {
            try
            {
                GenSpawn.Spawn(thing, IntVec3.Zero, destination, facing);
                if (!thing.Spawned || thing.Map != destination)
                    throw new InvalidOperationException("Scatter cargo did not spawn on its destination map.");
                thing.PostSwapMap();
            }
            catch
            {
                if (thing.Spawned && thing.Map == destination) thing.DeSpawn();
                throw;
            }
        }

        private bool TryCreateScatter(Thing thing, SpacePose pose, Action<Map> populate)
        {
            float score = thing is Pawn || universe.MapPool.HasRoom
                ? float.PositiveInfinity : universe.Lifecycle.ScatterCapacityScore(pose);
            bool success = universe.MapPool.TryRent(SpaceObjectTag.Scattered, new IntVec2(1, 1), populate,
                out SpaceMapLease lease, score);
            if (!success)
            {
                if (lease != null) universe.MapPool.TryReturn(lease);
                return false;
            }
            var scattered = new MapSpaceObject(universe.AllocateOwnerId(), lease.Map, pose)
            {
                baseTag = SpaceObjectTag.Scattered, faction = thing.Faction, ambientLabel = thing.LabelCap
            };
            universe.Lifecycle.FreezeScattered(lease.Map);
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

        internal void ReleaseAutomatic(Thing launcher) => pulls.RemoveAll(p => p.launcher == launcher);
        internal void ReleaseAutomatic(CompSpaceWeapon weapon) => pulls.RemoveAll(p => p.weapon == weapon);

        internal void ReleaseManual(DriveSubject owner, bool main, bool secondary)
            => pulls.RemoveAll(p => p.weapon.Component<CompSpaceTractor>()?.ManualCargo == true
                && p.weapon.OwnerShip.Identity == owner
                && !(p.weapon.salvoSet == SpaceWeaponSalvoSet.Main ? main : secondary));

        private static bool AllowedCargo(SpaceObject target, CompSpaceWeapon weapon)
        {
            if (target?.IsScattered != true || target.map == null || target.map.Disposed) return false;
            CompSpaceTractor tractor = weapon.Component<CompSpaceTractor>();
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
            if (!AllowedCargo(target, weapon)) return false;
            int now = Find.TickManager.TicksGame;
            return !pulls.Exists(p => p.target == target && p.weapon != weapon
                && p.tick >= now - 1 && Valid(p));
        }

        internal bool ClaimAutomatic(SpaceObject target, CompSpaceWeapon weapon)
            => weapon.aimMode == SpaceWeaponAimMode.PointDefense && Claim(target, weapon);

        private bool Claim(SpaceObject target, CompSpaceWeapon weapon)
        {
            if (!SpaceTractor.CanOperate(weapon)
                || !weapon.GroupActive || !AutoAvailable(target, weapon)) return false;
            Thing launcher = weapon.parent;
            SpaceObject receiver = universe.FrameOf(launcher.Map);
            if (receiver == null) return false;
            int now = Find.TickManager.TicksGame;
            PullRequest request = pulls.Find(p => p.target == target && p.weapon == weapon
                && p.tick >= now - 1 && Valid(p));
            if (request == null)
            {
                pulls.RemoveAll(p => p.target == target || p.weapon == weapon);
                request = new PullRequest
                {
                    target = target, receiver = receiver, weapon = weapon,
                    source = target.map, destination = launcher.Map, cell = launcher.Position,
                    targetVersion = target.RepresentationVersion, receiverVersion = receiver.RepresentationVersion
                };
                pulls.Add(request);
            }
            request.tick = now;
            return true;
        }

        internal bool Pull(SpaceObject target, CompSpaceWeapon weapon)
        {
            Thing launcher = weapon?.parent;
            SpaceObject receiver = universe.FrameOf(launcher?.Map);
            if (target?.IsScattered != true || target.map == null || target.map.Disposed
                || receiver == null || !SpaceTractor.CanOperate(weapon)) return false;
            Vector2 receiverLocal = SpaceTransform.MapToShipLocal(receiver.map,
                SpaceWeaponGeometry.MuzzleMapPoint(weapon));
            SpacePose emitter = SpaceTransform.RigidPointPose(receiver.pose, receiverLocal, 0f);
            if (OrbitSphere.ArcCells(emitter.direction, target.pose.direction) > weapon.WeaponProps.range) return false;
            int now = Find.TickManager.TicksGame;
            if (!Claim(target, weapon)) return false;
            PullRequest request = pulls.Find(p => p.target == target && p.weapon == weapon);
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
            && p.launcher.Position == p.cell && SpaceTractor.CanOperate(p.weapon)
            && p.weapon.GroupActive
            && AllowedCargo(p.target, p.weapon)
            && (p.weapon.aimMode == SpaceWeaponAimMode.PointDefense || p.weapon.Component<CompSpaceTractor>().ManualCargo);

        // Run after every receiver has drifted. Only refreshed, paid requests move; no candidate scan.
        internal void Tick()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = pulls.Count - 1; i >= 0; i--)
            {
                PullRequest p = pulls[i];
                if (p.tick != now || !Valid(p)) { pulls.RemoveAt(i); continue; }
                if (p.paidTick != now) continue; // Aiming claims exclude competitors but never move unpaid cargo.
                Vector2 destination = SpaceTransform.MapToShipLocal(p.destination,
                    SpaceWeaponGeometry.MuzzleMapPoint(p.weapon));
                p.local = Vector2.Lerp(p.local, destination, PullFraction);
                // Release inherits the receiver's rigid-point motion, never the old runaway velocity.
                p.target.pose = SpaceTransform.RigidPointPose(p.receiver.pose, p.local, p.angle);
                universe.Projectiles.RecordTractorBeam(p.weapon, p.receiver, p.target);
                QueueCollection(p);
            }
        }

        private void QueueCollection(PullRequest request)
        {
            CompSpaceTractor tractor = request.weapon.Component<CompSpaceTractor>();
            var context = new TractorReceiveContext(universe, request.weapon, request.target, request.receiver);
            if (!tractor.HasReachedReceiver(context) || !collecting.Add(request)) return;
            Map source = request.source;
            universe.QueueFrameAction(() =>
            {
                collecting.Remove(request);
                if (!pulls.Contains(request) || !Valid(request)
                    || request.paidTick != Find.TickManager.TicksGame || !tractor.HasReachedReceiver(context)) return;
                TractorReceiveResult result = tractor.TryReceive(context);
                if (result == TractorReceiveResult.Complete)
                {
                    pulls.Remove(request);
                    if (Find.CurrentMap == source) universe.SetFocused(request.receiver);
                    NotifyContentRemoved(source);
                }
                else if (result == TractorReceiveResult.Rejected) tractor.NotifyBlocked();
            });
        }

        internal SpaceObject ManualTarget(SpaceWeaponShot shot)
        {
            SpaceObject best = null;
            float nearest = float.MaxValue;
            Vector2 ray = SpaceTransform.Rotate(Vector2.right, shot.launchMapAngle);
            foreach (SpaceObject candidate in candidates)
            {
                if (!AutoAvailable(candidate, shot.weapon)) continue;
                Vector2 point = SpaceTransform.UniverseToMapPoint(shot.container, shot.weapon.parent.Map, candidate.pose.position);
                Vector2 offset = point - shot.muzzleMap;
                float along = Vector2.Dot(offset, ray);
                if (along < 0f || along > shot.weapon.EffectiveRange || along >= nearest
                    || (offset - ray * along).sqrMagnitude > .5f) continue;
                nearest = along; best = candidate;
            }
            return best;
        }
    }
}

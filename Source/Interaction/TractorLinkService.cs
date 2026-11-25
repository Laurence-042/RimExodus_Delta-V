using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal interface ILightTractorEmitter
    {
        float ContactRadius { get; }
        float ImpulsePerTick { get; }
        bool ValidSource(UniverseManager universe, SpaceObject source, CompSpaceWeapon weapon);
        bool AllowsTarget(SpaceObject target);
        bool MoveTarget(SpaceObject target);
        TractorReceiveResult TryReceive(UniverseManager universe, SpaceObject source,
            SpaceObject target);
        void NotifyBlocked();
    }

    /// <summary>
    /// Shared instance-level tractor link authority. Coordinate collectors and bilateral
    /// grapples keep different motion policies, but share target ownership, endpoints,
    /// validity, cancellation and safe-frame delivery through this service.
    /// </summary>
    public sealed class TractorLinkService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<int, GrappleLink> grapples = new Dictionary<int, GrappleLink>();
        private readonly HashSet<int> claimedTargets = new HashSet<int>();

        private sealed class GrappleLink
        {
            internal int sourceId, sourceVersion, targetId, targetVersion;
            internal Vector2 sourceLocal, targetLocal;
            internal bool collecting;
            internal int lastTriggerTick;
            internal CompSpaceWeapon weapon;
            internal ILightTractorEmitter emitter;
        }

        internal TractorLinkService(UniverseManager universe) { this.universe = universe; }

        internal void Reset()
        {
            grapples.Clear();
            claimedTargets.Clear();
        }

        internal void ForgetObject(int objectId)
        {
            if (grapples.Remove(objectId, out GrappleLink own)) claimedTargets.Remove(own.targetId);
            foreach (var pair in grapples.Where(pair => pair.Value.targetId == objectId).ToArray())
            {
                claimedTargets.Remove(pair.Value.targetId);
                grapples.Remove(pair.Key);
            }
        }

        internal bool Trigger(SpaceWeaponShot shot)
        {
            SpaceObject source = shot?.container;
            ILightTractorEmitter emitter = shot?.weapon?.Component<CompPawnGrapple>();
            emitter ??= shot?.weapon?.Component<CompCargoTractor>();
            if (source == null || emitter == null
                || !emitter.ValidSource(universe, source, shot.weapon)) return false;
            Vector2 sourceLocal = SpaceTransform.MapToShipLocal(source.map,
                SpaceWeaponGeometry.MuzzleMapPoint(shot.weapon));
            Vector2 aimLocal = SpaceTransform.UniverseToShipLocal(source.pose, shot.aimPoint);
            Vector2 ray = aimLocal - sourceLocal;
            SpaceObject target = FirstTarget(source, sourceLocal, ray, shot.weapon.EffectiveRange,
                emitter, out Vector2 targetLocal);
            if (target == null) return false;
            if (grapples.TryGetValue(source.objectId, out GrappleLink existing))
            {
                if (existing.targetId != target.objectId)
                {
                    Cancel(source.objectId);
                }
                else
                {
                    existing.lastTriggerTick = Find.TickManager.TicksGame;
                    existing.sourceLocal = sourceLocal;
                    existing.targetLocal = targetLocal;
                    existing.weapon = shot.weapon;
                    existing.emitter = emitter;
                    return true;
                }
            }
            return Start(source, target, sourceLocal, targetLocal, shot.weapon, emitter);
        }

        private SpaceObject FirstTarget(SpaceObject source, Vector2 sourceLocal, Vector2 ray,
            float range, ILightTractorEmitter emitter, out Vector2 targetLocal)
        {
            targetLocal = Vector2.zero;
            if (ray.sqrMagnitude < 0.0001f) return null;
            Vector2 direction = ray.normalized;
            Vector2 universeDirection = SpaceTransform.ShipDirectionToUniverseTangent(
                source.pose, sourceLocal, direction);
            Vector3 sourcePoint = SpaceTransform.RigidPointPose(source.pose, sourceLocal, 0f).position;
            SpaceObject best = null;
            float bestAlong = range + 1f;
            Vector2 bestLocal = Vector2.zero;
            foreach (SpaceObject candidate in universe.objects)
            {
                if (candidate == null || candidate == source || candidate.IsTransient
                    || !emitter.AllowsTarget(candidate)
                    || candidate.InterceptionThing == null && candidate.map == null) continue;
                Vector2 offset = SpaceTransform.UniverseToShipLocal(source.pose,
                    candidate.pose.position) - sourceLocal;
                float along = Vector2.Dot(offset, direction);
                if (along <= 0f || along > range || along >= bestAlong) continue;
                float radius = Mathf.Max(0.5f, SpaceCollision.BoundsRadiusOf(candidate));
                if ((offset - direction * along).sqrMagnitude > radius * radius) continue;
                var view = SpaceCollision.TargetView.Of(candidate);
                if (!view.Valid) continue;
                Vector2 from = view.FromUniverse(sourcePoint);
                Vector2 localDirection = view.DirectionFromUniverse(sourcePoint, universeDirection);
                var query = new SpaceCollision.RayQuery(candidate);
                if (!query.NextLocal(from, localDirection, range, null, 0f,
                    null, null, _ => false, _ => false, out SpaceCollision.RayContact contact)) continue;
                float contactAlong = OrbitSphere.ArcCells(sourcePoint, view.ToUniverse(contact.point));
                if (contactAlong >= bestAlong) continue;
                best = candidate;
                bestAlong = contactAlong;
                bestLocal = contact.point;
            }
            targetLocal = bestLocal;
            return best;
        }

        private bool Start(SpaceObject source, SpaceObject target, Vector2 sourceLocal,
            Vector2 targetLocal, CompSpaceWeapon weapon, ILightTractorEmitter emitter)
        {
            if (!emitter.ValidSource(universe, source, weapon) || target == null || target == source
                || !emitter.AllowsTarget(target)
                || weapon == null || claimedTargets.Contains(target.objectId)) return false;
            Vector3 sourcePoint = SpaceTransform.RigidPointPose(source.pose, sourceLocal, 0f).position;
            Vector3 targetPoint = SpaceTransform.RigidPointPose(target.pose, targetLocal, 0f).position;
            float distance = OrbitSphere.ArcCells(sourcePoint, targetPoint);
            if (distance > weapon.EffectiveRange) return false;
            var link = new GrappleLink
            {
                sourceId = source.objectId,
                sourceVersion = source.RepresentationVersion,
                targetId = target.objectId,
                targetVersion = target.RepresentationVersion,
                sourceLocal = sourceLocal,
                targetLocal = targetLocal,
                lastTriggerTick = Find.TickManager.TicksGame,
                weapon = weapon,
                emitter = emitter
            };
            grapples[source.objectId] = link;
            claimedTargets.Add(target.objectId);
            Messages.Message("DeltaV_GrappleAttached".Translate(target.label), MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        private void Cancel(int sourceId)
        {
            if (!grapples.Remove(sourceId, out GrappleLink link)) return;
            claimedTargets.Remove(link.targetId);
        }

        internal void Tick()
        {
            foreach (GrappleLink link in grapples.Values.ToArray())
            {
                if (link.lastTriggerTick < Find.TickManager.TicksGame - 1)
                {
                    Cancel(link.sourceId);
                    continue;
                }
                SpaceObject source = universe.ObjectById(link.sourceId);
                SpaceObject target = universe.ObjectById(link.targetId);
                if (!Valid(link, source, target)) { Cancel(link.sourceId); continue; }
                Vector3 sourcePoint = SpaceTransform.RigidPointPose(source.pose,
                    link.sourceLocal, 0f).position;
                Vector3 targetPoint = SpaceTransform.RigidPointPose(target.pose, link.targetLocal, 0f).position;
                float distance = OrbitSphere.ArcCells(sourcePoint, targetPoint);
                if (distance > link.weapon.EffectiveRange + 1f) { Cancel(link.sourceId); continue; }
                if (FirstTarget(source, link.sourceLocal,
                    SpaceTransform.UniverseToShipLocal(source.pose, targetPoint) - link.sourceLocal,
                    link.weapon.EffectiveRange, link.emitter, out _) != target)
                {
                    Cancel(link.sourceId);
                    continue;
                }
                bool movableTarget = link.emitter.MoveTarget(target);
                if (distance > link.emitter.ContactRadius)
                    ShipPhysics.ApplyTractorImpulse(source, link.sourceLocal, target, link.targetLocal,
                        link.emitter.ImpulsePerTick, movableTarget);
                universe.Projectiles.RecordTractorBeam(link.weapon, source, target, targetPoint);
                if (movableTarget && distance <= link.emitter.ContactRadius)
                    QueueReceive(link, source, target);
            }
        }

        /// <summary>
        /// A maintained light link is deliberate operator input. Flight assist may still
        /// stabilize untouched axes, but must not counter the link's translation or torque.
        /// </summary>
        internal bool TryGetOperatorForce(SpaceObject source, out Vector2 localDirection,
            out float torqueSign)
        {
            localDirection = Vector2.zero;
            torqueSign = 0f;
            if (source == null || !grapples.TryGetValue(source.objectId, out GrappleLink link)
                || link.lastTriggerTick < Find.TickManager.TicksGame - 1) return false;
            SpaceObject target = universe.ObjectById(link.targetId);
            if (!Valid(link, source, target)) return false;
            Vector3 targetPoint = SpaceTransform.RigidPointPose(target.pose,
                link.targetLocal, 0f).position;
            Vector2 toward = SpaceTransform.UniverseToShipLocal(source.pose, targetPoint)
                - link.sourceLocal;
            if (toward.sqrMagnitude < 0.0001f) return false;
            localDirection = toward.normalized;
            Vector2 arm = link.sourceLocal - source.MotionInfo.comLocal;
            torqueSign = Mathf.Sign(arm.x * localDirection.y - arm.y * localDirection.x);
            return true;
        }

        internal bool HasOperatorForce(SpaceObject source) =>
            TryGetOperatorForce(source, out _, out _);

        private bool Valid(GrappleLink link, SpaceObject source, SpaceObject target) =>
            source != null && source.RepresentationVersion == link.sourceVersion
            && link.emitter != null && link.emitter.ValidSource(universe, source, link.weapon)
            && target != null && target.RepresentationVersion == link.targetVersion
            && link.emitter.AllowsTarget(target)
            && universe.ObjectById(source.objectId) == source && universe.ObjectById(target.objectId) == target;

        private void QueueReceive(GrappleLink link, SpaceObject source, SpaceObject target)
        {
            if (link.collecting) return;
            link.collecting = true;
            universe.QueueFrameAction(() =>
            {
                link.collecting = false;
                if (!Valid(link, source, target)
                    || OrbitSphere.ArcCells(
                        SpaceTransform.RigidPointPose(source.pose, link.sourceLocal, 0f).position,
                        SpaceTransform.RigidPointPose(target.pose, link.targetLocal, 0f).position)
                        > link.emitter.ContactRadius)
                    return;
                TractorReceiveResult result = link.emitter.TryReceive(universe, source, target);
                if (result == TractorReceiveResult.Complete)
                {
                    if (universe.ObjectById(target.objectId) == target)
                        universe.UnregisterObject(target);
                    Cancel(link.sourceId);
                }
                else if (result == TractorReceiveResult.Rejected)
                {
                    Cancel(link.sourceId);
                    link.emitter.NotifyBlocked();
                    Messages.Message("DeltaV_GrappleReceiveBlocked".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            });
        }
    }
}

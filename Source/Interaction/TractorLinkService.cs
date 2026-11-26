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
            internal Vector2 arrivalLocal;
            internal bool collecting;
            internal bool docking, pulling = true;
            internal int dockingToken;
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
            if (grapples.Remove(objectId, out GrappleLink own) && !own.docking)
                claimedTargets.Remove(own.targetId);
            foreach (var pair in grapples.Where(pair => pair.Value.targetId == objectId).ToArray())
            {
                if (!pair.Value.docking) claimedTargets.Remove(pair.Value.targetId);
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

        /// <summary>
        /// Maintain a hard-docking-owned light link. Firing range and the first real hull
        /// contact are checked only against the actual weapon; no synthetic reach or target
        /// point is introduced. The frozen landing point remains separate from the hook.
        /// A successful attachment is remembered by HardDockingService for that operation;
        /// losing this runtime line near an exposed landing cell only removes its impulse.
        /// </summary>
        internal bool MaintainDockingLink(int token, SpaceObject source, SpaceObject target,
            Vector2 arrivalLocal, CompSpaceWeapon weapon)
        {
            ILightTractorEmitter emitter = weapon?.Component<CompPawnGrapple>();
            emitter ??= weapon?.Component<CompCargoTractor>();
            if (token <= 0 || source == null || target == null || emitter == null
                || !emitter.ValidSource(universe, source, weapon)
                || !emitter.AllowsTarget(target)) return false;
            Vector2 sourceLocal = SpaceTransform.MapToShipLocal(source.map,
                SpaceWeaponGeometry.MuzzleMapPoint(weapon));
            if (grapples.TryGetValue(source.objectId, out GrappleLink existing))
            {
                if (existing.docking && existing.dockingToken == token
                    && existing.targetId == target.objectId)
                {
                    existing.sourceLocal = sourceLocal;
                    existing.arrivalLocal = arrivalLocal;
                    existing.weapon = weapon;
                    existing.emitter = emitter;
                    return true;
                }
                Cancel(source.objectId);
            }
            Vector3 arrivalPoint = SpaceTransform.RigidPointPose(target.pose, arrivalLocal, 0f).position;
            Vector2 ray = SpaceTransform.UniverseToShipLocal(source.pose, arrivalPoint) - sourceLocal;
            SpaceObject hit = FirstTarget(source, sourceLocal, ray, weapon.EffectiveRange,
                emitter, out Vector2 targetLocal);
            if (hit != target) return false;
            return Start(source, target, sourceLocal, targetLocal, weapon, emitter,
                docking: true, dockingToken: token, arrivalLocal: arrivalLocal);
        }

        internal bool DockingAttached(int sourceId, int token, int targetId) =>
            grapples.TryGetValue(sourceId, out GrappleLink link)
            && link.docking && link.dockingToken == token && link.targetId == targetId;

        internal void CancelDocking(int sourceId, int token)
        {
            if (grapples.TryGetValue(sourceId, out GrappleLink link)
                && link.docking && link.dockingToken == token) Cancel(sourceId);
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
            Vector2 targetLocal, CompSpaceWeapon weapon, ILightTractorEmitter emitter,
            bool docking = false, int dockingToken = 0, Vector2 arrivalLocal = default)
        {
            if (!emitter.ValidSource(universe, source, weapon) || target == null || target == source
                || !emitter.AllowsTarget(target)
                || weapon == null || !docking && claimedTargets.Contains(target.objectId)) return false;
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
                arrivalLocal = docking ? arrivalLocal : targetLocal,
                docking = docking,
                dockingToken = dockingToken,
                lastTriggerTick = Find.TickManager.TicksGame,
                weapon = weapon,
                emitter = emitter
            };
            grapples[source.objectId] = link;
            if (!docking) claimedTargets.Add(target.objectId);
            if (!docking)
                Messages.Message("DeltaV_GrappleAttached".Translate(target.label),
                    MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        private void Cancel(int sourceId)
        {
            if (!grapples.Remove(sourceId, out GrappleLink link)) return;
            if (!link.docking) claimedTargets.Remove(link.targetId);
        }

        internal void Tick()
        {
            foreach (GrappleLink link in grapples.Values.ToArray())
            {
                if (!link.docking && link.lastTriggerTick < Find.TickManager.TicksGame - 1)
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
                if (link.docking) UpdateDockingPull(link, source, target);
                if (distance > link.emitter.ContactRadius && (!link.docking || link.pulling))
                    ShipPhysics.ApplyTractorImpulse(source, link.sourceLocal, target, link.targetLocal,
                        link.emitter.ImpulsePerTick, movableTarget);
                universe.Projectiles.RecordTractorBeam(link.weapon, source, target, targetPoint);
                if (movableTarget && distance <= link.emitter.ContactRadius)
                    QueueReceive(link, source, target);
            }
        }

        private static void UpdateDockingPull(GrappleLink link, SpaceObject source,
            SpaceObject target)
        {
            SpacePose destination = SpaceTransform.RigidPointPose(target.pose,
                link.arrivalLocal, 0f);
            Vector2 positionError = -OrbitSphere.Offset(destination.position, source.pose.position);
            if (positionError.sqrMagnitude < 0.0001f)
            {
                link.pulling = false;
                return;
            }
            Vector2 relativeVelocity = SpaceTransform.TransportVelocity(source.pose,
                destination.position) - destination.velocity;
            float closing = Mathf.Max(0f,
                Vector2.Dot(relativeVelocity, positionError.normalized));
            float braking = 0f;
            ShipBase ship = link.weapon?.OwnerShip;
            if (ship?.IsValid == true
                && source.TryGetDrive(DriveSubject.Of(ship), out DriveInfo report))
            {
                Vector2 towardLocal = SpaceTransform.RotateInverse(
                    SpaceTransform.TransportTangent(positionError.normalized,
                        destination.position, source.pose.position), source.pose.angle);
                braking = report.PreviewBraking(towardLocal);
            }
            if (braking <= 0.000001f)
            {
                link.pulling = true;
                return;
            }
            link.pulling = DockingPulling(link.pulling, positionError.magnitude,
                closing, braking);
        }

        internal static bool DockingPulling(bool wasPulling, float distance,
            float closing, float braking)
        {
            if (braking <= 0.000001f) return true;
            float excess = Mathf.Max(0f, closing - HardDockingService.LightApproachSpeed);
            double stopping = BrakingGuidanceFallback.StoppingDistance(excess,
                braking * BrakingGuidanceFallback.SafetyFactor);
            float threshold = double.IsInfinity(stopping) ? float.PositiveInfinity
                : (float)stopping + 0.75f;
            // Hysteresis keeps a moving target from making the line chatter at the
            // braking boundary. A slack line retensions only after the target opens a
            // meaningful gap again.
            return wasPulling ? distance > threshold : distance >= threshold + 0.75f;
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
                || link.docking
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

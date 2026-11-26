using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum HardDockingCompletion : byte
    {
        Collision,
        Arrival
    }

    internal readonly struct HardDockingDestination
    {
        internal readonly IntVec3 cell;
        internal readonly object state;
        internal HardDockingDestination(IntVec3 cell, object state = null)
        {
            this.cell = cell;
            this.state = state;
        }
    }

    internal readonly struct HardDockingContact
    {
        internal readonly SpaceObject source;
        internal readonly SpaceObject target;
        internal readonly Vector3 universePoint;
        internal readonly Vector2 targetLocalPoint;
        internal readonly IntVec3 targetCell;
        internal readonly object targetState;

        internal HardDockingContact(SpaceObject source, SpaceObject target, Vector3 universePoint,
            Vector2 targetLocalPoint, HardDockingDestination destination)
        {
            this.source = source;
            this.target = target;
            this.universePoint = universePoint;
            this.targetLocalPoint = targetLocalPoint;
            targetCell = destination.cell;
            targetState = destination.state;
        }
    }

    /// <summary>
    /// A vessel-specific hard-docking executor. Target selection, intercept guidance,
    /// collision contact and cancellation are shared; only the contact aftermath differs.
    /// </summary>
    internal interface IHardDockingExecutor
    {
        ShipBase Vessel { get; }
        Thing Body { get; }
        ThingDef CollisionDef { get; }
        HardDockingCompletion Completion { get; }
        bool RequiresAvoidanceDisabled { get; }
        bool UsesProjectileRole { get; }
        float ArrivalVelocityTolerance { get; }
        float ArrivalApproachSpeed { get; }
        CompSpaceWeapon CaptureTether { get; }
        float NoseHalfWidth { get; }
        float NoseForwardOffset { get; }
        string StartLabelKey { get; }
        string StartTipKey { get; }
        string CancelLabelKey { get; }
        string CancelTipKey { get; }
        string BlockReasonKey(UniverseManager universe, SpaceObject source, bool playerInitiated);
        bool ValidFor(SpaceObject source);
        bool TryResolveTarget(SpaceObject target, Vector2 targetMapPoint,
            out HardDockingDestination destination);
        bool TargetStillValid(SpaceObject target, HardDockingDestination destination);
        void NotifyHardDockingActive(bool active);
        void CompleteHardDocking(UniverseManager universe, HardDockingContact contact);
    }

    /// <summary>
    /// Runtime authority for hard-docking commands. Targeting, cancellation and guidance are
    /// shared; an executor chooses collision contact or exact-reference arrival as completion.
    /// </summary>
    public sealed class HardDockingService
    {
        internal const float LightArrivalVelocityTolerance = 2f;
        internal const float LightApproachSpeed = 1.5f;
        private readonly UniverseManager universe;
        private readonly Dictionary<int, Operation> active = new Dictionary<int, Operation>();
        private int nextCaptureToken = 1;

        private sealed class Operation
        {
            internal IHardDockingExecutor executor;
            internal CombatTarget target;
            internal int sourceVersion;
            internal int targetId;
            internal int targetVersion;
            internal HardDockingDestination destination;
            internal Func<SpacePose, SpacePose> reference;
            internal CompSpaceWeapon captureTether;
            internal int captureToken;
            internal bool captureEstablished;
        }

        internal HardDockingService(UniverseManager universe) { this.universe = universe; }

        internal void Reset()
        {
            foreach (KeyValuePair<int, Operation> pair in active)
                if (pair.Value.captureToken > 0)
                    universe.TractorLinks.CancelDocking(pair.Key, pair.Value.captureToken);
            active.Clear();
            nextCaptureToken = 1;
        }

        internal void Tick()
        {
            if (active.Count == 0) return;
            foreach (KeyValuePair<int, Operation> pair in
                new List<KeyValuePair<int, Operation>>(active))
            {
                Operation operation = pair.Value;
                if (operation.executor.Completion != HardDockingCompletion.Arrival
                    || operation.captureTether == null) continue;
                SpaceObject source = universe.ObjectById(pair.Key);
                SpaceObject target = universe.ObjectById(operation.targetId);
                if (source == null || target == null || !ArrivalValid(source, operation))
                {
                    Cancel(pair.Key, source);
                    continue;
                }
                Vector2 mapPoint = new Vector2(operation.destination.cell.x + .5f,
                    operation.destination.cell.z + .5f);
                Vector2 arrivalLocal = SpaceTransform.MapToShipLocal(target.map, mapPoint);
                if (universe.TractorLinks.MaintainDockingLink(operation.captureToken,
                    source, target, arrivalLocal, operation.captureTether))
                    operation.captureEstablished = true;
            }
        }

        internal bool IsActive(SpaceObject source) => source != null && active.ContainsKey(source.objectId);

        internal string BlockReasonKey(ShipBase vessel, SpaceObject source, bool playerInitiated)
        {
            IHardDockingExecutor executor = vessel?.HardDockingExecutor;
            if (executor == null || source == null || !executor.ValidFor(source))
                return "DeltaV_HardDockRequiresFlight";
            if (IsActive(source) || source.projectileRoleOverride) return "DeltaV_HardDockInProgress";
            if (playerInitiated && executor.RequiresAvoidanceDisabled
                && universe.Controls.CollisionAvoidanceEnabled(vessel))
                return "DeltaV_ShuttleBoardDisableAvoidance";
            return executor.BlockReasonKey(universe, source, playerInitiated);
        }

        internal void BeginTargeting(ShipBase vessel)
        {
            IHardDockingExecutor executor = vessel?.HardDockingExecutor;
            SpaceObject source = vessel?.Map != null ? universe.FrameOf(vessel.Map) : null;
            if (executor == null || BlockReasonKey(vessel, source, true) != null) return;
            Find.Targeter.BeginTargeting(new TargetingParameters
            {
                canTargetPawns = false,
                canTargetBuildings = false,
                canTargetItems = false,
                canTargetSelf = false
            }, ignored =>
            {
                Map frameMap = Find.CurrentMap;
                SpaceObject frame = universe.FrameOf(frameMap);
                if (frame != source) return;
                Vector3 mouse = UI.MouseMapPosition();
                SpaceObject target = ShellPicking.ShellAtFramePoint(universe, frameMap, mouse,
                    (map, cell) => executor.TryResolveTarget(universe.FrameOf(map),
                        new Vector2(cell.x + .5f, cell.z + .5f), out _));
                if (target == null || target == source || target.map == null || target.IsScattered
                    || target.HasTag(SpaceObjectTag.Personnel))
                {
                    Messages.Message("DeltaV_HardDockNoTarget".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Vector3 hit = SpaceTransform.MapPointToUniverse(frame, frameMap,
                    new Vector2(mouse.x, mouse.z));
                Vector2 local = SpaceTransform.UniverseToMapPoint(target, target.map, hit);
                if (!executor.TryResolveTarget(target, local,
                    out HardDockingDestination destination))
                {
                    Messages.Message("DeltaV_PersonnelLandingBlocked".Translate(),
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Engage(vessel, CombatTarget.ForContainer(target,
                    new Vector2(destination.cell.x + .5f, destination.cell.z + .5f)),
                    true, destination);
            }, (System.Action<LocalTargetInfo>)null, target => true);
        }

        internal bool Engage(ShipBase vessel, CombatTarget target, bool playerInitiated)
            => Engage(vessel, target, playerInitiated,
                new HardDockingDestination(IntVec3.Invalid));

        private bool Engage(ShipBase vessel, CombatTarget target, bool playerInitiated,
            HardDockingDestination destination)
        {
            IHardDockingExecutor executor = vessel?.HardDockingExecutor;
            SpaceObject source = vessel?.Map != null ? universe.FrameOf(vessel.Map) : null;
            if (executor == null || BlockReasonKey(vessel, source, playerInitiated) != null
                || !target.TryResolve(universe, out var resolved)
                || resolved.Container == null || resolved.Container == source)
                return false;
            if (!destination.cell.IsValid)
            {
                Vector2 point = target.MapPoint ?? SpaceTransform.UniverseToMapPoint(
                    resolved.Container, resolved.Container.map, resolved.Pose.position);
                if (!executor.TryResolveTarget(resolved.Container, point, out destination)) return false;
            }
            universe.Autopilot.Detach(DriveSubject.Of(PilotTarget.Of(vessel)), "hard docking intercept");
            if (universe.Pilot.PilotedTarget(universe).Identity == vessel.Identity)
                universe.Pilot.Exit(universe);
            var operation = new Operation
            {
                executor = executor,
                target = target,
                sourceVersion = source.RepresentationVersion,
                targetId = resolved.Container.objectId,
                targetVersion = resolved.Container.RepresentationVersion,
                destination = destination,
                captureTether = executor.CaptureTether
            };
            if (operation.captureTether != null)
            {
                operation.captureToken = nextCaptureToken++;
                if (nextCaptureToken <= 0) nextCaptureToken = 1;
            }
            bool engaged = executor.Completion == HardDockingCompletion.Collision
                ? universe.Projectiles.AdoptHardDocker(source, executor, target)
                : EngageArrival(source, operation);
            if (!engaged) return false;
            active[source.objectId] = operation;
            if (executor.Completion == HardDockingCompletion.Arrival)
                executor.NotifyHardDockingActive(true);
            Messages.Message("DeltaV_HardDockEngaged".Translate(resolved.Container.label),
                MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        private bool EngageArrival(SpaceObject source, Operation operation)
        {
            DriveSubject subject = DriveSubject.Of(operation.executor.Vessel);
            operation.reference = _ => operation.target.TryResolve(universe, out var resolved)
                ? resolved.Pose : source.pose;
            universe.Autopilot.NavigateToReference(subject, operation.reference,
                () => ArrivalValid(source, operation),
                () => CompleteArrival(source.objectId, operation),
                _ => Cancel(source),
                // A real capture must have happened during this operation. The line may
                // then go slack or lose its surface ray while the light unit crosses the
                // exposed landing cell; that must not revoke an already-earned capture.
                arrivalGate: () => CaptureGate(operation.captureTether != null,
                    operation.captureEstablished),
                arrivalVelocityTolerance: operation.executor.ArrivalVelocityTolerance,
                arrivalApproachSpeed: operation.executor.ArrivalApproachSpeed);
            return universe.Autopilot.IsFollowing(subject, operation.reference);
        }

        internal static bool CaptureGate(bool captureRequired, bool captureEstablished) =>
            !captureRequired || captureEstablished;

        private bool ArrivalValid(SpaceObject source, Operation operation)
        {
            return universe.ObjectById(source.objectId) == source
                && source.RepresentationVersion == operation.sourceVersion
                && universe.ObjectById(operation.targetId) is SpaceObject target
                && target.RepresentationVersion == operation.targetVersion
                && operation.executor.ValidFor(source)
                && operation.executor.TargetStillValid(target, operation.destination)
                && operation.target.TryResolve(universe, out var resolved)
                && resolved.Container == target;
        }

        private void CompleteArrival(int sourceId, Operation expected)
        {
            if (!active.TryGetValue(sourceId, out Operation current) || current != expected) return;
            SpaceObject source = universe.ObjectById(sourceId);
            SpaceObject target = universe.ObjectById(expected.targetId);
            if (source == null || target == null || !ArrivalValid(source, expected))
            {
                Cancel(source);
                return;
            }
            Vector2 mapPoint = new Vector2(expected.destination.cell.x + .5f,
                expected.destination.cell.z + .5f);
            Vector2 local = SpaceTransform.MapToShipLocal(target.map, mapPoint);
            Vector3 point = SpaceTransform.RigidPointPose(target.pose, local, 0f).position;
            ReleaseArrival(source, expected);
            expected.executor.CompleteHardDocking(universe, new HardDockingContact(source, target,
                point, local, expected.destination));
        }

        internal void Cancel(SpaceObject source)
        {
            if (source == null) return;
            Cancel(source.objectId, source);
        }

        private void Cancel(int sourceId, SpaceObject source)
        {
            if (!active.TryGetValue(sourceId, out Operation operation)) return;
            if (operation.executor.Completion == HardDockingCompletion.Collision)
                universe.Projectiles.ReleaseHardDocker(source);
            else ReleaseArrival(source, operation, sourceId);
        }

        private void ReleaseArrival(SpaceObject source, Operation operation, int sourceId = 0)
        {
            if (sourceId == 0) sourceId = source?.objectId ?? 0;
            universe.Autopilot.DetachReference(DriveSubject.Of(operation.executor.Vessel),
                operation.reference);
            if (operation.captureToken > 0)
                universe.TractorLinks.CancelDocking(sourceId, operation.captureToken);
            if (active.TryGetValue(sourceId, out Operation current) && current == operation)
                active.Remove(sourceId);
            operation.executor.NotifyHardDockingActive(false);
        }

        internal void NotifyReleased(int sourceId, IHardDockingExecutor executor)
        {
            if (active.TryGetValue(sourceId, out Operation current) && current.executor == executor)
                active.Remove(sourceId);
            executor?.NotifyHardDockingActive(false);
        }

        internal void NotifyContact(IHardDockingExecutor executor, SpaceObject source,
            SpaceObject target, Vector3 universePoint, Vector2 targetLocalPoint, IntVec3 targetCell)
        {
            if (executor == null || source == null || target == null) return;
            ReleaseForContact(source);
            executor.CompleteHardDocking(universe, new HardDockingContact(source, target,
                universePoint, targetLocalPoint, new HardDockingDestination(targetCell)));
        }

        private void ReleaseForContact(SpaceObject source)
        {
            universe.Projectiles.ReleaseHardDocker(source);
        }
    }
}

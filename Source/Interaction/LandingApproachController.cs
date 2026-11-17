using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Drift matching has no destination. A placement exists only after an EVA command.
    public class LandingApproachController
    {
        public sealed class DriftSyncPlan
        {
            public readonly PilotTarget Target;
            public readonly int hostId;
            public readonly int hostVersion;
            public SpaceObject host => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(hostId);
            public readonly Map targetMap;
            public readonly Map sourceMap;
            public readonly float relativeAngle;
            internal readonly System.Func<SpacePose, SpacePose> reference;
            public int holdTicks;

            internal DriftSyncPlan(PilotTarget target, SpaceObject host, float relativeAngle)
            {
                Target = target;
                hostId = host.objectId;
                hostVersion = host.RepresentationVersion;
                targetMap = host.map;
                sourceMap = target.Map;
                this.relativeAngle = SpaceTransform.WrapAngle(relativeAngle);
                reference = Reference;
            }

            public SpacePose Reference(SpacePose own)
                => MatchingReference(own, host.pose, relativeAngle, Target.Vessel);

            public float Distance() => OrbitSphere.ArcCells(shipContainerPose().position, host.pose.position);

            internal SpacePose shipContainerPose()
                => Current.Game.GetComponent<UniverseManager>().FrameOf(Target.Map).pose;
        }

        // Command-local integer placement, consumed immediately by the shared transfer chain.
        public sealed class DockingPlan
        {
            public readonly PilotTarget Target;
            public readonly Map targetMap;
            public readonly IntVec3 landingCell;
            public readonly Rot4 landingRotation;
            public readonly IntVec3 sourceRoot;
            public readonly int structureId;

            public DockingPlan(PilotTarget target, SpaceObject host, IntVec3 sourceRoot,
                IntVec3 landingCell, Rot4 landingRotation)
            {
                Target = target;
                targetMap = host.map;
                this.landingCell = landingCell;
                this.landingRotation = landingRotation;
                structureId = target.Vessel.Transfer.ownerId;
                this.sourceRoot = sourceRoot;
            }

            // Whole-map capture may use a different source root. Rebase in integer
            // grid space; never round a second continuous destination during transfer.
            internal IntVec3 PlacementRootFor(IntVec3 captureRoot)
                => landingCell + PrefabUtility.GetAdjustedLocalPosition(captureRoot - sourceRoot, landingRotation);

            internal Rot4 InverseRotation => new Rot4((4 - landingRotation.AsInt) % 4);
            internal IntVec3 InversePlacementRootFor(IntVec3 hostRoot)
                => sourceRoot + PrefabUtility.GetAdjustedLocalPosition(hostRoot - landingCell, InverseRotation);
        }

        private DriftSyncPlan plan;
        internal static float HoldAngleWindow => Mathf.Max(0.0017f, (DeltaVMod.CurrentSettings?.dockingAngleWindowDeg ?? 1.15f) * Mathf.Deg2Rad);
        internal const float VelocityWindow = 0.1f;
        internal const float AngularVelocityWindow = 0.002f;
        public DriftSyncPlan ActivePlan => plan;
        public bool Ready => plan != null && plan.holdTicks >= Mathf.Max(1, DeltaVMod.CurrentSettings.dockingHoldTicks);
        public bool ApproachActive => plan != null;
        public bool AnyActive => plan != null;
        internal PilotTarget ApproachTarget => plan?.Target ?? PilotTarget.Invalid;
        internal PilotTarget FlowTarget => ApproachTarget;
        internal Map ApproachShipMap => plan?.Target.Map;

        internal static DockingPlan NearestPlacement(UniverseManager universe, PilotTarget target, SpaceObject host)
        {
            SpaceObject own = universe.FrameOf(target.Map);
            Gravship preview = GravshipInterop.BuildPreview(target);
            if (own == null || host?.map == null || preview == null) return null;
            float relative = ShipOrientation.SnapAngle(own.pose.angle - host.pose.angle);
            Rot4 rotation = ShipOrientation.PlacementRotation(Rot4.East, ShipOrientation.Rot4FromAngle(relative));
            Vector2 origin = SpaceTransform.UniverseToMapPoint(host, host.map, own.pose.position);
            Vector2 offset = SpaceTransform.ShipOriginFromPlacementRoot(target.Map, preview.originalPosition, IntVec3.Zero, rotation);
            IntVec3 root = new IntVec3(Mathf.RoundToInt(origin.x - offset.x), 0, Mathf.RoundToInt(origin.y - offset.y));
            return new DockingPlan(target, host, preview.originalPosition, root, rotation);
        }

        internal void BeginSync(UniverseManager universe, PilotTarget target, SpaceObject host)
        {
            SpaceObject own = universe.FrameOf(target.Map);
            if (own == null || host?.map == null) return;
            if (!universe.Controls.SelectPlayerAutoTask(target.Vessel, PlayerAutoTaskKind.Sync,
                activate: false)) return;
            plan = new DriftSyncPlan(target, host, ShipOrientation.SnapAngle(own.pose.angle - host.pose.angle));
            EngageAutopilot(universe);
            string messageKey = host.HasGravityRegion ? "DeltaV_SyncStartedGravity" : "DeltaV_SyncStarted";
            Messages.Message(messageKey.Translate(host.label), MessageTypeDefOf.NeutralEvent, false);
        }

        public void EngageAutopilot(UniverseManager universe)
        {
            if (plan == null) return;
            DriftSyncPlan reference = plan;
            universe.Autopilot.FollowReference(DriveSubject.Of(plan.Target), reference.reference,
                true, () => IsValid(universe, reference), OnNavigationFailed);
        }

        public bool AutopilotEngaged(UniverseManager universe) => plan != null && universe.Autopilot.IsActive(DriveSubject.Of(plan.Target));
        private static void OnNavigationFailed()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.plan == null) return;
            universe.Approach.CancelSelectionOrPlan(universe, "sync navigation failed");
        }

        internal static bool MatchesMotion(SpacePose own, SpacePose host, ShipBase ship = null)
            => InWindow(own, MatchingReference(own, host,
                ShipOrientation.SnapAngle(own.angle - host.angle), ship), ship);

        internal static SpacePose MatchingReference(SpacePose own, SpacePose host, float relativeAngle, ShipBase ship)
        {
            SpacePose desired = ship?.MatchDockingPointVelocity == true
                ? SpaceTransform.RigidPointPose(host, SpaceTransform.UniverseToShipLocal(host, own.position), relativeAngle)
                : SpaceTransform.MatchingDriftPose(own.position, host, relativeAngle);
            if (ship?.RequiresHeadingAlignment == false) desired.angle = own.angle;
            if (ship?.RequiresAngularVelocityMatch == false) desired.angularVelocity = 0f;
            return desired;
        }

        internal static bool InWindow(SpacePose own, SpacePose desired, ShipBase ship = null)
            => (ship?.RequiresHeadingAlignment == false || Mathf.Abs(SpaceTransform.WrapAngle(own.angle - desired.angle)) <= HoldAngleWindow)
                && SpaceTransform.RelativeVelocity(own, desired).magnitude <= VelocityWindow
                && (ship?.RequiresAngularVelocityMatch == false || Mathf.Abs(own.angularVelocity - desired.angularVelocity) <= AngularVelocityWindow);

        internal static bool InSyncWindow(DriftSyncPlan active)
        {
            if (active?.host == null || !active.Target.IsValid) return false;
            SpacePose own = active.shipContainerPose();
            return InWindow(own, active.Reference(own), active.Target.Vessel);
        }

        internal static bool IsValid(UniverseManager universe, DriftSyncPlan active)
        {
            SpaceObject host = active.host;
            return active.Target.IsValid && active.sourceMap?.Disposed == false && active.Target.Map == active.sourceMap
                && universe.FrameOf(active.sourceMap) != null
                && host != null && host.RepresentationVersion == active.hostVersion
                && active.targetMap?.Disposed == false && host.map == active.targetMap
                && universe.FrameOf(active.targetMap) == host
                && !host.IsScattered && !host.HasTag(SpaceObjectTag.Projectile)
                && active.Distance() <= DeltaVMod.CurrentSettings.dockingCancelDistance;
        }

        // Replaces the existing approach task tick; no discovery or map scan here.
        public void Tick(UniverseManager universe)
        {
            if (plan == null) return;
            if (!IsValid(universe, plan))
            {
                CancelSelectionOrPlan(universe, "sync target lost");
                return;
            }
            plan.holdTicks = InSyncWindow(plan) ? Mathf.Min(plan.holdTicks + 1, Mathf.Max(1, DeltaVMod.CurrentSettings.dockingHoldTicks)) : 0;
        }

        internal static bool LandingSiteStillValid(DockingPlan active)
        {
            if (active == null || !active.Target.IsValid || active.targetMap?.Disposed != false) return false;
            Map original = Find.CurrentMap;
            try
            {
                Current.Game.CurrentMap = active.targetMap;
                Gravship preview = GravshipInterop.BuildPreview(active.Target);
                if (preview == null) return false;
                preview.Rotation = active.landingRotation;
                foreach (IntVec3 cell in ShipPocketMapFactory.PlacedBounds(preview))
                    if (!(cell + active.landingCell).InBounds(active.targetMap)) return false;
                var marker = (GravshipLandingMarker)ThingMaker.MakeThing(ThingDefOf.GravshipLandingMarker);
                marker.gravship = preview;
                marker.CacheCells();
                bool shuttle = active.Target.Vessel is ShuttleShip;
                if (shuttle) MoonPoolUtility.BeginShuttlePassage();
                try
                {
                    return GravshipInterop.CanDesignateRoot(new Designator_MoveGravship(active.targetMap, marker),
                        active.landingCell, preview.Bounds.Size, active.landingRotation).Accepted;
                }
                finally { if (shuttle) MoonPoolUtility.EndShuttlePassage(); }
            }
            finally { Current.Game.CurrentMap = original; }
        }

        public void CancelSelectionOrPlan(UniverseManager universe, string reason = null, bool silent = false)
        {
            PilotTarget target = ApproachTarget;
            DriftSyncPlan previous = plan;
            plan = null;
            if (target.IsValid)
            {
                universe?.Autopilot.DetachReference(DriveSubject.Of(target), previous.reference);
                universe?.Controls.PlayerFor(target.Vessel)?.ClearAutoTask(PlayerAutoTaskKind.Sync);
            }
            if (!silent) Messages.Message("DeltaV_DockPlanCancelled".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        public void ResetFlow(UniverseManager universe) => CancelSelectionOrPlan(universe, silent: true);
    }
}

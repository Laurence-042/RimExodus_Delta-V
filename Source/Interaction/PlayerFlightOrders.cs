using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace DeltaV
{
    internal enum PlayerAutoTaskKind : byte
    {
        Drift,
        Sync,
        Combat,
        Navigate,
        SoftDock
    }

    internal readonly struct PlayerNavigationTarget
    {
        internal readonly Vector3 Position;
        internal readonly float Angle;
        internal readonly int ReferenceObjectId;
        internal readonly Vector2 ReferenceOffset;
        internal readonly bool LongRange;
        internal readonly bool TranslationOnly;
        internal bool Moving => ReferenceObjectId > 0;

        internal PlayerNavigationTarget(Vector3 position, float angle,
            int referenceObjectId = 0, Vector2 referenceOffset = default)
            : this(position, angle, referenceObjectId, referenceOffset, false)
        {
        }

        internal PlayerNavigationTarget(Vector3 position, float angle,
            int referenceObjectId, Vector2 referenceOffset, bool longRange)
            : this(position, angle, referenceObjectId, referenceOffset, longRange, false)
        {
        }

        internal PlayerNavigationTarget(Vector3 position, float angle,
            int referenceObjectId, Vector2 referenceOffset, bool longRange,
            bool translationOnly)
        {
            Position = position;
            Angle = SpaceTransform.WrapAngle(angle);
            ReferenceObjectId = referenceObjectId;
            ReferenceOffset = referenceOffset;
            LongRange = longRange;
            TranslationOnly = translationOnly;
        }

        internal bool Valid(UniverseManager universe, DriveSubject owner)
        {
            if (!Moving) return true;
            SpaceObject reference = universe.ObjectById(ReferenceObjectId);
            return reference != null && reference.objectId != owner.Resolve(universe)?.objectId
                && !reference.IsTransient && !reference.IsScattered;
        }

        internal SpacePose Reference(UniverseManager universe)
        {
            if (!Moving) return new SpacePose(Position, Angle);
            SpaceObject host = universe.ObjectById(ReferenceObjectId);
            if (TranslationOnly)
            {
                // Star-map perimeter anchors follow the target origin's translation but
                // never its spin. A rigid local point would orbit a rotating target and,
                // after arrival, leave the ship with tangential velocity but no centripetal
                // control. Position is the target origin at confirmation; the offset is
                // stored in that tangent frame and transported with the moving origin.
                Vector2 offset = SpaceTransform.TransportTangent(ReferenceOffset,
                    Position, host.pose.position);
                Vector3 point = OrbitSphere.Move(host.pose.position, offset);
                return new SpacePose(point, Angle)
                {
                    velocity = SpaceTransform.TransportVelocity(host.pose, point),
                    angularVelocity = 0f
                };
            }
            SpacePose pose = SpaceTransform.RigidPointPose(host.pose, ReferenceOffset, 0f);
            pose.angle = Angle; // The dragged heading stays absolute as the host rotates.
            pose.angularVelocity = 0f;
            return pose;
        }
    }

    public sealed partial class PlayerShipController
    {
        private PlayerAutoTaskKind autoTask;
        private PlayerNavigationTarget navigationTarget;
        private int taskRevision;
        internal PlayerAutoTaskKind AutoTask => autoTask;
        internal bool ManualControl => target.IsValid && target.Identity == OwnerId;
        internal string AutoTaskLabel => TaskName(autoTask);

        private static string TaskName(PlayerAutoTaskKind task) => task switch
        {
            PlayerAutoTaskKind.Sync => "DeltaV_AutoSync".Translate(),
            PlayerAutoTaskKind.Combat => "DeltaV_AutoCombat".Translate(),
            PlayerAutoTaskKind.Navigate => "DeltaV_AutoNavigate".Translate(),
            PlayerAutoTaskKind.SoftDock => "DeltaV_AutoSoftDock".Translate(),
            _ => "DeltaV_AutoDrift".Translate()
        };

        internal string FlightStatusLabel
        {
            get
            {
                string mode = (ManualControl ? "DeltaV_ControlManual" : "DeltaV_ControlAutopilot").Translate();
                if (autoTask == PlayerAutoTaskKind.Drift)
                    return ManualControl ? mode : mode + " · " + AutoTaskLabel;
                string progress = AutoTaskProgress();
                return mode + " · " + AutoTaskLabel + " · " + progress;
            }
        }

        private string AutoTaskProgress()
        {
            if (ManualControl) return "DeltaV_AutoPaused".Translate();
            if (autoTask == PlayerAutoTaskKind.Sync)
            {
                var plan = universe.Approach.ActivePlan;
                if (plan?.Target.Identity == OwnerId)
                    return universe.Approach.Ready ? "DeltaV_AutoReady".Translate()
                        : "DeltaV_AutoStabilizing".Translate(plan.holdTicks,
                            Mathf.Max(1, DeltaVMod.CurrentSettings.dockingHoldTicks));
            }
            if (autoTask == PlayerAutoTaskKind.Combat
                && !universe.Autopilot.IsCombatOrbit(OwnerId))
                return "DeltaV_AutoSearching".Translate();
            if (autoTask == PlayerAutoTaskKind.SoftDock)
                return universe.SoftDock.SteeringStatus(OwnerId);
            if (autoTask == PlayerAutoTaskKind.Navigate && navigationTarget.Valid(universe, OwnerId))
            {
                ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
                SpaceObject own = ship.IsValid ? universe.FrameOf(ship.Map) : null;
                if (own != null)
                    return "DeltaV_AutoDistance".Translate(
                        OrbitSphere.ArcCells(own.pose.position,
                            navigationTarget.Reference(universe).position).ToString("F1"));
            }
            return "DeltaV_AutoRunning".Translate();
        }

        internal void ShowAutoTaskMenu(PilotTarget panelTarget)
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption(TaskName(PlayerAutoTaskKind.Drift),
                    () => universe.Controls.SelectPlayerAutoTask(panelTarget.Vessel,
                        PlayerAutoTaskKind.Drift)),
                new FloatMenuOption(TaskName(PlayerAutoTaskKind.Combat),
                    () => universe.Controls.SelectPlayerAutoTask(panelTarget.Vessel,
                        PlayerAutoTaskKind.Combat)),
                new FloatMenuOption(TaskName(PlayerAutoTaskKind.Navigate),
                    () => universe.Pilot.BeginNavigationTargeting(panelTarget))
            };
            if (panelTarget.Vessel.State == ShipDerivedState.FreeFlight)
            {
                options.Insert(1, new FloatMenuOption(TaskName(PlayerAutoTaskKind.Sync),
                    () => universe.Worksites.SelectSyncTarget(panelTarget)));
                if (!panelTarget.Identity.shuttle)
                    options.Insert(2, new FloatMenuOption(TaskName(PlayerAutoTaskKind.SoftDock),
                        () => universe.SoftDock.SelectTarget(panelTarget)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        internal void ReplaceAutoTask(PlayerAutoTaskKind next, PlayerNavigationTarget navigation = default,
            bool activate = true)
        {
            universe.Pilot.EndNavigationTargeting();
            ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
            if (autoTask == PlayerAutoTaskKind.Sync)
                universe.Approach.CancelSelectionOrPlan(universe, silent: true);
            if (autoTask == PlayerAutoTaskKind.SoftDock)
                universe.SoftDock.StopSteeringFor(OwnerId);
            if (autoTask == PlayerAutoTaskKind.Combat) AutoManeuver.Clear(ship);
            universe.Autopilot.Detach(OwnerId, "player auto task replaced");
            autoTask = next;
            navigationTarget = navigation;
            taskRevision++;
            if (activate && !ManualControl) ResumeAutoTask();
        }

        internal void ClearAutoTask(PlayerAutoTaskKind expected)
        {
            if (autoTask != expected) return;
            autoTask = PlayerAutoTaskKind.Drift;
            navigationTarget = default;
            taskRevision++;
            universe.Autopilot.Detach(OwnerId, "player auto task ended");
        }

        internal void ResumeAutoTask()
        {
            if (ManualControl) return;
            ShipBase ship = ShipRegistry.ShipByIdentity(OwnerId);
            if (!ship.IsValid || !CanEnter(PilotTarget.Of(ship)))
            {
                if (autoTask == PlayerAutoTaskKind.SoftDock)
                    universe.SoftDock.StopSteeringFor(OwnerId);
                else ReplaceAutoTask(PlayerAutoTaskKind.Drift, activate: false);
                return;
            }
            switch (autoTask)
            {
                case PlayerAutoTaskKind.Sync:
                    var plan = universe.Approach.ActivePlan;
                    if (plan?.Target.Identity == OwnerId
                        && LandingApproachController.IsValid(universe, plan))
                        universe.Approach.EngageAutopilot(universe);
                    else FailAutoTask(PlayerAutoTaskKind.Sync);
                    break;
                case PlayerAutoTaskKind.Combat:
                    DecideAutoCombat(ship);
                    break;
                case PlayerAutoTaskKind.Navigate:
                    if (!navigationTarget.Valid(universe, OwnerId))
                    {
                        FailAutoTask(PlayerAutoTaskKind.Navigate, "DeltaV_NavigationTargetLost");
                        break;
                    }
                    int revision = taskRevision;
                    PlayerNavigationTarget goal = navigationTarget;
                    universe.Autopilot.NavigateToReference(OwnerId,
                        _ => goal.Reference(universe), () => goal.Valid(universe, OwnerId),
                        () =>
                        {
                            if (taskRevision != revision) return;
                            ClearAutoTask(PlayerAutoTaskKind.Navigate);
                            Messages.Message("DeltaV_NavigationArrived".Translate(),
                                MessageTypeDefOf.PositiveEvent, false);
                        },
                        reason =>
                        {
                            if (taskRevision == revision)
                                FailAutoTask(PlayerAutoTaskKind.Navigate, reason);
                        }, goal.LongRange);
                    break;
                case PlayerAutoTaskKind.SoftDock:
                    universe.SoftDock.ResumeSteeringFor(OwnerId);
                    break;
            }
        }

        private void FailAutoTask(PlayerAutoTaskKind expected, string reason = null)
        {
            if (autoTask != expected) return;
            ReplaceAutoTask(PlayerAutoTaskKind.Drift, activate: false);
            Messages.Message((reason ?? "DeltaV_AutoTaskLost").Translate(),
                MessageTypeDefOf.NeutralEvent, false);
        }
    }
}

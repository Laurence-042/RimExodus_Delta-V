namespace DeltaV
{
    internal enum BrakingRecoveryReason { None, NoBrakingAuthority, InfeasibleApproach, SolverFailure }

    // Per-flight, unsaved recovery orchestration. Both modes use the same guidance instance.
    internal sealed class BrakingRecoveryState
    {
        internal const int ControlTicks = 24;
        internal const int RetryTicks = 60;
        internal bool Active { get; private set; }
        internal BrakingRecoveryReason Reason { get; private set; }
        internal int NextApproachTick { get; private set; }
        internal int NextControlTick { get; private set; }
        internal bool Fallback { get; private set; }

        internal bool ApproachDue(int tick, bool changed) => Active && (changed || tick >= NextApproachTick);
        internal bool ControlDue(int tick, bool changed) => changed || tick >= NextControlTick;
        internal void Enter(int tick, BrakingRecoveryReason reason)
        {
            Active = true;
            Reason = reason;
            NextApproachTick = tick + RetryTicks;
            NextControlTick = tick;
            Fallback = false;
        }
        internal void ApproachAttempted(int tick) => NextApproachTick = tick + RetryTicks;
        internal void ControlAttempted(int tick, bool solved)
        {
            Fallback = !solved;
            NextControlTick = tick + (solved ? ControlTicks : RetryTicks);
        }
        internal void Exit()
        {
            Active = Fallback = false;
            Reason = BrakingRecoveryReason.None;
            NextApproachTick = NextControlTick = 0;
        }

        internal bool Compute(IAutopilotGuidance guidance, AutopilotMpcProfile profile,
            GuidanceRequest request, DriveInfo report, SpacePose pose, int tick, bool changed,
            bool eligible, out GuidanceCommand command, out GuidanceCommand approach)
        {
            bool retry = ApproachDue(tick, changed);
            var localVelocity = request.referencePosition.sqrMagnitude > 0f
                ? SpaceTransform.TransportTangent(request.relativeVelocity, request.referencePosition, pose.position)
                : request.relativeVelocity;
            localVelocity = SpaceTransform.RotateInverse(localVelocity, pose.angle);
            bool solved = false;
            approach = default;
            if (!Active || retry)
            {
                if (retry) ApproachAttempted(tick);
                // Crossing the target reverses the position direction, not the velocity.
                // Do not mistake thrust towards the old travel direction for braking authority.
                bool noBraking = eligible && (request.brakingAcceleration * BrakingGuidanceFallback.SafetyFactor <= 1e-6
                    || request.relativeVelocity.sqrMagnitude > 1e-8f
                    && report.PreviewBraking(localVelocity.normalized)
                        * BrakingGuidanceFallback.SafetyFactor <= 1e-6);
                if (!noBraking) solved = guidance.Compute(request, out approach);
                bool stillReceding = UnityEngine.Vector2.Dot(request.relativeVelocity, request.positionError) < 0
                    && request.relativeVelocity.sqrMagnitude > .15f * .15f;
                // A feasible stop plane after an overshoot does not mean braking is done.
                // Keep speed-based progress until the ship can begin approaching again.
                if (solved && Active && !stillReceding) Exit();
                else if (!solved && eligible && !Active)
                {
                    Enter(tick, noBraking ? BrakingRecoveryReason.NoBrakingAuthority
                        : approach.qpStatus == QpSolveStatus.ProvenInfeasible ? BrakingRecoveryReason.InfeasibleApproach
                        : BrakingRecoveryReason.SolverFailure);
                    guidance.Reset(profile);
                }
            }
            command = approach;
            if (Active)
            {
                using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.MpcBrakeRecovery);
                request.motionOnly = true;
                request.accuracy = GuidanceAccuracy.Strict;
                request.voidBlend = 0;
                request.approachReserveDistance = 0;
                request.brakingAcceleration = report.PreviewBraking(localVelocity.sqrMagnitude > 1e-8f
                    ? localVelocity.normalized : UnityEngine.Vector2.zero);
                ShipAutopilot.BuildControlAcceleration(pose, request.controlModel, 0f, request.controlAcceleration, request.referencePosition);
                if (!Fallback || ControlDue(tick, changed))
                {
                    solved = guidance.Compute(request, out command);
                    command.recoverySolves = 1;
                    ControlAttempted(tick, solved);
                }
                else
                {
                    BrakingGuidanceFallback.Compute(request, out command);
                    solved = false;
                }
                command.qpCalls += approach.qpCalls;
                command.qpIterations += approach.qpIterations;
                command.qpIterationLimit += approach.qpIterationLimit;
                command.infeasiblePrechecks += approach.infeasiblePrechecks;
                command.feasibilityChecks += approach.feasibilityChecks;
                command.solveMilliseconds += approach.solveMilliseconds;
            }
            if (retry) command.approachRetries = 1;
            return solved;
        }
    }
}

using UnityEngine;
using Verse;
using System.Linq;

namespace DeltaV
{
    internal sealed partial class ShipAutoManeuverControl
    {
        // Observation only. Cleared with the controller; never participates in a decision or save.
        private string lastAiDecisionState;

        internal string EngagementDecisionDetails() => engagementResult == null || activeRule == null ? ""
            : CombatDecisionText.Engagement(engagementResult, activeRule.engagement, reposition.selectedScore);

        private void TraceAiDecision(ShipBase ship, SpaceObject own, SpacePose self, SpacePose target,
            CombatStrategyRule rule, CombatTacticSettings settings, CombatReplanCause cause, bool reset,
            CombatRepositionStatus status, float oldPhase, int oldNextTick, bool poseReevaluated)
        {
            Vector2 offset = OrbitSphere.Offset(target.position, self.position);
            Vector2 relative = SpaceTransform.TransportVelocity(self, target.position) - target.velocity;
            float phase = Mathf.Atan2(offset.y, offset.x), goal = reposition.PhaseAt(target.position);
            float error = SpaceTransform.WrapAngle(goal - phase);
            float radialSpeed = Vector2.Dot(relative, offset.normalized);
            float tangentSpeed = Vector2.Dot(relative, new Vector2(-offset.y, offset.x).normalized);
            float? flightHeading = !tacticIntent.holdCurrent || tacticIntent.brakeTarget.HasValue
                ? SpaceTransform.WrapAngle(-tacticIntent.frameAngle) * Mathf.Rad2Deg : (float?)null;
            string headingMode = tacticIntent.brakeTarget.HasValue ? "AimingBrake" :
                tacticIntent.holdCurrent ? "Brake" : "Tracking";
            int now = Find.TickManager.TicksGame;
            // Log meaningful motion/state edges, not every planner invocation or game tick.
            string state = $"{anchorTarget.Ship}/{rule.name}/{attackPose.behavior}/{status}/{reset}/"
                + $"{cause & ~CombatReplanCause.Periodic}/{reposition.nextTick}/{blockedPose}/"
                + $"{Mathf.RoundToInt(offset.magnitude / 5f)}/{Mathf.RoundToInt(attackPose.distance)}/"
                + $"{Mathf.RoundToInt(error * Mathf.Rad2Deg / 2f)}/{Mathf.RoundToInt(attackPose.heading * Mathf.Rad2Deg / 5f)}/"
                + $"{headingMode}/{attackPose.fireHeading}/{flightHeading:F1}/"
                + $"{Mathf.RoundToInt(radialSpeed)}/{Mathf.RoundToInt(tangentSpeed)}";
            if (state == lastAiDecisionState) return;
            lastAiDecisionState = state;
            tacticIntent.Sample(0f, out _, out var referenceVelocity, out float referenceAngle, out float referenceOmega);
            tacticIntent.Sample(tacticIntent.seconds, out _, out var terminalVelocity, out _, out float terminalOmega);
            DeltaVLog.Message(DeltaVLogModule.AI,
                $"maneuver tick={now} ship={ownerId} object={own.objectId} {UniverseManager.MapTag(ship.Map)} "
                + $"target={anchorTarget.Ship} rule='{rule.name}' behavior={attackPose.behavior} offense={offenseEnabled} "
                + $"cause={cause} reset={reset} driveVersion={tacticDriveVersion} structureVersion={tacticStructureVersion} "
                + $"range={offset.magnitude:F2}->{attackPose.distance:F2} bearing={settings.bearing} "
                + $"phaseDeg={phase * Mathf.Rad2Deg:F2}->{goal * Mathf.Rad2Deg:F2} oldPreviewDeg={oldPhase * Mathf.Rad2Deg:F2} "
                + $"previewArc={error * offset.magnitude:F2} intentSpeed={maneuverIntent.tangentSpeed:F3} radialSpeed={radialSpeed:F3} tangentSpeed={tangentSpeed:F3} "
                + $"reposition={status} nextTick={oldNextTick}->{reposition.nextTick} waitTicks={Mathf.Max(0, reposition.nextTick - now)} "
                + $"candidates={reposition.candidateCount} choiceScore={reposition.selectedScore:F3} continuationScore={reposition.continuationScore:F3} "
                + $"velocityCost={reposition.selectedVelocityCost:F3} reversalCost={reposition.selectedReversalCost:F3} "
                + (engagementResult == null ? "" : $"outgoingDps={engagementResult.outgoing:F2} incomingDps={engagementResult.incoming:F2} "
                    + $"arrivalSeconds={engagementResult.travel:F2} velocitySeconds={engagementResult.velocity:F2} "
                    + $"reverseSeconds={engagementResult.reverse:F2} uncertain={engagementResult.uncertain} "
                    + $"hold={engagementResult.holding} "
                    + $"alternativeScore={engagementResult.alternativeScore:F3} terms=["
                    + string.Join(";", rule.engagement.terms.Select(t =>
                    {
                        float raw = engagementResult.Raw(t);
                        float curve = t.curve.Evaluate(raw);
                        return $"{t.side}/{t.metric}/{t.weapons.family}:raw={raw:F3},curve={curve:F3},weight={t.weight:F3},score={curve * t.weight:F3}";
                    })) + "] ")
                + $"headingDeg={attackPose.heading * Mathf.Rad2Deg:F2} commandHeadingDeg={maneuverIntent.heading * Mathf.Rad2Deg:F2} "
                + $"flightHeadingDeg={flightHeading?.ToString("F2") ?? "none"} fireHeading={attackPose.fireHeading} headingMode={headingMode} "
                + (SharedFixedAim is FixedWeaponAim aim
                    ? $"fixedWeapon={aim.Weapon.Identity} fixedPart={aim.Target.SystemId} leadTick={aim.Tick} "
                        + $"lead=({aim.Point.x:F7},{aim.Point.y:F7},{aim.Point.z:F7}) hullAimDeg={aim.HullAngle * Mathf.Rad2Deg:F2} " : "")
                + $"mobility={attackPose.mobility:F3} movement={settings.movement:F2} "
                + $"coverage={attackPose.firepower:F2} ready={attackPose.readyOutput:F2} preference={attackPose.preference:F3} "
                + $"scores={attackPose.distanceScore:F2}/{attackPose.outgoingScore:F2}/{attackPose.incomingScore:F2}/"
                + $"{attackPose.timeScore:F2}/{attackPose.overlapScore:F2}/{attackPose.alignmentScore:F2} "
                + $"blocked={blockedPose} poseReevaluated={poseReevaluated} "
                + $"radialStep={tacticIntent.radialStep:F3} phaseAdvanceDeg={tacticIntent.phaseAdvance * Mathf.Rad2Deg:F3} "
                + $"ownVelocity=({self.velocity.x:F3},{self.velocity.y:F3}) targetVelocity=({target.velocity.x:F3},{target.velocity.y:F3}) "
                + $"referenceVelocity=({referenceVelocity.x:F3},{referenceVelocity.y:F3}) referenceAngleDeg={referenceAngle * Mathf.Rad2Deg:F2} "
                + $"ownAngleDeg={self.angle * Mathf.Rad2Deg:F2} targetAngleDeg={target.angle * Mathf.Rad2Deg:F2} "
                + $"shipFrameAngleDeg={ship.FrameAngle * Mathf.Rad2Deg:F2} ownOmega={self.angularVelocity:F4} targetOmega={target.angularVelocity:F4} "
                + $"terminalVelocity=({terminalVelocity.x:F3},{terminalVelocity.y:F3}) terminalOmega={terminalOmega:F4} "
                + $"referenceOmega={referenceOmega:F4} ownPosition=({self.position.x:F7},{self.position.y:F7},{self.position.z:F7}) "
                + $"targetPosition=({target.position.x:F7},{target.position.y:F7},{target.position.z:F7})");
            universe.Autopilot.RequestAiTrace(ownerId);
        }
    }
}

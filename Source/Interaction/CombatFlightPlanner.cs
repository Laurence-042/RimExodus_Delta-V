using UnityEngine;

namespace DeltaV
{
    // Controller-owned intent. A preview bearing is not an arrival condition.
    internal struct CombatManeuverIntent
    {
        internal CombatTactic behavior;
        internal float distance, heading, tangentSpeed;
        internal float? bearing;
        internal float bearingRate;
    }

    internal static class CombatFlightPlanner
    {
        internal static CombatTrajectoryIntent Brake(SpacePose own, DriveInfo drive, int tick)
        {
            float acceleration = drive?.PreviewBraking(SpaceTransform.RotateInverse(own.velocity, own.angle)) ?? 0f;
            float alpha = drive?.PreviewAngular(own.angularVelocity > 0f ? -1 : 1) ?? 0f;
            return new CombatTrajectoryIntent { holdCurrent = true, center = own.position,
                frameAngle = own.angle, holdVelocity = own.velocity, holdOmega = own.angularVelocity,
                brakingDecay = acceleration * .8f / Mathf.Max(.1f, own.velocity.magnitude),
                brakingAngularDecay = alpha * .8f / Mathf.Max(.01f, Mathf.Abs(own.angularVelocity)),
                seconds = 4f, startTick = tick };
        }

        internal static CombatTrajectoryIntent BrakeWhileAiming(SpacePose own, SpacePose target, float heading,
            DriveInfo drive, int tick)
        {
            var intent = Brake(own, drive, tick);
            intent.brakeTarget = target;
            intent.frameAngle = -heading;
            float bearingRate = intent.BrakeBearingRate(0f);
            intent.headingError = SpaceTransform.WrapAngle(own.angle - intent.BrakeBearing(0f, own.position) - heading);
            intent.headingRateError = own.angularVelocity - bearingRate;
            float alpha = Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            intent.headingDecay = HeadingDecay(alpha, intent.headingError, intent.headingRateError);
            return intent;
        }

        private static float HeadingDecay(float alpha, float error, float rate) =>
            Mathf.Min(1.5f, Mathf.Sqrt(alpha * .5f / Mathf.Max(.01f, Mathf.Abs(error))),
                alpha * .5f / Mathf.Max(.01f, 2f * Mathf.Abs(rate)));

        internal static CombatTrajectoryIntent RetargetHeading(CombatTrajectoryIntent intent,
            SpacePose own, float heading, DriveInfo drive, float elapsed)
        {
            // Sample the bearing of the unchanged translation curve with angular correction removed.
            var bearing = intent;
            bearing.localAnchor = Vector2.zero;
            bearing.frameAngle = bearing.headingError = bearing.headingRateError = 0f;
            bearing.Sample(elapsed, out _, out _, out float angle, out float omega);

            float error = SpaceTransform.WrapAngle(own.angle - angle - heading);
            float rate = own.angularVelocity - omega;
            float alpha = Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            float decay = HeadingDecay(alpha, error, rate);
            // Rebase the angular solution to the existing trajectory clock so translation stays continuous.
            float factor = Mathf.Exp(decay * elapsed);
            float linear = (rate + decay * error) * factor;
            intent.headingError = error * factor - linear * elapsed;
            intent.headingRateError = linear - decay * intent.headingError;
            intent.headingDecay = decay;
            intent.frameAngle = -heading;
            return intent;
        }

        internal static float CruiseSpeed(DriveInfo drive, float heading, float radius, int turn, float movement, bool freeFacing = false)
        {
            float tangent = CombatAttackPose.Acceleration(drive, new Vector2(0f, -turn), heading);
            float inward = CombatAttackPose.Acceleration(drive, Vector2.right, heading);
            if (freeFacing)
            {
                foreach (Vector2 axis in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
                    tangent = Mathf.Max(tangent, CombatAttackPose.Acceleration(drive, axis, 0f));
                inward = tangent;
            }
            float angular = drive == null ? 0f : Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            return Mathf.Min(tangent * 6f, Mathf.Sqrt(Mathf.Max(0f, inward * radius * .35f)),
                radius * Mathf.Sqrt(Mathf.Max(0f, angular * .5f))) * movement;
        }

        internal static CombatTrajectoryIntent Plan(SpacePose own, SpacePose target,
            CombatManeuverIntent action, DriveInfo drive, int tick)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.CombatTrajectory);
            if (drive == null || drive.AvailableThrust <= .001f) return Brake(own, drive, tick);
            Vector2 offset = OrbitSphere.Offset(target.position, own.position);
            float radius = Mathf.Max(1f, offset.magnitude);
            Vector2 outward = offset.normalized, tangent = new Vector2(-outward.y, outward.x);
            Vector2 relative = SpaceTransform.TransportVelocity(own, target.position) - target.velocity;
            float phase = Mathf.Atan2(offset.y, offset.x);
            Vector2 line = OrbitSphere.Offset(own.position, target.position);
            float headingError = SpaceTransform.WrapAngle(own.angle - Mathf.Atan2(line.y, line.x) - action.heading);
            float alpha = drive == null ? 0f : Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            float turnTime = Mathf.Sqrt(4f * Mathf.Abs(headingError) / Mathf.Max(.001f, alpha));
            float radialSpeed = Vector2.Dot(relative, outward);
            float phaseRate = Vector2.Dot(relative, tangent) / radius;
            float actualHeading = SpaceTransform.WrapAngle(own.angle - Mathf.Atan2(line.y, line.x));
            float radialCapability = Mathf.Min(CombatAttackPose.Acceleration(drive, Vector2.right, actualHeading),
                CombatAttackPose.Acceleration(drive, Vector2.left, actualHeading));
            float radialError = radius - action.distance;
            // A critically damped radius reference starts at the actual radial velocity.
            // Reserve acceleration for centripetal motion and limit the initial transient.
            float radialDecay = Mathf.Min(.7f, Mathf.Sqrt(radialCapability * .9f / Mathf.Max(1f, Mathf.Abs(radialError))),
                radialCapability * .95f / Mathf.Max(.01f, 2f * Mathf.Abs(radialSpeed)));
            float speed = action.tangentSpeed;
            if (action.bearing.HasValue)
            {
                float arc = SpaceTransform.WrapAngle(action.bearing.Value - phase) * radius;
                float a = Mathf.Min(CombatAttackPose.Acceleration(drive, Vector2.up, action.heading),
                    CombatAttackPose.Acceleration(drive, Vector2.down, action.heading));
                speed = Mathf.Clamp(arc * .25f, -Mathf.Sqrt(2f * a * Mathf.Abs(arc)), Mathf.Sqrt(2f * a * Mathf.Abs(arc)))
                    + action.bearingRate * radius;
            }
            float actualTangent = CombatAttackPose.Acceleration(drive,
                new Vector2(0f, speed / Mathf.Max(1f, action.distance) >= phaseRate ? -1f : 1f), actualHeading);
            float speedDecay = Mathf.Min(1f / Mathf.Max(2f, turnTime), actualTangent * .5f
                / Mathf.Max(.1f, Mathf.Abs(speed * radius / Mathf.Max(1f, action.distance) - phaseRate * radius)));
            float headingDecay = HeadingDecay(alpha, headingError, own.angularVelocity - phaseRate);
            var trajectory = new CombatTrajectoryIntent
            {
                startTick = tick, seconds = 4f, center = target.position, centerVelocity = target.velocity,
                startRadius = radius, targetRadius = Mathf.Max(1f, action.distance), initialRadialSpeed = radialSpeed,
                radialDecay = radialDecay, startPhase = phase, initialPhaseRate = phaseRate,
                targetPhaseRate = speed / Mathf.Max(1f, action.distance), speedDecay = speedDecay,
                frameAngle = -action.heading, headingError = headingError,
                headingRateError = own.angularVelocity - phaseRate, headingDecay = headingDecay
            };
            trajectory.headingRateError = own.angularVelocity - trajectory.BearingRate(0f);
            return trajectory;
        }
    }
}

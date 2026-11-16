using UnityEngine;

namespace DeltaV
{
    /// <summary>飞行规划给共同自动驾驶器的滚动时域参考；角度均为 universe 切基角。</summary>
    internal struct CombatTrajectoryIntent
    {
        internal float seconds;
        internal int startTick;
        internal Vector3 center;
        internal Vector2 centerVelocity;
        internal float startRadius;
        internal float targetRadius, initialRadialSpeed, radialDecay;
        internal float initialPhaseRate, targetPhaseRate, speedDecay;
        internal float headingError, headingRateError, headingDecay;
        internal float yawAuthority;
        internal float radialStep { get { Radius(seconds, out float r, out _); return r - startRadius; } }
        internal float phaseAdvance { get { Phase(seconds, out float p, out _); return p - startPhase; } }
        internal float startPhase;
        internal float frameAngle;
        internal bool holdCurrent;
        internal SpacePose? brakeTarget;
        internal Vector2 holdVelocity;
        internal float holdOmega, brakingDecay, brakingAngularDecay;
        internal Vector2 localAnchor;

        private static void Decay(float error, float speed, float rate, float time, out float value, out float derivative)
        {
            float b = speed + rate * error;
            float e = Mathf.Exp(-rate * time);
            value = (error + b * time) * e;
            derivative = (speed - rate * b * time) * e;
        }
        private void Radius(float time, out float radius, out float speed)
        {
            Decay(startRadius - targetRadius, initialRadialSpeed, radialDecay, time, out float error, out speed);
            radius = targetRadius + error;
            if (radius < .1f) { radius = .1f; speed = 0f; }
        }
        internal Vector2 RadiusBounds(float from, float until)
        {
            Radius(from, out float first, out _);
            Radius(until, out float last, out _);
            float lower = Mathf.Min(first, last), upper = Mathf.Max(first, last);
            float slope = radialDecay * (initialRadialSpeed + radialDecay * (startRadius - targetRadius));
            if (slope != 0f)
            {
                float turningPoint = initialRadialSpeed / slope;
                if (turningPoint > from && turningPoint < until)
                {
                    Radius(turningPoint, out float extreme, out _);
                    lower = Mathf.Min(lower, extreme);
                    upper = Mathf.Max(upper, extreme);
                }
            }
            return new Vector2(lower, upper);
        }
        private void Phase(float time, out float phase, out float rate)
        {
            if (speedDecay <= 0f) { rate = initialPhaseRate; phase = startPhase + rate * time; return; }
            float decay = speedDecay;
            float e = Mathf.Exp(-decay * time);
            rate = targetPhaseRate + (initialPhaseRate - targetPhaseRate) * e;
            phase = startPhase + targetPhaseRate * time + (initialPhaseRate - targetPhaseRate) * (1f - e) / decay;
        }

        private float Bearing(float time)
        {
            Phase(time, out float phase, out _);
            Radius(time, out float radius, out _);
            Vector3 at = OrbitSphere.Move(center, centerVelocity * time);
            Vector2 radial = SpaceTransform.TransportTangent(new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)), center, at);
            Vector2 toCenter = OrbitSphere.Offset(OrbitSphere.Move(at, radial * radius), at);
            return Mathf.Atan2(toCenter.y, toCenter.x);
        }
        // Differentiate the same spherical bearing, including tangent-basis rotation near poles.
        // This is reference geometry, not a second motion integrator.
        internal float BearingRate(float time) => SpaceTransform.WrapAngle(Bearing(time + .05f) - Bearing(time - .05f)) / .1f;

        private Vector3 BrakePosition(float time, float linearFactor) => OrbitSphere.Move(center, holdVelocity *
            (brakingDecay > .00001f ? (1f - linearFactor) / brakingDecay : time));

        internal float BrakeBearing(float time, Vector3 position)
        {
            var target = brakeTarget.Value;
            var direction = OrbitSphere.Offset(position, OrbitSphere.Move(target.position, target.velocity * time));
            return Mathf.Atan2(direction.y, direction.x);
        }

        internal float BrakeBearingRate(float time)
        {
            const float step = .05f;
            float before = time - step, after = time + step;
            float a = BrakeBearing(before, BrakePosition(before, Mathf.Exp(-brakingDecay * before)));
            float b = BrakeBearing(after, BrakePosition(after, Mathf.Exp(-brakingDecay * after)));
            return SpaceTransform.WrapAngle(b - a) / (2f * step);
        }

        private Vector3 OrbitPosition(float time, out Vector3 centerAtTime, out Vector2 outward,
            out float radius, out float radialSpeed, out float phaseRate)
        {
            Phase(time, out float phase, out phaseRate);
            Radius(time, out radius, out radialSpeed);
            outward = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
            centerAtTime = OrbitSphere.Move(center, centerVelocity * time);
            outward = SpaceTransform.TransportVelocity(new SpacePose(center, 0f) { velocity = outward }, centerAtTime);
            return OrbitSphere.Move(centerAtTime, outward * radius);
        }

        internal void SampleHeadingBasis(float time, out float angle, out float omega)
        {
            float t = Mathf.Max(0f, time);
            if (brakeTarget.HasValue)
            {
                angle = SpaceTransform.WrapAngle(BrakeBearing(t, BrakePosition(t, Mathf.Exp(-brakingDecay * t))));
                omega = BrakeBearingRate(t);
                return;
            }
            var position = OrbitPosition(t, out var centerAtTime, out _, out _, out _, out _);
            Vector2 bearing = OrbitSphere.Offset(position, centerAtTime);
            angle = SpaceTransform.WrapAngle(Mathf.Atan2(bearing.y, bearing.x));
            omega = BearingRate(t);
        }

        /// <summary>用统一球面坐标链采样整段预期位姿；MPC 各预测步读取同一曲线。</summary>
        internal void Sample(float time, out Vector3 position, out Vector2 velocity,
            out float angle, out float angularVelocity)
        {
            float t = Mathf.Max(0f, time);
            if (holdCurrent)
            {
                float linearFactor = Mathf.Exp(-brakingDecay * t);
                position = BrakePosition(t, linearFactor);
                velocity = SpaceTransform.TransportTangent(holdVelocity * linearFactor, center, position);
                if (brakeTarget.HasValue)
                {
                    Decay(headingError, headingRateError, headingDecay, t, out float brakeCorrection, out float brakeRate);
                    angle = SpaceTransform.WrapAngle(BrakeBearing(t, position) - frameAngle + brakeCorrection);
                    angularVelocity = BrakeBearingRate(t) + brakeRate;
                    ApplyAnchor(ref position, ref velocity, ref angle, ref angularVelocity);
                    return;
                }
                float angularFactor = Mathf.Exp(-brakingAngularDecay * t);
                float turn = brakingAngularDecay > .00001f ? (1f - angularFactor) / brakingAngularDecay : t;
                angle = SpaceTransform.WrapAngle(frameAngle + holdOmega * turn);
                angularVelocity = holdOmega * angularFactor; return;
            }
            position = OrbitPosition(t, out var centerAtTime, out var outward, out float radius,
                out float radialSpeed, out float phaseRate);
            Vector2 tangent = new Vector2(-outward.y, outward.x);
            var centerPose = new SpacePose(centerAtTime, 0f)
            {
                velocity = SpaceTransform.TransportVelocity(
                    new SpacePose(center, 0f) { velocity = centerVelocity }, centerAtTime)
                    + outward * radialSpeed
                    + tangent * (phaseRate * radius)
            };
            velocity = SpaceTransform.TransportVelocity(centerPose, position);
            Vector2 toCenter = OrbitSphere.Offset(position, centerAtTime);
            Decay(headingError, headingRateError, headingDecay, t, out float correction, out float correctionRate);
            angle = SpaceTransform.WrapAngle(Mathf.Atan2(toCenter.y, toCenter.x)
                - frameAngle + correction);
            angularVelocity = BearingRate(t) + correctionRate;
            ApplyAnchor(ref position, ref velocity, ref angle, ref angularVelocity);
        }

        private void ApplyAnchor(ref Vector3 position, ref Vector2 velocity, ref float angle,
            ref float angularVelocity)
        {
            if (localAnchor.sqrMagnitude > 1e-8f)
            {
                var anchored = SpaceTransform.AlignLocalPoint(new SpacePose(position, angle)
                    { velocity = velocity, angularVelocity = angularVelocity }, Vector2.zero, localAnchor, 0f);
                position = anchored.position; velocity = anchored.velocity;
                angle = anchored.angle; angularVelocity = anchored.angularVelocity;
            }
        }
    }

    internal delegate bool CombatTargetSampler(out SpacePose pose);

    internal delegate bool CombatTrajectoryPlanner(SpaceObject own, SpacePose target,
        float radius, DriveInfo report, out CombatTrajectoryIntent intent);
}

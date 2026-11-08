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
        internal float radialStep { get { Radius(seconds, out float r, out _); return r - startRadius; } }
        internal float phaseAdvance { get { Phase(seconds, out float p, out _); return p - startPhase; } }
        internal float startPhase;
        internal float frameAngle;
        internal bool holdCurrent;
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

        /// <summary>用统一球面坐标链采样整段预期位姿；MPC 各预测步读取同一曲线。</summary>
        internal void Sample(float time, out Vector3 position, out Vector2 velocity,
            out float angle, out float angularVelocity)
        {
            float t = Mathf.Max(0f, time);
            if (holdCurrent)
            {
                float linearFactor = Mathf.Exp(-brakingDecay * t), angularFactor = Mathf.Exp(-brakingAngularDecay * t);
                float travel = brakingDecay > .00001f ? (1f - linearFactor) / brakingDecay : t;
                float turn = brakingAngularDecay > .00001f ? (1f - angularFactor) / brakingAngularDecay : t;
                position = OrbitSphere.Move(center, holdVelocity * travel);
                velocity = SpaceTransform.TransportTangent(holdVelocity * linearFactor, center, position);
                angle = SpaceTransform.WrapAngle(frameAngle + holdOmega * turn);
                angularVelocity = holdOmega * angularFactor; return;
            }
            Phase(t, out float phase, out float phaseRate);
            Radius(t, out float radius, out float radialSpeed);
            Vector2 outward = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
            Vector3 centerAtTime = OrbitSphere.Move(center, centerVelocity * t);
            outward = SpaceTransform.TransportVelocity(new SpacePose(center, 0f) { velocity = outward }, centerAtTime);
            Vector2 tangent = new Vector2(-outward.y, outward.x);
            position = OrbitSphere.Move(centerAtTime, outward * radius);
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

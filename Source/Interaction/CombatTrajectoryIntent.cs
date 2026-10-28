using UnityEngine;

namespace DeltaV
{
    /// <summary>战术层给共同自动驾驶器的有限时域参考；角度均为 universe 切基角。</summary>
    internal struct CombatTrajectoryIntent
    {
        internal float seconds;
        internal bool authored;
        internal Vector3 startPosition;
        internal Vector2 startVelocity;
        internal float startAngle;
        internal float startAngularVelocity;
        internal Vector3 center;
        internal Vector2 centerVelocity;
        internal float startRadius;
        internal float radialStep;
        internal float startPhase;
        internal float frameAngle;
        internal Vector3 futurePosition;
        internal Vector2 futureVelocity;
        internal float futureAngle;
        internal float futureAngularVelocity;
        internal float pathAngle;
        internal float pathAngularVelocity;
        internal float phaseAdvance;

        /// <summary>用统一球面坐标链采样整段预期位姿；MPC 各预测步读取同一曲线。</summary>
        internal void Sample(float time, out Vector3 position, out Vector2 velocity,
            out float angle, out float angularVelocity)
        {
            if (authored)
            {
                float authoredTime = Mathf.Clamp(time, 0f, seconds);
                float u = seconds > 0f ? authoredTime / seconds : 0f;
                float u2 = u * u, u3 = u2 * u;
                Vector2 delta = OrbitSphere.Offset(startPosition, futurePosition);
                Vector2 v0 = startVelocity * seconds, v1 = futureVelocity * seconds;
                Vector2 offset = (2f * u3 - 3f * u2 + 1f) * Vector2.zero
                    + (u3 - 2f * u2 + u) * v0
                    + (-2f * u3 + 3f * u2) * delta
                    + (u3 - u2) * v1;
                position = OrbitSphere.Move(startPosition, offset);
                velocity = seconds > 0f ? ((3f * u2 - 4f * u + 1f) * v0
                    + (-6f * u2 + 6f * u) * delta + (3f * u2 - 2f * u) * v1) / seconds
                    : startVelocity;
                float rotation = SpaceTransform.WrapAngle(futureAngle - startAngle);
                float eased = u2 * (3f - 2f * u);
                angle = SpaceTransform.WrapAngle(startAngle + rotation * eased);
                angularVelocity = seconds > 0f ? rotation * 6f * u * (1f - u) / seconds : 0f;
                return;
            }
            float t = Mathf.Clamp(time, 0f, seconds);
            float portion = seconds > 0f ? t / seconds : 0f;
            float phase = startPhase + phaseAdvance * portion;
            float radius = Mathf.Max(0.1f, startRadius + radialStep * portion);
            Vector2 outward = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
            Vector2 tangent = new Vector2(-outward.y, outward.x);
            Vector3 centerAtTime = OrbitSphere.Move(center, centerVelocity * t);
            position = OrbitSphere.Move(centerAtTime, outward * radius);
            var centerPose = new SpacePose(centerAtTime, 0f)
            {
                velocity = SpaceTransform.TransportVelocity(
                    new SpacePose(center, 0f) { velocity = centerVelocity }, centerAtTime)
                    + outward * (seconds > 0f ? radialStep / seconds : 0f)
                    + tangent * (seconds > 0f ? phaseAdvance * radius / seconds : 0f)
            };
            velocity = SpaceTransform.TransportVelocity(centerPose, position);
            Vector2 toCenter = OrbitSphere.Offset(position, centerAtTime);
            angle = SpaceTransform.WrapAngle(Mathf.Atan2(toCenter.y, toCenter.x)
                - frameAngle);
            angularVelocity = seconds > 0f ? phaseAdvance / seconds : 0f;
        }
    }

    internal delegate bool CombatTargetSampler(out SpacePose pose);

    internal delegate bool CombatTrajectoryPlanner(SpaceObject own, SpacePose target,
        float radius, DriveInfo report, out CombatTrajectoryIntent intent);
}

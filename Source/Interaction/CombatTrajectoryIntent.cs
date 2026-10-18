using UnityEngine;

namespace DeltaV
{
    /// <summary>战术层给共同自动驾驶器的有限时域参考；角度均为 universe 切基角。</summary>
    internal struct CombatTrajectoryIntent
    {
        internal float seconds;
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

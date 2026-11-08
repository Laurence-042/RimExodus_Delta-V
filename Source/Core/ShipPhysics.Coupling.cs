using UnityEngine;

namespace DeltaV
{
    public static partial class ShipPhysics
    {
        /// <summary>One paid beam tick transfers equal and opposite impulse at the two hit points.</summary>
        internal static void ApplyTractorImpulse(SpaceObject receiver, Vector2 receiverLocal,
            SpaceObject target, Vector2 targetLocal, float impulseMagnitude)
        {
            MassReport own = GetReport(receiver), other = GetReport(target);
            if (own == null || other == null || own.mass <= 0f || other.mass <= 0f) return;
            SpacePose emitter = SpaceTransform.RigidPointPose(receiver.pose, receiverLocal, 0f);
            SpacePose hit = SpaceTransform.RigidPointPose(target.pose, targetLocal, 0f);
            Vector2 toward = OrbitSphere.Offset(emitter.direction, hit.direction);
            if (toward.sqrMagnitude < 0.0001f) return;
            Vector2 impulse = toward.normalized * impulseMagnitude;
            ApplyAt(receiver, own, receiverLocal, emitter.position, impulse);
            ApplyAt(target, other, targetLocal, hit.position,
                -SpaceTransform.TransportTangent(impulse, emitter.position, hit.position));
        }

        private static void ApplyAt(SpaceObject body, MassReport report, Vector2 localPoint,
            Vector3 point, Vector2 tangentImpulse)
        {
            SpacePose pose = body.pose;
            SpacePose center = SpaceTransform.RigidPointPose(pose, report.comLocal, 0f);
            center.velocity += SpaceTransform.TransportTangent(tangentImpulse, point, center.position) / report.mass;
            Vector2 localImpulse = SpaceTransform.RotateInverse(
                SpaceTransform.TransportTangent(tangentImpulse, point, pose.position), pose.angle);
            Vector2 arm = localPoint - report.comLocal;
            pose.angularVelocity += (arm.x * localImpulse.y - arm.y * localImpulse.x) / report.inertia;
            if (!report.allowsAngularMotion) pose.angularVelocity = 0f;
            pose.velocity = SpaceTransform.OriginVelocityForPoint(pose, report.comLocal, center.position, center.velocity);
            body.pose = pose;
        }

        /// <summary>
        /// 分别交换平移动量和自旋角动量；命中点只确定共同切平面，
        /// 不把线速度差转成绕双方中心公转。共同漂移保留，位置仍只由 Integrate 推进。
        /// </summary>
        internal static void CoupleMotion(SpaceObject a, MassReport ma, SpaceObject b, MassReport mb,
            Vector3 point, float fraction, float maxImpulse, float maxAngularImpulse)
        {
            SpacePose centerA = SpaceTransform.RigidPointPose(a.pose, ma.comLocal, 0f);
            SpacePose centerB = SpaceTransform.RigidPointPose(b.pose, mb.comLocal, 0f);
            Vector2 va = SpaceTransform.TransportVelocity(centerA, point);
            Vector2 vb = SpaceTransform.TransportVelocity(centerB, point);
            Vector2 beforeA = va, beforeB = vb;
            float wa = a.pose.angularVelocity, wb = b.pose.angularVelocity;
            CoupleVelocities(ma.mass, ma.inertia, mb.mass, mb.inertia,
                ref va, ref wa, ref vb, ref wb, fraction, maxImpulse, maxAngularImpulse);
            // 只换回速度增量，避免来回切平面投影凭空衰减原有速度。
            centerA.velocity += SpaceTransform.TransportTangent(va - beforeA, point, centerA.position);
            centerB.velocity += SpaceTransform.TransportTangent(vb - beforeB, point, centerB.position);
            a.pose.angularVelocity = ma.allowsAngularMotion ? wa : 0f;
            b.pose.angularVelocity = mb.allowsAngularMotion ? wb : 0f;
            a.pose.velocity = SpaceTransform.OriginVelocityForPoint(a.pose, ma.comLocal, centerA.position, centerA.velocity);
            b.pose.velocity = SpaceTransform.OriginVelocityForPoint(b.pose, mb.comLocal, centerB.position, centerB.velocity);
        }

        // 纯数值解算供真实对象与离线守恒回归共用。
        internal static void CoupleVelocities(float massA, float inertiaA, float massB, float inertiaB,
            ref Vector2 velocityA, ref float omegaA,
            ref Vector2 velocityB, ref float omegaB, float fraction,
            float maxImpulse, float maxAngularImpulse)
        {
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0f || massA <= 0f || massB <= 0f || inertiaA <= 0f || inertiaB <= 0f)
                return;
            float imA = 1f / massA, imB = 1f / massB;
            float iiA = 1f / inertiaA, iiB = 1f / inertiaB;
            Vector2 relative = velocityB - velocityA;
            // Finish a small residual with the same bounded, equal-and-opposite exchange.
            // Otherwise fractional pulses eventually fall below float velocity resolution.
            float linearFraction = relative.sqrMagnitude <= .000001f ? 1f : fraction;
            Vector2 impulse = relative * (linearFraction / (imA + imB));
            impulse = Vector2.ClampMagnitude(impulse, Mathf.Max(0f, maxImpulse));
            velocityA += impulse * imA;
            velocityB -= impulse * imB;
            float angularFraction = Mathf.Abs(omegaB - omegaA) <= .00001f ? 1f : fraction;
            float spin = Mathf.Clamp((omegaB - omegaA) * angularFraction / (iiA + iiB),
                -Mathf.Max(0f, maxAngularImpulse), Mathf.Max(0f, maxAngularImpulse));
            omegaA += spin * iiA;
            omegaB -= spin * iiB;
        }
    }
}

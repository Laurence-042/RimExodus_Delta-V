using UnityEngine;

namespace DeltaV
{
    public static partial class ShipPhysics
    {
        /// <summary>
        /// 分别交换平移动量和自旋角动量；命中点只确定共同切平面，
        /// 不把线速度差转成绕双方中心公转。共同漂移保留，位置仍只由 Integrate 推进。
        /// </summary>
        internal static void CoupleMotion(SpaceObject a, MassReport ma, SpaceObject b, MassReport mb,
            Vector3 point, float fraction, float maxImpulse, float maxAngularImpulse)
        {
            Vector2 va = SpaceTransform.TransportVelocity(a.pose, point);
            Vector2 vb = SpaceTransform.TransportVelocity(b.pose, point);
            Vector2 beforeA = va, beforeB = vb;
            float wa = a.pose.angularVelocity, wb = b.pose.angularVelocity;
            CoupleVelocities(ma.mass, ma.inertia, mb.mass, mb.inertia,
                ref va, ref wa, ref vb, ref wb, fraction, maxImpulse, maxAngularImpulse);
            // 只换回速度增量，避免来回切平面投影凭空衰减原有速度。
            a.pose.velocity += SpaceTransform.TransportTangent(va - beforeA, point, a.pose.position);
            b.pose.velocity += SpaceTransform.TransportTangent(vb - beforeB, point, b.pose.position);
            a.pose.angularVelocity = wa;
            b.pose.angularVelocity = wb;
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

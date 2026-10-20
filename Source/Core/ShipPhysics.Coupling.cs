using UnityEngine;

namespace DeltaV
{
    public static partial class ShipPhysics
    {
        /// <summary>
        /// 在共同命中点施加等大反向冲量及力矩，再交换自旋角动量。
        /// 每步耗散相对动能，不把速度设为零；空间位置仍只由 Integrate 推进。
        /// 守恒按现有球面模型的共同战术切平面计算。
        /// </summary>
        internal static void CoupleMotion(SpaceObject a, MassReport ma, SpaceObject b, MassReport mb,
            Vector3 point, float fraction, float maxImpulse, float maxAngularImpulse)
        {
            Vector2 va = SpaceTransform.TransportVelocity(a.pose, point);
            Vector2 vb = SpaceTransform.TransportVelocity(b.pose, point);
            Vector2 beforeA = va, beforeB = vb;
            Vector2 ra = SpaceTransform.MomentArmAt(a.pose, ma.comLocal, point);
            Vector2 rb = SpaceTransform.MomentArmAt(b.pose, mb.comLocal, point);
            float wa = a.pose.angularVelocity, wb = b.pose.angularVelocity;
            CoupleVelocities(ma.mass, ma.inertia, mb.mass, mb.inertia, ra, rb,
                ref va, ref wa, ref vb, ref wb, fraction, maxImpulse, maxAngularImpulse);
            // 只换回速度增量，避免来回切平面投影凭空衰减原有速度。
            a.pose.velocity += SpaceTransform.TransportTangent(va - beforeA, point, a.pose.position);
            b.pose.velocity += SpaceTransform.TransportTangent(vb - beforeB, point, b.pose.position);
            a.pose.angularVelocity = wa;
            b.pose.angularVelocity = wb;
        }

        // 纯数值解算供真实对象与离线守恒回归共用。
        internal static void CoupleVelocities(float massA, float inertiaA, float massB, float inertiaB,
            Vector2 armA, Vector2 armB, ref Vector2 velocityA, ref float omegaA,
            ref Vector2 velocityB, ref float omegaB, float fraction,
            float maxImpulse, float maxAngularImpulse)
        {
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0f || massA <= 0f || massB <= 0f || inertiaA <= 0f || inertiaB <= 0f)
                return;
            float imA = 1f / massA, imB = 1f / massB;
            float iiA = 1f / inertiaA, iiB = 1f / inertiaB;
            Vector2 pa = new Vector2(-armA.y, armA.x);
            Vector2 pb = new Vector2(-armB.y, armB.x);
            Vector2 relative = velocityB + omegaB * pb - velocityA - omegaA * pa;
            float kxx = imA + imB + pa.x * pa.x * iiA + pb.x * pb.x * iiB;
            float kyy = imA + imB + pa.y * pa.y * iiA + pb.y * pb.y * iiB;
            float kxy = pa.x * pa.y * iiA + pb.x * pb.y * iiB;
            float determinant = kxx * kyy - kxy * kxy;
            Vector2 impulse = determinant > 0f
                ? new Vector2(kyy * relative.x - kxy * relative.y,
                    kxx * relative.y - kxy * relative.x) * (fraction / determinant)
                : Vector2.zero;
            impulse = Vector2.ClampMagnitude(impulse, Mathf.Max(0f, maxImpulse));
            velocityA += impulse * imA;
            velocityB -= impulse * imB;
            omegaA += Cross(armA, impulse) * iiA;
            omegaB -= Cross(armB, impulse) * iiB;

            float spin = Mathf.Clamp((omegaB - omegaA) * fraction / (iiA + iiB),
                -Mathf.Max(0f, maxAngularImpulse), Mathf.Max(0f, maxAngularImpulse));
            omegaA += spin * iiA;
            omegaB -= spin * iiB;
        }
    }
}

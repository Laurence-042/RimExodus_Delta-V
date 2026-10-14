using UnityEngine;

namespace DeltaV
{
    /// <summary>One optical rule for collision and the settings reference.</summary>
    public static class SpaceShieldRefraction
    {
        private const float PassiveRatio = 1.4f;

        public readonly struct Plan
        {
            private readonly Vector2 passive;
            private readonly float turnSign;
            public readonly float incidenceDeg;
            public readonly float addedDeg;

            public Plan(Vector2 passive, float turnSign, float incidenceDeg, float addedDeg)
            {
                this.passive = passive;
                this.turnSign = turnSign;
                this.incidenceDeg = incidenceDeg;
                this.addedDeg = addedDeg;
            }

            public Vector2 Direction(float fundedFraction) => SpaceTransform.Rotate(passive,
                turnSign * addedDeg * Mathf.Clamp01(fundedFraction) * Mathf.Deg2Rad).normalized;
        }

        public static Plan Calculate(Vector2 incoming, Vector2 frontNormal, int strengthPercent)
        {
            Vector2 ray = incoming.normalized;
            Vector2 normal = frontNormal.normalized;
            float cosine = Mathf.Clamp01(-Vector2.Dot(ray, normal));
            float incidenceDeg = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            Vector2 tangent = ray + cosine * normal;
            float transverse = tangent.magnitude * PassiveRatio;
            Vector2 passive = transverse >= 1f
                ? (ray + 2f * cosine * normal).normalized
                : (tangent * PassiveRatio - normal * Mathf.Sqrt(1f - transverse * transverse)).normalized;

            Vector2 target = -ray;
            float cross = passive.x * target.y - passive.y * target.x;
            float signed = Mathf.Atan2(cross, Vector2.Dot(passive, target)) * Mathf.Rad2Deg;
            if (Mathf.Abs(cross) < 0.00001f && Vector2.Dot(passive, target) < 0f)
            {
                // A straight-on ray has two equally short 180-degree paths.
                Vector2 shieldTangent = new Vector2(normal.y, -normal.x);
                signed = Vector2.Dot(ray, shieldTangent) < 0f ? -180f : 180f;
            }
            float addedDeg = Mathf.Min(Mathf.Abs(signed), 1.8f * Mathf.Clamp(strengthPercent, 0, 100));
            return new Plan(passive, Mathf.Sign(signed), incidenceDeg, addedDeg);
        }
    }
}

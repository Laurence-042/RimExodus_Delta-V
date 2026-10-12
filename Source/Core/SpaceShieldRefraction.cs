using UnityEngine;

namespace DeltaV
{
    /// <summary>One optical rule for the collision path and the shield settings reference.</summary>
    public static class SpaceShieldRefraction
    {
        public static Vector2 Refract(Vector2 incoming, Vector2 frontNormal, int strength,
            out float incidenceDeg, out bool totalReflection)
        {
            float cosine = Mathf.Clamp01(-Vector2.Dot(incoming, frontNormal));
            incidenceDeg = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            Vector2 tangent = incoming + cosine * frontNormal;
            float refractedTangent = tangent.magnitude * (1f + 0.2f * strength);
            totalReflection = refractedTangent >= 1f;
            return totalReflection
                ? (incoming + 2f * cosine * frontNormal).normalized
                : (tangent * (1f + 0.2f * strength)
                    - frontNormal * Mathf.Sqrt(1f - refractedTangent * refractedTangent)).normalized;
        }
    }
}

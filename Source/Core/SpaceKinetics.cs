using UnityEngine;

namespace DeltaV
{
    /// <summary>Normalized projectile mass and energy, independent of launcher and reference frame.</summary>
    public static class SpaceKinetics
    {
        public const float UnitSpeed = 60f;
        public const float EnergyUnitsPerWattDay = 1800f; // Game balance conversion, not SI watts.
        public static float Energy(float mass, float speed) => 0.5f * Mathf.Max(0f, mass) * speed * speed;
        public static float Speed(float mass, float energy) => mass <= 0f ? 0f : Mathf.Sqrt(2f * Mathf.Max(0f, energy) / mass);
        public static float LaunchSpeed(float charge, float mass) => Speed(mass, Energy(charge, UnitSpeed));
        public static float ImpactFactor(float mass, float speed) => Mathf.Max(0f, mass) * Mathf.Max(0f, speed) / UnitSpeed;
        public static float ShieldNormalEnergy(float strength) => Energy(1f, 240f) * Mathf.Clamp01(strength / 100f);
        public static float ShieldCost(float normalEnergy, float baseCost) =>
            baseCost + Mathf.Max(0f, normalEnergy) / EnergyUnitsPerWattDay;
        public static Vector2 ApplyShieldNormalEnergy(float mass, Vector2 velocity, Vector2 outwardNormal, float energy)
        {
            if (mass <= 0f || energy <= 0f) return velocity;
            Vector2 normal = outwardNormal.normalized;
            float incoming = Vector2.Dot(velocity, normal);
            // Negative normal energy travels inward; the pulse first brakes it, then pushes outward.
            float remaining = Mathf.Sign(incoming) * Energy(mass, incoming) + energy;
            float outgoing = Mathf.Sign(remaining) * Speed(mass, Mathf.Abs(remaining));
            return velocity + normal * (outgoing - incoming);
        }
    }
}

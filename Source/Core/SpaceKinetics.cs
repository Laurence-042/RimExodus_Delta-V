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
        public static float ReverseEnergy(float strength) => Energy(1f, 240f) * Mathf.Clamp01(strength / 100f);
        public static float ShieldCost(float mass, float speed, float reverseEnergy, float baseCost, bool active) =>
            baseCost + (active ? (Energy(mass, speed) + reverseEnergy) / EnergyUnitsPerWattDay : 0f);
        public static float AfterShield(float mass, float speed, float paid, float baseCost, bool active)
        {
            if (!active) return speed;
            float remaining = Energy(mass, speed) - Mathf.Max(0f, paid - baseCost) * EnergyUnitsPerWattDay;
            return remaining >= 0f ? Speed(mass, remaining) : -Speed(mass, -remaining);
        }
    }
}

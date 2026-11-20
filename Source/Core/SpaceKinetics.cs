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
        public static float ImpactFactor(float mass, float speed) => Energy(mass, speed) / EnergyUnitsPerWattDay;
        public static float PenetrationBudget(float energy, float maximum, float energyScale)
        {
            double x = energy / (double)energyScale;
            return (float)(maximum * (x < 1e-8 ? x : 1.0 - System.Math.Exp(-x)));
        }

        // Invert the remaining share of B(E) at this contact before carrying energy to the next one.
        internal static float RemainingEnergyRatio(float energy, float energyScale, float budgetRatio)
        {
            if (energyScale <= 0f || budgetRatio <= 0f || budgetRatio >= 1f) return budgetRatio;
            double x = energy / (double)energyScale;
            if (x < 1e-8) return budgetRatio;
            double remaining = budgetRatio * (1.0 - System.Math.Exp(-x));
            return Mathf.Clamp01((float)((remaining < 1e-8 ? remaining : -System.Math.Log(1.0 - remaining)) / x));
        }

        public static float ShieldImpulse(float strength) => 360f * Mathf.Clamp01(strength / 100f);
        public static float ShieldCost(float mass, float normalSpeed, float impulse, float baseCost)
        {
            if (mass <= 0f || impulse <= 0f || normalSpeed >= 0f) return 0f;
            double outgoing = normalSpeed + (double)impulse / mass;
            double work = .5 * mass * (outgoing * outgoing - (double)normalSpeed * normalSpeed);
            return baseCost + 16f * impulse / 360f + (float)System.Math.Max(0, work) / EnergyUnitsPerWattDay;
        }
        public static float FundedShieldImpulse(float mass, float normalSpeed, float requested, float baseCost, float available)
        {
            if (mass <= 0f || normalSpeed >= 0f || requested <= 0f) return 0f;
            if (ShieldCost(mass, normalSpeed, requested, baseCost) <= available) return requested;
            if (available <= baseCost) return 0f;
            float lo = 0f, hi = requested;
            for (int i = 0; i < 32; i++)
            {
                float mid = (lo + hi) * .5f;
                if (ShieldCost(mass, normalSpeed, mid, baseCost) <= available) lo = mid;
                else hi = mid;
            }
            return lo;
        }
        public static Vector2 ApplyShieldImpulse(float mass, Vector2 velocity, Vector2 outwardNormal, float impulse)
            => mass <= 0f || impulse <= 0f ? velocity : velocity + outwardNormal.normalized * (impulse / mass);
        public static float LateralImpulse(Vector2 incoming, Vector2 normal, float impulse)
        {
            if (incoming.sqrMagnitude < 1e-8f || impulse <= 0f) return 0f;
            Vector2 direction = incoming.normalized;
            normal.Normalize();
            return impulse * Mathf.Abs(direction.x * normal.y - direction.y * normal.x);
        }

        // 0: intact continuation; 1: one full-mass tumbling body; N: torn fragments.
        public static int ShieldFragmentCount(float mass, Vector2 incoming, Vector2 normal,
            float impulse, CompProperties_SpaceProjectile props)
        {
            if (mass <= 0f || props.shieldFragmentCount <= 1) return 0;
            float lateralDeltaV = LateralImpulse(incoming, normal, impulse) / mass;
            if (lateralDeltaV > props.shieldBreakDeltaV + 1e-5f) return Mathf.Clamp(props.shieldFragmentCount, 2, 12);
            return !props.shieldTumbling && lateralDeltaV > props.shieldTumbleDeltaV + 1e-5f ? 1 : 0;
        }
    }
}

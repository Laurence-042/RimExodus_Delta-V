using System;

namespace DeltaV
{
    public enum AutoSpaceWeaponModPolicy : byte
    {
        Recommended,
        EnableAll,
        DisableAll
    }

    public enum AutoSpaceWeaponOverride : byte
    {
        Inherit,
        Enabled,
        Disabled
    }

    [Flags]
    public enum AutoSpaceWeaponWarning : ushort
    {
        None = 0,
        CustomVerb = 1 << 0,
        ExplosiveProjectile = 1 << 1,
        FlyOverhead = 1 << 2,
        ExternalResourceTransaction = 1 << 3,
        ThirdPartyMechanics = 1 << 4,
        MortarBarrel = 1 << 5,
        CustomFlight = 1 << 6,
        RuntimeProjectile = 1 << 7,
        NonKineticDamage = 1 << 8
    }

    /// <summary>Pure policy resolution shared by Def generation, settings UI and offline audit.</summary>
    public static class AutoSpaceWeaponPolicy
    {
        public static string NormalizePackageId(string packageId) =>
            (packageId ?? "").Trim().ToLowerInvariant();

        public static string WeaponKey(string packageId, string defName) =>
            NormalizePackageId(packageId) + "|" + (defName ?? "").Trim();

        public static bool Enabled(AutoSpaceWeaponModPolicy modPolicy,
            AutoSpaceWeaponOverride weaponOverride, bool structurallySupported,
            AutoSpaceWeaponWarning warnings, bool definitionForcesSupport)
        {
            if (!structurallySupported) return false;
            if (weaponOverride == AutoSpaceWeaponOverride.Enabled) return true;
            if (weaponOverride == AutoSpaceWeaponOverride.Disabled) return false;
            if (modPolicy == AutoSpaceWeaponModPolicy.EnableAll) return true;
            if (modPolicy == AutoSpaceWeaponModPolicy.DisableAll) return false;
            // Native entity replay preserves custom Verbs, source resource transactions,
            // explosive Impact logic and other third-party mechanics. Only source-map flight
            // behavior is lost when the projectile is captured before its first Tick.
            return (warnings & AutoSpaceWeaponWarning.CustomFlight) == 0 || definitionForcesSupport;
        }

        public static int SourceSupportedShots(float capacity, float consumptionPerShot,
            float consumptionPerBurst, int burstShotCount)
        {
            if (!(capacity > 0f) || float.IsNaN(capacity) || float.IsInfinity(capacity)
                || float.IsNaN(consumptionPerShot) || float.IsInfinity(consumptionPerShot)
                || float.IsNaN(consumptionPerBurst) || float.IsInfinity(consumptionPerBurst)) return 0;
            double perShot = Math.Max(0d, consumptionPerShot);
            double perBurst = Math.Max(0d, consumptionPerBurst);
            long burst = Math.Max(1, burstShotCount);
            if (!(perShot > 0d))
            {
                if (!(perBurst > 0d)) return 0;
                double completeBursts = Math.Floor(capacity / perBurst);
                return ClampShotCount(completeBursts * burst);
            }

            // Verb checks and pays per-shot fuel before every successful shot, then pays the
            // per-burst charge even when the last burst ended early. Reproduce that ordering
            // without iterating potentially billions of source bursts.
            double fullBurstShotCost = burst * perShot;
            double cycleCost = fullBurstShotCost + perBurst;
            double fullBursts = capacity + 1e-9d < fullBurstShotCost ? 0d
                : 1d + Math.Floor((capacity - fullBurstShotCost) / cycleCost);
            double remaining = Math.Max(0d, capacity - fullBursts * cycleCost);
            double partialShots = Math.Min(burst - 1d, Math.Floor((remaining + 1e-9d) / perShot));
            return ClampShotCount(fullBursts * burst + partialShots);
        }

        private static int ClampShotCount(double value) => (int)Math.Min(int.MaxValue,
            Math.Max(0d, double.IsNaN(value) || double.IsInfinity(value) ? int.MaxValue : value));
    }
}

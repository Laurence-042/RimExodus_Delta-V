using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum GuardEmergencyReason : byte { None, Automatic, Manual }

    public partial class CompSpaceShield
    {
        // Regular capacity lives here; emergency capacity is the native fuel balance.
        // The interceptor uses hitPoints=0 / currentHitPoints=-1 (no native HP accounting).
        private double regularCapacity;
        private int recoveryWaitTicks;
        private int autoRejoinPercent = 10;
        private GuardEmergencyReason emergencyReason;
        internal bool draggingRegularBar;
        public double RegularCapacity => regularCapacity;
        private CompRefuelable EmergencyFuel => silverFuel ??= parent.TryGetComp<CompRefuelable>();
        public double EmergencyCapacity => EmergencyFuel?.Fuel ?? 0f;
        public GuardEmergencyReason EmergencyReason => emergencyReason;
        public int RecoveryWaitTicks => recoveryWaitTicks;
        internal CompSpaceShield ForecastGuardCopy() => new CompSpaceShield
        {
            props = props, parent = parent, mode = mode, projection = projection, length = length, regularCapacity = regularCapacity,
            recoveryWaitTicks = recoveryWaitTicks, autoRejoinPercent = autoRejoinPercent,
            emergencyReason = emergencyReason
        };
        internal double DebitRegular(double amount)
        {
            double debit = Math.Min(Math.Max(0, amount), regularCapacity);
            regularCapacity -= debit;
            if (debit > 0) recoveryWaitTicks = ShieldProps.recoveryDelayTicks;
            if (regularCapacity <= 0) emergencyReason = GuardEmergencyReason.Automatic;
            return debit;
        }
        public int AutoRejoinPercent
        {
            get => autoRejoinPercent;
            set { autoRejoinPercent = Math.Max(1, Math.Min(100, value)); TryRejoinRegular(); }
        }
        public bool UsesRegular => mode == SpaceShieldMode.Guard && ShieldProps.regularCapacity > 0
            && emergencyReason == GuardEmergencyReason.None && regularCapacity > 0;
        private bool ContributesRegular => parent.Spawned && !parent.Destroyed && FlickedOn && UsesRegular;
        public bool HasGuardCapacity => mode == SpaceShieldMode.Guard && (UsesRegular || EmergencyCapacity > 0);
        private bool WantsRegularRecovery => ShieldProps.regularCapacity > 0
            && regularCapacity < ShieldProps.regularCapacity && recoveryWaitTicks == 0 && FlickedOn;

        internal void InitializeGuard()
        {
            regularCapacity = ShieldProps.regularCapacity;
            emergencyReason = ShieldProps.regularCapacity > 0 ? GuardEmergencyReason.None : GuardEmergencyReason.Automatic;
        }

        private void ExposeGuardData()
        {
            Scribe_Values.Look(ref regularCapacity, "DeltaV_regularCapacity", (double)ShieldProps.regularCapacity);
            Scribe_Values.Look(ref recoveryWaitTicks, "DeltaV_regularRecoveryWait");
            Scribe_Values.Look(ref emergencyReason, "DeltaV_emergencyReason");
            Scribe_Values.Look(ref autoRejoinPercent, "DeltaV_autoRejoinPercent", 10);
        }

        internal void AdvanceGuardTick(bool powered)
        {
            // No absolute timestamps: unloaded/archived time never becomes free charge.
            if (recoveryWaitTicks > 0) recoveryWaitTicks--;
            else if (powered && regularCapacity < ShieldProps.regularCapacity)
            {
                double next = regularCapacity + (double)ShieldProps.regularCapacity / ShieldProps.guardRecoveryTicks;
                regularCapacity = next >= ShieldProps.regularCapacity - 1e-8 ? ShieldProps.regularCapacity : next;
            }
            TryRejoinRegular();
        }

        internal void TryRejoinRegular()
        {
            if (ShieldProps.regularCapacity <= 0 || emergencyReason != GuardEmergencyReason.Automatic) return;
            double threshold = ShieldProps.regularCapacity * autoRejoinPercent / 100d;
            if (regularCapacity >= threshold - 1e-8) emergencyReason = GuardEmergencyReason.None;
        }

        public void EnterManualEmergency()
        {
            if (mode != SpaceShieldMode.Guard || ShieldProps.regularCapacity <= 0 || EmergencyCapacity <= 0) return;
            emergencyReason = GuardEmergencyReason.Manual;
        }

        public void UseRegularGuard()
        {
            if (mode != SpaceShieldMode.Guard || ShieldProps.regularCapacity <= 0 || regularCapacity <= 0) return;
            emergencyReason = GuardEmergencyReason.None;
        }

        internal IReadOnlyList<CompSpaceShield> OwnedShields =>
            (ShipRegistry.ShipOf(parent, requireWholeFootprint: true) as FoundationShip)?.Structure.console?.Shields;

        public double SharedRegularCapacity
        {
            get
            {
                if (!UsesRegular) return 0;
                var shields = OwnedShields;
                if (shields == null || shields.Count == 0) return regularCapacity;
                double total = 0;
                foreach (var shield in shields) if (shield.ContributesRegular) total += shield.regularCapacity;
                return total;
            }
        }

        public static bool CanAbsorbGuard(CompProjectileInterceptor interceptor) =>
            interceptor.parent.TryGetComp<CompSpaceShield>()?.HasGuardCapacity ?? interceptor.currentHitPoints > 0;

        public static float AvailableGuardCapacity(CompProjectileInterceptor interceptor)
        {
            var shield = interceptor.parent.TryGetComp<CompSpaceShield>();
            return shield == null ? interceptor.currentHitPoints
                : shield.mode != SpaceShieldMode.Guard ? 0f
                : (float)(shield.SharedRegularCapacity + shield.EmergencyCapacity);
        }

        /// <summary>Returns the actual absorbed amount; emergency belongs only to this surface.</summary>
        public float ConsumeGuard(float damage) => (float)ConsumeGuard(damage, UsesRegular ? OwnedShields : null);

        internal double ConsumeGuard(double damage, IReadOnlyList<CompSpaceShield> ownedShields)
        {
            if (damage <= 0 || mode != SpaceShieldMode.Guard) return 0;
            double remaining = damage;
            if (UsesRegular)
            {
                var members = new List<CompSpaceShield>();
                if (ownedShields != null)
                    foreach (var shield in ownedShields)
                        if (shield.ContributesRegular && !members.Contains(shield)) members.Add(shield);
                if (members.Count == 0) members.Add(this);
                // Water filling: equal shares, then redistribute unpaid shares after depletion.
                while (members.Count > 0 && remaining > 0)
                {
                    double share = remaining / members.Count;
                    double paid = 0;
                    for (int i = members.Count - 1; i >= 0; i--)
                    {
                        var shield = members[i];
                        double debit = shield.DebitRegular(share);
                        paid += debit;
                        if (shield.regularCapacity <= 0)
                        {
                            members.RemoveAt(i);
                        }
                        shield.UpdatePowerDraw();
                    }
                    remaining = Math.Max(0, remaining - paid);
                    if (paid == 0) break;
                    // All survivors paid their complete share; avoid numerical dust iterations.
                    if (remaining <= damage * 1e-14) { remaining = 0; break; }
                }
            }
            double local = Math.Min(remaining, EmergencyCapacity);
            if (local > 0) EmergencyFuel.ConsumeFuel((float)local);
            remaining -= local;
            return damage - remaining;
        }
    }
}

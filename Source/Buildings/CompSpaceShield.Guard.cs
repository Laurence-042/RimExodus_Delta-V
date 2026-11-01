using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum GuardEmergencyReason : byte { None, Automatic, Manual }

    public partial class CompSpaceShield
    {
        // These are the only capacity balances. The vanilla interceptor uses its
        // hitPoints=0 / currentHitPoints=-1 capacity-free interception contract.
        private double regularCapacity, emergencyCapacity;
        private int recoveryWaitTicks;
        private int autoRejoinPercent = 100;
        private GuardEmergencyReason emergencyReason;
        public double RegularCapacity => regularCapacity;
        public double EmergencyCapacity => emergencyCapacity;
        public GuardEmergencyReason EmergencyReason => emergencyReason;
        public int RecoveryWaitTicks => recoveryWaitTicks;
        public int AutoRejoinPercent
        {
            get => autoRejoinPercent;
            set { autoRejoinPercent = Math.Max(1, Math.Min(100, value)); TryRejoinRegular(); }
        }
        public bool UsesRegular => mode == SpaceShieldMode.Guard && ShieldProps.regularCapacity > 0
            && emergencyReason == GuardEmergencyReason.None && regularCapacity > 0;
        private bool ContributesRegular => parent.Spawned && !parent.Destroyed && FlickedOn && UsesRegular;
        public bool HasGuardCapacity => mode == SpaceShieldMode.Guard && (UsesRegular || emergencyCapacity > 0);
        private bool WantsRegularRecovery => ShieldProps.regularCapacity > 0
            && regularCapacity < ShieldProps.regularCapacity && recoveryWaitTicks == 0 && FlickedOn;

        internal void InitializeGuard()
        {
            regularCapacity = ShieldProps.regularCapacity;
            emergencyCapacity = ShieldProps.emergencyCapacity;
            emergencyReason = ShieldProps.regularCapacity > 0 ? GuardEmergencyReason.None : GuardEmergencyReason.Automatic;
        }

        private void ExposeGuardData()
        {
            Scribe_Values.Look(ref regularCapacity, "DeltaV_regularCapacity", (double)ShieldProps.regularCapacity);
            Scribe_Values.Look(ref emergencyCapacity, "DeltaV_emergencyCapacity", (double)ShieldProps.emergencyCapacity);
            Scribe_Values.Look(ref recoveryWaitTicks, "DeltaV_regularRecoveryWait");
            Scribe_Values.Look(ref emergencyReason, "DeltaV_emergencyReason");
            Scribe_Values.Look(ref autoRejoinPercent, "DeltaV_autoRejoinPercent", 100);
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
            if (ShieldProps.regularCapacity <= 0) return;
            double threshold = ShieldProps.regularCapacity *
                (emergencyReason == GuardEmergencyReason.Manual ? 1d : autoRejoinPercent / 100d);
            if (regularCapacity >= threshold - 1e-8) emergencyReason = GuardEmergencyReason.None;
        }

        public void EnterManualEmergency()
        {
            if (ShieldProps.regularCapacity <= 0) return;
            emergencyReason = GuardEmergencyReason.Manual;
            TryRejoinRegular();
        }

        private IReadOnlyList<CompSpaceShield> OwnedShields =>
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
                : (float)(shield.SharedRegularCapacity + shield.emergencyCapacity);
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
                        double debit = Math.Min(share, shield.regularCapacity);
                        shield.regularCapacity -= debit;
                        paid += debit;
                        if (debit > 0) shield.recoveryWaitTicks = shield.ShieldProps.recoveryDelayTicks;
                        if (shield.regularCapacity <= 0)
                        {
                            shield.emergencyReason = GuardEmergencyReason.Automatic;
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
            double local = Math.Min(remaining, emergencyCapacity);
            emergencyCapacity -= local;
            remaining -= local;
            UpdateSilverTarget();
            return damage - remaining;
        }
    }
}

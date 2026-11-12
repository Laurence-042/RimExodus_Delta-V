using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class CombatGuardAvailability
    {
        // CompProjectileInterceptor.Active, verified against vanilla 1.6 and 1.7.
        // Capacity is debited in the single-shot workspace.
        private static readonly FieldInfo stunner = AccessTools.Field(typeof(CompProjectileInterceptor), "stunner");
        private static readonly FieldInfo shutDown = AccessTools.Field(typeof(CompProjectileInterceptor), "shutDown");
        private static readonly FieldInfo activated = AccessTools.Field(typeof(CompProjectileInterceptor), "activatedTick");
        internal static bool Operational(CompProjectileInterceptor shield)
        {
            if (shield.parent.TryGetComp<CompSpaceShield>() is not { mode: SpaceShieldMode.Guard } custom)
                return shield.Active;
            if (!shield.parent.Spawned || !custom.FlickedOn || shield.CooldownTicksLeft > 0
                || shield.ChargingTicksLeft > 0 || (bool)shutDown.GetValue(shield)
                || (stunner.GetValue(shield) as StunHandler)?.Stunned == true) return false;
            if (shield.Props.activated && Find.TickManager.TicksGame
                > (int)activated.GetValue(shield) + shield.Props.activeDuration) return false;
            return shield.parent.GetComp<CompCanBeDormant>()?.Awake != false;
        }
    }
}

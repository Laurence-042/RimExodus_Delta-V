using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class CombatGuardAvailability
    {
        // CompProjectileInterceptor.Active, verified against vanilla 1.6 and 1.7.
        // Capacity and time belong to the forecast; stun/shutdown remain observed facts.
        private static readonly FieldInfo stunner = AccessTools.Field(typeof(CompProjectileInterceptor), "stunner");
        private static readonly FieldInfo shutDown = AccessTools.Field(typeof(CompProjectileInterceptor), "shutDown");
        private static readonly FieldInfo activated = AccessTools.Field(typeof(CompProjectileInterceptor), "activatedTick");
        internal static bool Operational(CompProjectileInterceptor shield, int elapsedTicks)
        {
            if (shield.parent.TryGetComp<CompSpaceShield>() is not { mode: SpaceShieldMode.Guard } custom)
                return shield.Active;
            if (!shield.parent.Spawned || !custom.FlickedOn || shield.CooldownTicksLeft > elapsedTicks
                || shield.ChargingTicksLeft > elapsedTicks || (bool)shutDown.GetValue(shield)
                || (stunner.GetValue(shield) as StunHandler)?.Stunned == true) return false;
            if (shield.Props.activated && Find.TickManager.TicksGame + elapsedTicks
                > (int)activated.GetValue(shield) + shield.Props.activeDuration) return false;
            return shield.parent.GetComp<CompCanBeDormant>()?.Awake != false;
        }
    }
}

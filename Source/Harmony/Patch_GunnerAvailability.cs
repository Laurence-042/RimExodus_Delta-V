using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>Pawn 重新可用时只登记所在图；真正分配在下一游戏 tick 执行。</summary>
    internal static class GunnerAvailabilitySignal
    {
        internal static void Notify(Pawn pawn)
        {
            if (pawn?.Spawned != true || pawn.Map == null || pawn.Faction == null) return;
            ShipControlService.NotifyGunnerChange(pawn.Map);
        }
    }

    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.RegisterPawn))]
    internal static class Patch_GunnerPawnRegistered
    {
        private static void Postfix(Pawn p) => GunnerAvailabilitySignal.Notify(p);
    }

    [HarmonyPatch(typeof(Pawn_DraftController), "Drafted", MethodType.Setter)]
    internal static class Patch_GunnerUndrafted
    {
        private static void Postfix(Pawn_DraftController __instance)
        {
            if (!__instance.Drafted) GunnerAvailabilitySignal.Notify(__instance.pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeUndowned")]
    internal static class Patch_GunnerUndowned
    {
        private static void Postfix(Pawn ___pawn) => GunnerAvailabilitySignal.Notify(___pawn);
    }

    [HarmonyPatch(typeof(MentalState), nameof(MentalState.RecoverFromState))]
    internal static class Patch_GunnerMentalRecovery
    {
        private static void Postfix(MentalState __instance) => GunnerAvailabilitySignal.Notify(__instance.pawn);
    }
}

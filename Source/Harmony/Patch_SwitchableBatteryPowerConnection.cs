using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    // Vanilla checks exact CompPowerTrader/CompPowerBattery types, excluding our shared subclass.
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.ConnectToPower), MethodType.Getter)]
    public static class Patch_SwitchableBatteryPowerConnection
    {
        public static void Postfix(ThingDef __instance, ref bool __result)
        {
            if (!__result && !__instance.EverTransmitsPower
                && __instance.GetCompProperties<CompProperties_SwitchableBattery>() != null)
                __result = true;
        }
    }

    // All automatic, manual and load-time connector paths converge here.
    [HarmonyPatch(typeof(CompPower), nameof(CompPower.ConnectToTransmitter))]
    public static class Patch_SwitchableBatteryConnect
    {
        public static bool Prefix(CompPower __instance, bool reconnectingAfterLoading)
        {
            if (__instance is not CompSwitchableBattery battery || battery.WantsGridConnection) return true;
            // CompPower.PostExposeData has restored the parent reference, but has not registered it yet.
            if (reconnectingAfterLoading) battery.connectParent = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.AmountCanAccept), MethodType.Getter)]
    public static class Patch_SpaceCapacitorCharging
    {
        private static void Postfix(CompPowerBattery __instance, ref float __result)
        {
            if (__result > 0f && __instance is CompSwitchableBattery
                && __instance.parent.TryGetComp<CompSpaceWeaponCapacitor>() is { } capacitor
                && !capacitor.CanCharge) __result = 0f;
        }
    }
}

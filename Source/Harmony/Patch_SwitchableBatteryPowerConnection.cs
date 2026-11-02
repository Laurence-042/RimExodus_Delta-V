using HarmonyLib;
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
}

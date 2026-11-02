using HarmonyLib;
using AdaptiveStorage.Utility;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(TradeDeal), "InSellablePosition")]
    internal static class Patch_SpaceStationTrade
    {
        private static bool Prefix(Thing t, ref string reason, ref bool __result)
        {
            if (TradeSession.trader is Building_SpaceTradeTerminal terminal
                && t?.def.category == ThingCategory.Item
                && Building_SpaceTradeTerminal.IsStillOnShip(t, terminal.ShipTradeMap))
            {
                reason = null;
                __result = true;
                return false;
            }
            return true;
        }
    }

    // Exposed hull modules use outdoor ambient, including the optional ASF thermal transform.
    [HarmonyPatch(typeof(Thing), nameof(Thing.AmbientTemperature), MethodType.Getter)]
    internal static class Patch_SpaceCargoOutdoorTemperature
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Thing __instance, ref float __result)
        {
            if (__instance.StoringAdaptiveStorage() is not Building_SpaceCargoContainer cargo
                || !cargo.Contents.Contains(__instance)) return;
            __result = cargo.CargoTemperature;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    internal static class Patch_SpaceStationTradeDeal
    {
        private static bool Prefix(TradeDeal __instance, ref bool actuallyTraded, ref bool __result)
        {
            if (TradeSession.trader is not Building_SpaceTradeTerminal terminal) return true;
            if (terminal.DealStillValid(__instance, out bool noRoom)) return true;
            actuallyTraded = false;
            __result = false;
            Messages.Message((noRoom ? "DeltaV_TradeNoRoom" : "DeltaV_TradeChanged").Translate(),
                MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}

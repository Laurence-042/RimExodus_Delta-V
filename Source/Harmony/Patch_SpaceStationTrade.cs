using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using AdaptiveStorage.Utility;
using RimWorld;
using Verse;

namespace DeltaV
{
    // The native Hack job uses InteractionCell, but faces the centre of the
    // entire footprint. This bay's console is one cell ahead of its operator.
    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.FaceTarget))]
    internal static class Patch_SpaceCargoHackFacing
    {
        private static void Prefix(Pawn ___pawn, ref LocalTargetInfo target)
        {
            if (target.Thing is not Building_SealedSpaceCargoContainer cargo
                || ___pawn.CurJobDef != JobDefOf.Hack || ___pawn.CurJob.targetA.Thing != cargo) return;
            target = SpaceWeaponGeometry.RotatedCell(cargo, new IntVec3(0, 0, 1));
        }
    }

    // Exposed hull modules use outdoor ambient, including the optional ASF thermal transform.
    [HarmonyPatch(typeof(Thing), nameof(Thing.AmbientTemperature), MethodType.Getter)]
    internal static class Patch_SpaceCargoOutdoorTemperature
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Thing __instance, ref float __result)
        {
            ISpaceCargoContainer cargo = __instance.ParentHolder as Building_SealedSpaceCargoContainer
                ?? __instance.StoringAdaptiveStorage() as ISpaceCargoContainer;
            if (cargo?.Spawned != true || !cargo.Contents.Contains(__instance)) return;
            __result = cargo.Thing.GetComp<CompSpaceCargo>().CargoTemperature;
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

        private static void Postfix(bool __result, bool actuallyTraded)
        {
            if (!__result || !actuallyTraded
                || TradeSession.trader is not Building_SpaceTradeTerminal terminal
                || !terminal.Spawned) return;
            Current.Game?.GetComponent<UniverseManager>()?.PublishPlayerMapActivity(
                terminal.Map, PlayerContactKind.Trade, terminal);
        }
    }
}

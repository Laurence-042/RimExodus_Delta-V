using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    [HarmonyPatch(typeof(RefuelWorkGiverUtility), nameof(RefuelWorkGiverUtility.CanRefuel))]
    internal static class Patch_GunSupplyCanRefuel
    {
        private static bool Prefix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (t.TryGetComp<CompGunSupply>() == null) return true;
            __result = GunSupplyUtility.FindSupply(pawn, t, forced) != null;
            return false;
        }

        private static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result || t.TryGetComp<CompAfterburnerTank>() == null
                || t.TryGetComp<CompInteractable>() is CompInteractable { OnCooldown: true } interactable
                    && interactable.Props.cooldownPreventsRefuel) return;
            __result = GunSupplyUtility.FindSupply(pawn, t, forced, afterburner: true) != null;
        }
    }

    [HarmonyPatch(typeof(RefuelWorkGiverUtility), nameof(RefuelWorkGiverUtility.RefuelJob))]
    internal static class Patch_GunSupplyRefuelJob
    {
        private static bool Prefix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            if (t.TryGetComp<CompAfterburnerTank>() != null)
            {
                if (GunSupplyUtility.CanLoad(t.TryGetComp<CompRefuelable>(), forced)) return true;
                __result = GunSupplyUtility.RefuelJob(pawn, t, forced, afterburner: true);
                return __result == null;
            }
            if (t.TryGetComp<CompGunSupply>() == null) return true;
            __result = GunSupplyUtility.RefuelJob(pawn, t, forced);
            return false;
        }
    }

    /// <summary>弹药容器改变后只登记地图；炮手岗位在下一 tick 走统一可用性判定。</summary>
    [HarmonyPatch]
    internal static class Patch_SpaceWeaponMagazineChanged
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CompRefuelable), nameof(CompRefuelable.ConsumeFuel),
                new[] { typeof(float) });
            yield return AccessTools.Method(typeof(CompChangeableProjectile),
                nameof(CompChangeableProjectile.LoadShell));
            yield return AccessTools.Method(typeof(CompChangeableProjectile),
                nameof(CompChangeableProjectile.RemoveShell));
            yield return AccessTools.Method(typeof(CompChangeableProjectile),
                nameof(CompChangeableProjectile.Notify_ProjectileLaunched));
        }

        private static void Postfix(ThingComp __instance)
        {
            ThingWithComps parent = __instance.parent;
            if (parent?.TryGetComp<CompSpaceWeaponMagazine>() == null) return;
            ShipControlService.NotifyGunnerChange(parent.Map);
        }
    }
}

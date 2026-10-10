using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>原版同图 Verb 的 1 fuel/shot 映射到同一 Magazine 单发消耗。</summary>
    [HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.ConsumeFuel))]
    public static class Patch_CompRefuelable_SpaceWeaponAmmoCost
    {
        public static void Prefix(CompRefuelable __instance, ref float amount)
        {
            if (Mathf.Abs(amount - 1f) > 0.0001f) return;
            CompSpaceWeaponMagazine magazine =
                __instance.parent?.TryGetComp<CompSpaceWeaponMagazine>();
            if (magazine != null) amount = magazine.MagazineProps.ammoPerShot;
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

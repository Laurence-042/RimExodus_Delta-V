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
}

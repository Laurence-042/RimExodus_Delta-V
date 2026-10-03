using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>宿主离图后同格 Edifice 已消失；销毁前先保存其真实槽内插件。</summary>
    [HarmonyPatch(typeof(Building), nameof(Building.Destroy))]
    public static class Patch_SpaceWeaponAttachmentLifecycle
    {
        public static void Prefix(Building __instance, out List<ThingWithComps> __state)
        {
            __state = null;
            CompSpaceWeapon weapon = __instance.TryGetComp<CompSpaceWeapon>();
            if (weapon != null && __instance.Spawned)
                __state = new List<ThingWithComps>(weapon.AttachedThings());
            CompSpaceRailgun rail = __instance.TryGetComp<CompSpaceRailgun>();
            if (rail != null && __instance.Spawned)
                __state = new List<ThingWithComps>(rail.AttachedThings());
        }

        public static void Postfix(Building __instance, DestroyMode mode, List<ThingWithComps> __state)
        {
            if (!__instance.Destroyed || __state == null) return;
            foreach (ThingWithComps attachment in __state)
            {
                if (!attachment.Destroyed) attachment.Destroy(mode);
            }
        }
    }
}

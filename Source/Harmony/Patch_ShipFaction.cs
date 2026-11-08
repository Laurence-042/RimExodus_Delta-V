using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>身份锚改派系后统一交接 controller，不依赖玩家打开面板触发。</summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetFaction))]
    internal static class Patch_ShipFaction
    {
        private static void Postfix(Thing __instance)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (!__instance.Spawned || universe?.FrameOf(__instance.Map) == null) return;
            // 武器派系也参与归属设备过滤；不能只让身份锚改派系使缓存失效。
            ShipStructureChanges.Notify(__instance.Map);
            foreach (CompSpaceWeapon weapon in CompSpaceWeapon.On(__instance as ThingWithComps))
                universe.Controls.ForgetWeaponTarget(weapon);
            ShipBase ship = ShipRegistry.ShipOf(__instance);
            if (ship.IsValid && ship.Anchor == __instance)
                universe.Controls.ReconcileOwner(ship.Identity, __instance.Map);
            ShipControlService.NotifyGunnerChange(__instance.Map);
        }
    }
}

using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版储物架从 edifice 读取单格容量；采矿货箱必须作为非 edifice 插件与炮座共格。
    /// 只在该格的真实 SlotGroup 属于采矿货箱时改读插件容量。
    /// </summary>
    [HarmonyPatch(typeof(GridsUtility), nameof(GridsUtility.GetMaxItemsAllowedInCell))]
    public static class Patch_MiningCargoStorageCapacity
    {
        public static bool Prefix(IntVec3 c, Map map, ref int __result)
        {
            if (map != null && c.InBounds(map)
                && map.haulDestinationManager.SlotGroupParentAt(c)
                    is Building_MiningCargoAttachment { Spawned: true } cargo)
            {
                __result = cargo.MaxItemsInCell;
                return false;
            }
            return true;
        }
    }

    /// <summary>非 edifice 货箱仍保留原版小物品架的防变质语义。</summary>
    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.ProtectedByEdifice))]
    public static class Patch_MiningCargoStorageProtection
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (!__result && map != null && c.InBounds(map)
                && map.haulDestinationManager.SlotGroupParentAt(c)
                    is Building_MiningCargoAttachment { Spawned: true })
                __result = true;
        }
    }
}

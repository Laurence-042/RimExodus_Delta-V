using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(Designator_MoveGravship), nameof(Designator_MoveGravship.SelectedUpdate))]
    public static class Patch_DockingPort_LandingPreview
    {
        public static void Postfix(Designator_MoveGravship __instance)
        {
            DockingChannelPreview.Draw(__instance);
        }
    }

    // 原版单格门在每次绘制前按邻墙改 Rotation；对接口的 Rotation 是玩家指定的伸出方向。
    [HarmonyPatch(typeof(Building_Door), "DoorPreDraw")]
    public static class Patch_DockingPort_Facing
    {
        public static bool Prefix(Building_Door __instance)
        {
            return !(__instance is Building_DockingPort);
        }
    }

    // 门房间的原版 Vacuum 会平均邻室压力，ExposedToSpace 则读取屋顶。
    // 力场通行单元独立密封；仅覆盖本模组这两类 Portal，不改变普通房间/Space 的拓扑。
    [HarmonyPatch(typeof(Room), nameof(Room.ExposedToSpace), MethodType.Getter)]
    public static class Patch_DockingPort_Exposure
    {
        public static bool Prefix(Room __instance, ref bool __result)
        {
            if (!(__instance.Door is Building_SealedDockingDoor door) || !door.IsAirtight) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.Vacuum), MethodType.Getter)]
    public static class Patch_DockingPort_Vacuum
    {
        public static bool Prefix(Room __instance, ref float __result)
        {
            if (!(__instance.Door is Building_SealedDockingDoor door) || !door.IsAirtight) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class Patch_DockingPort_TerrainChanged
    {
        public static void Postfix(TerrainGrid __instance, IntVec3 c)
        {
            Building_DockingPort.NotifyCellChanged(TerrainGridOwnershipAccess.MapOf(__instance), c);
        }
    }

    // 原版逆重起飞绕过 DoTerrainChangedEffects，仍须收回宿主一侧伸向离场甲板的通道。
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveGravshipTerrainUnsafe))]
    public static class Patch_DockingPort_GravshipTerrainRemoved
    {
        public static void Postfix(TerrainGrid __instance, IntVec3 cell)
        {
            Building_DockingPort.NotifyCellChanged(TerrainGridOwnershipAccess.MapOf(__instance), cell);
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.DeSpawn))]
    public static class Patch_DockingPort_StructureRemoved
    {
        public static void Prefix(Building __instance, out (Map map, CellRect cells) __state)
        {
            __state = (__instance.Map, __instance.OccupiedRect());
        }

        public static void Postfix((Map map, CellRect cells) __state)
        {
            if (__state.map == null) return;
            foreach (IntVec3 cell in __state.cells)
                Building_DockingPort.NotifyCellChanged(__state.map, cell);
        }
    }
}

using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 缓存降落 marker（Destroy 前的引用）供 LandingApproachController 到达重放时
    /// 复活原版 InitiateLanding 依赖的 GravshipCells；接管判定见 TryConsume。
    /// </summary>
    [HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.BeginLanding))]
    public static class Patch_GravshipLanding
    {
        public static void Prefix(GravshipLandingMarker __instance)
        {
            LandingApproachController.CaptureMarker(__instance);
        }
    }

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.TravelTo))]
    public static class Patch_GravshipUtility_TravelTo_DeltaVMobile
    {
        public static bool Prefix(Gravship gravship)
        {
            return !DockController.TryCompleteMobileLaunch(gravship);
        }
    }

    /// <summary>
    /// 口袋图起飞的战略源地址改写与 Delta-V 身份清理（星区架构 §6/§9）：原版
    /// InitiateTakeoff 把 engine.Map.Tile 记为 takeoffTile，口袋图无 tile；TakeoffEnded →
    /// TravelTo 会拿它做跨层换算并登记进 WorldObjects。Postfix 在字段刚写入后改写为
    /// TileFromDirection(船 pose)——原版起飞管线中一切以源图 tile 为输入的环节由此改接源地址。
    /// 详见 DockController.HandleVanillaTakeoffFromDeltaVSource。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    public static class Patch_GravshipController_InitiateTakeoff_DeltaV
    {
        public static void Postfix(WorldComponent_GravshipController __instance, Building_GravEngine engine)
        {
            DockController.HandleVanillaTakeoffFromDeltaVSource(engine, __instance);
        }
    }

    /// <summary>
    /// 逆重战略飞行到达链（星区架构 §4 触发时机的到达分支）：在生成/复用降落图之前
    /// 对目标星区执行首次生成，然后降落到其中对应 worldtile 的对象上。幂等。
    /// </summary>
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.ArriveNewMap))]
    public static class Patch_GravshipUtility_ArriveNewMap_DeltaVSector
    {
        public static void Prefix(Gravship gravship)
        {
            SectorContentService.EnsureSectorGenerated(
                Current.Game?.GetComponent<UniverseManager>(), gravship.destinationTile);
        }
    }

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.ArriveExistingMap))]
    public static class Patch_GravshipUtility_ArriveExistingMap_DeltaVSector
    {
        public static void Prefix(Gravship gravship)
        {
            SectorContentService.EnsureSectorGenerated(
                Current.Game?.GetComponent<UniverseManager>(), gravship.destinationTile);
        }
    }
}

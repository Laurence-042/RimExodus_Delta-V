using System;
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
            Current.Game?.GetComponent<UniverseManager>()?.Approach.CaptureMarker(__instance);
        }
    }

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.TravelTo))]
    public static class Patch_GravshipUtility_TravelTo_DeltaVMobile
    {
        public static bool Prefix(Gravship gravship)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe?.ShipModes.TryCompleteMobileLaunch(gravship) != true;
        }

        // 走到这里说明 TravelTo 被放行（原版战略起飞完成；移动模式在 Prefix 已消费
        // 并拦截，不会执行本 Postfix）——善后时机见 HandleStrategicTravelDeparture：
        // 此刻相机未必离开源图（战略航行中 CurrentMap 停在源图，焦点在那里显式处理）。
        public static void Postfix()
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipModes.HandleStrategicTravelDeparture();
        }
    }

    /// <summary>
    /// 口袋图战略发射的世界视图入口（星区架构 §6/§7）：口袋图 parent 只进
    /// Find.World.pocketMaps、不在 Find.WorldObjects 中，Spawned 恒 false，原版开头的
    /// CameraJumper.TryJump(GetWorldTarget(parent)) 得到 Invalid 而 no-op——TilePicker
    /// 的选点 UI 只在世界视图激活时被 WorldInterface 泵送，于是静默失活。Prefix 先
    /// 跳到战略源地址 tile 切入世界视图；原版随后的 TryJump(Invalid) 无害。真实
    /// tile 图早退零影响。
    /// </summary>
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_CompPilotConsole_StartChoosingDestination_WorldJump
    {
        public static void Prefix(CompPilotConsole __instance)
        {
            Map map = __instance.parent?.Map;
            if (map == null || map.Tile.Valid) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            PlanetTile address = universe?.ShipModes.StrategicSourceAddress(universe, map) ?? PlanetTile.Invalid;
            if (address.Valid) CameraJumper.TryJump(address);
        }
    }

    /// <summary>
    /// 选点界面「Invalid tile 不得进入 FastTileFinder」守卫：原版 onStart/onGui lambda
    /// 捕获的局部 curTile 在口袋图上为 Invalid（Layer 假象为表面层），与选中的轨道层
    /// 不等时原版直接把 -1 传给 FastTileFinder（GetTileCenter(-1) 每帧红字）。curTile
    /// 是方法体内局部变量、无 patch 边界，全网唯一共享出口是本方法。Invalid 输入只在
    /// 「当前聚焦图是可解析战略源地址的 Delta-V 口袋图」时接管：地址恰在被查询层上时
    /// 圆环/悬停距离按星区 tile 计算，否则 Invalid。其余 Invalid 语境一律放行原版——
    /// Invalid 是全体口袋图共享的信号，其他 mod 的口袋图处理照常生效，不互相吞 patch。
    /// </summary>
    [HarmonyPatch(typeof(PlanetLayer), nameof(PlanetLayer.GetClosestTile_NewTemp))]
    public static class Patch_PlanetLayer_GetClosestTile_InvalidSource
    {
        public static bool Prefix(PlanetLayer __instance, PlanetTile other, ref PlanetTile __result)
        {
            if (other.Valid) return true;
            Map map = Find.CurrentMap;
            if (map == null || map.Tile.Valid) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            PlanetTile address = universe?.ShipModes.StrategicSourceAddress(universe, map) ?? PlanetTile.Invalid;
            if (!address.Valid) return true;
            __result = address.Layer == __instance ? address : PlanetTile.Invalid;
            return false;
        }
    }

    /// <summary>
    /// 口袋图战略发射的燃料/距离计算源地址改写（2026-09-19 放开移动模式发射）：
    /// 口袋图 Map.Tile 无效，原版 TryGetPathFuelCost 对 Invalid 起点给出天文距离 →
    /// 选图界面全部「燃料不足」。Prefix 在「起点无效且当前聚焦图可解析出 Delta-V
    /// 战略源地址」时改写——船自有口袋图与停靠 POI 口袋图同口径（§7：口袋图起飞
    /// 一律视为从所在星区起飞）。validator、悬浮标签与 ConsumeFuel 全部按真实星区
    /// 距离计算；起点恒有效的调用零影响。
    /// </summary>
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.TryGetPathFuelCost))]
    public static class Patch_GravshipUtility_TryGetPathFuelCost_SectorSource
    {
        public static void Prefix(ref PlanetTile from)
        {
            if (from.Valid) return;
            Map map = Find.CurrentMap;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            PlanetTile address = universe?.ShipModes.StrategicSourceAddress(universe, map) ?? PlanetTile.Invalid;
            if (address.Valid) from = address;
        }
    }

    /// <summary>
    /// 无 tile 源图战略发射的弃置警告补全（2026-09-19 用户决策；2026-09-20 星区标准
    /// 流程扩展到一切无 tile 源）：原版 PreLaunchConfirmation 文案只覆盖「当前地图」，
    /// 战略发射善后会按无锚语义弃置所在星区 tile 的图——自有飞行口袋图与停靠 POI
    /// 口袋图（wreck 等）统一，先弹星区弃置确认，确认后链回原版弹窗（滞留者列表
    /// 口径经捕获域 patch，准确）。chaining 标志防递归：链回的调用直接放行原方法。
    /// </summary>
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.PreLaunchConfirmation))]
    public static class Patch_GravshipUtility_PreLaunchConfirmation_Sector
    {
        public static bool Prefix(Building_GravEngine engine, Action launchAction)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return true;
            if (universe.ShipModes.preLaunchConfirmationChaining)
            {
                universe.ShipModes.preLaunchConfirmationChaining = false;
                return true;
            }
            Map sourceMap = engine?.Map;
            if (sourceMap == null || sourceMap.Tile.Valid) return true;
            // 星区标准流程第 ② 段对任意源执行锚规则弃置；发射前船仍注册（自有口袋
            // 或停靠），DepartedSectorMapFor 的地址解析链有效。
            Map sectorMap = universe.ShipModes.DepartedSectorMapFor(universe, sourceMap);
            if (!universe.ShipModes.SectorMapWillBeAbandoned(universe, sectorMap)) return true;
            string label = sectorMap.Parent?.Label ?? sectorMap.ToString();
            Find.WindowStack.Add(new Dialog_MessageBox(
                "DeltaV_SectorMapAbandonConfirm".Translate(label), null, delegate
                {
                    universe.ShipModes.preLaunchConfirmationChaining = true;
                    GravshipUtility.PreLaunchConfirmation(engine, launchAction);
                }));
            return false;
        }
    }

    /// <summary>
    /// 口袋图起飞的战略源地址改写与 Delta-V 身份清理（星区架构 §6/§9）：原版
    /// InitiateTakeoff 把 engine.Map.Tile 记为 takeoffTile，口袋图无 tile；TakeoffEnded →
    /// TravelTo 会拿它做跨层换算并登记进 WorldObjects。Postfix 在字段刚写入后改写为
    /// TileFromDirection(船 pose)——原版起飞管线中一切以源图 tile 为输入的环节由此改接源地址。
    /// 详见 ShipModeService.HandleVanillaTakeoffFromDeltaVSource。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    public static class Patch_GravshipController_InitiateTakeoff_DeltaV
    {
        public static void Postfix(WorldComponent_GravshipController __instance, Building_GravEngine engine)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipModes
                .HandleVanillaTakeoffFromDeltaVSource(engine, __instance);
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

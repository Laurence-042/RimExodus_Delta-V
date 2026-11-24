using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.TravelTo))]
    public static class Patch_GravshipUtility_TravelTo_DeltaVMobile
    {
        // 解耦重构（2026-09-24）：脱离不再经原版起飞/旅行管线（无引擎直接搬运），
        // 本 Prefix 的自由飞行拦截退役；TravelTo 只服务原版战略发射。

        // 战略起飞放行后的星区离场善后：此刻相机未必离开源图（战略航行中 CurrentMap
        // 停在源图，焦点在那里显式处理）。
        public static void Postfix()
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions.HandleStrategicTravelDeparture();
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
        private static readonly MethodInfo MapTileGetter =
            AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile));
        private static readonly MethodInfo StrategicSelectionTile =
            AccessTools.Method(typeof(ShipFlightTransitionService),
                nameof(ShipFlightTransitionService.StrategicSelectionTile));

        public static void Prefix(CompPilotConsole __instance)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions
                .OpenStrategicDestinationPicker(__instance);
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var list = new List<CodeInstruction>(instructions);
            var matches = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction instruction = list[i];
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && Equals(instruction.operand, MapTileGetter))
                {
                    matches.Add(i);
                }
            }
            if (matches.Count != 2)
            {
                Log.Warning($"[DeltaV] Strategic destination patch signature drift in " +
                    $"{__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}: found " +
                    $"{matches.Count} Map.Tile call(s), expected 2; leaving the method unchanged.");
                return list;
            }
            foreach (int index in matches)
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = StrategicSelectionTile;
            }
            return list;
        }
    }

    /// <summary>
    /// 自由飞行口袋图战略发射的燃料/距离计算源地址改写：
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
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions
                .RewriteStrategicSourceTile(Find.CurrentMap, ref from);
        }
    }

    /// <summary>
    /// 口袋图起飞的战略源地址改写与 Delta-V 身份清理（星区架构 §6/§9）：原版
    /// InitiateTakeoff 把 engine.Map.Tile 记为 takeoffTile，口袋图无 tile；TakeoffEnded →
    /// TravelTo 会拿它做跨层换算并登记进 WorldObjects。Postfix 在字段刚写入后改写为
    /// TileFromDirection(船 pose)——原版起飞管线中一切以源图 tile 为输入的环节由此改接源地址。
    /// 详见 ShipFlightTransitionService.HandleVanillaTakeoffFromDeltaVSource。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    public static class Patch_GravshipController_InitiateTakeoff_DeltaV
    {
        public static void Postfix(WorldComponent_GravshipController __instance, Building_GravEngine engine)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions
                .HandleVanillaTakeoffFromDeltaVSource(engine, __instance);
        }
    }

    /// <summary>战略降落冻结范围、结构归属盖章和落定收尾的原版事件接线。</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
    public static class Patch_GravshipController_InitiateLanding_DeltaV
    {
        public static void Prefix(Gravship gravship, Map map, IntVec3 landingPos)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions
                .BeginStrategicLanding(gravship, map, landingPos);
        }
    }

    /// <summary>原版到达画面只读取战略降落开始时冻结的本船 foundation。</summary>
    [HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.ValidSubstructure), MethodType.Getter)]
    public static class Patch_GravEngine_ConversionCaptureScope
    {
        public static void Postfix(Building_GravEngine __instance, ref HashSet<IntVec3> __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.ShipTransitions.TryGetLandingCaptureScope(__instance,
                    out HashSet<IntVec3> scope) == true)
            {
                __result = scope;
            }
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    public static class Patch_GravshipController_LandingEnded
    {
        public static void Prefix(Map ___map, out Map __state)
        {
            __state = ___map;
        }

        public static void Postfix(Map __state)
        {
            Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions
                .CompleteStrategicLanding(__state);
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

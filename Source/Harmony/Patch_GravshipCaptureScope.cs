using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 固定↔移动转换的源端捕获范围 = 控制台所属引擎的整个相连基架连通域
    /// （AllConnectedSubstructure），不受引擎力场脚印限制，允许船体超出力场。
    /// 移动→固定的目标端二次画面捕获则返回落地前冻结的 Gravship foundation 集合，
    /// 禁止按目标图现场拓扑把相邻 wreck 并入飞船形态。
    /// 原版战略发射（玩家从驾驶台发起的原版起飞）不经门控，仍按力场内捕获
    /// （"逆重发射只能带走范围内的结构"）。
    /// 捕获管线的全部读者（GravshipUtility.GenerateGravship、Gravship 构造器、
    /// GravshipCapturer 捕获边界、WorldComponent_GravshipController）都经
    /// ValidSubstructure 属性读取，此处一处收口。
    /// </summary>
    [HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.ValidSubstructure), MethodType.Getter)]
    public static class Patch_GravEngine_ConversionCaptureScope
    {
        public static void Postfix(Building_GravEngine __instance, ref HashSet<IntVec3> __result)
        {
            if (!DockController.TryGetConversionCaptureScope(__instance, out HashSet<IntVec3> scope))
            {
                return;
            }
            __result = scope;
        }
    }

    /// <summary>
    /// 降落放置前冻结本船 foundation（战术降落与原版战略到达两条路径统一在此覆盖；
    /// 放置发生在 InitiateLanding 内的 LongEvent，Prefix 时序在一切读取之前）：
    /// PlaceGravship 后原版会再次读取 engine.ValidSubstructure 制作降落画面/遮罩；
    /// 若读现场集合，邻接且处于力场内的 wreck 会被误并入飞船形态。冻结集来自
    /// Gravship 自身的 Foundations（起飞捕获时已定格），与目标图现场拓扑无关。
    /// 仅 Delta-V 语境图生效；地表降落完全走原版语义。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
    public static class Patch_GravshipController_InitiateLanding_FreezeFoundation
    {
        public static void Prefix(Gravship gravship, Map map, IntVec3 landingPos)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) == null || gravship?.Engine == null)
            {
                return;
            }
            var cells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys)
            {
                cells.Add(cell + landingPos);
            }
            DockController.SetLandingCaptureScope(gravship.Engine, cells);
        }
    }
}

using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 引擎 ValidSubstructure 的到达画面冻结范围收口（2026-09-24 解耦重构）：
    /// 覆盖**冻结的 Gravship foundation 集合**（Patch_LandingApproach 的 InitiateLanding
    /// Prefix 冻结，服务原版战略到达的二次画面捕获），禁止按目标图现场拓扑把相邻
    /// wreck 并入飞船形态。
    /// 旧的「口袋图战略发射 = 整域绕过引擎力场」例外已按解耦架构 §12 退役：战略发射
    /// 捕获回到原版力场脚印语义（「逆重发射只能带走引擎覆盖范围内的结构」），归属
    /// 过滤由 Patch_SubstructureOwnership 的连通域 prefix 单点承担。
    /// 捕获管线的全部读者（GravshipUtility.GenerateGravship、Gravship 构造器、
    /// GravshipCapturer 捕获边界、WorldComponent_GravshipController）都经
    /// ValidSubstructure 属性读取，此处一处收口。
    /// </summary>
    [HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.ValidSubstructure), MethodType.Getter)]
    public static class Patch_GravEngine_ConversionCaptureScope
    {
        public static void Postfix(Building_GravEngine __instance, ref HashSet<IntVec3> __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null
                || !universe.ShipTransitions.TryGetLandingCaptureScope(__instance, out HashSet<IntVec3> scope))
            {
                return;
            }
            __result = scope;
        }
    }
}

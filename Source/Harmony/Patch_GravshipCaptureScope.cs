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
    /// 移动→固定的目标端二次画面捕获则返回落地前冻结的 Gravship foundation 集合
    /// （Patch_LandingApproach 的 InitiateLanding Prefix 冻结，覆盖到达重放与原版
    /// 战略到达两条路径），禁止按目标图现场拓扑把相邻 wreck 并入飞船形态。
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
}

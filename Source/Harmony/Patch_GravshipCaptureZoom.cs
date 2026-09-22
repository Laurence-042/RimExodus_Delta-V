using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版起飞/降落 cinematic 的缩放包络在捕获时刻从 CameraDriver.config.sizeRange.max
    /// 快照（GravshipCapturer.BeginGravshipRender/BeginTerrainRender 的 maxCameraSize/
    /// minCameraSize），整段过场的 pan 与 SetRootSize 插值都消费这个包络，结果还会被
    /// rememberedCameraPos 持久化。Delta-V 在参考系图上把该上限放宽到宇宙视野上限
    /// （默认 400），不还原的话战略起飞/降落会把相机拉到超广。
    /// 捕获窗口内临时压回原版值即可让快照恢复原版包络（~30..60）；快照在方法内同步
    /// 完成，Postfix 在返回前恢复放宽值，UniverseManager 每帧的再放宽不受影响。
    /// 非放宽状态（含 RimExodus 地表）零行为变化。
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GravshipCapturer_VanillaZoomEnvelope
    {
        private struct State
        {
            public CameraMapConfig config;
            public float raisedMax;
            public bool lowered;
        }

        // Harmony 的多个 [HarmonyPatch(type, method)] 类特性不是“补丁两个方法”的列表，
        // 而是同一目标描述的组成部分/覆盖项。显式枚举才能保证两个 capture 入口都命中；
        // 其中 BeginGravshipRender 正是战略起飞缩放快照的关键入口。
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginGravshipRender));
            yield return AccessTools.Method(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginTerrainRender));
        }

        private static void Prefix(ref State __state)
        {
            CameraDriver driver = Find.CameraDriver;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (driver?.config == null || !(universe?.TryGetVanillaZoomMax(out float vanilla) ?? false))
            {
                return;
            }
            if (driver.config.sizeRange.max <= vanilla)
            {
                return;
            }
            __state.config = driver.config;
            __state.raisedMax = driver.config.sizeRange.max;
            driver.config.sizeRange.max = vanilla;
            __state.lowered = true;
        }

        private static void Postfix(State __state)
        {
            if (!__state.lowered || __state.config == null)
            {
                return;
            }
            // 使用 Prefix 捕获的 config 实例恢复；切图过程中 Find.CameraDriver.config
            // 可能已被替换，恢复“当前实例”反而会污染另一张图的配置。
            __state.config.sizeRange.max = __state.raisedMax;
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段2 驾驶模式按键抑制（story 要求："驾驶模式中避免触发对应的原版按键"）。
    ///
    /// 原版 KeyBindingDef 是轮询式（Input.GetKey），没有事件消费机制：
    /// CameraDriver（WASD 平移，CameraDriver.Update 326-340）与设计器
    /// （Q/E 旋转，经 KeyDownEvent）都在各自循环里直接读这三个 getter——
    /// 只能在读取入口收口。
    ///
    /// 目标方法用 TargetMethods() 显式枚举（2026-09-13 修正：多个类级
    /// [HarmonyPatch] 特性会被 Harmony 合并成单一目标——标量字段后者覆盖前者，
    /// 实测只挂上最后一个 getter，相机走的 IsDown 未被打补丁，导致驾驶中相机仍平移）。
    ///
    /// 诊断（供 DebugActions "Diagnose key suppression"）：SuppressedReads 计数
    /// 抑制命中的读取次数；logHarmony 门控下每个 def 只记一次命中明细。
    /// </summary>
    [HarmonyPatch]
    public static class Patch_KeyBindingSuppression
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDown));
            yield return AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.JustPressed));
            yield return AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.KeyDownEvent));
        }

        public static bool Prefix(KeyBindingDef __instance, ref bool __result)
        {
            if (!PilotKeyBridge.SuppressVanillaKeys)
            {
                return true;
            }
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Pilot.TrySuppressBinding(__instance) == true)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}

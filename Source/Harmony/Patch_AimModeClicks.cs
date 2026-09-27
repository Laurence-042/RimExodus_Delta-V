using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 瞄准会话内的原版点击让位（太空战架构 §6.1/§10）：瞄准模式激活时左键 = 主套
    /// 射击、右键 = 副套射击——原版左键点选/框选（Selector）与右键命令浮单
    ///（同样产自 SelectorOnGUI）、建造设计器与 Targeter 输入全部让位。让位条件与
    /// HandleShellClick 同族：Targeter 激活（ESC/取消语义）与悬停窗口优先。
    /// 不 patch 原版 verb/射击代码；巡航模式零介入。
    /// </summary>
    [HarmonyPatch]
    public static class Patch_AimModeClicks
    {
        /// <summary>Prefix 返回 false = 跳过原版点击处理（void 方法，无需 __result）。</summary>
        public static bool Prefix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || !universe.Weapons.AimSessionOnFocusedMap) return true;
            if (Find.CurrentMap == null || !WorldRendererUtility.DrawingMap) return true;
            // 悬停窗口（含本 mod 面板）：窗口交互优先，点击交还原版。
            if (Find.WindowStack.GetWindowAt(Verse.UI.GUIToScreenPoint(Verse.UI.MousePositionOnUI)) != null)
            {
                return true;
            }
            // 让位中让 Targeter 保留输入（取消会话语义，ShellPicking 同族契约）。
            if (Find.Targeter.IsTargeting) return true;
            return false;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleMapClicks));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleLowPriorityInput));
        }
    }
}

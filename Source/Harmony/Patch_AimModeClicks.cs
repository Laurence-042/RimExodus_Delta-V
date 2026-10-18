using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

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
        public static bool Prefix(MethodBase __originalMethod)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (__originalMethod.Name == nameof(MapInterface.HandleLowPriorityInput))
                universe?.Controls.ProcessAimInputEvent();
            if (universe == null || !universe.Controls.AimSessionOnFocusedMap) return true;
            if (Find.CurrentMap == null || !WorldRendererUtility.DrawingMap) return true;
            // 悬停窗口（含本 mod 面板）：窗口交互优先，点击交还原版。
            if (SpaceAimController.MouseOverUiWindow())
            {
                return true;
            }
            // 让位中让 Targeter 保留输入（取消会话语义，ShellPicking 同族契约）。
            if (Find.Targeter.IsTargeting) return true;
            // designator 选中（对接/降落选点）时放行：DesignatorManager 的
            // ESC/右键取消路径不能被瞄准会话整体吞掉。
            if (Find.DesignatorManager.SelectedDesignator != null) return true;
            return false;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleMapClicks));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleLowPriorityInput));
        }
    }

    // Vanilla's explicit input-absorbing rectangles also block a held salvo. Keep their
    // actual transformed geometry rather than duplicating gizmo/time-control layouts.
    internal static class AimUiRegions
    {
        private static readonly List<Rect> regions = new List<Rect>();
        internal static void Clear() => regions.Clear();
        internal static void Add(Rect rect) => regions.Add(UI.GUIToScreenRect(rect));
        internal static bool Contains(Vector2 point) => regions.Exists(rect => rect.Contains(point));
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class Patch_AimUiRegions_Begin
    {
        private static void Prefix() => AimUiRegions.Clear();
    }

    [HarmonyPatch(typeof(GenUI), nameof(GenUI.AbsorbClicksInRect))]
    internal static class Patch_AimUiRegions_Record
    {
        private static void Prefix(Rect r)
        {
            if (Current.Game?.GetComponent<UniverseManager>()?.Controls.AimActive == true)
                AimUiRegions.Add(r);
        }
    }
}

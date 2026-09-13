using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// POI 图软休眠的运行时拦截（阶段3；RimExodus Patches_Dormancy 同款模式）。
    ///
    /// - Map.MapPreTick / MapPostTick / MapUpdate：休眠 POI 图早退（原版
    ///   TickManager/Game.UpdatePlay 直接遍历 Find.Maps 调用，无其他拦截点）。
    ///   跳过即停全部组件模拟/更新；外壳代理渲染（ShellRenderer）是自驱动重放，
    ///   不依赖目标图的 MapUpdate，休眠图照常作为代理显示。
    /// - Game.CurrentMap setter：进入休眠 POI 图 = 玩家的显式唤醒意图，先唤醒再切图
    ///   （单点覆盖全部进入路径：殖民者栏分组框、地图按钮、CameraJumper、外壳点击）。
    /// - Storyteller.AllIncidentTargets：剔除休眠 POI 图，防事件打到冻结图上。
    ///
    /// 判定集合只含 Delta-V 自己的 POI 图（SpaceLifecycle 维护），与 RimExodus 的
    /// 地表休眠体系集合不相交、互不干扰（AGENTS §16/§3）。
    /// </summary>
    public static class Patch_PoiDormancy
    {
        [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
        private static class Patch_Map_MapPreTick
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsDormant(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
        private static class Patch_Map_MapPostTick
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsDormant(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        private static class Patch_Map_MapUpdate
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsDormant(__instance);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
        private static class Patch_Game_CurrentMap_Setter
        {
            private static void Prefix(Map value)
            {
                if (value != null && SpaceLifecycle.IsDormant(value))
                {
                    // 同步唤醒（轻量，无生成），玩家切进去的图不能是冻结的。
                    SpaceLifecycle.Wake(value, "entering map (CurrentMap switch)");
                }
            }
        }

        [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets), MethodType.Getter)]
        private static class Patch_Storyteller_AllIncidentTargets
        {
            private static void Postfix(List<IIncidentTarget> __result)
            {
                // 返回的是原版静态缓存列表，调用方即用即弃，原地 RemoveAll 安全（RimExodus 同款结论）。
                __result.RemoveAll(t => t is Map m && SpaceLifecycle.IsDormant(m));
            }
        }
    }
}

using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
    internal static class Patch_Game_CurrentMapFocus
    {
        private static Map CurrentMapIfValid(Game game)
        {
            int index = game?.currentMapIndex ?? -1;
            return index >= 0 && index < game.Maps.Count ? game.Maps[index] : null;
        }

        private static Map NextFocusableMap(Game game, Map requested)
        {
            int count = game?.Maps.Count ?? 0;
            int requestedIndex = game?.Maps.IndexOf(requested) ?? -1;
            if (requestedIndex < 0) return null;
            for (int offset = 1; offset <= count; offset++)
            {
                Map candidate = game.Maps[(requestedIndex + offset) % count];
                if (!SpaceMapStateManager.IsUniverseHost(candidate)) return candidate;
            }
            return null;
        }

        private static bool Prefix(Game __instance, ref Map value)
        {
            // 原版删图时先移除旧图，后调用此 setter。旧索引此时可能越界。
            if (value != null && !__instance.Maps.Contains(value)) return true;
            if (value != null && SpaceMapStateManager.IsUniverseHost(value))
                value = NextFocusableMap(__instance, value);

            UniverseManager universe = __instance.GetComponent<UniverseManager>();
            if (value != null)
            {
                bool wasFrozen = universe?.MapStates.IsDormant(value) == true;
                if (wasFrozen)
                    universe.MapStates.Wake(value, "entering map (CurrentMap switch)");
                // 新档/读档的 setter 可在后台线程运行；Map.FinalizeInit 已安排主线程初次重建。
                // 运行时切图仍立即恢复冻结期间的脏 section，不能在后台创建材质和网格。
                if (UnityData.IsInMainThread && (wasFrozen || universe?.SpaceObjectOnMap(value) != null))
                    value.mapDrawer.RegenerateEverythingNow();
            }
            // MapUI 会在 setter 内恢复新图记忆的镜头；先按目标图设置上限。
            universe?.ApplyCameraZoomScope(value);
            return true;
        }

        private static void Postfix(Game __instance)
        {
            Map focused = CurrentMapIfValid(__instance);
            UniverseManager universe = __instance.GetComponent<UniverseManager>();
            if (universe?.TargetCamera is TargetCameraFocus focus && focus.SourceMap != focused)
                universe.TargetCamera = null;
            universe?.ApplyCameraZoomScope(focused);
            // 每次切入地图都应用聚焦船规则，不区分由哪个界面发起。读档在后台线程设置当前图，
            // 其规则由读档收尾的帧动作重新应用。
            if (focused != null && UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing)
                universe?.Controls.ActivateFocusedVessel(focused);
            CameraDriver driver = Find.CameraDriver;
            if (driver?.config != null && driver.RootSize > driver.config.sizeRange.max)
                driver.SetRootSize(driver.config.sizeRange.max);
        }
    }

    // SCS 缺席时 Prepare 跳过；它的设置写入完成后才读取最新 max。
    [HarmonyPatch]
    internal static class Patch_CameraZoomScope_SimpleCameraSetting
    {
        private static MethodBase target;

        private static bool Prepare()
        {
            Type type = AccessTools.TypeByName("SimpleCameraSetting.CameraConfigPatch");
            if (type == null) return false;
            target = AccessTools.Method(type, "ConfigPatch", Type.EmptyTypes);
            if (target != null && target.IsStatic && target is MethodInfo method && method.ReturnType == typeof(void))
                return true;
            Log.Warning("[DeltaV] SimpleCameraSetting.CameraConfigPatch.ConfigPatch() signature changed; zoom compatibility is disabled.");
            return false;
        }

        private static MethodBase TargetMethod() => target;

        private static void Postfix() =>
            Current.Game?.GetComponent<UniverseManager>()?.NotifyExternalCameraZoomMaxWritten(Find.CameraDriver?.config);
    }
}

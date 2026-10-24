using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.Notify_SwitchedMap))]
    internal static class Patch_CameraZoomScope_MapSwitch
    {
        // CurrentMap 已切换；原版随后用新图 rememberedCameraPos 设定缩放，须先换上限。
        private static void Prefix() =>
            Current.Game?.GetComponent<UniverseManager>()?.ApplyCameraZoomScope();

        private static void Postfix() =>
            Current.Game?.GetComponent<UniverseManager>()?.ApplyCameraZoomScope();
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

using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>原版捕获先添加设施，再确定发射方向；在两者之间补认组件驾驶台。</summary>
    [HarmonyPatch(typeof(Gravship), "AddThing")]
    internal static class Patch_GravshipPilotConsole
    {
        private static void Postfix(Gravship __instance, Thing thing)
        {
            Map map = __instance.Engine?.Map;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (map == null || universe?.FrameOf(map) == null
                || thing is not Building || !__instance.ContainsThing(thing)) return;
            if (thing.TryGetComp<CompDeltaVPilotConsole>() == null && __instance.PilotConsole != thing) return;
            // 原版添加普通 PilotConsole 时也可能把已选择的自定义主台覆盖为备用台。
            GravshipInterop.RefreshPilotConsole(__instance);
        }
    }
}

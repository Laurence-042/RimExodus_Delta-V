using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>仅搬运至源图收尾窗口禁存档；同步任务不落盘，飞行期间允许保存。</summary>
    [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SavingIsTemporarilyDisabled), MethodType.Getter)]
    public static class Patch_SavingIsTemporarilyDisabled_DeltaVRuntime
    {
        public static void Postfix(ref bool __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            if (universe.HasUnpublishedPayload || universe.ShipTransitions.HasLandedTargetAwaitingCleanup)
            {
                __result = true;
            }
        }
    }
}

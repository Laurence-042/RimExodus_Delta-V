using RimWorld;
using RimWorld.Planet;
using Verse;
using HarmonyLib;

namespace DeltaV
{
    /// <summary>
    /// 对接计划期间对进场船地图的发射封锁（2026-09-21 阶段4）：InitiateTakeoff 是原版
    /// 全部发射入口（驾驶台/仪式/dev）的唯一汇点——计划存续期从计划船的地图发起逆重
    /// 发射会与计划生命周期冲突（自动驾驶任务与目标引用悬空）；取消计划后
    /// 即可发射。建造/规划不封锁：真实捕获在开始作业时刻，同步期间的
    /// 扩建全部随捕获带走。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    public static class Patch_GravshipController_InitiateTakeoff_DockingPlan
    {
        public static bool Prefix(Building_GravEngine engine)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.AnyActive != true) return true;
            Map shipMap = universe.Approach.ApproachShipMap;
            if (shipMap == null || engine?.Map != shipMap) return true;
            Messages.Message("DeltaV_DockPlanBlocksLaunch".Translate(), MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}

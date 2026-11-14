using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>原版战略到达的基架捕获冻结；战术停靠直接走共同搬运链。</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
    public static class Patch_GravshipController_InitiateLanding_LandingApproach
    {
        public static bool Prefix(Gravship gravship, Map map, IntVec3 landingPos, Rot4 landingRot)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            FreezeLandingCaptureScope(universe, gravship, map, landingPos);
            return true;
        }

        /// <summary>
        /// 降落放置前冻结本船 foundation（原版战略到达路径覆盖；放置发生在 InitiateLanding
        /// 内的 LongEvent，Prefix 时序在一切读取之前）：PlaceGravship 后原版会再次读取
        /// engine.ValidSubstructure 制作降落画面/遮罩；若读现场集合，邻接且处于力场内的
        /// wreck 会被误并入飞船形态。冻结集来自 Gravship 自身的 Foundations（捕获时已
        /// 定格），与目标图现场拓扑无关。仅 Delta-V 语境图生效；地表降落完全走原版语义。
        /// 同一冻结集同时登记基架归属盖章（SetPendingStamp）：落点基架的归属 ID 按
        /// 引擎当前图归属（同图移动）→
        /// 驾驶台持久身份（战略到达：身份随 comp 飞行）→ 新分配 ID 解析。
        /// </summary>
        private static void FreezeLandingCaptureScope(UniverseManager universe,
            Gravship gravship, Map map, IntVec3 landingPos)
        {
            if (universe?.FrameOf(map) == null || gravship?.Engine == null)
            {
                return;
            }
            var cells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys)
            {
                cells.Add(cell + landingPos);
            }
            universe.ShipTransitions.SetLandingCaptureScope(map, gravship.Engine, cells);
            SubstructureOwnership.For(map).SetPendingStamp(cells, ResolveLandingOwnerId(universe, gravship));
        }

        private static int ResolveLandingOwnerId(UniverseManager universe, Gravship gravship)
        {
            // 战术转移不进入 InitiateLanding；战略到达只读取本次 Gravship 身份，
            // 不能借用同时进行的其他飞船对接状态。
            if (gravship.Engine.Spawned)
            {
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(gravship.Engine.Map);
                int owner = ownership?.OwnerAt(gravship.Engine.Position) ?? SubstructureOwnership.Unassigned;
                if (owner != SubstructureOwnership.Unassigned)
                {
                    return owner;
                }
            }
            // 战略到达（身份随驾驶台 comp 飞行）：落点域直接盖驾驶台的持久身份 ID
            // （comp 未 Spawned 也可解析/领号），归属域名跨飞行连续；无驾驶台的
            // gravship 兜底新领裸 ID。
            CompDeltaVPilotConsole identity = CompDeltaVPilotConsole.IdentityConsoleOf(gravship);
            if (identity != null)
            {
                return identity.ShipIdentityId(universe);
            }
            return universe.AllocateOwnerId();
        }
    }

    /// <summary>
    /// 原版 LandingEnded Postfix（战略到达链路）：清到达冻结捕获范围，通知对接口
    /// 自动铺设通道。战术对接完成不进本链路（直接放置，FinishDocking 自行收尾）。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    public static class Patch_GravshipController_LandingEnded
    {
        public static void Prefix(Map ___map, out Map __state)
        {
            __state = ___map;
        }

        public static void Postfix(Map __state)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipTransitions.ClearLandingCaptureScope();
            Building_DockingPort.NotifyLandingSettled(__state);
        }
    }

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

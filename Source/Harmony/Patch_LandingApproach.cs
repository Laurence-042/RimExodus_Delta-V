using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 战术降落接管（星区架构 §7；2026-09-24 解耦重构：完成路径 = 直接放置，不再重放
    /// 原版 InitiateLanding——到达重放期的 mask/cutscene/pan/curtain 守卫全部退役）。
    /// InitiateLanding Prefix 把 Delta-V 战术降落接到 LandingApproachController（进场
    /// 自动驾驶）；原版战略到达仍走原方法，foundation 冻结逻辑在此收口。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
    public static class Patch_GravshipController_InitiateLanding_LandingApproach
    {
        public static bool Prefix(Gravship gravship, Map map, IntVec3 landingPos, Rot4 landingRot)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.TryConsume(universe, gravship, map, landingPos, landingRot) == true)
            {
                return false;
            }
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
        /// 战术降落船（PendingDockingShip 的结构 ID）→ 引擎当前图归属（同图移动）→
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
            // 战术降落到达时 pendingShip 已被转移消费，改读 landedShipAwaitingCleanup
            // 窗口（完成 → 收尾）；其结构 ID 即落点归属。
            Ship pending = universe.ShipTransitions.PendingDockingShip(universe);
            if (pending.IsValid)
            {
                return pending.ownerId;
            }
            Ship landed = universe.ShipTransitions.LandedShipAwaitingPocketCleanup(universe);
            if (landed.IsValid)
            {
                return landed.ownerId;
            }
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
        public static void Postfix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipTransitions.ClearLandingCaptureScope();
            Building_DockingPort.NotifyLandingSettled();
        }
    }

    /// <summary>
    /// Delta-V 运行时流程禁存档门禁（2026-09-22 用户决策修订；2026-09-24 解耦重构：
    /// 到达重放窗口已退役）。仍禁存档的窗口：
    /// 选点阶段（原版会深序列化 marker.gravship 与控制器 landingMarker——空洞预览
    /// Gravship 不具备落盘语义）、对接完成→收尾之间的源图处置窗口（转移中状态，
    /// 毫秒级）、跳帮舱飞行（乘员在运行时 ThingOwner 容器中，落盘即丢失）。
    /// 计划飞行阶段可正常存档（计划与自动驾驶任务均为实例运行时数据、不落盘）。
    /// </summary>
    [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SavingIsTemporarilyDisabled), MethodType.Getter)]
    public static class Patch_SavingIsTemporarilyDisabled_DeltaVRuntime
    {
        public static void Postfix(ref bool __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            // 选点阶段 = pendingShip 已置位但计划尚未发布（发布后 pendingShip 保留到
            // 完成转移，由 AnyActive 区分选点与计划两段）。
            bool siteSelection = (universe.ShipTransitions.PendingDockingShip(universe).IsValid
                    || universe.ShipTransitions.pendingShuttleBody != null)
                && !universe.Approach.AnyActive;
            bool transferPending = universe.ShipTransitions.LandedShipAwaitingPocketCleanup(universe).IsValid;
            if (siteSelection || transferPending)
            {
                __result = true;
            }
        }
    }
}

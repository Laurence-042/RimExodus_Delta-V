using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 战术降落接管（星区架构 §7）。
    /// InitiateLanding Prefix 把 Delta-V 战术降落接到 LandingApproachController（进场
    /// 自动驾驶）；到达重放与原版战略到达仍走原方法，foundation 冻结逻辑在此收口。
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
        /// 降落放置前冻结本船 foundation（战术降落到达重放与原版战略到达两条路径统一
        /// 覆盖；放置发生在 InitiateLanding 内的 LongEvent，Prefix 时序在一切读取之前）：
        /// PlaceGravship 后原版会再次读取 engine.ValidSubstructure 制作降落画面/遮罩；
        /// 若读现场集合，邻接且处于力场内的 wreck 会被误并入飞船形态。冻结集来自
        /// Gravship 自身的 Foundations（捕获时已定格），与目标图现场拓扑无关。
        /// 仅 Delta-V 语境图生效；地表降落完全走原版语义。
        /// 同一冻结集同时登记基架归属盖章（SetPendingStamp）：落点基架的归属 ID 按
        /// 战术降落船（PendingDockingShip）→ 引擎当前图归属（同图移动）→ 新分配 ID
        /// （原版战略到达：身份已释放，落地即独立域，bootstrap 时再具象化）解析。
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
            universe.ShipModes.SetLandingCaptureScope(map, gravship.Engine, cells);
            SubstructureOwnership.For(map).SetPendingStamp(cells, ResolveLandingOwnerId(universe, gravship));
        }

        private static int ResolveLandingOwnerId(UniverseManager universe, Gravship gravship)
        {
            // 战术降落到达重放时 pendingShip 已被 WriteDockedState 清空，改读
            // landedShipAwaitingPocketCleanup 窗口（WriteDockedState → LandingEnded）。
            ShipState pending = universe.ShipModes.PendingDockingShip(universe)
                ?? universe.ShipModes.LandedShipAwaitingPocketCleanup(universe);
            if (pending != null)
            {
                return pending.objectId;
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
            // 战略到达（身份已注销）：驾驶台 = 身份锚点——落点域直接盖驾驶台的持久
            // 身份 ID（comp 未 Spawned 也可解析/领号），归属域名跨飞行连续；无驾驶台
            /// 的 gravship 兜底新领裸 ID。
            CompDeltaVPilotConsole identity = CompDeltaVPilotConsole.IdentityConsoleOf(gravship);
            if (identity != null)
            {
                return identity.ShipIdentityId(universe);
            }
            return universe.AllocateOwnerId();
        }
    }

    /// <summary>
    /// 对接计划期间跳过 GravshipMask 层重生成：到达重放（ArrivalPending）期间
    /// CutsceneInProgress=true 会让 None 模式走阴影分支（此时
    /// SectionLayer_GravshipMask.Engine 为空）。计划飞行阶段无 cutscene、模拟照常，
    /// 不需要跳过；到达重放阶段恢复正常重生成。
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_GravshipMask), nameof(SectionLayer_GravshipMask.Regenerate))]
    public static class Patch_GravshipMask_Regenerate_Approach
    {
        public static bool Prefix()
        {
            return Current.Game?.GetComponent<UniverseManager>()?.Approach.ArrivalPending != true;
        }
    }

    /// <summary>移动模式起飞 / 战术降落到达重放：起播即压零 cutscene 时长，收尾照常。</summary>
    [HarmonyPatch(typeof(GravshipRenderer), nameof(GravshipRenderer.BeginCutscene))]
    public static class Patch_GravshipRenderer_BeginCutscene
    {
        public static void Postfix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipModes.SuppressMobileLaunchAnimation(universe);
            universe?.Approach.SuppressArrivalCutscene();
        }
    }

    /// <summary>
    /// Delta-V 接管的降落（进场 + 到达重放）与移动模式起飞期间跳过原版相机平移：
    /// 到达重放的 pan 目标来自截图包络（快照尺度），在对接尺度不可接受；相机在
    /// 选点跳转后保持不动即可。原版战略降落不受影响。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "PanIf")]
    public static class Patch_GravshipController_PanIf
    {
        public static bool Prefix(PanCompletionCallback onComplete)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.AnyActive != true && universe?.ShipModes.MobileLaunchPending() != true) return true;
            onComplete?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// Delta-V 接管的降落期间不绘制原版 terrain curtain：进场阶段它本就不会被触发
    /// （防御性一并跳过），到达重放阶段其截图内容的位置/范围误差在对接尺度不可
    /// 接受。原版战略降落不受影响。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "DrawTerrainCurtain")]
    public static class Patch_GravshipController_DrawTerrainCurtain
    {
        public static bool Prefix()
        {
            return Current.Game?.GetComponent<UniverseManager>()?.Approach.AnyActive != true;
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    public static class Patch_GravshipController_LandingEnded
    {
        public static void Prefix()
        {
            // LandingEnded 读取 gravship.Engine.launchInfo；战术降落在选点阶段已合成
            // （BeginOriginalLanding），此处只做缺失防御，战略到达原版自有。
            Gravship gravship = Current.Game?.Gravship;
            if (gravship?.Engine == null)
            {
                return;
            }
            if (gravship.Engine.launchInfo == null)
            {
                gravship.Engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
                return;
            }
            // 战术降落（移动→固定）不掷原版降落结局（2026-09-20 用户决策）：战略发射
            // 仪式的 launchInfo 消费后从不复位，残留 doNegativeOutcome=true 不得在战术
            // 降落二次生效（其"引擎过热"结局还会延长引擎冷却——战术流程本身从不
            // 启动冷却，这是降落期唯一的冷却写入点）。
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Approach.TacticalLandingEnding(universe) == true)
            {
                gravship.Engine.launchInfo.doNegativeOutcome = false;
            }
        }

        public static void Postfix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipModes.NotifyVanillaLandingEnded(universe);
            universe?.Approach.NotifyArrivalEnded();
        }
    }

    /// <summary>
    /// 战术降落（移动→固定）不触发原版场景落地钩子（2026-09-20 用户决策）：
    /// Odyssey 开局的 ScenPart_PursuingMechanoids 会经 PostGravshipLanded 启动
    /// 机械族追击（警告信+袭击）——对接停靠不应召唤追击。PostGravshipLanded 的
    /// 唯一调用方是 LandingEnded 末尾，TacticalLandingEnding 窗口即精确判别；
    /// 原版战略到达照常触发。
    /// </summary>
    [HarmonyPatch(typeof(Scenario), nameof(Scenario.PostGravshipLanded))]
    public static class Patch_Scenario_PostGravshipLanded
    {
        public static bool Prefix()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe?.Approach.TacticalLandingEnding(universe) != true;
        }
    }

    /// <summary>移动模式起飞 / 到达重放期间持续压零动画时钟（跨更新边界双保险）。</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.WorldComponentUpdate))]
    public static class Patch_GravshipController_WorldComponentUpdate_Approach
    {
        public static void Prefix(WorldComponent_GravshipController __instance)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.ShipModes.BeforeGravshipControllerUpdate(universe, __instance);
            universe?.Approach.BeforeControllerUpdate(__instance);
        }
    }

    /// <summary>
    /// Delta-V 运行时流程禁存档门禁（2026-09-22 用户决策修订：对接计划不再全程禁存档）：
    /// 计划**飞行阶段可正常存档**——计划与自动驾驶任务均为 universe 实例服务上的运行时
    /// 数据、不落盘，读档后组件重建即丢弃、船按已保存速度漂移、不自动续接（序列化标志
    /// 自愈 = RestoreDanglingDockingFlags）。仍禁存档的窗口：
    /// 选点阶段（原版会深序列化 marker.gravship 与控制器 landingMarker——空洞预览
    /// Gravship 不具备落盘语义）、到达重放（转移中状态，毫秒级窗口）、跳帮舱飞行
    /// （乘员在运行时 ThingOwner 容器中，落盘即丢失）。
    /// </summary>
    [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SavingIsTemporarilyDisabled), MethodType.Getter)]
    public static class Patch_SavingIsTemporarilyDisabled_DeltaVRuntime
    {
        public static void Postfix(ref bool __result)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null) return;
            // 选点阶段 = pendingShip 已置位但计划尚未发布（发布后 pendingShip 保留到
            // WriteDockedState，由 AnyActive 区分选点与计划两段）。
            bool siteSelection = universe.ShipModes.PendingDockingShip(universe) != null
                && !universe.Approach.AnyActive;
            if (siteSelection || universe.Approach.ArrivalPending
                || universe.BoardingPods.AnyInFlight)
            {
                __result = true;
            }
        }
    }
}

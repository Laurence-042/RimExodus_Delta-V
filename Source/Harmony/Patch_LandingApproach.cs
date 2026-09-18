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
            if (LandingApproachController.TryConsume(gravship, map, landingPos, landingRot))
            {
                return false;
            }
            FreezeLandingCaptureScope(gravship, map, landingPos);
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
        private static void FreezeLandingCaptureScope(Gravship gravship, Map map, IntVec3 landingPos)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(map) == null || gravship?.Engine == null)
            {
                return;
            }
            var cells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys)
            {
                cells.Add(cell + landingPos);
            }
            DockController.SetLandingCaptureScope(gravship.Engine, cells);
            SubstructureOwnership.SetPendingStamp(map, cells, ResolveLandingOwnerId(universe, gravship));
        }

        private static int ResolveLandingOwnerId(UniverseManager universe, Gravship gravship)
        {
            // 战术降落到达重放时 pendingShip 已被 WriteDockedState 清空，改读
            // landedShipAwaitingPocketCleanup 窗口（WriteDockedState → LandingEnded）。
            ShipState pending = DockController.PendingDockingShip
                ?? DockController.LandedShipAwaitingPocketCleanup;
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
            return universe.AllocateOwnerId();
        }
    }

    /// <summary>
    /// 进场期间保持目标图内容可见：原版 cutscene 级联会让 SectionLayer_Things 全图隐藏
    /// 建筑/Pawn（CutsceneInProgress &amp;&amp; !GravshipRenderInProgess 分支），与
    /// "聚焦目标图观看进场"的语义冲突。这里只在进场阶段覆写该只读透传，让原版把
    /// 进场当作"截图进行中"处理——地图内容正常绘制；副作用仅为 MapDrawer/
    /// DynamicDrawManager 多提交几个 section，无视觉差异。GravshipCapturer 自身
    /// 读字段不读属性，不受影响；到达重放阶段原版捕获会真实置位该状态。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.GravshipRenderInProgess), MethodType.Getter)]
    public static class Patch_GravshipController_RenderInProgress_Approach
    {
        public static void Postfix(ref bool __result)
        {
            if (LandingApproachController.ApproachActive) __result = true;
        }
    }

    /// <summary>
    /// 进场期间跳过 GravshipMask 层重生成：CutsceneInProgress=true 会让 None 模式走
    /// 阴影分支（此时 SectionLayer_GravshipMask.Engine 为空）。进场期间模拟冻结、
    /// 该层无内容变化，保留旧网格即可；到达重放阶段恢复正常重生成。
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_GravshipMask), nameof(SectionLayer_GravshipMask.Regenerate))]
    public static class Patch_GravshipMask_Regenerate_Approach
    {
        public static bool Prefix()
        {
            return !LandingApproachController.ApproachActive;
        }
    }

    /// <summary>移动模式起飞 / 战术降落到达重放：起播即压零 cutscene 时长，收尾照常。</summary>
    [HarmonyPatch(typeof(GravshipRenderer), nameof(GravshipRenderer.BeginCutscene))]
    public static class Patch_GravshipRenderer_BeginCutscene
    {
        public static void Postfix()
        {
            DockController.SuppressMobileLaunchAnimation();
            LandingApproachController.SuppressArrivalCutscene();
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
            if (!LandingApproachController.AnyActive && !DockController.MobileLaunchPending) return true;
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
            return !LandingApproachController.AnyActive;
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
            if (gravship?.Engine != null && gravship.Engine.launchInfo == null)
            {
                gravship.Engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }
        }

        public static void Postfix()
        {
            DockController.NotifyVanillaLandingEnded();
            LandingApproachController.NotifyArrivalEnded();
        }
    }

    /// <summary>移动模式起飞 / 到达重放期间持续压零动画时钟（跨更新边界双保险）。</summary>
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.WorldComponentUpdate))]
    public static class Patch_GravshipController_WorldComponentUpdate_Approach
    {
        public static void Prefix(WorldComponent_GravshipController __instance)
        {
            DockController.BeforeGravshipControllerUpdate(__instance);
            LandingApproachController.BeforeControllerUpdate(__instance);
        }
    }
}

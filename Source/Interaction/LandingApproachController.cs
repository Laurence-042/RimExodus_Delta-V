using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 战术降落进场编排（星区架构 §7）。
    ///
    /// 玩家在原版选点流程确认落点后，InitiateLanding Prefix 把流程接到这里：
    /// - 选点结果（落点 cell + 旋转）经 DockController.ComputeLandingPose 反向映射为
    ///   轨道层目标位姿（宿主帧 ⊕ 落点局部，含最终放置旋转）；
    /// - 船体放回飞行口袋图恢复飞行（North 布局 + pocketRoot，与移动模式起飞失败
    ///   回退同一机制），过场期间它不属于目标图；
    /// - 控制权夺取完全复用原版 cutscene 级联（cutsceneInProgress + ScreenshotMode）：
    ///   玩家输入、时间速度、存档与世界视图由原版锁定；原版动画管线被 Prefix 拦截
    ///   不运行；目标图正常聚焦、正常渲染（GravshipRenderInProgess 覆写避免原版全图
    ///   隐藏地图内容），玩家飞船船壳由 ShellRenderer 的标准非聚焦外壳分支自动显示
    ///   ——与停靠写入共用同一目标位姿，收敛帧与真船落位严格重合；
    /// - 导航交给轨道层通用自动驾驶 ShipAutopilot.FlyTo；完成信号（OnNavigationArrived）
    ///   触发真转移：再捕获 → WriteDockedState → 重放原版 InitiateLanding（动画时长
    ///   压零，与移动模式起飞同款）→ 放置/收尾全归原版；LandingEnded Postfix 销毁
    ///   空口袋图。
    ///
    /// 进场期间原版禁存档（SavingIsTemporarilyDisabled 级联），本类状态全部为运行时
    /// 数据、不做存档兼容；读档防御性重置见 ResetOnLoad。
    /// </summary>
    public static class LandingApproachController
    {
        // ---- 进场状态（全部运行时，不存档）----

        private static ShipState approachShip;
        private static GravshipLandingMarker confirmMarker;
        private static Map targetMap;
        private static IntVec3 landingCell;
        private static Rot4 landingRotation;
        private static bool arrivalReplayActive;
        private static bool bypassInitiateLanding;

        private static readonly FieldInfo CutsceneInProgressField =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "cutsceneInProgress");

        /// <summary>进场飞行阶段：船在口袋图中，由 ShipAutopilot 驾驶。</summary>
        public static bool ApproachActive => approachShip != null && !arrivalReplayActive;

        /// <summary>到达重放阶段：原版 InitiateLanding 管线运行中（动画压零）。</summary>
        public static bool ArrivalPending => approachShip != null && arrivalReplayActive;

        public static bool AnyActive => approachShip != null;

        public static bool IsApproaching(ShipState ship)
        {
            return ApproachActive && ReferenceEquals(approachShip, ship);
        }

        /// <summary>原版 GravshipLandingMarker.BeginLanding Prefix：缓存 marker 引用（Destroy 前），
        /// 供到达重放时复活原版 InitiateLanding 依赖的 GravshipCells。</summary>
        public static void CaptureMarker(GravshipLandingMarker marker)
        {
            confirmMarker = marker;
        }

        /// <summary>
        /// InitiateLanding Prefix 的接管判定：是本 mod 的战术降落（选点阶段的捕获匹配）则
        /// 启动进场并拦截原版动画管线。口袋图意外失效时退化为旧式立即降落
        /// （WriteDockedState + 放行原方法），保证流程总能收尾。
        /// </summary>
        public static bool TryConsume(Gravship gravship, Map map, IntVec3 landingPos, Rot4 landingRot)
        {
            ShipState ship = DockController.PendingDockingShip;
            if (bypassInitiateLanding || ship == null || confirmMarker == null
                || !ReferenceEquals(confirmMarker.gravship, gravship))
            {
                if (ship == null) confirmMarker = null; // 战略降落：不持有无关 marker
                return false;
            }
            if (ship.map == null || ship.map.Disposed || !Find.Maps.Contains(ship.map))
            {
                Log.Error($"[DeltaV] Approach for '{ship.label}': flight pocket map lost; degrading to immediate vanilla landing.");
                DockController.WriteDockedState(ship, gravship, map, landingPos, landingRot);
                ClearState();
                return false;
            }

            approachShip = ship;
            targetMap = map;
            landingCell = landingPos;
            landingRotation = landingRot;

            // 目标位姿必须在把 gravship.Rotation 重置为 North（恢复口袋布局）之前反向映射：
            // 原版 Gravship.ThingPlacements getter 用**当前** gravship.Rotation 合成每个
            // Thing 的最终放置朝向，此刻它仍是玩家确认的落点旋转（== landingRot）。
            DockController.ComputeLandingPose(ship, gravship, map, landingPos, landingRot,
                out Vector3 targetDirection, out float targetAngle, out _);

            // 放回口袋图恢复飞行：GenerateGravship 的捕获布局归一为 North，
            // 与移动模式起飞失败回退同一机制。
            gravship.Rotation = Rot4.North;
            DockController.PlaceIntoMap(gravship, ship.pocketRoot, ship.map);

            // 控制权夺取：复用原版 cutscene 级联（PlayerHasControl / ForcePaused /
            // 禁存档 / 锁世界视图）。原版动画管线被 Prefix 拦截，不会运行。
            Current.Game.CurrentMap = map;
            Find.ScreenshotModeHandler.Active = true;
            SetCutsceneInProgress(true);

            // 导航交给轨道层通用自动驾驶；到达/失败信号驱动后续流程。
            ShipAutopilot.FlyTo(ship, targetDirection, targetAngle, OnNavigationArrived, OnNavigationFailed);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: approach autopilot engaged toward '{Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map)?.label}'; rot={landingRot.AsInt}");
            return true;
        }

        /// <summary>ShipAutopilot 完成信号：导航到达落点位姿，执行真转移。</summary>
        private static void OnNavigationArrived()
        {
            CompleteTransfer();
        }

        /// <summary>ShipAutopilot 失败信号：释放控制权，回到移动模式可重试。</summary>
        private static void OnNavigationFailed()
        {
            Abort("autopilot failed");
        }

        /// <summary>
        /// 真转移：再捕获 → 写停靠状态 → 重放原版 InitiateLanding（放置/PostSwapMap/
        /// 着陆结局/收尾全归原版；动画时长经 SuppressArrivalCutscene/BeforeControllerUpdate
        /// 压零）。
        /// </summary>
        private static void CompleteTransfer()
        {
            ShipState ship = approachShip;
            if (targetMap == null || targetMap.Disposed)
            {
                Abort("target map lost");
                return;
            }

            Gravship gravship = DockController.CaptureShipForArrival(ship);
            if (gravship == null)
            {
                Abort("re-capture failed");
                return;
            }
            gravship.Rotation = landingRotation;
            PlanetTile addressTile = targetMap.Tile.Valid
                ? targetMap.Tile
                : OrbitSphere.TileFromDirection(ship.pose.position);
            gravship.Tile = addressTile;
            gravship.destinationTile = addressTile;

            DockController.WriteDockedState(ship, gravship, targetMap, landingCell, landingRotation);

            arrivalReplayActive = true;
            // 复活原版 InitiateLanding 依赖的 marker（GravshipCells 缓存仍有效），
            // LandingEnded 后在 NotifyArrivalEnded 里置回 null。
            Find.GravshipController.Notify_LandingAreaConfirmationStarted(confirmMarker);
            bypassInitiateLanding = true;
            try
            {
                Find.GravshipController.InitiateLanding(gravship, targetMap, landingCell, landingRotation);
            }
            finally
            {
                bypassInitiateLanding = false;
            }
        }

        /// <summary>异常出口：取消导航、释放原版控制权，飞船回到速度归零的移动状态，可重新发起降落。</summary>
        private static void Abort(string reason)
        {
            Log.Error($"[DeltaV] Landing approach aborted: {reason}.");
            ShipState ship = approachShip;
            if (ship != null)
            {
                ShipAutopilot.Cancel(ship);
                ship.pose.velocity = Vector2.zero;
                ship.pose.angularVelocity = 0f;
                ship.docking = false; // 回到移动模式；docked 从未置位
            }
            ReleaseControl();
            ClearState();
            Messages.Message("DeltaV_ApproachAborted".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>LandingEnded Postfix：到达重放收尾——清掉复活的 marker 与全部进场状态。</summary>
        public static void NotifyArrivalEnded()
        {
            if (approachShip == null) return;
            Find.GravshipController?.Notify_LandingAreaConfirmationStarted(null);
            ClearState();
        }

        /// <summary>GravshipRenderer.BeginCutscene Postfix：到达重放起播即压零动画时长。</summary>
        public static void SuppressArrivalCutscene()
        {
            if (ArrivalPending) DockController.ForceCutsceneTimeZero(Find.GravshipController);
        }

        /// <summary>WorldComponentUpdate Prefix：到达重放期间持续压零动画时钟（起飞同款兜底）。</summary>
        public static void BeforeControllerUpdate(WorldComponent_GravshipController controller)
        {
            if (ArrivalPending) DockController.ForceCutsceneTimeZero(controller);
        }

        /// <summary>读档防御：进场期间原版禁存档，正常不会带状态读档；异常路径下释放控制权并丢弃。</summary>
        public static void ResetOnLoad()
        {
            if (approachShip == null) return;
            ReleaseControl();
            ClearState();
        }

        private static void ReleaseControl()
        {
            Find.ScreenshotModeHandler.Active = false;
            SetCutsceneInProgress(false);
        }

        private static void SetCutsceneInProgress(bool value)
        {
            CutsceneInProgressField?.SetValue(null, value);
        }

        private static void ClearState()
        {
            approachShip = null;
            confirmMarker = null;
            targetMap = null;
            landingCell = IntVec3.Invalid;
            landingRotation = Rot4.North;
            arrivalReplayActive = false;
        }
    }
}

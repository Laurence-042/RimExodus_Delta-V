using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 统一宇宙状态管理器（阶段1）。GameComponent 由 Game.FillComponents 反射自动注册，
    /// 无需 XML。ships 列表是权威宇宙状态；外壳代理只是缓存。
    ///
    /// 职责边界：
    /// - 非聚焦船代理绘制在 GameComponentUpdate 分发（WorldComponent_GravshipController 同款节奏）；
    /// - SetFocused 只切换参考系（CurrentMap），绝不修改任何 pose 数值（不变量 #5）。
    /// </summary>
    public class UniverseManager : GameComponent
    {
        public List<ShipState> ships = new List<ShipState>();

        public int focusedShipId = -1;

        /// <summary>驾驶中的船（阶段2）；-1 = 未驾驶。持久化，读档后驾驶状态恢复。</summary>
        public int pilotShipId = -1;

        /// <summary>
        /// 宿主/显示地图：轨道层真实 tile 上的普通地图，是飞船口袋图的 sourceMap 锚点
        /// （原版 storyteller/财富/任务逻辑从口袋图重定向到 sourceMap）。聚焦宿主图时
        /// 以 HostFrame 显示全部飞船代理。
        /// </summary>
        public Map hostMap;

        public ShipState Focused => ships.FirstOrDefault(s => s.shipId == focusedShipId);

        /// <summary>当前地图对应的船（无则 null）。显示参考系与同步逻辑都以此为准。</summary>
        public ShipState ShipOnMap(Map map)
        {
            return map == null ? null : ships.FirstOrDefault(s => s.map == map);
        }

        /// <summary>承载指定地图的显示参考系：船图为其自身，宿主图为 HostFrame，其余 null。</summary>
        public ShipState FrameOf(Map map)
        {
            ShipState frame = ShipOnMap(map);
            if (frame != null)
            {
                return frame;
            }
            return hostMap == map ? HostFrame : null;
        }

        [NonSerialized] private ShipState hostFrame;

        /// <summary>
        /// 宿主图显示帧：纯显示层合成参考系（宇宙原点、0 角，锚在宿主图中心），
        /// 只用于聚焦宿主图时投影飞船代理，不是宇宙状态（不变量 #9/#5）。
        /// </summary>
        public ShipState HostFrame
        {
            get
            {
                if (hostFrame == null)
                {
                    hostFrame = new ShipState(0, "host frame (display-only)", hostMap, new SpacePose(Vector2.zero, 0f));
                }
                hostFrame.map = hostMap;
                return hostFrame;
            }
        }

        public UniverseManager(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref ships, "ships", LookMode.Deep);
            Scribe_Values.Look(ref focusedShipId, "focusedShipId", -1);
            Scribe_Values.Look(ref pilotShipId, "pilotShipId", -1);
            Scribe_References.Look(ref hostMap, "hostMap");
        }

        public override void LoadedGame()
        {
            // 派生地形网格只用于代理 UV，绝不存档；读档后从 SectionLayer 重建。
            ShellRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            hostFrame = null;
            PilotController.SuppressVanillaKeys = false;
            if (hostMap != null && !Find.Maps.Contains(hostMap))
            {
                Log.Warning("[DeltaV] Host map missing after load; cleared reference.");
                hostMap = null;
            }
            // 地图被外部移除等异常情况下的收尾：清掉悬空引用，聚焦无效时退回第一艘活着的船。
            foreach (ShipState ship in ships.ToList())
            {
                if (ship.map == null)
                {
                    Log.Warning($"[DeltaV] Ship #{ship.shipId} ({ship.label}) lost its map after load; removing from universe state.");
                    ships.Remove(ship);
                }
            }
            if (Focused == null && ships.Count > 0)
            {
                focusedShipId = ships[0].shipId;
            }
        }

        public override void GameComponentUpdate()
        {
            // 玩家可能经殖民者栏/地图按钮等原版途径切图：focusedShipId 跟随当前船图，
            // 只同步参考系字段，不触碰任何 pose（不变量 #5）。
            ShipState onMap = ShipOnMap(Find.CurrentMap);
            if (onMap != null && onMap.shipId != focusedShipId)
            {
                focusedShipId = onMap.shipId;
            }
            // 驾驶状态维护（自动退出 + Harmony 按键抑制开关）。
            PilotController.UpdateFrameState(this);
            DebugActions_DeltaV.UpdateFrameMeter();
            ShellRenderer.DrawNonFocusedShips(this);
        }

        /// <summary>
        /// 阶段2 物理积分（TickManager 每 tick 调用，暂停时冻结——正确的模拟语义）。
        /// 驾驶船按输入+辅助出力积分；其余船只在有速度/角速度时漂移积分。
        /// 只写 SpacePose（Map 外的宇宙状态），绝不触碰任何 Map 内 Thing（不变量 #6）。
        /// </summary>
        public override void GameComponentTick()
        {
            const float dt = 1f / 60f;
            ShipState piloted = PilotController.PilotedShip(this);
            bool piloting = piloted?.map != null && Find.CurrentMap == piloted.map;
            for (int i = 0; i < ships.Count; i++)
            {
                ShipState ship = ships[i];
                if (ship?.map == null)
                {
                    continue;
                }
                bool isPiloted = piloting && ship == piloted;
                bool drifting = ship.pose.velocity.sqrMagnitude > 1e-10f
                    || Mathf.Abs(ship.pose.angularVelocity) > 1e-10f;
                if (!isPiloted && !drifting)
                {
                    continue;
                }
                ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
                if (report == null)
                {
                    continue;
                }
                Vector2 force = Vector2.zero;
                float torque = 0f;
                bool assistX = false;
                bool assistZ = false;
                bool assistW = false;
                if (isPiloted)
                {
                    PilotController.PilotCommand command = PilotController.ComposeCommand(ship);
                    ShipPhysics.Allocate(report, command.command, out force, out torque);
                    assistX = command.assistX;
                    assistZ = command.assistZ;
                    assistW = command.assistW;
                    LogEngineFiringTransition(ship, report, force, torque);
                }
                ShipPhysics.Integrate(ship, report, force, torque, dt, assistX, assistZ, assistW);
            }
            // Universe Layer：飞船移动后惰性重锚背景 tile（universe → tile 单向）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                UniverseLayerMapping.ReanchorShips(this);
            }
        }

        // 物理埋点（触发式）：只记录引擎点火/停火的转换沿，杜绝持续出力期间的每 tick 输出。
        private readonly Dictionary<int, bool> enginesFiringByShip = new Dictionary<int, bool>();

        private void LogEngineFiringTransition(ShipState ship, ShipPhysics.MassReport report, Vector2 force, float torque)
        {
            bool firing = force.sqrMagnitude > 1e-6f || Mathf.Abs(torque) > 1e-6f;
            bool was = enginesFiringByShip.TryGetValue(ship.shipId, out bool previous) && previous;
            if (firing == was)
            {
                return;
            }
            enginesFiringByShip[ship.shipId] = firing;
            DeltaVLog.Message(DeltaVLogModule.Physics, firing
                ? $"{ship.label}: thrusters firing | F=({force.x:F0},{force.y:F0}) tau={torque:F0} | mass={report.mass:F0} I={report.inertia:F0} thrusters={report.thrusters.Count}"
                : $"{ship.label}: engines stopped | v=({ship.pose.velocity.x:F2},{ship.pose.velocity.y:F2}) omega={ship.pose.angularVelocity:F3}");
        }

        public override void GameComponentOnGUI()
        {
            // 驾驶模式切换键（本 mod 分类的绑定不受抑制影响，驾驶中仍可退出）。
            if (DeltaVDefOf.DeltaV_TogglePilotMode != null && DeltaVDefOf.DeltaV_TogglePilotMode.KeyDownEvent)
            {
                PilotController.Toggle(this);
            }
            PilotController.DrawHud(this);
            DebugActions_DeltaV.DrawFrameMeter();
            HandleShellClick();
        }

        // ---- 外壳点选（选择层；瞄准兼容契约见 ShellPicking 类注释）----

        private ShipState pendingClickShip;
        private Vector2 pendingClickScreen;

        /// <summary>
        /// 点击显示中的飞船外壳 → 聚焦切换到对应地图。
        /// 让位规则：瞄准会话（Targeter 激活）或建造设计器选中时完全不参与；
        /// 悬停 UI、原版可选物优先；按下-抬起间发生拖拽（框选）即取消。
        /// 不消费事件，原版点击流程不受影响。
        /// </summary>
        private void HandleShellClick()
        {
            Map current = Find.CurrentMap;
            if (current == null || !RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                pendingClickShip = null;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingClickShip = null;
                bool targeting = Find.Targeter.IsTargeting;
                bool designator = Find.DesignatorManager.SelectedDesignator != null;
                bool overWindow = Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(ev.mousePosition)) != null;
                if (targeting || designator || overWindow)
                {
                    return;
                }
                IntVec3 mouseCell = UI.MouseMapPosition().ToIntVec3();
                bool selectableHere = mouseCell.InBounds(current) && mouseCell.GetThingList(current).Any(ThingSelectionUtility.SelectableByMapClick);
                if (selectableHere)
                {
                    return;
                }
                pendingClickShip = ShellPicking.ShipAtFramePoint(this, current, UI.MouseMapPosition());
                pendingClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingClickShip = null;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingClickShip != null)
            {
                ShipState target = pendingClickShip;
                pendingClickShip = null;
                bool noDrag = (ev.mousePosition - pendingClickScreen).sqrMagnitude < 100f;
                ShipState recheck = ShellPicking.ShipAtFramePoint(this, current, UI.MouseMapPosition());
                if (noDrag && recheck == target)
                {
                    // 相机跳到点击位置对应的船内格（clamp 进地图），落点即所见。
                    ShipState frame = FrameOf(current);
                    Vector2 frameLocal = SpaceTransform.MapToShipLocal(current, new Vector2(UI.MouseMapPosition().x, UI.MouseMapPosition().z));
                    Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(target, SpaceTransform.FocusedToUniverse(frame, frameLocal));
                    int x = Mathf.Clamp(Mathf.FloorToInt(mapPoint.x), 2, target.map.Size.x - 3);
                    int z = Mathf.Clamp(Mathf.FloorToInt(mapPoint.y), 2, target.map.Size.z - 3);
                    // 缩放同步（RimExodus SeamlessCameraFocus 同款结论）：CurrentMap setter 的
                    // Notify_SwitchedMap 会用目标图 rememberedCameraPos 同时恢复位置+缩放，
                    // 只 TryJump 恢复位置不够，必须记录 RootSize 并在跳转后显式重设。
                    float zoom = Find.CameraDriver.RootSize;
                    SetFocused(target);
                    CameraJumper.TryJump(new IntVec3(x, 0, z), target.map, CameraJumper.MovementMode.Cut);
                    Find.CameraDriver.SetRootSize(zoom);
                }
            }
        }

        /// <summary>
        /// 切换聚焦参考系。只设置 CurrentMap——原版 setter 负责关闭 targeter、清选区、
        /// 恢复该图记忆的相机位置。宇宙姿态不受影响。
        /// </summary>
        public void SetFocused(ShipState ship)
        {
            if (ship?.map == null)
            {
                Log.Error($"[DeltaV] SetFocused: ship #{ship?.shipId} has no live map.");
                return;
            }
            focusedShipId = ship.shipId;
            Current.Game.CurrentMap = ship.map;
            // 与 PocketMapUtility 一致：确保回到地图视图而不是世界球面。
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
        }

        public void ResetShips(List<ShipState> newShips, int focusId)
        {
            ShellRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            PilotController.Exit(this);
            ships = newShips;
            focusedShipId = focusId;
            hostFrame = null;
        }
    }
}

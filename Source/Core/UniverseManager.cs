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
    /// 无需 XML。objects 列表（飞船 + POI）是权威宇宙状态；外壳代理只是缓存。
    ///
    /// 职责边界：
    /// - 非聚焦对象代理绘制在 GameComponentUpdate 分发（WorldComponent_GravshipController 同款节奏）；
    /// - SetFocused 只切换参考系（CurrentMap），绝不修改任何 pose 数值（不变量 #5）；
    /// - POI 地图生命周期统一委托 SpaceLifecycle（单一管理入口，AGENTS §11）；
    /// - 动态锚（阶段3）：宿主图位置记账随玩家主船平移（anchorDisplacement），
    ///   宿主图 tile/Map 永不移动。
    /// </summary>
    public class UniverseManager : GameComponent
    {
        /// <summary>权威宇宙对象列表（ShipState + PoiState，多态深序列化）。存档事实。</summary>
        public List<SpaceObject> objects = new List<SpaceObject>();

        public int focusedShipId = -1;

        /// <summary>驾驶中的船（阶段2）；-1 = 未驾驶。持久化，读档后驾驶状态恢复。</summary>
        public int pilotShipId = -1;

        /// <summary>
        /// 宿主/显示地图：轨道层真实 tile 上的普通地图，是口袋图的 sourceMap 锚点
        /// （原版 storyteller/财富/任务逻辑从口袋图重定向到 sourceMap）。聚焦宿主图时
        /// 以 HostFrame 显示全部宇宙对象代理。
        /// </summary>
        public Map hostMap;

        /// <summary>
        /// 玩家主船（阶段3）：动态锚的跟随对象——背景相机、HostFrame 总览投影、
        /// floating origin 平移都以它为参考。宿主图 tile 与 Map 永不移动。
        /// </summary>
        public int primaryShipId = -1;

        /// <summary>
        /// 动态锚记账（阶段3，存档事实）：宇宙坐标系随主船整体平移的累计量。
        /// 绝对偏移 = pose.position + anchorDisplacement；平移时刻绝对值不变，
        /// 背景球面视角因此连续（见 UniverseLayerMapping.AbsoluteSpherePoint）。
        /// </summary>
        public Vector2 anchorDisplacement = Vector2.zero;

        /// <summary>全部飞船（objects 的 ShipState 视图；每次调用有少量分配，勿在热循环内反复取）。</summary>
        public IEnumerable<ShipState> Ships => objects.OfType<ShipState>();

        public ShipState Focused => ShipById(focusedShipId);

        /// <summary>主船（锚跟随对象）；失效时回退第一艘活着的船，保持锚与显示有参考。</summary>
        public ShipState PrimaryShip =>
            (objects.FirstOrDefault(o => o is ShipState s && s.objectId == primaryShipId
                && (s.map != null || s.dockedMap != null)) as ShipState)
            ?? objects.FirstOrDefault(o => o is ShipState s2 && (s2.map != null || s2.dockedMap != null)) as ShipState;

        public ShipState ShipById(int id)
        {
            return objects.FirstOrDefault(o => o.objectId == id) as ShipState;
        }

        /// <summary>当前地图对应的宇宙对象（船或 POI；无则 null）。显示参考系以此为准。</summary>
        public SpaceObject SpaceObjectOnMap(Map map)
        {
            return map == null ? null : objects.FirstOrDefault(o => o.map == map);
        }

        /// <summary>当前地图对应的船（无则 null）。驾驶等飞船专属逻辑用。</summary>
        public ShipState ShipOnMap(Map map)
        {
            return SpaceObjectOnMap(map) as ShipState;
        }

        /// <summary>承载指定地图的显示参考系：任一对象图为其自身，宿主图为 HostFrame，其余 null。</summary>
        public SpaceObject FrameOf(Map map)
        {
            SpaceObject frame = SpaceObjectOnMap(map);
            if (frame != null)
            {
                return frame;
            }
            return hostMap == map ? HostFrame : null;
        }

        [NonSerialized] private ShipState hostFrame;

        /// <summary>
        /// 宿主图显示帧：纯显示层合成参考系（锚 = 主船宇宙位置、0 角，投影到宿主图中心），
        /// 只用于聚焦宿主图时投影宇宙对象代理，不是宇宙状态（不变量 #9/#5）。
        /// 阶段3 起锚随主船每帧平移；背景相机的旋转另由聚焦姿态驱动，不动宿主图地块。
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
                ShipState primary = PrimaryShip;
                if (primary != null)
                {
                    hostFrame.pose.position = primary.docked && primary.dockedMap == hostMap
                        ? primary.pose.position + SpaceTransform.ShipLocalOrigin(hostMap)
                            - DockController.DockedOriginMapPoint(primary)
                        : primary.pose.position;
                    hostFrame.pose.angle = 0f;
                }
                return hostFrame;
            }
        }

        public UniverseManager(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref objects, "objects", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && objects == null)
            {
                // 存档由阶段2 及更早版本保存（旧键名 "ships"，字段结构已变、不做迁移）：
                // XML 加载路径绕过字段初始化器，这里必须兜底，否则 LoadedGame/tick 全链 NRE。
                // 宇宙状态按空处理，用 dev action "Create two-ship test scene" 重建即可。
                Log.Warning("[DeltaV] Save predates phase 3 (no 'objects' list); universe state starts empty. Recreate the test scene via Debug actions.");
                objects = new List<SpaceObject>();
            }
            Scribe_Values.Look(ref focusedShipId, "focusedShipId", -1);
            Scribe_Values.Look(ref pilotShipId, "pilotShipId", -1);
            Scribe_Values.Look(ref primaryShipId, "primaryShipId", -1);
            Scribe_Values.Look(ref anchorDisplacement, "anchorDisplacement", Vector2.zero);
            Scribe_References.Look(ref hostMap, "hostMap");
        }

        public override void LoadedGame()
        {
            // 白名单/逆重基架 UV mesh 都是可重建的表现缓存，绝不存档。
            ShellRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            hostFrame = null;
            PilotController.SuppressVanillaKeys = false;
            DockController.RestorePendingAfterLoad(this);
            if (hostMap != null && !Find.Maps.Contains(hostMap))
            {
                Log.Warning("[DeltaV] Host map missing after load; cleared reference.");
                hostMap = null;
            }
            // 地图被外部移除等异常情况下的收尾：清掉悬空引用，聚焦无效时退回第一艘活着的船。
            foreach (SpaceObject obj in objects.ToList())
            {
                if (obj.map != null && !Find.Maps.Contains(obj.map))
                {
                    Log.Warning($"[DeltaV] Object #{obj.objectId} ({obj.label}) lost its map after load; clearing map reference.");
                    obj.map = null;
                }
                if (obj is ShipState lost && lost.map == null && !lost.docked)
                {
                    // 飞船没有"删除段"——口袋图丢失属异常，整体移除避免悬空状态。
                    Log.Warning($"[DeltaV] Ship #{lost.objectId} ({lost.label}) lost its map; removing from universe state.");
                    objects.Remove(lost);
                }
            }
            if (Focused == null)
            {
                ShipState first = objects.OfType<ShipState>().FirstOrDefault(s => s.map != null);
                if (first != null)
                {
                    focusedShipId = first.objectId;
                }
            }
            DockController.CleanupObsoleteDockedPocketMaps(this);
            HostColonistBarVisibility.NotifyDockingStateChanged();
        }

        public override void GameComponentUpdate()
        {
            // 玩家可能经殖民者栏/地图按钮等原版途径切图：focusedShipId 跟随当前船图，
            // 只同步参考系字段，不触碰任何 pose（不变量 #5）。
            ShipState onMap = ShipOnMap(Find.CurrentMap);
            if (onMap != null && onMap.objectId != focusedShipId)
            {
                focusedShipId = onMap.objectId;
            }
            UpdateAnchor();
            UpdateUniverseZoom();
            // 驾驶状态维护（自动退出 + Harmony 按键抑制开关）。
            PilotController.UpdateFrameState(this);
            DebugActions_DeltaV.UpdateFrameMeter();
            ShellRenderer.DrawNonFocusedShells(this);
            ThrusterEffects.Draw(this);
        }

        /// <summary>
        /// 动态锚维护（阶段3）：主船离原点超过阈值时做 floating origin 平移——全体 pose
        /// 一次性平移 −主船位置，anchorDisplacement 累计。所有对象的相对位置不变，
        /// 绝对偏移（pose + displacement）不变，因此外壳投影与背景球面视角都无跳变。
        /// HostFrame 的锚点随后同步为主船位置（宿主图总览中心 = 主船附近宇宙区域）。
        /// </summary>
        private void UpdateAnchor()
        {
            ShipState primary = PrimaryShip;
            if (primary == null)
            {
                return;
            }
            if (primaryShipId != primary.objectId)
            {
                primaryShipId = primary.objectId;
            }
            float threshold = Mathf.Max(1f, DeltaVMod.Settings.originShiftThreshold);
            if (primary.pose.position.sqrMagnitude > threshold * threshold)
            {
                Vector2 delta = primary.pose.position;
                foreach (SpaceObject obj in objects)
                {
                    obj.pose.position -= delta;
                    if (obj is ShipState ship)
                    {
                        // 停靠前姿态与 pose 同坐标系，必须一起平移，否则起飞时恢复到旧原点。
                        ship.preDockPose.position -= delta;
                    }
                }
                anchorDisplacement += delta;
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"floating origin shifted by -({delta.x:F0}, {delta.y:F0}); cumulative anchor displacement = ({anchorDisplacement.x:F0}, {anchorDisplacement.y:F0})");
            }
            // HostFrame 属性同时处理飞行中心锚与停靠真实原点锚。
            if (hostFrame != null) _ = HostFrame;
        }

        // 相机缩放放宽（宇宙视野）：进入 Delta-V 参考系（对象图/宿主图）时记录原 max 并放宽，
        // 离开时恢复。每帧幂等——config 对象可能被原版在切图等时机替换。
        private static float savedVanillaZoomMax = float.NaN;

        private void UpdateUniverseZoom()
        {
            CameraDriver driver = Find.CameraDriver;
            if (driver?.config == null)
            {
                return;
            }
            float target = Mathf.Max(driver.config.sizeRange.min + 1f, DeltaVMod.Settings.universeZoomMax);
            if (FrameOf(Find.CurrentMap) != null)
            {
                if (float.IsNaN(savedVanillaZoomMax))
                {
                    savedVanillaZoomMax = driver.config.sizeRange.max;
                }
                if (driver.config.sizeRange.max < target)
                {
                    driver.config.sizeRange.max = target;
                }
            }
            else if (!float.IsNaN(savedVanillaZoomMax))
            {
                driver.config.sizeRange.max = savedVanillaZoomMax;
                savedVanillaZoomMax = float.NaN;
            }
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
            foreach (SpaceObject obj in objects)
            {
                if (!(obj is ShipState ship) || ship.map == null)
                {
                    continue;
                }
                if (ship.docked || ship.docking)
                {
                    continue; // 停靠/选点中：船体不在活动口袋图上，宇宙状态冻结
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
            // Universe Layer：飞船移动后惰性重锚背景 tile（universe → tile 单向）；
            // POI 地图生命周期三段扫描（单一入口 SpaceLifecycle）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                UniverseLayerMapping.ReanchorShips(this);
                SpaceLifecycle.Tick(this);
            }
        }

        // 物理埋点（触发式）：只记录引擎点火/停火的转换沿，杜绝持续出力期间的每 tick 输出。
        // 非 readonly：GameComponent 的 XML 加载路径（GetUninitializedObject）不跑字段初始化器，
        // readonly 字段会停留在 null 且无法补偿；这里用惰性属性兜底。
        private Dictionary<int, bool> enginesFiringByShip;

        private Dictionary<int, bool> EnginesFiringByShip =>
            enginesFiringByShip ?? (enginesFiringByShip = new Dictionary<int, bool>());

        private void LogEngineFiringTransition(ShipState ship, ShipPhysics.MassReport report, Vector2 force, float torque)
        {
            bool firing = force.sqrMagnitude > 1e-6f || Mathf.Abs(torque) > 1e-6f;
            bool was = EnginesFiringByShip.TryGetValue(ship.objectId, out bool previous) && previous;
            if (firing == was)
            {
                return;
            }
            EnginesFiringByShip[ship.objectId] = firing;
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
            DockController.DrawHud(this);
            DebugActions_DeltaV.DrawFrameMeter();
            HandleShellClick();
        }

        // ---- 外壳点选（选择层；瞄准兼容契约见 ShellPicking 类注释）----

        private SpaceObject pendingClickObject;
        private Vector2 pendingClickScreen;

        /// <summary>
        /// 点击显示中的宇宙对象外壳 → 聚焦切换到对应地图（飞船与 POI 通用）。
        /// 让位规则：瞄准会话（Targeter 激活）或建造设计器选中时完全不参与；
        /// 悬停 UI、原版可选物优先；按下-抬起间发生拖拽（框选）即取消。
        /// 不消费事件，原版点击流程不受影响。
        /// </summary>
        private void HandleShellClick()
        {
            Map current = Find.CurrentMap;
            if (current == null || !RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                pendingClickObject = null;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingClickObject = null;
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
                pendingClickObject = ShellPicking.ShellAtFramePoint(this, current, UI.MouseMapPosition());
                pendingClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingClickObject = null;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingClickObject != null)
            {
                SpaceObject target = pendingClickObject;
                pendingClickObject = null;
                bool noDrag = (ev.mousePosition - pendingClickScreen).sqrMagnitude < 100f;
                SpaceObject recheck = ShellPicking.ShellAtFramePoint(this, current, UI.MouseMapPosition());
                if (noDrag && recheck == target)
                {
                    // 相机跳到点击位置对应的对象内格（clamp 进地图），落点即所见。
                    SpaceObject frame = FrameOf(current);
                    Vector2 frameLocal = SpaceTransform.MapToShipLocal(current, new Vector2(UI.MouseMapPosition().x, UI.MouseMapPosition().z));
                    Vector2 universePoint = SpaceTransform.FocusedToUniverse(frame, frameLocal);
                    ShipState dockedTarget = target as ShipState;
                    Map targetMap = dockedTarget != null && dockedTarget.docked
                        ? dockedTarget.dockedMap
                        : target.map;
                    Vector2 mapPoint = dockedTarget != null && dockedTarget.docked
                        ? DockController.DockedOriginMapPoint(dockedTarget)
                            + universePoint - dockedTarget.pose.position
                        : SpaceTransform.UniverseToMapPoint(target, universePoint);
                    int x = Mathf.Clamp(Mathf.FloorToInt(mapPoint.x), 2, targetMap.Size.x - 3);
                    int z = Mathf.Clamp(Mathf.FloorToInt(mapPoint.y), 2, targetMap.Size.z - 3);
                    // 缩放同步（RimExodus SeamlessCameraFocus 同款结论）：CurrentMap setter 的
                    // Notify_SwitchedMap 会用目标图 rememberedCameraPos 同时恢复位置+缩放，
                    // 只 TryJump 恢复位置不够，必须记录 RootSize 并在跳转后显式重设。
                    float zoom = Find.CameraDriver.RootSize;
                    SetFocused(target);
                    CameraJumper.TryJump(new IntVec3(x, 0, z), targetMap, CameraJumper.MovementMode.Cut);
                    Find.CameraDriver.SetRootSize(zoom);
                }
            }
        }

        /// <summary>
        /// 切换聚焦参考系。只设置 CurrentMap——原版 setter 负责关闭 targeter、清选区、
        /// 恢复该图记忆的相机位置。宇宙姿态不受影响。
        /// </summary>
        public void SetFocused(SpaceObject obj)
        {
            Map targetMap = obj is ShipState dockedShip && dockedShip.docked
                ? dockedShip.dockedMap
                : obj?.map;
            if (targetMap == null)
            {
                Log.Error($"[DeltaV] SetFocused: object #{obj?.objectId} has no live map.");
                return;
            }
            if (obj is ShipState ship)
            {
                focusedShipId = ship.objectId;
            }
            Current.Game.CurrentMap = targetMap;
            // 与 PocketMapUtility 一致：确保回到地图视图而不是世界球面。
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
        }

        public void ResetObjects(List<SpaceObject> newObjects, int focusId)
        {
            ShellRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            PilotController.Exit(this);
            objects = newObjects;
            focusedShipId = focusId;
            hostFrame = null;
        }
    }
}

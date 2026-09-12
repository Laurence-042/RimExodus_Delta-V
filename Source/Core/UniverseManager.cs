using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// - 轨道测试动画在 GameComponentTick 推进（驾驶系统的占位，阶段2 替换为真实物理）；
    /// - 非聚焦船代理绘制在 GameComponentUpdate 分发（WorldComponent_GravshipController 同款节奏）；
    /// - SetFocused 只切换参考系（CurrentMap），绝不修改任何 pose 数值（不变量 #5）。
    /// </summary>
    public class UniverseManager : GameComponent
    {
        public List<ShipState> ships = new List<ShipState>();

        public int focusedShipId = -1;

        /// <summary>
        /// 宿主/显示地图：轨道层真实 tile 上的普通地图，是飞船口袋图的 sourceMap 锚点
        /// （原版 storyteller/财富/任务逻辑从口袋图重定向到 sourceMap）。聚焦宿主图时
        /// 以 HostFrame 显示全部飞船代理。
        /// </summary>
        public Map hostMap;

        // ---- 测试工具开关（dev 专用，不存档）----
        public bool orbitTestEnabled;
        public bool debugHudEnabled;

        private float orbitPhase;

        private const float OrbitRadius = 55f;
        private const float OrbitAngularSpeed = 0.008f;   // rad/tick，一整圈约 13 秒
        private const float SelfSpinSpeed = 0.01f;        // rad/tick

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
            Scribe_References.Look(ref hostMap, "hostMap");
        }

        public override void LoadedGame()
        {
            // 派生地形网格只用于代理 UV，绝不存档；读档后从 SectionLayer 重建。
            ShellRenderer.ClearRuntimeCaches();
            hostFrame = null;
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
            if (ships.Count > 0)
            {
                Log.Message(BuildPoseDump("poses after load"));
            }
        }

        public override void GameComponentTick()
        {
            if (!orbitTestEnabled)
            {
                return;
            }
            ShipState focused = Focused;
            ShipState enemy = ships.FirstOrDefault(s => s != focused && s.map != null);
            if (focused == null || enemy == null)
            {
                return;
            }
            // 轨道测试：敌舰绕聚焦船做匀速圆周 + 自转。
            // 位置每 tick 从玩家绝对位置重新推导（而不是累加位移），避免浮点漂移积累。
            orbitPhase += OrbitAngularSpeed;
            enemy.pose.position = focused.pose.position + new Vector2(Mathf.Cos(orbitPhase), Mathf.Sin(orbitPhase)) * OrbitRadius;
            enemy.pose.angle = SpaceTransform.WrapAngle(enemy.pose.angle + SelfSpinSpeed);
        }

        public override void GameComponentUpdate()
        {
            // 玩家可能经殖民者栏/地图按钮等原版途径切图：focusedShipId 跟随当前船图，
            // 只同步参考系字段，不触碰任何 pose（不变量 #5）。
            ShipState onMap = ShipOnMap(Find.CurrentMap);
            if (onMap != null && onMap.shipId != focusedShipId)
            {
                focusedShipId = onMap.shipId;
                DeltaVLog.Message(DeltaVLogModule.Core, $"focused ship synced to #{onMap.shipId} ({onMap.label}) via map switch");
            }
            ShellRenderer.DrawNonFocusedShips(this);
        }

        public override void GameComponentOnGUI()
        {
            HandleShellClick();
            if (debugHudEnabled)
            {
                DrawDebugHud();
            }
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
                    if (Prefs.DevMode)
                    {
                        Log.Message($"[DeltaV:Click] down ignored: targeting={targeting} designator={designator} overWindow={overWindow}");
                    }
                    return;
                }
                IntVec3 mouseCell = UI.MouseMapPosition().ToIntVec3();
                bool selectableHere = mouseCell.InBounds(current) && mouseCell.GetThingList(current).Any(ThingSelectionUtility.SelectableByMapClick);
                if (selectableHere)
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("[DeltaV:Click] down ignored: vanilla-selectable thing under mouse");
                    }
                    return;
                }
                pendingClickShip = ShellPicking.ShipAtFramePoint(this, current, UI.MouseMapPosition());
                pendingClickScreen = ev.mousePosition;
                if (Prefs.DevMode)
                {
                    string pending = pendingClickShip != null ? ("#" + pendingClickShip.shipId) : "none";
                    Log.Message($"[DeltaV:Click] down at map({UI.MouseMapPosition().x:F0},{UI.MouseMapPosition().z:F0}) -> pending ship={pending}");
                }
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
                if (Prefs.DevMode)
                {
                    Log.Message($"[DeltaV:Click] up: noDrag={noDrag} pending=#{target.shipId} rehit={(recheck != null ? "#" + recheck.shipId : "none")}");
                }
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
                    DeltaVLog.Message(DeltaVLogModule.Core, $"focus by shell click: ship #{target.shipId} ({target.label}), camera at map({x},{z}) zoom={zoom:F0}");
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
            ships = newShips;
            focusedShipId = focusId;
            orbitPhase = 0f;
            hostFrame = null;
        }

        public string BuildPoseDump(string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[DeltaV] {title} — focused id = {focusedShipId}");
            if (hostMap != null)
            {
                sb.AppendLine($"  host: tile={hostMap.Tile} maps={Find.Maps.Count}");
            }
            foreach (ShipState ship in ships)
            {
                sb.AppendLine(
                    $"  #{ship.shipId} {ship.label}: pos=({ship.pose.position.x:F3}, {ship.pose.position.y:F3}) " +
                    $"angle={Mathf.Rad2Deg * SpaceTransform.WrapAngle(ship.pose.angle):F3}deg " +
                    $"vel=({ship.pose.velocity.x:F3}, {ship.pose.velocity.y:F3}) angVel={ship.pose.angularVelocity:F3}");
            }
            ShipState focused = Focused;
            if (focused != null)
            {
                foreach (ShipState ship in ships)
                {
                    if (ship == focused)
                    {
                        continue;
                    }
                    Vector2 rel = SpaceTransform.UniverseToFocused(focused, ship.pose.position);
                    float relAngle = SpaceTransform.UniverseAngleToFocused(focused, ship.pose.angle);
                    sb.AppendLine(
                        $"  rel(#{ship.shipId} in focused frame): pos=({rel.x:F3}, {rel.y:F3}) angle={Mathf.Rad2Deg * relAngle:F3}deg");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private void DrawDebugHud()
        {
            ShipState focused = Focused;
            if (focused == null || Find.CurrentMap == null || Find.CurrentMap != focused.map)
            {
                return;
            }
            Text.Font = GameFont.Tiny;
            string text = BuildPoseDump("universe state");
            float height = Text.CalcHeight(text, 430f) + 14f;
            var rect = new Rect(10f, 200f, 450f, height);
            Widgets.DrawWindowBackground(rect);
            GUI.Label(rect.ContractedBy(7f), text);
        }
    }
}

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
            ShellRenderer.DrawNonFocusedShips(this);
        }

        public override void GameComponentOnGUI()
        {
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
            ships = newShips;
            focusedShipId = focusId;
            hostFrame = null;
        }
    }
}

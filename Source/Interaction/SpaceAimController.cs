using System.Collections.Generic;
using RimWorld.Planet;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 瞄准输入会话（太空战架构 §6）：挂在玩家船 controller 上，只采样玩家输入与
    /// 显示转发（预瞄由每船火控解算，绘制走 <see cref="AimMarkersRenderer"/> 无状态助手）；Dispose 即
    /// 收尾——标记管线以会话存在为唯一门槛，会话销毁后自然停发，无注册/反注册
    /// 路径可泄漏（修复：脱离对接后其他船上的预瞄残留）。
    ///
    /// 身份 = 带种类的 DriveSubject（跨图稳定）：火控经船体共同能力读取当前武器，
    /// 对接合图/脱离换图后自动跟随——瞄准会话与战斗状态跨地图变更保持。
    /// 会话独立于驾驶（PlayerController）与自动驾驶：进入对接/切换控制态不影响；
    /// 跨聚焦切换保持（非本船聚焦图不射击、不画标记）；船损毁/归属域消失自动析构。
    /// 运行时对象：不序列化，读档无瞄准会话。
    /// </summary>
    public sealed class SpaceAimController
    {
        private readonly UniverseManager universe;
        private bool disposed;
        private readonly ShipFireControl fireControl;
        private readonly AimButtonLatch buttons = new AimButtonLatch();
        private Map inputMap;
        private bool sampledAllowed, sampledMain, sampledSecondary;
        private int previewFrame = -1, previewStructureVersion = -1;
        private IReadOnlyList<Vector3> previewPoints;

        /// <summary>瞄准的船（带种类的跨图稳定身份）。</summary>
        public readonly DriveSubject ownerId;

        internal SpaceAimController(UniverseManager universe, DriveSubject ownerId, ShipFireControl fireControl)
        {
            this.universe = universe;
            this.ownerId = ownerId;
            this.fireControl = fireControl;
#if !RIMWORLD_1_7
            Application.focusChanged += OnFocusChanged;
#endif
        }

        private void OnFocusChanged(bool focused)
        {
            universe.PlayerInput.InvalidateGate();
            if (!focused) buttons.Clear();
        }

        public bool Active => !disposed;
        public bool HasInput { get; private set; }
        public bool FireMain { get; private set; }
        public bool FireSecondary { get; private set; }
        public Vector2 LocalAimPoint { get; private set; }

        public ShipBase Vessel => ShipRegistry.ShipByIdentity(ownerId);

        /// <summary>
        /// 会话是否作用于当前聚焦图（射击、标记绘制与点击让位的共同前提）。
        /// 会话本身跨聚焦保持；此属性为 false 时只挂起、不析构。
        /// </summary>
        public bool SessionOnFocusedMap =>
            !disposed && WorldRendererUtility.DrawingMap && Find.CurrentMap != null
            && Vessel.IsValid && Vessel.Map == Find.CurrentMap;

        // ---- tick 路径（PlayerShipController 转发）----

        /// <summary>只采样瞄准输入；公共 ShipController 负责转向和开火。</summary>
        public void Tick()
        {
            HasInput = false;
            FireMain = FireSecondary = false;
            if (disposed) return;
            ShipBase ship = Vessel;
            if (!ship.IsValid)
            {
                Dispose("aim ship lost");
                return;
            }
            SampleFrame(Time.frameCount);
            if (!sampledAllowed) { buttons.Clear(); return; }
            SpaceObject container = universe.FrameOf(ship.Map);
            if (container == null) return;
            // 悬停窗口（面板/原版 UI）不射击；暂停时无 tick，天然不射。
            buttons.Validate(true, sampledMain, sampledSecondary);
            FireMain = buttons.Main;
            FireSecondary = buttons.Secondary;
            HasInput = true;
        }

        // ---- 帧路径（ShellRenderer 经 Controls.EmitAimMarkers 转发）----

        internal int EmitMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            if (disposed || !Vessel.IsValid || Vessel.Map != frameMap) return baseSlot;
            SampleFrame(Time.frameCount);
            long start = universe.Performance.Start();
            int slot = baseSlot;
            if (previewFrame != Time.frameCount || previewStructureVersion != SubstructureOwnership.GlobalStructureVersion)
            {
                previewPoints = fireControl.LeadPoints(Vessel);
                previewFrame = Time.frameCount;
                previewStructureVersion = SubstructureOwnership.GlobalStructureVersion;
            }
            foreach (Vector3 point in previewPoints)
                AimMarkersRenderer.EmitMarker(frame, frameMap, point, AimMarkerKind.Lead, layer, ref slot);
            AimMarkersRenderer.EmitMarker(frame, frameMap,
                SpaceTransform.MapPointToUniverse(frame, frameMap, LocalAimPoint), AimMarkerKind.Aim, layer, ref slot);
            universe.Performance.Record(PerformanceStage.AimPreview, start);
            return slot;
        }

        internal void SampleFrame(int frame)
        {
            var input = universe.PlayerInput;
            input.Sample(frame);
            sampledAllowed = InputAllowed();
            sampledMain = input.MainHeld; sampledSecondary = input.SecondaryHeld;
            LocalAimPoint = input.LocalMouse;
            if (!sampledAllowed) buttons.Clear();
        }

        internal static bool MouseOverUiWindow()
        {
            // tick 与 OnGUI 共用实时屏幕鼠标；Event.current 可残留，GUI 变换仅在 OnGUI 内有效。
            Vector2 point = UI.MousePositionOnUIInverted;
            return Find.WindowStack.GetWindowAt(point) != null
                || Find.WindowStack.AnyWindowAbsorbingAllInput
                || ShipStatusPanel.ContainsScreenPoint(point)
                || AimUiRegions.Contains(point)
                || Find.ColonistBar.AnyColonistOrCorpseAt(point)
                || point.y >= UI.screenHeight - 35f;
        }

        private bool InputAllowed()
        {
            if (inputMap != Find.CurrentMap)
            { buttons.Clear(); inputMap = Find.CurrentMap; previewFrame = -1; previewPoints = null; }
            return SessionOnFocusedMap && universe.Controls.ManualAimOwner(ownerId)
                && universe.PlayerInput.MapAllowed(Time.frameCount);
        }

        // Called after vanilla windows/widgets have consumed their events. Only an unclaimed
        // map MouseDown can authorize a volley; UI drags can never acquire authorization.
        internal void ProcessInputEvent()
        {
            universe.PlayerInput.InvalidateGate(); // GUI authorization/geometry changes must invalidate a prior tick sample.
            bool allowed = InputAllowed();
            universe.PlayerInput.Sample(Time.frameCount);
            buttons.Validate(allowed, universe.PlayerInput.MainHeld, universe.PlayerInput.SecondaryHeld);
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.Used
                && (ev.rawType == EventType.MouseDown || ev.rawType == EventType.MouseDrag)) buttons.Clear();
            if (!allowed || ev == null) return;
            if (ev.type == EventType.MouseDown && (ev.button == 0 || ev.button == 1))
            {
                buttons.Press(ev.button);
                ev.Use();
            }
        }

        /// <summary>析构收尾：切出瞄准/切换目标/船失效统一走这里（边沿日志）。标记以
        /// 会话存在为门槛，Dispose 后自动停发，无逐项清理。</summary>
        public void Dispose(string reason)
        {
            if (disposed) return;
            disposed = true;
            universe.PlayerInput.InvalidateGate(); inputMap = null;
            previewFrame = -1; previewPoints = null;
#if !RIMWORLD_1_7
            Application.focusChanged -= OnFocusChanged;
#endif
            buttons.Clear();
            DeltaVLog.Message(DeltaVLogModule.Input, $"aim session ended ({reason}, owner #{ownerId})");
        }
    }

    /// <summary>Session-owned mouse authorization; independent of Unity for regression checks.</summary>
    internal sealed class AimButtonLatch
    {
        public bool Main { get; private set; }
        public bool Secondary { get; private set; }
        public void Press(int button) { if (button == 0) Main = true; if (button == 1) Secondary = true; }
        public void Validate(bool allowed, bool mainHeld, bool secondaryHeld)
        {
            Main &= allowed && mainHeld;
            Secondary &= allowed && secondaryHeld;
        }
        public void Clear() { Main = Secondary = false; }
    }
}

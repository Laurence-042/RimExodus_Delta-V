using RimWorld.Planet;
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

        /// <summary>瞄准的船（带种类的跨图稳定身份）。</summary>
        public readonly DriveSubject ownerId;

        internal SpaceAimController(UniverseManager universe, DriveSubject ownerId, ShipFireControl fireControl)
        {
            this.universe = universe;
            this.ownerId = ownerId;
            this.fireControl = fireControl;
        }

        public bool Active => !disposed;
        public bool HasInput { get; private set; }
        public bool FireMain { get; private set; }
        public bool FireSecondary { get; private set; }
        public Vector3 AimPoint { get; private set; }

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
            if (!WorldRendererUtility.DrawingMap || ship.Map != Find.CurrentMap) return;
            SpaceObject container = universe.FrameOf(ship.Map);
            if (container == null) return;
            // 悬停窗口（面板/原版 UI）不射击；暂停时无 tick，天然不射。
            if (MouseOverUiWindow()) return;
            FireMain = Input.GetMouseButton(0);
            FireSecondary = Input.GetMouseButton(1);
            AimPoint = MouseUniversePoint(container, ship.Map);
            HasInput = true;
        }

        // ---- 帧路径（ShellRenderer 经 Controls.EmitAimMarkers 转发）----

        internal int EmitMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            if (disposed || !Vessel.IsValid || Vessel.Map != frameMap) return baseSlot;
            int slot = baseSlot;
            foreach (Vector3 point in fireControl.LeadPoints(Vessel))
                AimMarkersRenderer.EmitMarker(frame, frameMap, point, AimMarkerKind.Lead, layer, ref slot);
            AimMarkersRenderer.EmitMarker(frame, frameMap, MouseUniversePoint(frame, frameMap), AimMarkerKind.Aim, layer, ref slot);
            return slot;
        }

        public static Vector3 MouseUniversePoint(SpaceObject container, Map map)
        {
            Vector3 mouse = UI.MouseMapPosition();
            return SpaceTransform.MapPointToUniverse(container, map, new Vector2(mouse.x, mouse.z));
        }

        internal static bool MouseOverUiWindow()
        {
            // 与 HandleShellClick 同一坐标口径（GUI → screen 后查窗口矩形）。
            return Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(UI.MousePositionOnUI)) != null;
        }

        /// <summary>析构收尾：切出瞄准/切换目标/船失效统一走这里（边沿日志）。标记以
        /// 会话存在为门槛，Dispose 后自动停发，无逐项清理。</summary>
        public void Dispose(string reason)
        {
            if (disposed) return;
            disposed = true;
            DeltaVLog.Message(DeltaVLogModule.Input, $"aim session ended ({reason}, owner #{ownerId})");
        }
    }
}

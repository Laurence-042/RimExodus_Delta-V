using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 瞄准输入会话（太空战架构 §6）：挂在玩家船 controller 上，只采样玩家输入与
    /// 标记解算（预瞄指示器/瞄准位置标记的截击
    /// 计算在此，绘制走 <see cref="AimMarkersRenderer"/> 无状态助手）；Dispose 即
    /// 收尾——标记管线以会话存在为唯一门槛，会话销毁后自然停发，无注册/反注册
    /// 路径可泄漏（修复：脱离对接后其他船上的预瞄残留）。
    ///
    /// 身份 = 归属域 ownerId（跨图稳定）：武器由共同 controller 每 tick 经
    /// <see cref="SpaceWeaponService.WeaponsOn"/> 重查（§21 单实现：不缓存武器清单），
    /// 对接合图/脱离换图后自动跟随——瞄准会话与战斗状态跨地图变更保持。
    /// 会话独立于驾驶（PlayerController）与自动驾驶：进入对接/切换控制态不影响；
    /// 跨聚焦切换保持（非本船聚焦图不射击、不画标记）；船损毁/归属域消失自动析构。
    /// 运行时对象：不序列化，读档无瞄准会话。
    /// </summary>
    public sealed class SpaceAimController
    {
        private readonly UniverseManager universe;
        private bool disposed;

        /// <summary>瞄准的船（归属域 id；跨图稳定的唯一身份）。</summary>
        public readonly int ownerId;

        public SpaceAimController(UniverseManager universe, int ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        public bool Active => !disposed;
        public bool HasInput { get; private set; }
        public bool FireMain { get; private set; }
        public bool FireSecondary { get; private set; }
        public Vector3 AimPoint { get; private set; }

        public Ship Ship => ShipRegistry.ShipByOwner(ownerId);

        /// <summary>
        /// 会话是否作用于当前聚焦图（射击、标记绘制与点击让位的共同前提）。
        /// 会话本身跨聚焦保持；此属性为 false 时只挂起、不析构。
        /// </summary>
        public bool SessionOnFocusedMap =>
            !disposed && Find.CurrentMap != null && Ship.IsValid && Ship.map == Find.CurrentMap;

        // ---- tick 路径（PlayerShipController 转发）----

        /// <summary>只采样瞄准输入；公共 ShipController 负责转向和开火。</summary>
        public void Tick()
        {
            HasInput = false;
            FireMain = FireSecondary = false;
            if (disposed) return;
            Ship ship = Ship;
            if (!ship.IsValid)
            {
                Dispose("aim ship lost");
                return;
            }
            if (ship.map != Find.CurrentMap) return;
            SpaceObject container = universe.FrameOf(ship.map);
            if (container == null) return;
            // 悬停窗口（面板/原版 UI）不射击；暂停时无 tick，天然不射。
            if (SpaceWeaponService.MouseOverUiWindow()) return;
            FireMain = Input.GetMouseButton(0);
            FireSecondary = Input.GetMouseButton(1);
            AimPoint = SpaceWeaponService.MouseUniversePoint(container, ship.map);
            HasInput = true;
        }

        // ---- 帧路径（ShellRenderer 经 Controls.EmitAimMarkers 转发）----

        private readonly HashSet<float> speedScratch = new HashSet<float>();

        /// <summary>
        /// 预瞄指示器（§6.3，纯显示派生、不参与发射判定）：可见敌方容器 × 本船就绪
        /// 齐射武器的去重弹速档解算截击点（目标匀速外推 + 弹速，与 PD 提前量同一纯
        /// 函数；激光档 = 目标当前位置）；瞄准位置标记画在鼠标 universe 点。只在本船
        /// 聚焦图上出现。
        /// </summary>
        internal int EmitMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            if (disposed) return baseSlot;
            Ship ship = Ship;
            if (!ship.IsValid || ship.map != frameMap) return baseSlot;
            SpaceObject own = universe.FrameOf(ship.map);
            if (own == null) return baseSlot;
            speedScratch.Clear();
            bool laserSeen = false;
            foreach (CompSpaceWeapon weapon in SpaceWeaponService.WeaponsOn(frameMap, ownerId))
            {
                if (weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive) continue;
                CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
                ThingDef projectileDef = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                var props = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
                if (props == null) continue;
                if (props.laserRange > 0f) laserSeen = true;
                else speedScratch.Add(SpaceWeaponService.ProjectileSpeedOf(weapon));
            }
            int slot = baseSlot;
            bool drewAny = false;
            foreach (SpaceObject target in universe.objects)
            {
                if (target == own || target.map == null) continue;
                if (!SpaceWeaponService.HostileTo(target, Faction.OfPlayer)) continue;
                if (laserSeen)
                {
                    drewAny |= AimMarkersRenderer.EmitMarker(frame, frameMap,
                        target.pose.position, AimMarkerKind.Lead, layer, ref slot);
                }
                foreach (float speed in speedScratch)
                {
                    Vector2 p = OrbitSphere.Offset(own.pose.position, target.pose.position);
                    Vector2 targetVel = SpaceTransform.TransportVelocity(target.pose, own.pose.position);
                    if (!SpaceTransform.TrySolveInterceptPlanar(p, targetVel - own.pose.velocity, speed, out float time))
                    {
                        continue;
                    }
                    Vector3 intercept = OrbitSphere.Move(target.pose.position, target.pose.velocity * time);
                    drewAny |= AimMarkersRenderer.EmitMarker(frame, frameMap,
                        intercept, AimMarkerKind.Lead, layer, ref slot);
                }
            }
            Vector3 aimPoint = SpaceWeaponService.MouseUniversePoint(frame, frameMap);
            drewAny |= AimMarkersRenderer.EmitMarker(frame, frameMap, aimPoint, AimMarkerKind.Aim, layer, ref slot);
            return drewAny ? slot : baseSlot;
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

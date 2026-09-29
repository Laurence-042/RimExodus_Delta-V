using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 瞄准会话 controller（太空战架构 §6，OOP 收口）：瞄准模式 = 挂在武器服务上的
    /// 一个会话对象，自行完成射击 tick 与标记解算（预瞄指示器/瞄准位置标记的截击
    /// 计算在此，绘制走 <see cref="AimMarkersRenderer"/> 无状态助手）；Dispose 即
    /// 收尾——标记管线以会话存在为唯一门槛，会话销毁后自然停发，无注册/反注册
    /// 路径可泄漏（修复：脱离对接后其他船上的预瞄残留）。
    ///
    /// 身份 = 归属域 ownerId（跨图稳定）：武器每 tick 经
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

        public Ship Ship => ShipRegistry.ShipByOwner(ownerId);

        /// <summary>
        /// 会话是否作用于当前聚焦图（射击、标记绘制与点击让位的共同前提）。
        /// 会话本身跨聚焦保持；此属性为 false 时只挂起、不析构。
        /// </summary>
        public bool SessionOnFocusedMap =>
            !disposed && Find.CurrentMap != null && Ship.IsValid && Ship.map == Find.CurrentMap;

        // ---- tick 路径（Weapons.TickAim 转发）----

        /// <summary>玩家瞄准射击（§6.1）：会话期间炮塔持续跟踪瞄点（不依赖射击按键），
        /// 按住左键（主套）/右键（副套）持续开火；组未激活的武器不参与跟踪与开火。</summary>
        public void Tick()
        {
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
            bool main = Input.GetMouseButton(0);
            bool secondary = Input.GetMouseButton(1);
            Vector3 aimPoint = SpaceWeaponService.MouseUniversePoint(container, ship.map);
            foreach (CompSpaceWeapon weapon in SpaceWeaponService.WeaponsOn(ship.map, ownerId))
            {
                if (weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive) continue;
                CompSpaceWeaponTraverse traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
                if (traverse != null)
                {
                    // 跟踪与 Prepare 同口径：muzzle → 瞄点的 Map 角，每 tick 喂给 Traverse。
                    Vector2 muzzleMap = SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                    Vector2 aimMap = SpaceTransform.UniverseToMapPoint(container, ship.map, aimPoint);
                    Vector2 delta = aimMap - muzzleMap;
                    if (delta.sqrMagnitude > 1e-6f)
                        traverse.AimAt(Mathf.Atan2(delta.y, delta.x));
                }
                bool wanted = main && weapon.salvoSet == SpaceWeaponSalvoSet.Main
                    || secondary && weapon.salvoSet == SpaceWeaponSalvoSet.Secondary;
                if (!wanted) continue;
                universe.Weapons.TryFire(weapon, container, aimPoint, null);
            }
        }

        // ---- 帧路径（ShellRenderer 经 Weapons.EmitAimMarkers 转发）----

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
                ThingDef projectileDef = magazine?.MagazineProps.projectileDef ?? weapon.WeaponProps.projectileDef;
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

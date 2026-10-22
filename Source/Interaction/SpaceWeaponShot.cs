using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public interface ISpaceWeaponFireGate
    {
        bool CanFire(SpaceWeaponShot shot);
    }

    public interface ISpaceWeaponShotModifier
    {
        void ModifyShot(SpaceWeaponShot shot);
    }

    public interface ISpaceWeaponFireCommit
    {
        void NotifyShotCommitted(SpaceWeaponShot shot);
    }

    public interface ISpaceWeaponLaunch
    {
        bool TryLaunch(SpaceWeaponShot shot);
    }

    /// <summary>
    /// 一次跨容器射击事务。Prepare 冻结炮口、弹仓、弹体、速度、程序和目标；Launch
    /// 成功后才 Commit。能力 Comp 只读写本对象，不得建立武器家族分支。
    /// </summary>
    public sealed class SpaceWeaponShot
    {
        private readonly UniverseManager universe;
        public readonly CompSpaceWeapon weapon;
        public readonly SpaceObject container;
        public readonly Vector3 aimPoint;
        public readonly CombatTarget guidanceTarget;
        public SpaceObject target { get; private set; }
        public ThingDef projectileDef;
        public CompProperties_SpaceProjectile projectileProps;
        public CompSpaceWeaponMagazine magazine;
        public Vector2 muzzleMap;
        public Vector3 muzzleUniverse;
        public float desiredMapAngle;
        public float launchMapAngle;
        public float speed;
        public int ignitionDelayTicks;
        public float coldLaunchSpeed;
        public float damageScale = 1f;
        public float armorPenetrationScale = 1f;
        public int skill;
        private readonly Dictionary<PowerNet, float> energyByNet = new Dictionary<PowerNet, float>();
        private readonly Dictionary<CompSpaceWeaponCapacitor, float> dischargingCapacitors =
            new Dictionary<CompSpaceWeaponCapacitor, float>();
        private readonly List<CompSpaceRailgun> activeAccelerators = new List<CompSpaceRailgun>();
        private float activeSpeedBonus;
        private ISpaceWeaponLaunch emitter;
        public IReadOnlyList<CompSpaceRailgun> ActiveAccelerators => activeAccelerators;

        public SpaceWeaponShot(UniverseManager universe, CompSpaceWeapon weapon,
            SpaceObject container, WeaponFireRequest request)
        {
            this.universe = universe;
            this.weapon = weapon;
            this.container = container;
            aimPoint = request.AimPoint;
            guidanceTarget = request.GuidanceTarget;
            damageScale = weapon.WeaponProps.impactScale;
        }

        public bool TryExecute()
        {
            if (!Prepare()) return false;
            if (emitter != null)
            {
                if (!emitter.TryLaunch(this)) return false;
                Commit();
                universe.Projectiles.RegisterShotVisuals(this);
                return true;
            }
            Vector2 mapDirection = SpaceTransform.Rotate(Vector2.right, launchMapAngle);
            bool guaranteedMissileHit = false;
            if (target?.map != null && !target.map.Disposed
                && target.HasTag(SpaceObjectTag.Projectile) == true)
            {
                foreach (Thing thing in target.map.listerThings.AllThings)
                {
                    if (SpaceImpactEffects.GuaranteedMissileHit(projectileDef, thing.def))
                    {
                        guaranteedMissileHit = true;
                        break;
                    }
                }
            }
            float spread = guaranteedMissileHit ? 0f
                : Mathf.Clamp(Rand.Gaussian(0f, 0.4f), -1.2f, 1.2f)
                    * weapon.WeaponProps.baseSpreadDeg * Mathf.Deg2Rad
                    * SpaceWeaponService.SpreadFactorForSkill(skill);
            mapDirection = SpaceTransform.Rotate(mapDirection, spread);
            Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(container,
                weapon.parent.Map, muzzleMap, mapDirection);
            if (tangent.sqrMagnitude < 1e-8f) return false;
            OrbitSphere.Basis(muzzleUniverse, out Vector3 east, out Vector3 north);
            Vector3 directionWorld = east * tangent.x + north * tangent.y;
            var spec = new SpaceProjectileLaunchSpec(speed, ignitionDelayTicks,
                coldLaunchSpeed, damageScale, armorPenetrationScale);
            if (!universe.Projectiles.Launch(projectileDef, container, muzzleUniverse,
                directionWorld, weapon.parent.Faction, weapon.parent, guidanceTarget, spec))
            {
                return false;
            }
            Commit();
            universe.Projectiles.RegisterShotVisuals(this);
            return true;
        }

        private bool Prepare()
        {
            target = null;
            emitter = null;
            if (container == null || weapon?.parent == null || !weapon.parent.Spawned
                || weapon.parent.Destroyed || weapon.WeaponProps.projectileDef == null
                || !weapon.SpaceModeEnabled || weapon.Container != container)
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            if (now < weapon.cooldownUntilTick || !weapon.TryGetSkillLevel(out skill)) return false;
            CompPowerTrader power = weapon.parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;

            magazine = weapon.SelectMagazine();
            if (!weapon.MagazineAvailable(magazine)) return false;
            projectileDef = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
            armorPenetrationScale = magazine?.MagazineProps.armorPenetrationScale ?? 1f;
            projectileProps = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (projectileProps == null) return false;
            ignitionDelayTicks = weapon.WeaponProps.ignitionDelayTicks >= 0
                ? weapon.WeaponProps.ignitionDelayTicks
                : projectileProps.coldLaunchTicks;
            bool coldLaunch = ignitionDelayTicks > 0;

            SpaceWeaponAccelerationSolution acceleration = CompSpaceRailgun.Resolve(weapon);
            muzzleMap = acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon);
            muzzleUniverse = SpaceTransform.MapPointToUniverse(container, weapon.parent.Map, muzzleMap);
            float shotDistance = OrbitSphere.Offset(muzzleUniverse, aimPoint).magnitude;
            if (shotDistance < weapon.WeaponProps.minRange
                || shotDistance > weapon.WeaponProps.range) return false;
            Vector2 aimMap = SpaceTransform.UniverseToMapPoint(container, weapon.parent.Map, aimPoint);
            Vector2 delta = aimMap - muzzleMap;
            if (delta.sqrMagnitude < 1e-6f) return false;
            desiredMapAngle = Mathf.Atan2(delta.y, delta.x);

            float baseAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
            float launcherLockArc = weapon.WeaponProps.lockArcDeg >= 0f
                ? weapon.WeaponProps.lockArcDeg
                : weapon.WeaponProps.fireArcDeg;
            // 编程弹药可以进一步限制发射架的锁定包线；不能反过来扩大安装座能力。
            float lockArc = projectileProps.guided || coldLaunch
                ? Mathf.Min(launcherLockArc, projectileProps.lockArcDeg)
                : launcherLockArc;
            if (!WithinArc(desiredMapAngle, baseAngle, lockArc)) return false;

            CompSpaceWeaponTraverse traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
            if (traverse != null)
            {
                traverse.AimAt(desiredMapAngle);
                launchMapAngle = traverse.CurrentAngle;
            }
            else if (!coldLaunch && weapon.WeaponProps.topTurret)
            {
                // 未装 Delta-V Traverse 的第三方顶置炮沿其瞄点开火，维持核心 Comp 的最小契约。
                launchMapAngle = desiredMapAngle;
            }
            else
            {
                launchMapAngle = baseAngle;
            }
            if (!SpaceWeaponGeometry.WithinFireDeviation(desiredMapAngle, launchMapAngle,
                    weapon.fireDeviationDeg)) return false;

            if (!PrepareEnergy(acceleration)) return false;
            speed = projectileProps.speed + activeSpeedBonus;
            coldLaunchSpeed = speed * projectileProps.coldLaunchSpeedFactor;
            target = guidanceTarget.TryResolve(universe, out var resolved) ? resolved.Container : null;
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponShotModifier modifier) modifier.ModifyShot(this);
            }
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponFireGate gate && !gate.CanFire(this)) return false;
                if (comp is ISpaceWeaponLaunch launch) emitter = launch;
            }
            if (emitter == null && weapon.safeFire && !SafePathClear()) return false;
            return true;
        }

        private bool PrepareEnergy(SpaceWeaponAccelerationSolution acceleration)
        {
            dischargingCapacitors.Clear();
            energyByNet.Clear();
            activeAccelerators.Clear();
            activeSpeedBonus = 0f;
            if (!ReserveCapacitor(weapon.AttachedThings(), weapon.WeaponProps.shotEnergy)
                && !TryReserveGridEnergy(weapon.parent, weapon.WeaponProps.shotEnergy)) return false;
            foreach (CompSpaceRailgun segment in acceleration.segments)
            {
                if (segment.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }) continue;
                if (!ReserveCapacitor(segment.AttachedThings(), segment.RailProps.shotEnergy)
                    && !TryReserveGridEnergy(segment.parent, segment.RailProps.shotEnergy)) continue;
                activeAccelerators.Add(segment);
                activeSpeedBonus += segment.RailProps.speedBonus;
            }
            return true;
        }

        /// <summary>武器候选筛选与真实发射共用逐段能量规划。</summary>
        public static bool HasShotEnergy(CompSpaceWeapon weapon)
        {
            if (weapon?.parent == null || !weapon.parent.Spawned) return false;
            var probe = new SpaceWeaponShot(null, weapon, null, default);
            return probe.PrepareEnergy(CompSpaceRailgun.Resolve(weapon));
        }

        // 状态显示和真实发射共用 PrepareEnergy 的逐段预留结果。
        public static bool TryPlanEnergy(CompSpaceWeapon weapon,
            SpaceWeaponAccelerationSolution acceleration, out List<CompSpaceRailgun> active)
        {
            if (weapon?.parent == null || !weapon.parent.Spawned)
            {
                active = null;
                return false;
            }
            var probe = new SpaceWeaponShot(null, weapon, null, default);
            bool ready = probe.PrepareEnergy(acceleration);
            active = probe.activeAccelerators;
            return ready;
        }

        /// <summary>预瞄弹速与实际发射共用逐段能量规划。</summary>
        public static float ProjectedSpeed(CompSpaceWeapon weapon)
        {
            if (weapon == null) return 0f;
            CompSpaceWeaponMagazine magazine = weapon.SelectMagazine();
            ThingDef projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
            float baseSpeed = projectile?.GetCompProperties<CompProperties_SpaceProjectile>()?.speed ?? 0f;
            return ProjectedSpeed(weapon, CompSpaceRailgun.Resolve(weapon), baseSpeed);
        }

        internal static float ProjectedSpeed(CompSpaceWeapon weapon, SpaceWeaponAccelerationSolution acceleration, float baseSpeed)
        {
            var probe = new SpaceWeaponShot(null, weapon, null, default);
            return probe.PrepareEnergy(acceleration) ? baseSpeed + probe.activeSpeedBonus : baseSpeed;
        }

        public static bool HasClearFixedPath(CompSpaceWeapon weapon,
            SpaceWeaponAccelerationSolution acceleration)
        {
            if (weapon?.parent == null || !weapon.parent.Spawned) return false;
            if (!weapon.safeFire || weapon.WeaponProps.topTurret) return true;
            var probe = new SpaceWeaponShot(null, weapon, null, default);
            probe.muzzleMap = acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon);
            probe.launchMapAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
            return probe.SafePathClear();
        }

        private bool ReserveCapacitor(IEnumerable<ThingWithComps> attachments, float wattDays)
        {
            if (wattDays <= 0f) return false;
            CompSpaceWeaponCapacitor selected = null;
            foreach (ThingWithComps attached in attachments)
            {
                CompSpaceWeaponCapacitor capacitor = attached.TryGetComp<CompSpaceWeaponCapacitor>();
                if (capacitor == null || !capacitor.CanSupply(wattDays)) continue;
                if (selected == null || capacitor.StoredEnergy > selected.StoredEnergy
                    || (capacitor.StoredEnergy == selected.StoredEnergy
                        && capacitor.parent.thingIDNumber < selected.parent.thingIDNumber))
                    selected = capacitor;
            }
            if (selected == null) return false;
            dischargingCapacitors[selected] = wattDays;
            return true;
        }

        private bool TryReserveGridEnergy(ThingWithComps source, float wattDays)
        {
            if (wattDays <= 0f) return true;
            PowerNet net = source.TryGetComp<CompPowerTrader>()?.PowerNet;
            if (net == null) return false;
            energyByNet.TryGetValue(net, out float existing);
            if (net.CurrentStoredEnergy() + 0.0001f < existing + wattDays) return false;
            energyByNet[net] = existing + wattDays;
            return true;
        }

        private void Commit()
        {
            foreach (var cost in energyByNet)
            {
                float remaining = cost.Value;
                foreach (CompPowerBattery battery in cost.Key.batteryComps)
                {
                    if (battery.StunnedByEMP || remaining <= 0f) continue;
                    float drawn = Mathf.Min(remaining, battery.StoredEnergy);
                    if (drawn <= 0f) continue;
                    battery.DrawPower(drawn);
                    remaining -= drawn;
                }
            }
            foreach (var capacitor in dischargingCapacitors)
                capacitor.Key.Consume(capacitor.Value);
            magazine?.ConsumeShot();
            weapon.NotifyMagazineConsumed(magazine);
            weapon.cooldownUntilTick = Find.TickManager.TicksGame + weapon.WeaponProps.cooldownTicks;
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponFireCommit commit) commit.NotifyShotCommitted(this);
                if (comp is IFireEffect fireEffect)
                {
                    fireEffect.NotifyFired(weapon, container, muzzleUniverse);
                }
            }
        }

        private IEnumerable<ThingComp> CapabilityComps()
        {
            foreach (ThingComp comp in weapon.parent.AllComps) yield return comp;
            foreach (ThingWithComps attached in weapon.AttachedThings())
            {
                CompSpaceWeaponAttachment attachment = attached.TryGetComp<CompSpaceWeaponAttachment>();
                if (attachment != null && !attachment.Enabled) continue;
                foreach (ThingComp comp in attached.AllComps) yield return comp;
            }
        }

        private bool SafePathClear()
        {
            if (weapon.WeaponProps.topTurret) return true;
            Map map = weapon.parent.Map;
            ShipBase ship = weapon.OwnerShip;
            if (map == null || !ship.IsValid) return false;
            var hull = new HashSet<IntVec3>(ship.HullCells);
            Vector2 dir = SpaceTransform.Rotate(Vector2.right, launchMapAngle);
            IntVec3 previous = IntVec3.Invalid;
            float max = Mathf.Sqrt(map.Size.x * map.Size.x + map.Size.z * map.Size.z) + 2f;
            for (float distance = 0.25f; distance <= max; distance += 0.25f)
            {
                Vector2 point = muzzleMap + dir * distance;
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));
                if (!cell.InBounds(map)) return true;
                if (cell == previous) continue;
                previous = cell;
                if (weapon.parent.OccupiedRect().Contains(cell)) continue;
                if (!hull.Contains(cell)) continue;
                SpaceCellKind kind = SpaceStructureRules.ClassifyCell(map, cell, out Thing building);
                if ((kind == SpaceCellKind.Building || kind == SpaceCellKind.NaturalRock)
                    && SpaceStructureRules.BuildingHitChance(building) > 0f) return false;
            }
            return true;
        }

        private static bool WithinArc(float angle, float baseAngle, float arcDeg)
        {
            float half = arcDeg * 0.5f * Mathf.Deg2Rad;
            return half >= Mathf.PI - 0.001f
                || Mathf.Abs(SpaceTransform.WrapAngle(angle - baseAngle)) <= half + 0.0001f;
        }
    }
}

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
        public SpaceObject target;
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
        public int skill;

        public SpaceWeaponShot(UniverseManager universe, CompSpaceWeapon weapon,
            SpaceObject container, Vector3 aimPoint, SpaceObject explicitTarget)
        {
            this.universe = universe;
            this.weapon = weapon;
            this.container = container;
            this.aimPoint = aimPoint;
            target = explicitTarget;
        }

        public bool TryExecute()
        {
            if (!Prepare()) return false;
            Vector2 mapDirection = SpaceTransform.Rotate(Vector2.right, launchMapAngle);
            float spread = Mathf.Clamp(Rand.Gaussian(0f, 0.4f), -1.2f, 1.2f)
                * weapon.WeaponProps.baseSpreadDeg * Mathf.Deg2Rad
                * SpaceWeaponService.SpreadFactorForSkill(skill);
            mapDirection = SpaceTransform.Rotate(mapDirection, spread);
            Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(container,
                weapon.parent.Map, muzzleMap, mapDirection);
            if (tangent.sqrMagnitude < 1e-8f) return false;
            OrbitSphere.Basis(muzzleUniverse, out Vector3 east, out Vector3 north);
            Vector3 directionWorld = east * tangent.x + north * tangent.y;
            var spec = new SpaceProjectileLaunchSpec(speed, ignitionDelayTicks,
                coldLaunchSpeed, damageScale);
            if (!universe.Projectiles.Launch(projectileDef, container, muzzleUniverse,
                directionWorld, weapon.parent.Faction, weapon.parent, target, spec))
            {
                return false;
            }
            Commit();
            return true;
        }

        private bool Prepare()
        {
            if (container == null || weapon?.parent == null || !weapon.parent.Spawned
                || weapon.parent.Destroyed || weapon.WeaponProps.projectileDef == null)
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            if (now < weapon.cooldownUntilTick || !weapon.TryGetSkillLevel(out skill)) return false;
            CompPowerTrader power = weapon.parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn) return false;

            magazine = weapon.SelectMagazine();
            bool magazineRequired = weapon.parent.TryGetComp<CompSpaceWeaponMagazine>() != null;
            if (magazineRequired && magazine == null) return false;
            projectileDef = magazine?.MagazineProps.projectileDef ?? weapon.WeaponProps.projectileDef;
            projectileProps = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (projectileProps == null) return false;
            ignitionDelayTicks = weapon.WeaponProps.ignitionDelayTicks >= 0
                ? weapon.WeaponProps.ignitionDelayTicks
                : projectileProps.coldLaunchTicks;
            bool coldLaunch = ignitionDelayTicks > 0;

            SpaceWeaponAccelerationSolution acceleration = CompSpaceRailgun.Resolve(weapon);
            muzzleMap = acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon);
            muzzleUniverse = SpaceTransform.MapPointToUniverse(container, weapon.parent.Map, muzzleMap);
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
                if (!weapon.canFireWithoutAlignment && !traverse.AlignedTo(desiredMapAngle)) return false;
                launchMapAngle = traverse.CurrentAngle;
            }
            else if (coldLaunch)
            {
                // 冷发射固定沿架体轴线推出；目标锁定角与发射轴相互独立。
                launchMapAngle = baseAngle;
            }
            else if (weapon.canFireWithoutAlignment)
            {
                // 固定轴炮（轨道炮）或无 Traverse 的第三方武器沿安装轴发射；
                // lockArc 仍负责能否选中目标，发射轴与目标方向不再混为一谈。
                launchMapAngle = baseAngle;
            }
            else
            {
                if (!WithinArc(desiredMapAngle, baseAngle, weapon.WeaponProps.fireArcDeg)) return false;
                launchMapAngle = desiredMapAngle;
            }

            speed = projectileProps.speed + acceleration.speedBonus;
            coldLaunchSpeed = speed * projectileProps.coldLaunchSpeedFactor;
            if (target == null && projectileProps.guided)
            {
                Vector2 lockDirection = SpaceTransform.MapDirectionToUniverseTangent(container,
                    weapon.parent.Map, muzzleMap,
                    SpaceTransform.Rotate(Vector2.right, desiredMapAngle));
                target = SpaceWeaponService.TargetAlongRay(universe, container, muzzleUniverse,
                    lockDirection, weapon.parent.Faction);
            }

            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponShotModifier modifier) modifier.ModifyShot(this);
            }
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponFireGate gate && !gate.CanFire(this)) return false;
            }
            return !weapon.safeFire || SafePathClear();
        }

        private void Commit()
        {
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
            int owner = SpaceWeaponGeometry.OwnerIdOf(weapon.parent);
            if (map == null || owner <= 0) return false;
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
                if (SubstructureOwnership.TryGet(map)?.OwnerAt(cell) != owner) continue;
                SpaceCellKind kind = SpaceStructureRules.ClassifyCell(map, cell, out _);
                if (kind == SpaceCellKind.Building || kind == SpaceCellKind.NaturalRock) return false;
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

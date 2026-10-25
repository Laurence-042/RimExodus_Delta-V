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
        public readonly bool manualFire;
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
        private SpaceWeaponParameters parameters;
        private bool hasParameters;
        private Vector2? preparedAimMap;
        private SpaceObject resolvedTarget;
        private bool targetResolved;
        private ISpaceWeaponLaunch emitter;
        public IReadOnlyList<CompSpaceRailgun> ActiveAccelerators => parameters.energy != null
            ? parameters.energy.active : System.Array.Empty<CompSpaceRailgun>();

        public SpaceWeaponShot(UniverseManager universe, CompSpaceWeapon weapon,
            SpaceObject container, WeaponFireRequest request)
        {
            this.universe = universe;
            this.weapon = weapon;
            this.container = container;
            aimPoint = request.AimPoint;
            guidanceTarget = request.GuidanceTarget;
            manualFire = request.ManualFire;
            damageScale = weapon.WeaponProps.impactScale;
        }

        internal SpaceWeaponShot(UniverseManager universe, CompSpaceWeapon weapon, SpaceObject container,
            WeaponFireRequest request, SpaceWeaponParameters parameters, int skill, Vector2 aimMap, SpaceObject resolvedTarget, bool targetResolved)
            : this(universe, weapon, container, request)
        { this.parameters = parameters; this.skill = skill; preparedAimMap = aimMap; hasParameters = true;
            this.resolvedTarget = resolvedTarget; this.targetResolved = targetResolved; }

        private bool Reject(WeaponReject reason)
        { universe?.Performance.RecordWeaponResult(reason); return false; }

        public bool TryExecute()
        {
            long prepareStart = universe?.Performance.Start() ?? 0L;
            bool prepared;
            try { prepared = Prepare(); }
            finally { universe?.Performance.Record(PerformanceStage.WeaponPrepare, prepareStart); }
            if (!prepared) return false;
            long launchStart = universe?.Performance.Start() ?? 0L;
            try { return LaunchPrepared(); }
            finally { universe?.Performance.Record(PerformanceStage.WeaponLaunch, launchStart); }
        }

        private bool LaunchPrepared()
        {
            if (emitter != null)
            {
                if (!emitter.TryLaunch(this)) return Reject(WeaponReject.Launch);
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
            if (tangent.sqrMagnitude < 1e-8f) return Reject(WeaponReject.Launch);
            OrbitSphere.Basis(muzzleUniverse, out Vector3 east, out Vector3 north);
            Vector3 directionWorld = east * tangent.x + north * tangent.y;
            var spec = new SpaceProjectileLaunchSpec(speed, ignitionDelayTicks,
                coldLaunchSpeed, damageScale, armorPenetrationScale);
            if (!universe.Projectiles.Launch(projectileDef, container, muzzleUniverse,
                directionWorld, weapon.parent.Faction, weapon.parent, guidanceTarget, spec))
            {
                return Reject(WeaponReject.Launch);
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
                return Reject(WeaponReject.Invalid);
            }
            if (!hasParameters)
            {
                WeaponReject readiness = SpaceWeaponService.CheckReadiness(weapon, out skill);
                if (readiness != WeaponReject.None) return Reject(readiness);
                parameters = SpaceWeaponBallistics.Read(weapon, container, new SpaceWeaponEnergy(), true);
                hasParameters = true;
            }
            magazine = parameters.magazine;
            if (!weapon.MagazineAvailable(magazine)) return Reject(WeaponReject.Ammo);
            projectileDef = parameters.aim.Projectile;
            armorPenetrationScale = magazine?.MagazineProps.armorPenetrationScale ?? 1f;
            projectileProps = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (projectileProps == null) return Reject(WeaponReject.Ammo);
            ignitionDelayTicks = weapon.WeaponProps.ignitionDelayTicks >= 0
                ? weapon.WeaponProps.ignitionDelayTicks
                : projectileProps.coldLaunchTicks;
            bool coldLaunch = ignitionDelayTicks > 0;
            CompSpaceWeaponTraverse traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();

            muzzleMap = parameters.aim.MuzzleMap;
            muzzleUniverse = parameters.aim.Muzzle;
            float baseAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
            if (!manualFire)
            {
                float shotDistance = OrbitSphere.Offset(muzzleUniverse, aimPoint).magnitude;
                if (shotDistance < weapon.WeaponProps.minRange
                    || shotDistance > weapon.WeaponProps.range) return Reject(WeaponReject.Range);
            }
            Vector2 aimMap = preparedAimMap ?? SpaceTransform.UniverseToMapPoint(container, weapon.parent.Map, aimPoint);
            Vector2 delta = aimMap - muzzleMap;
            if (delta.sqrMagnitude < 1e-6f && !manualFire) return Reject(WeaponReject.Alignment);
            // 鼠标恰在炮口时没有瞄准方向，手控沿当前炮口／固定轴继续射击。
            desiredMapAngle = delta.sqrMagnitude < 1e-6f
                ? traverse?.CurrentAngle ?? baseAngle : Mathf.Atan2(delta.y, delta.x);

            float launcherLockArc = weapon.WeaponProps.lockArcDeg >= 0f
                ? weapon.WeaponProps.lockArcDeg
                : weapon.WeaponProps.fireArcDeg;
            // 编程弹药可以进一步限制发射架的锁定包线；不能反过来扩大安装座能力。
            float lockArc = projectileProps.guided || coldLaunch
                ? Mathf.Min(launcherLockArc, projectileProps.lockArcDeg)
                : launcherLockArc;
            if (!WithinArc(desiredMapAngle, baseAngle, lockArc)) return Reject(WeaponReject.Arc);

            if (traverse != null)
            {
                if (!preparedAimMap.HasValue) traverse.AimAt(desiredMapAngle);
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
            float allowedDeviation = manualFire ? weapon.manualFireDeviationDeg : weapon.fireDeviationDeg;
            if (!SpaceWeaponGeometry.WithinFireDeviation(desiredMapAngle, launchMapAngle,
                    allowedDeviation)) return Reject(WeaponReject.Alignment);

            if (!parameters.energy.ready) return Reject(WeaponReject.Energy);
            speed = parameters.aim.Speed;
            coldLaunchSpeed = speed * projectileProps.coldLaunchSpeedFactor;
            target = targetResolved ? resolvedTarget
                : guidanceTarget.TryResolve(universe, out var resolved) ? resolved.Container : null;
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponShotModifier modifier) modifier.ModifyShot(this);
            }
            foreach (ThingComp comp in CapabilityComps())
            {
                if (comp is ISpaceWeaponFireGate gate && !gate.CanFire(this)) return Reject(WeaponReject.Gate);
                if (comp is ISpaceWeaponLaunch launch) emitter = launch;
            }
            if (emitter == null && weapon.safeFire && !HasClearPath(weapon, muzzleMap, launchMapAngle))
                return Reject(WeaponReject.Safety);
            return true;
        }

        public static bool HasClearFixedPath(CompSpaceWeapon weapon, SpaceWeaponAccelerationSolution acceleration)
        {
            if (weapon?.parent?.Spawned != true) return false;
            return !weapon.safeFire || HasClearPath(weapon,
                acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon),
                ShipOrientation.AngleFromRot4(weapon.parent.Rotation));
        }

        private void Commit()
        {
            long start = universe?.Performance.Start() ?? 0L;
            try
            {
                parameters.energy.Commit();
                universe?.Performance.RecordWeaponResult(WeaponReject.None);
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
            finally { universe?.Performance.Record(PerformanceStage.WeaponCommit, start); }
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

        private static bool HasClearPath(CompSpaceWeapon weapon, Vector2 muzzleMap, float launchMapAngle)
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

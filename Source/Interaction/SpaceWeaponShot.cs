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
            damageScale = weapon.WeaponProps.impactScale * (weapon.Tuning?.power ?? 1);
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
            if (!(emitter != null ? emitter.TryLaunch(this) : LaunchProjectile()))
                return Reject(WeaponReject.Launch);
            Commit();
            universe.Projectiles.RegisterShotVisuals(this);
            return true;
        }

        internal static float FanOffset(CompProperties_SpaceWeapon props, int index)
            => props.projectilesPerShot <= 1 ? 0f
                : Mathf.Lerp(-props.projectileFanHalfAngle, props.projectileFanHalfAngle,
                    (float)index / (props.projectilesPerShot - 1)) * Mathf.Deg2Rad;

        // Launch capabilities may use the same prepared projectile without committing twice.
        internal bool LaunchProjectile()
        {
            if (weapon.WeaponProps.projectilesPerShot > 1) return LaunchFan();
            Vector2 mapDirection = SpaceTransform.Rotate(Vector2.right, launchMapAngle);
            bool guaranteedMissileHit = SpaceImpactEffects.GuaranteedMissileHit(
                projectileDef, target?.InterceptionBody?.def);
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
                coldLaunchSpeed, damageScale, armorPenetrationScale, parameters.mass, parameters.power);
            bool traceCannon = DeltaVLog.Enabled(DeltaVLogModule.Development)
                && weapon.WeaponProps.projectileDef == DeltaVDefOf.DeltaV_Projectile_Cannon
                && guidanceTarget.Kind == CombatTargetKind.Ship;
            if (!universe.Projectiles.Launch(projectileDef, container, muzzleUniverse,
                directionWorld, weapon.parent.Faction, weapon.parent, guidanceTarget, spec,
                traceCannon ? aimPoint : (Vector3?)null,
                SpaceTransform.WrapAngle(launchMapAngle - desiredMapAngle) * Mathf.Rad2Deg,
                spread * Mathf.Rad2Deg, weapon))
            {
                return false;
            }
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
            if (weapon.OwnerShip.EffectiveFireControlMode == ShipFireControlMode.Disabled
                || !manualFire && weapon.WeaponProps.manualOnly
                    && (weapon.aimMode != SpaceWeaponAimMode.PointDefense || !weapon.AllowsPointDefense)) return Reject(WeaponReject.Gate);
            if (!hasParameters)
            {
                WeaponReject readiness = SpaceWeaponService.CheckReadiness(weapon, out skill);
                if (readiness != WeaponReject.None) return Reject(readiness);
                parameters = SpaceWeaponBallistics.Read(weapon, container, new SpaceWeaponEnergy(), true);
                hasParameters = true;
            }
            damageScale = weapon.WeaponProps.impactScale * parameters.power;
            magazine = parameters.magazine;
            if (!weapon.MagazineAvailable(magazine)) return Reject(WeaponReject.Ammo);
            projectileDef = parameters.aim.Projectile;
            armorPenetrationScale = parameters.penetrationScale;
            projectileProps = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (projectileProps == null) return Reject(WeaponReject.Ammo);
            ignitionDelayTicks = weapon.WeaponProps.ignitionDelayTicks >= 0
                ? weapon.WeaponProps.ignitionDelayTicks
                : projectileProps.coldLaunchTicks;
            bool coldLaunch = ignitionDelayTicks > 0;
            CompSpaceWeaponTraverse traverse = weapon.Component<CompSpaceWeaponTraverse>();

            muzzleMap = parameters.aim.MuzzleMap;
            muzzleUniverse = parameters.aim.Muzzle;
            float baseAngle = ShipOrientation.AngleFromRot4(weapon.parent.Rotation);
            if (!manualFire || weapon.coverage.range > 0f)
            {
                float shotDistance = OrbitSphere.Offset(muzzleUniverse, aimPoint).magnitude;
                if (shotDistance < weapon.WeaponProps.minRange
                    || shotDistance > weapon.EffectiveRange) return Reject(WeaponReject.Range);
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
            if (weapon.CoverageError(desiredMapAngle) > 0.05f || !WithinArc(desiredMapAngle, baseAngle, lockArc)) return Reject(WeaponReject.Arc);

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
            if (emitter == null && ProtectsHull(weapon) && !HasClearShot(weapon, muzzleMap, launchMapAngle))
                return Reject(WeaponReject.Safety);
            return true;
        }

        private bool LaunchFan()
        {
            var props = projectileDef.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null || props.hasMap || props.guided || props.laserRange > 0f) return false;
            DriveSubject selfFire = weapon.WeaponProps.casemate && !weapon.safeFire
                ? weapon.OwnerShip.Identity.InContainer(container.objectId) : default;
            var spec = new SpaceProjectileLaunchSpec(speed, 0, speed, damageScale,
                armorPenetrationScale, parameters.mass / weapon.WeaponProps.projectilesPerShot,
                parameters.power, selfFire);
            OrbitSphere.Basis(muzzleUniverse, out Vector3 east, out Vector3 north);
            for (int i = 0; i < weapon.WeaponProps.projectilesPerShot; i++)
            {
                Vector2 direction = SpaceTransform.Rotate(Vector2.right, launchMapAngle + FanOffset(weapon.WeaponProps, i));
                Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(container, weapon.parent.Map, muzzleMap, direction);
                // With safety off, a wall under the barrel still receives the shot.
                Vector3? boreStart = selfFire.key > 0
                    ? SpaceTransform.MapPointToUniverse(container, weapon.parent.Map,
                        muzzleMap - direction * weapon.WeaponProps.turretMuzzleDistance)
                    : null;
                // Prevalidated mapless pellets have no fallible lease/allocation step.
                universe.Projectiles.Launch(projectileDef, container, muzzleUniverse,
                    east * tangent.x + north * tangent.y, weapon.parent.Faction, weapon.parent, default, spec,
                    weapon: weapon, launchSweepStart: boreStart);
            }
            return true;
        }

        private static bool ProtectsHull(CompSpaceWeapon weapon)
            => weapon.WeaponProps.requireHullClearance || weapon.safeFire;

        private static bool HasClearShot(CompSpaceWeapon weapon, Vector2 muzzle, float centerAngle)
        {
            for (int i = 0; i < weapon.WeaponProps.projectilesPerShot; i++)
                if (!HasClearPath(weapon, muzzle, centerAngle + FanOffset(weapon.WeaponProps, i))) return false;
            return true;
        }

        public static bool HasClearFixedPath(CompSpaceWeapon weapon, SpaceWeaponAccelerationSolution acceleration)
        {
            if (weapon?.parent?.Spawned != true) return false;
            return weapon.WeaponProps.casemate || !ProtectsHull(weapon) || HasClearPath(weapon,
                acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon),
                ShipOrientation.AngleFromRot4(weapon.parent.Rotation));
        }

        internal static bool HasClearAimPath(CompSpaceWeapon weapon, SpaceObject own, Vector3 point)
        {
            if (!weapon.WeaponProps.casemate) return HasClearFixedPath(weapon, weapon.Layout.acceleration);
            Vector2 delta = SpaceTransform.UniverseToMapPoint(own, weapon.parent.Map, point)
                - SpaceWeaponGeometry.FootprintCenter(weapon.parent)
                - SpaceWeaponGeometry.CasematePivotOffset(weapon.parent);
            return HasClearCasemateAim(weapon, Mathf.Atan2(delta.y, delta.x));
        }

        internal static bool HasClearCasemateAim(CompSpaceWeapon weapon, float mapAngle)
        {
            if (!weapon.safeFire) return true;
            Vector2 muzzle = SpaceWeaponGeometry.MuzzleMapPointAtAngle(weapon, mapAngle);
            return HasClearShot(weapon, muzzle, mapAngle);
        }

        private void Commit()
        {
            long start = universe?.Performance.Start() ?? 0L;
            try
            {
                parameters.energy.Commit();
                universe?.Performance.RecordWeaponResult(WeaponReject.None);
                magazine?.ConsumeLoad(parameters.heads, parameters.charge);
                weapon.NotifyMagazineConsumed(magazine);
                if (weapon.Tuning != null) weapon.Tuning.CommitShot(parameters.shotHeat, parameters.cycleTicks);
                else weapon.CommitCooldown(Find.TickManager.TicksGame, parameters.cycleTicks);
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
            foreach (ThingComp comp in weapon.Mount.Components) yield return comp;
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
            var hull = weapon.WeaponProps.casemate ? null : new HashSet<IntVec3>(ship.HullCells);
            var ownership = hull == null ? SubstructureOwnership.TryGet(map) : null;
            Vector2 dir = SpaceTransform.Rotate(Vector2.right, launchMapAngle);
            IntVec3 previous = IntVec3.Invalid;
            float max = hull == null ? weapon.EffectiveRange
                : Mathf.Sqrt(map.Size.x * map.Size.x + map.Size.z * map.Size.z) + 2f;
            float start = hull == null ? -weapon.WeaponProps.turretMuzzleDistance : 0.25f;
            for (float distance = start; distance <= max; distance += 0.25f)
            {
                Vector2 point = muzzleMap + dir * distance;
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));
                if (!cell.InBounds(map)) return true;
                if (cell == previous) continue;
                previous = cell;
                if (weapon.parent.OccupiedRect().Contains(cell)) continue;
                if (hull != null ? !hull.Contains(cell) : ownership?.OwnerAt(cell) != ship.Identity.key) continue;
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

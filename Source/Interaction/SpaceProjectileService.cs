using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DeltaV
{
    public readonly struct SpaceProjectileLaunchSpec
    {
        public readonly float speed;
        public readonly int coldLaunchTicks;
        public readonly float coldLaunchSpeed;
        public readonly float damageScale;
        public readonly float armorPenetrationScale;
        public readonly float mass, beamEnergyScale;

        public SpaceProjectileLaunchSpec(float speed, int coldLaunchTicks, float coldLaunchSpeed,
            float damageScale = 1f, float armorPenetrationScale = 1f, float mass = 1f, float beamEnergyScale = 1f)
        {
            this.speed = speed;
            this.coldLaunchTicks = coldLaunchTicks;
            this.coldLaunchSpeed = coldLaunchSpeed;
            this.damageScale = damageScale;
            this.armorPenetrationScale = armorPenetrationScale;
            this.mass = mass; this.beamEnergyScale = beamEnergyScale;
        }

        public static SpaceProjectileLaunchSpec FromDef(CompProperties_SpaceProjectile props,
            float damageScale = 1f)
        {
            return new SpaceProjectileLaunchSpec(props.speed, props.coldLaunchTicks,
                props.speed * props.coldLaunchSpeedFactor, damageScale, 1f, props.mass);
        }
    }

    /// <summary>
    /// 弹体服务（太空战架构 §4.1；取代 BoardingPods 槽位，段7 完成接管）：弹体
    /// 创建/注册（图池借出）、制导（导弹有限加速度/直射弹漂移）、超时自毁、碰撞调度
    /// 对接（SpaceCollision 的主动方来源）、渲染（弹体 def 图形投影 + 激光线段）、
    /// 图池管理与读档孤儿图回收、ResetFlow。
    ///
    /// 实例服务（UniverseManager.Projectiles）：不序列化、读档归零（弹体是瞬态成员，
    /// 写档按基础类型剔除，UniverseManager.ExposeData）；普通弹体由主循环漂移积分，
    /// 侵入 shuttle 由共同自动驾驶推进，本服务为两者记录位移段并结算碰撞。
    ///
    /// 图池纪律（§3.2）：借出 = 注册弹体容器（图归其持有，map 单一持有不变量照常）；
    /// 归还 = 注销容器 + 清内容入池（帧边界）。池内空闲图**无承载容器**——生命周期
    /// 扫描按对象迭代天然不触及，HandleDepartedSourceMap 只处理「有 owner 且空白」
    /// 的图，池图两道闸都不命中，无需豁免清单；读档后残留的弹体小图（写档剔除了
    /// 容器、图随档落盘）由 SweepOrphanProjectileMaps 按内容回收。
    ///
    /// 一切模拟 tick 驱动（不变量 #20：暂停冻结、倍速加速）；销毁/注销/图归池一律
    /// 经 QueueFrameAction 帧边界（禁止在扫掠 foreach 内执行）。
    /// </summary>
    [StaticConstructorOnStartup]
    public sealed partial class SpaceProjectileService
    {
        private const float Dt = 1f / 60f;
        private const float GuidanceVelocityTolerance = 0.25f;

        /// <summary>激光线段视觉存留（tick）。</summary>
        private const int BeamLifetimeTicks = 15;
        private const int MaxTrailPuffs = 480;

        private readonly UniverseManager universe;

        private enum FlightPhase { ColdLaunch, InitialTurn, PoweredFlight }

        private sealed class CannonShotTrace
        {
            public CombatTarget target;
            public Vector3 targetAtLaunch;
            public Vector2 targetVelocityAtLaunch;
            public Vector2 launchDirection;
            public float targetRadius;
            public float closestCenter = float.PositiveInfinity;
            public float targetDrift, lateralDrift;
            public bool hullContact, shieldContact, reported;
            public System.Action<SpaceCollision.VisualContact> onContact;
        }

        private sealed class Projectile
        {
            public int containerId;
            public SpaceObject container { get => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(containerId); set => containerId = value?.objectId ?? 0; }
            public ThingDef def;
            public CompProperties_SpaceProjectile props;
            public Building building;
            public SpaceMapLease mapLease;
            public Thing launcher;
            private int sourceContainerId;
            public SpaceObject sourceContainer { get => Current.Game?.GetComponent<UniverseManager>()?.ObjectById(sourceContainerId); set => sourceContainerId = value?.objectId ?? 0; }
            public bool reflectedByShield;
            public CombatTarget target;
            public int targetSampleTick = -1;
            public int targetContainerId;
            public SpacePose targetPose;
            public bool adoptedShuttle;
            public DriveSubject boardingDriver;
            public System.Func<SpacePose, SpacePose> boardingReference;
            public int spawnedAtTick;
            public bool dead;
            public Vector3 prevPosition;
            public bool laser;
            public float cruiseSpeed;
            public int coldLaunchEndsTick;
            public FlightPhase phase;
            public int motorStartedTick;
            public float motorDeltaVelocityPerTick;
            public bool motorThrusting;
            public float rcsTurnSign;
            public Vector3 laserFrom;
            public Vector2 continuousFromLocal, continuousToLocal;
            public Vector3 laserTo;
            /// <summary>发射伤害倍率乘剩余冲击比例；跨 tick 沿用。</summary>
            public float damageScale = 1f;
            public float armorPenetrationScale = 1f;
            public float mass = 1f, beamEnergyScale = 1f;
            public CannonShotTrace cannonTrace;
            /// <summary>复用的扫掠查询（每 tick 重填字段，热路径零分配）。</summary>
            public readonly SpaceCollision.SweptBody body = new SpaceCollision.SweptBody();
        }

        private sealed class Beam
        {
            public Vector3 from;
            public Vector3 to;
            public int bornTick;
            public float width;
            public int lifetimeTicks = BeamLifetimeTicks;
            public CombatVisualStyle style = CombatVisualStyle.Energy;
            public Thing continuousLauncher;
            public int sourceObjectId, sourceMapId;

            public bool VisibleAt(int tick) => continuousLauncher == null
                ? tick >= bornTick && tick - bornTick < lifetimeTicks
                : tick == bornTick && continuousLauncher.Spawned && !continuousLauncher.Destroyed
                    && continuousLauncher.Map.uniqueID == sourceMapId;
        }

        private sealed class TrailPuff
        {
            public Vector3 pos;
            public int bornTick;
            public int lifetimeTicks;
            public float size;
        }

        /// <summary>壳路径音效的近/远衰减界（格；镜像 ThrusterAudio 的 30/120）。</summary>
        private const float ImpactHearNear = 30f;
        private const float ImpactHearFar = 120f;

        private readonly List<Projectile> projectiles = new List<Projectile>();
        internal int ActiveCount => projectiles.Count;
        // 只复用短命的弹道工作对象；SpaceObject 身份和 Map 分别由注册表、现有图池管理。
        private const int MaxPooledProjectiles = 256;
        private readonly Stack<Projectile> projectilePool = new Stack<Projectile>();
        private readonly List<Beam> beams = new List<Beam>();
        private readonly List<TrailPuff> trailPuffs = new List<TrailPuff>();
        private readonly List<MuzzleFlash> muzzleFlashes = new List<MuzzleFlash>();
        private readonly List<ImpactFx> impacts = new List<ImpactFx>();
        private readonly List<Projectile> sweepScratch = new List<Projectile>();
        private readonly List<SpaceCollision.SweptBody> bodyScratch = new List<SpaceCollision.SweptBody>();
        private readonly List<SpaceCollision.ShieldVisualSegment> shieldVisualScratch =
            new List<SpaceCollision.ShieldVisualSegment>();

        public void ForgetMap(Map map)
        {
            if (map == null) return;
            foreach (Projectile p in projectiles)
                if (p.target.HasLock && (!p.target.TryResolve(universe, out var resolved)
                    || resolved.Container?.map == null || resolved.Container.map == map)) ForgetTarget(p);
            ForgetCombatVisuals(map);
            ForgetMotionFeedback(map);
        }

        // 盾线材质（静态渲染缓存，ClearRuntimeCaches 换档销毁）。
        private static Material kineticShieldLineMaterial;
        private static Material electromagneticShieldLineMaterial;

        // 壳上命中特效材质（静态渲染缓存；种类 × 渐隐档。贴图复用原版 mote 素材，
        // 克隆材质自持以设 renderQueue，ClearRuntimeCaches 只销毁克隆不动共享贴图）。
        private static Material[] impactFlashMaterials;   // 爆炸闪光（BrightFlash 橙）
        private static Material[] impactSmokeMaterials;   // 爆炸烟（Smoke 灰）
        private static Material[] impactEnergyMaterials;  // 激光熔蚀（BrightFlash 红）
        private static Material[] exhaustFlashMaterials;
        private static Material[] exhaustSmokeMaterials;
        public SpaceProjectileService(UniverseManager universe)
        {
            this.universe = universe;
        }

        /// <summary>在飞弹体数（诊断/图池状态用）。</summary>
        public int InFlightCount => projectiles.Count;

        // ---- 创建 ----

        /// <summary>
        /// 发射一枚弹体。muzzle = 炮口 universe 位置；directionWorld = 炮口处切空间
        /// 单位方向（3D）。初速 = 方向 × 离架速度 ⊕ 发射瞬间发射容器速度（弹道继承炮口
        /// 运动；相对速度结算因此对头/追尾天然成立）。导弹传 missileTarget。
        /// damageScale = 轨道炮等的伤害/穿深倍率（随弹体携带到命中结算）。
        /// </summary>
        public bool Launch(ThingDef def, SpaceObject source, Vector3 muzzle, Vector3 directionWorld,
            Faction faction, Thing launcher = null, CombatTarget missileTarget = default, float damageScale = 1f)
        {
            CompProperties_SpaceProjectile props = def?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null)
            {
                Log.Error($"[DeltaV] Launch: {def?.defName ?? "null"} has no CompProperties_SpaceProjectile.");
                return false;
            }
            return Launch(def, source, muzzle, directionWorld, faction, launcher, missileTarget,
                SpaceProjectileLaunchSpec.FromDef(props, damageScale));
        }

        internal void LaunchShieldFragment(ThingDef def, SpaceObject source, SpaceObject defender,
            Thing launcher, Vector3 hit, Vector2 direction, float signedSpeed, float mass, float armorPenetrationScale)
        {
            OrbitSphere.Basis(hit, out Vector3 east, out Vector3 north);
            Vector3 worldDirection = east * direction.x + north * direction.y;
            Vector2 travel = signedSpeed >= 0f ? direction : -direction;
            Vector3 muzzle = OrbitSphere.Move(hit, travel * 0.06f);
            float speed = Mathf.Abs(signedSpeed);
            if (!Launch(def, null, muzzle, worldDirection, source?.faction, launcher, default,
                new SpaceProjectileLaunchSpec(speed, 0, speed, 1f, armorPenetrationScale, mass))) return;
            Projectile fragment = projectiles[projectiles.Count - 1];
            fragment.sourceContainer = source;
            fragment.reflectedByShield = signedSpeed < 0f;
            fragment.container.pose.velocity = travel * speed
                + SpaceTransform.TransportTangent(SpaceTransform.VelocityAtUniversePoint(defender.pose, hit), hit, muzzle);
            fragment.container.pose.angle = Mathf.Atan2(travel.y, travel.x);
            fragment.cruiseSpeed = speed;
        }

        public bool Launch(ThingDef def, SpaceObject source, Vector3 muzzle, Vector3 directionWorld,
            Faction faction, Thing launcher, CombatTarget missileTarget, SpaceProjectileLaunchSpec launchSpec,
            Vector3? diagnosticAim = null, float barrelErrorDeg = 0f, float spreadDeg = 0f)
        {
            var props = def?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null)
            {
                Log.Error($"[DeltaV] Launch: {def?.defName ?? "null"} has no CompProperties_SpaceProjectile.");
                return false;
            }
            OrbitSphere.Basis(muzzle, out Vector3 east, out Vector3 north);
            var dirTangent = new Vector2(Vector3.Dot(directionWorld, east), Vector3.Dot(directionWorld, north));
            if (dirTangent.sqrMagnitude < 1e-8f) dirTangent = Vector2.right;
            dirTangent.Normalize();
            Vector2 launcherVelocity = source != null
                ? SpaceTransform.VelocityAtUniversePoint(source.pose, muzzle)
                : Vector2.zero;

            Projectile projectile = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            projectile.def = def;
            projectile.props = props;
            projectile.launcher = launcher;
            projectile.sourceContainer = source;
            projectile.reflectedByShield = false;
            projectile.target = missileTarget;
            projectile.cannonTrace = null;
            projectile.targetSampleTick = -1;
            projectile.spawnedAtTick = Find.TickManager.TicksGame;
            projectile.laser = props.laserRange > 0f;
            projectile.cruiseSpeed = launchSpec.speed;
            projectile.coldLaunchEndsTick = Find.TickManager.TicksGame + Mathf.Max(0, launchSpec.coldLaunchTicks);
            projectile.phase = launchSpec.coldLaunchTicks > 0 || props.initialTurnTicks > 0
                ? FlightPhase.ColdLaunch : FlightPhase.PoweredFlight;
            projectile.motorStartedTick = projectile.spawnedAtTick - Mathf.Max(0, props.accelerationTicks) - 1;
            float launchSpeed = launchSpec.coldLaunchTicks > 0
                ? launchSpec.coldLaunchSpeed : launchSpec.speed;
            projectile.motorDeltaVelocityPerTick = Mathf.Max(0f,
                launchSpec.speed - launchSpeed) / Mathf.Max(1, props.accelerationTicks);
            if (props.guided && projectile.motorDeltaVelocityPerTick <= 0f)
                projectile.motorDeltaVelocityPerTick = launchSpec.speed / Mathf.Max(1, props.accelerationTicks);

            SpaceObject created;
            if (projectile.laser)
            {
                // 激光：即时链——无图、不积分；容器只为「同一注册表结算链」存在，
                // 速度取发射瞬间双方速度的攻方位（相对速度语义，§4.3）。
                projectile.laserFrom = muzzle;
                projectile.laserTo = OrbitSphere.Move(muzzle, dirTangent * props.laserRange);
                if (props.continuousBeam && source?.map != null)
                {
                    projectile.continuousFromLocal = SpaceTransform.UniverseToMapPoint(source, source.map, muzzle);
                    projectile.continuousToLocal = SpaceTransform.UniverseToMapPoint(source, source.map, projectile.laserTo);
                }
                created = new SpaceObject(universe.AllocateOwnerId(),
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                created.pose.velocity = launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            else if (props.hasMap)
            {
                bool populated = universe.MapPool.TryRent(SpaceObjectTag.Projectile, def.Size, map =>
                {
                    projectile.building = ThingMaker.MakeThing(def) as Building;
                    if (projectile.building == null)
                        throw new System.InvalidOperationException($"Mapped projectile {def.defName} is not a Building.");
                    // Position is the center anchor, including even footprints; never use the lower-left cell.
                    CellRect footprint = GenAdj.OccupiedRect(IntVec3.Zero, Rot4.North, def.Size);
                    GenSpawn.Spawn(projectile.building,
                        new IntVec3(-footprint.minX, 0, -footprint.minZ), map, Rot4.North);
                }, out SpaceMapLease lease);
                if (!populated)
                {
                    if (lease != null) ReturnProjectileMap(lease, projectile.building);
                    RecycleProjectile(projectile);
                    return false;
                }
                projectile.mapLease = lease;
                Map map = lease.Map;
                created = new MapSpaceObject(universe.AllocateOwnerId(), map,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                created.pose.velocity = dirTangent * launchSpeed + launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            else
            {
                created = new SpaceObject(universe.AllocateOwnerId(),
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                created.pose.velocity = dirTangent * launchSpeed + launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            universe.RegisterObject(created);
            projectile.container = created;
            projectile.damageScale = launchSpec.damageScale;
            projectile.armorPenetrationScale = launchSpec.armorPenetrationScale;
            projectile.mass = launchSpec.mass; projectile.beamEnergyScale = launchSpec.beamEnergyScale;
            projectiles.Add(projectile);
            if (diagnosticAim.HasValue && missileTarget.Kind == CombatTargetKind.Ship
                && CombatTarget.ForShip(missileTarget.Ship).TryResolve(universe, out var aimed)
                && aimed.Ship?.IsValid == true)
            {
                var trace = new CannonShotTrace
                {
                    target = CombatTarget.ForShip(missileTarget.Ship),
                    targetAtLaunch = aimed.Pose.position,
                    targetVelocityAtLaunch = aimed.Pose.velocity,
                    launchDirection = dirTangent,
                    targetRadius = ShipFireControl.HullRadius(aimed.Ship)
                };
                trace.onContact = contact =>
                {
                    if (contact.defenderSubject == trace.target.Ship
                        && !contact.shieldMode.HasValue) trace.hullContact = true;
                    if (contact.shieldMode.HasValue && trace.target.TryResolve(universe, out var current)
                        && contact.defender == current.Container) trace.shieldContact = true;
                    QueueVisualContact(contact);
                };
                projectile.cannonTrace = trace;
                float launchRange = OrbitSphere.ArcCells(muzzle, aimed.Pose.position);
                float aimLead = OrbitSphere.ArcCells(aimed.Pose.position, diagnosticAim.Value);
                DeltaVLog.Message(DeltaVLogModule.Development,
                    $"cannon-shot launch id={projectile.containerId} weapon={launcher?.def?.defName} "
                    + $"source={UniverseManager.MapTag(source?.map)} target={missileTarget.Ship} "
                    + $"targetMap={UniverseManager.MapTag(aimed.Ship.Map)} range={launchRange:F1} "
                    + $"radius={trace.targetRadius:F1} speed={launchSpec.speed:F1} "
                    + $"targetVel={trace.targetVelocityAtLaunch} lead={aimLead:F1} "
                    + $"barrel={barrelErrorDeg:F2}deg spread={spreadDeg:F2}deg");
            }
            if (!props.continuousBeam && DeltaVLog.Enabled(DeltaVLogModule.Combat)) DeltaVLog.Message(DeltaVLogModule.Combat,
                $"{def.defName} launched from '{source?.label ?? "nowhere"}' " +
                $"(speed {launchSpec.speed:F0}, {(props.hasMap ? "mapped" : "mapless")}" +
                $"{(projectile.laser ? ", laser" : "")})");
            return true;
        }

        /// <summary>为在飞 shuttle 登记共同碰撞工作项；运动仍经真实推进器与截击 MPC。</summary>
        internal bool AdoptShuttle(SpaceObject shuttle, Building hull, CombatTarget target)
        {
            CompProperties_SpaceProjectile props = hull?.def.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null || !props.hasMap || shuttle?.map != hull.Map
                || hull.TryGetComp<CompSpaceShuttle>()?.Vessel.CanDriveIn(shuttle) != true
                || !target.TryResolve(universe, out var resolved) || resolved.Container == null || projectiles.Any(p => p.container == shuttle)) return false;
            Projectile p = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            p.container = shuttle;
            p.building = hull;
            p.def = hull.def;
            p.props = props;
            p.launcher = hull;
            p.sourceContainer = null;
            p.reflectedByShield = false;
            p.target = target;
            p.targetSampleTick = -1;
            p.adoptedShuttle = true;
            p.spawnedAtTick = Find.TickManager.TicksGame;
            p.dead = false;
            p.laser = false;
            ShipBase vessel = hull.TryGetComp<CompSpaceShuttle>().Vessel;
            p.prevPosition = shuttle.pose.position;
            p.damageScale = p.armorPenetrationScale = p.mass = p.beamEnergyScale = 1f;
            shuttle.projectileRoleOverride = true;
            projectiles.Add(p);
            p.boardingDriver = DriveSubject.Of(vessel);
            hull.TryGetComp<CompSpaceShuttle>().intrusionDriveActive = true;
            p.boardingReference = pose =>
            {
                SpacePose reference = p.targetPose;
                Vector2 offset = OrbitSphere.Offset(pose.position, reference.position);
                Vector2 relative = SpaceTransform.TransportVelocity(reference, pose.position) - pose.velocity;
                reference.angle = SpaceTransform.WrapAngle(Mathf.Atan2(offset.y, offset.x) - vessel.FrameAngle);
                reference.angularVelocity = offset.sqrMagnitude > 0.01f
                    ? (offset.x * relative.y - offset.y * relative.x) / offset.sqrMagnitude : 0f;
                return reference;
            };
            ResolveTarget(p);
            universe.Autopilot.Intercept(p.boardingDriver, p.boardingReference,
                () => !p.dead && ResolveTarget(p), () => ReleaseShuttle(shuttle));
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"boarding intercept adopted shuttle #{shuttle.objectId} target #{resolved.Container.objectId}");
            return true;
        }

        internal bool ReleaseShuttle(SpaceObject shuttle)
        {
            if (shuttle == null) return false;
            Projectile p = projectiles.FirstOrDefault(item => item.adoptedShuttle && item.containerId == shuttle.objectId);
            if (p == null) return false;
            QueueProjectileCleanup(p, destroyContainer: false);
            return true;
        }

        internal void NotifyContainerRemoving(SpaceObject container)
        {
            if (container == null) return;
            engineGroups.Remove(container.objectId);
            if (pendingHullSound.objectId == container.objectId) pendingHullSound = default;
            muzzleFlashes.RemoveAll(f => f.anchor.objectId == container.objectId);
            impacts.RemoveAll(f => f.anchor.objectId == container.objectId);
            beams.RemoveAll(b => b.continuousLauncher != null && b.sourceObjectId == container.objectId);
            foreach (Projectile flight in projectiles)
                if (flight.target.Kind == CombatTargetKind.Container && flight.target.ObjectId == container.objectId
                    || flight.target.Kind == CombatTargetKind.Ship
                        && flight.target.TryResolve(universe, out var resolved) && resolved.Container == container)
                    ForgetTarget(flight);
            Projectile p = projectiles.FirstOrDefault(item => item.containerId == container.objectId);
            if (p != null) QueueProjectileCleanup(p, destroyContainer: !p.adoptedShuttle);
        }

        // ---- tick（制导/超时；容器积分之前）----

        /// <summary>
        /// 弹体制导与超时（GameComponentTick 内、容器积分之前调用：本 tick 施加
        /// 有限加速度、积分随即位移，SpaceCollision 在积分后扫掠——先动后撞，§3.3）。
        /// prevPosition 在此记录（扫掠段起点 = 积分前位姿）。
        /// </summary>
        public void Tick()
        {
            if (projectiles.Count > 0)
            {
                int now = Find.TickManager.TicksGame;
                for (int i = projectiles.Count - 1; i >= 0; i--)
                {
                    Projectile p = projectiles[i];
                    if (p.dead) continue;
                    if (p.container == null) { QueueProjectileCleanup(p, !p.adoptedShuttle); continue; }
                    // 有图弹体被图内结算摧毁（PD/毁伤链经 TakeDamage）→ 销毁收尾。
                    if (p.building != null && p.building.Destroyed)
                    {
                        if (p.adoptedShuttle) ReleaseShuttle(p.container);
                        else DestroyProjectile(p, "building destroyed in-map");
                        continue;
                    }
                    // 超时自毁（无后效；激光同 tick 结算不超时）。
                    if (!p.laser && now - p.spawnedAtTick > p.props.timeoutTicks)
                    {
                        if (p.adoptedShuttle) ReleaseShuttle(p.container);
                        else DestroyProjectile(p, "timeout");
                        continue;
                    }
                    bool hasTarget = ResolveTarget(p);
                    if (p.adoptedShuttle && (p.building.Map != p.container.map
                        || p.building.TryGetComp<CompSpaceShuttle>()?.Vessel.CanDriveIn(p.container) != true
                        || !hasTarget || !universe.Autopilot.IsFollowing(p.boardingDriver, p.boardingReference)))
                    {
                        ReleaseShuttle(p.container);
                        continue;
                    }
                    p.prevPosition = p.container.pose.position;
                    if (!p.laser && !p.adoptedShuttle) AdvanceFlight(p, hasTarget, now);
                    if (p.props.exhaustTrail && now % 3 == 0
                        && (p.phase != FlightPhase.PoweredFlight || p.motorThrusting))
                        EmitTrail(p, now);
                }
            }
            // 激光线段视觉寿命（纯表现）。
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                if (Find.TickManager.TicksGame - beams[i].bornTick >= beams[i].lifetimeTicks) beams.RemoveAt(i);
            }
            int tick = Find.TickManager.TicksGame;
            for (int i = trailPuffs.Count - 1; i >= 0; i--)
                if (tick - trailPuffs[i].bornTick >= trailPuffs[i].lifetimeTicks)
                    trailPuffs.RemoveAt(i);
            TickCombatVisuals(tick);
        }

        /// <summary>制导只施加有限速度增量；位移仍由容器物理积分，不调用飞船 MPC。</summary>
        private static void AdvanceFlight(Projectile p, bool hasTarget, int now)
        {
            p.rcsTurnSign = 0f;
            p.motorThrusting = false;
            if (p.phase == FlightPhase.ColdLaunch)
            {
                if (now < p.coldLaunchEndsTick) return;
                p.phase = p.props.guided && hasTarget && p.props.initialTurnTicks > 0
                    ? FlightPhase.InitialTurn : FlightPhase.PoweredFlight;
                p.motorStartedTick = now;
            }
            if (p.phase == FlightPhase.InitialTurn)
            {
                bool aligned = !hasTarget || TurnTowards(p,
                    OrbitSphere.Offset(p.container.pose.position, p.targetPose.position),
                    Mathf.Max(p.props.turnRateDegPerSec, p.props.initialTurnRateDegPerSec));
                if (!aligned && now - p.coldLaunchEndsTick + 1 < p.props.initialTurnTicks) return;
                p.phase = FlightPhase.PoweredFlight;
                p.motorStartedTick = now;
            }
            if (p.props.guided && hasTarget)
            {
                Vector2 toTarget = OrbitSphere.Offset(p.container.pose.position, p.targetPose.position);
                if (toTarget.sqrMagnitude < 1e-8f) return;
                Vector2 lineOfSight = toTarget.normalized;
                Vector2 targetVelocity = SpaceTransform.TransportVelocity(p.targetPose,
                    p.container.pose.position);
                Vector2 relativeVelocity = p.container.pose.velocity - targetVelocity;
                Vector2 lateralVelocity = relativeVelocity
                    - lineOfSight * Vector2.Dot(relativeVelocity, lineOfSight);
                // 始终以视线为朝向基准，只用横向漂移修正偏角；微小速度残差不能让弹头反向翻转。
                TurnTowards(p, lineOfSight * p.cruiseSpeed - lateralVelocity,
                    p.props.turnRateDegPerSec);
                Vector2 desiredVelocity = targetVelocity + lineOfSight * p.cruiseSpeed;
                Vector2 correction = desiredVelocity - p.container.pose.velocity;
                if (correction.sqrMagnitude <= GuidanceVelocityTolerance * GuidanceVelocityTolerance)
                    return;
                Vector2 forward = SpaceTransform.Rotate(Vector2.right, p.container.pose.angle);
                float projectedCorrection = Vector2.Dot(correction, forward);
                if (projectedCorrection > 0f)
                {
                    float deltaVelocity = Mathf.Min(p.motorDeltaVelocityPerTick, projectedCorrection);
                    p.container.pose.velocity += forward * deltaVelocity;
                    p.motorThrusting = deltaVelocity > 0f;
                }
            }
            else if (now - p.motorStartedTick < p.props.accelerationTicks)
            {
                // 失锁后仅完成已开始的点火段；随后保持当前宇宙速度漂移。
                p.container.pose.velocity += SpaceTransform.Rotate(Vector2.right, p.container.pose.angle)
                    * p.motorDeltaVelocityPerTick;
                p.motorThrusting = p.motorDeltaVelocityPerTick > 0f;
            }
        }

        private static float MotorProgress(Projectile p, int now) => p.props.accelerationTicks > 0
            ? Mathf.Clamp01((float)(now - p.motorStartedTick + 1) / p.props.accelerationTicks) : 1f;

        /// <summary>只转弹体朝向；转向本身不改变平移速度。</summary>
        private static bool TurnTowards(Projectile p, Vector2 desired, float turnRate)
        {
            if (desired.sqrMagnitude < 1e-8f) return true;
            desired.Normalize();
            Vector2 current = SpaceTransform.Rotate(Vector2.right, p.container.pose.angle);
            float error = Mathf.Atan2(current.x * desired.y - current.y * desired.x,
                Vector2.Dot(current, desired));
            float maxTurn = Mathf.Max(0f, turnRate) * Mathf.Deg2Rad * Dt;
            float turn = Mathf.Clamp(error, -maxTurn, maxTurn);
            Vector2 newDir = SpaceTransform.Rotate(current, turn);
            if (p.phase == FlightPhase.InitialTurn && Mathf.Abs(turn) > 0.001f) p.rcsTurnSign = Mathf.Sign(turn);
            p.container.pose.angle = Mathf.Atan2(newDir.y, newDir.x);
            return Mathf.Abs(error) <= maxTurn + 0.0001f;
        }
        private static SpacePose ExhaustPose(Projectile p)
            => SpaceTransform.RigidPointPose(p.container.pose, p.props.exhaustOffset, 0f);

        private void EmitTrail(Projectile p, int now)
        {
            if (p.adoptedShuttle) return; // shuttle 使用推进器 Comp 的宇宙烟焰，不复用导弹贴片。
            bool powered = p.phase == FlightPhase.PoweredFlight;
            SpacePose nozzle = ExhaustPose(p);
            trailPuffs.Add(new TrailPuff
            {
                pos = nozzle.position,
                bornTick = now,
                lifetimeTicks = powered ? Mathf.Max(1, p.props.smokeLifetimeTicks) : 24,
                size = Mathf.Max(0.1f, p.props.exhaustSize * (powered ? 1f : 0.22f)),
            });
            if (trailPuffs.Count > MaxTrailPuffs) trailPuffs.RemoveAt(0);
        }

        // 同 tick 的制导与限定命中对象消费同一次身份解析；不在碰撞阶段重新选船。
        private bool ResolveTarget(Projectile p)
        {
            int tick = Find.TickManager.TicksGame;
            if (p.targetSampleTick != tick)
            {
                p.targetSampleTick = tick;
                p.targetContainerId = 0;
                if (p.target.TryResolve(universe, out var resolved))
                { p.targetContainerId = resolved.Container?.objectId ?? 0; p.targetPose = resolved.Pose; }
                else ForgetTarget(p); // 身份消失后永久停止制导，不等待另一个同号对象。
            }
            return p.targetContainerId != 0 && universe.ObjectById(p.targetContainerId) != null;
        }

        private static void ForgetTarget(Projectile p)
        {
            p.target = default;
            p.targetContainerId = 0;
            p.targetPose = default;
            p.targetSampleTick = Find.TickManager.TicksGame;
            p.body.requiredDefender = null;
            // 若失效发生在同一批碰撞后效中，不能把已提交的侵入扫掠变成不限定守方。
            if (p.adoptedShuttle) p.body.dead = true;
        }

        // ---- 碰撞调度（容器积分之后）----

        /// <summary>
        /// 组装在飞弹体的扫掠查询并结算（GameComponentTick 容器积分之后调用）。
        /// 激光在此完成全射程即时扫掠并随即销毁。
        /// </summary>
        public void ResolveCollisions()
        {
            sweepScratch.Clear();
            bodyScratch.Clear();
            visualContacts.Clear();
            bool traceCannons = false;
            for (int i = 0; i < projectiles.Count; i++)
            {
                Projectile p = projectiles[i];
                if (p.dead) continue;
                if (p.props.continuousBeam)
                {
                    if (p.launcher?.Spawned != true || p.sourceContainer?.map != p.launcher.Map)
                    { DestroyProjectile(p, "emitter removed"); continue; }
                    // 连续束使用积分后的炮口与安装方向，避免快船拖出一 tick 的旧起点。
                    p.laserFrom = SpaceTransform.MapPointToUniverse(p.sourceContainer, p.launcher.Map, p.continuousFromLocal);
                    p.laserTo = SpaceTransform.MapPointToUniverse(p.sourceContainer, p.launcher.Map, p.continuousToLocal);
                }
                bool hasTarget = ResolveTarget(p);
                SpaceObject targetContainer = hasTarget ? universe.ObjectById(p.targetContainerId) : null;
                // 解除接管会立即移出工作表；保留其余弹体的处理顺序，容器已消失也能收尾。
                if (p.adoptedShuttle && !hasTarget)
                { QueueProjectileCleanup(p, destroyContainer: false); i--; continue; }
                SpaceCollision.SweptBody body = p.body;
                body.attacker = p.container;
                body.projectileDef = p.def;
                body.attackerBuilding = p.building;
                body.excludeContainer = p.reflectedByShield ? null : p.sourceContainer;
                body.requiredDefender = p.adoptedShuttle ? targetContainer : null;
                body.ignoreNonHostileProjectiles = !p.reflectedByShield;
                body.launcher = p.launcher;
                body.dead = false;
                body.resolvedHit = false;
                body.onVisualContact = p.cannonTrace is { reported: false } trace
                    ? trace.onContact : QueueVisualContact;
                body.didRedirect = false;
                body.continued = body.didContinue = false;
                body.sweepRadius = FootprintRadius(p.def);
                body.noseHalfWidth = p.adoptedShuttle ? p.def.Size.x * 0.5f : 0f;
                body.noseForwardOffset = p.adoptedShuttle ? (p.def.Size.z - 1) * 0.5f : 0f;
                body.damageScale = p.damageScale;
                body.armorPenetrationScale = p.armorPenetrationScale;
                body.mass = p.mass; body.beamEnergyScale = p.beamEnergyScale;
                if (p.laser)
                {
                    // 专用点防束在结算 tick 追踪导弹现位，避免一 tick 位移把必中特性变成擦边。
                    if (p.props.guaranteedHitMissiles && targetContainer?.map != null && !targetContainer.map.Disposed)
                    {
                        foreach (Thing thing in targetContainer.map.listerThings.AllThings)
                        {
                            if (!SpaceImpactEffects.GuaranteedMissileHit(p.def, thing.def)) continue;
                            p.laserTo = targetContainer.pose.position;
                            break;
                        }
                    }
                    body.from = p.laserFrom;
                    body.to = p.laserTo;
                }
                else
                {
                    body.from = p.prevPosition;
                    body.to = p.container.pose.position;
                }
                Projectile captured = p;
                body.onSelfDestruct = delegate
                {
                    if (captured.adoptedShuttle) ReleaseShuttle(captured.container);
                    else DestroyProjectile(captured, "impact");
                };
                body.onPlace = p.adoptedShuttle
                    ? (System.Action<SpaceObject, Vector3, Vector2>)((defender, hit, local) =>
                    {
                        SpaceObject shuttle = captured.container;
                        Vector2 forwardAtHit = SpaceTransform.TransportTangent(
                            SpaceTransform.Rotate(Vector2.right, shuttle.pose.angle
                                + ShipOrientation.AngleFromRot4(captured.building.Rotation)),
                            shuttle.pose.position, hit);
                        ReleaseShuttle(shuttle);
                        universe.Shuttles.PlaceBoardedShuttle(shuttle, captured.building, defender, hit, local,
                            forwardAtHit, captured.body.resolvedHitCell);
                    }) : null;
                body.onShieldRedirect = delegate (Vector3 from, Vector3 hit)
                {
                    if (captured.laser)
                        AddBounded(beams, new Beam { from = from, to = hit,
                            bornTick = Find.TickManager.TicksGame,
                            width = captured.props.laserBeamWidth,
                            style = captured.props.visualStyle,
                            lifetimeTicks = captured.props.guaranteedHitMissiles ? 7 : BeamLifetimeTicks }, 256);
                };
                body.onShieldShatter = delegate (Vector3 hit, SpaceObject defender,
                    Vector2 direction, float signedSpeed)
                {
                    SpaceShieldProjectileBehavior.SpawnFragments(this, captured.def,
                        captured.sourceContainer, defender, captured.launcher, hit,
                        direction, signedSpeed, captured.mass * captured.body.damageScale, captured.armorPenetrationScale);
                };
                sweepScratch.Add(p);
                bodyScratch.Add(body);
                if (p.cannonTrace is { reported: false }) traceCannons = true;
            }
            if (traceCannons)
                foreach (Projectile p in sweepScratch) SampleCannonTrace(p);
            SpaceCollision.SweepAndResolve(universe, bodyScratch);
            if (traceCannons)
                foreach (Projectile p in sweepScratch) ResolveCannonTrace(p);
            foreach (SpaceCollision.VisualContact contact in visualContacts) RegisterImpactFx(contact);
            visualContacts.Clear();
            foreach (Projectile p in sweepScratch)
            {
                if (p.dead || p.laser || !p.body.didRedirect) continue;
                p.container.pose.position = p.body.to;
                Vector2 direction = p.body.redirectDirection;
                float speed = p.container.pose.velocity.magnitude;
                p.container.pose.velocity = direction * speed;
                p.container.pose.angle = Mathf.Atan2(direction.y, direction.x);
            }
            foreach (Projectile p in sweepScratch)
            {
                if (p.dead) continue;
                // 碰撞链已经扣过一次冲击预算；速度与弹药穿甲系数不再重复衰减。
                p.damageScale = p.body.damageScale;
                if (p.body.didContinue) p.container.pose.position = p.body.to;
            }
            // 激光当 tick 消费完毕：视觉线段（命中点或全射程）+ 销毁。
            foreach (Projectile p in sweepScratch)
            {
                if (!p.laser) continue;
                Vector3 end = p.body.resolvedHit ? p.body.resolvedHitUniverse : p.body.to;
                RecordBeam(p, end);
                if (!p.dead) DestroyProjectile(p, "laser spent");
            }
        }

        private void SampleCannonTrace(Projectile p)
        {
            CannonShotTrace trace = p.cannonTrace;
            if (trace == null || trace.reported || p.container == null
                || !trace.target.TryResolve(universe, out var current)) return;
            Vector2 end = OrbitSphere.Offset(current.Pose.position, p.body.to);
            Vector2 start = OrbitSphere.Offset(current.Pose.position, p.body.from)
                + current.Pose.velocity * Dt;
            Vector2 sweep = end - start;
            float fraction = sweep.sqrMagnitude < 1e-8f ? 0f
                : Mathf.Clamp01(-Vector2.Dot(start, sweep) / sweep.sqrMagnitude);
            trace.closestCenter = Mathf.Min(trace.closestCenter,
                (start + fraction * sweep).magnitude);
            float elapsed = (Find.TickManager.TicksGame - p.spawnedAtTick) * Dt;
            Vector3 expected = OrbitSphere.Move(trace.targetAtLaunch,
                trace.targetVelocityAtLaunch * elapsed);
            Vector2 drift = OrbitSphere.Offset(expected, current.Pose.position);
            trace.targetDrift = drift.magnitude;
            trace.lateralDrift = Mathf.Abs(Vector2.Dot(drift,
                new Vector2(-trace.launchDirection.y, trace.launchDirection.x)));
        }

        private void ResolveCannonTrace(Projectile p)
        {
            CannonShotTrace trace = p.cannonTrace;
            if (trace == null || trace.reported) return;
            if (trace.hullContact) { ReportCannonTrace(p, "hull-hit"); return; }
            if (p.dead)
            {
                ReportCannonTrace(p, trace.shieldContact ? "shield-intercept"
                    : p.body.resolvedHit ? "other-impact" : "removed");
                return;
            }
            if (!trace.target.TryResolve(universe, out var current) || p.container == null)
                return;
            Vector2 relativeVelocity = SpaceTransform.TransportVelocity(
                p.container.pose, current.Pose.position) - current.Pose.velocity;
            if (relativeVelocity.sqrMagnitude < 1e-8f) return;
            Vector2 relativePosition = OrbitSphere.Offset(current.Pose.position,
                p.container.pose.position);
            if (Vector2.Dot(relativePosition, relativeVelocity.normalized)
                > trace.targetRadius + 2f)
                ReportCannonTrace(p, "miss");
        }

        private void ReportCannonTrace(Projectile p, string outcome)
        {
            CannonShotTrace trace = p.cannonTrace;
            if (trace == null || trace.reported) return;
            trace.reported = true;
            if (!DeltaVLog.Enabled(DeltaVLogModule.Development)) return;
            string closest = float.IsInfinity(trace.closestCenter) ? "n/a"
                : trace.closestCenter.ToString("F1");
            string clearance = float.IsInfinity(trace.closestCenter) ? "n/a"
                : (trace.closestCenter - trace.targetRadius).ToString("F1");
            DeltaVLog.Message(DeltaVLogModule.Development,
                $"cannon-shot result id={p.containerId} target={trace.target.Ship} "
                + $"outcome={outcome} flight={(Find.TickManager.TicksGame - p.spawnedAtTick) * Dt:F2}s "
                + $"closestCenter={closest} circleClearance={clearance} "
                + $"targetDrift={trace.targetDrift:F1} lateralDrift={trace.lateralDrift:F1}");
        }

        internal void RecordTractorBeam(Thing launcher, SpaceObject receiver, SpaceObject target,
            Vector3? targetPoint = null)
        {
            Beam beam = beams.Find(b => b.continuousLauncher == launcher && b.sourceObjectId == receiver.objectId);
            if (beam == null) { beam = new Beam(); AddBounded(beams, beam, 256); }
            var weapon = launcher.TryGetComp<CompSpaceWeapon>();
            beam.from = SpaceTransform.MapPointToUniverse(receiver, launcher.Map, SpaceWeaponGeometry.MuzzleMapPoint(weapon));
            beam.to = targetPoint ?? target.pose.position;
            beam.bornTick = Find.TickManager.TicksGame;
            beam.width = weapon.WeaponProps.projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()?.laserBeamWidth ?? .18f;
            beam.style = CombatVisualStyle.Tractor;
            beam.continuousLauncher = launcher;
            beam.sourceObjectId = receiver.objectId;
            beam.sourceMapId = receiver.map.uniqueID;
            beam.lifetimeTicks = 2;
        }

        private void RecordBeam(Projectile p, Vector3 end)
        {
            Beam beam = p.props.continuousBeam
                ? beams.Find(b => b.continuousLauncher == p.launcher && b.sourceObjectId == p.sourceContainer.objectId)
                : null;
            if (beam == null)
            {
                beam = new Beam();
                AddBounded(beams, beam, 256);
            }
            beam.from = p.body.from;
            beam.to = end;
            beam.bornTick = Find.TickManager.TicksGame;
            beam.width = p.props.laserBeamWidth;
            beam.style = p.props.visualStyle;
            beam.continuousLauncher = p.props.continuousBeam ? p.launcher : null;
            beam.sourceObjectId = p.sourceContainer?.objectId ?? 0;
            beam.sourceMapId = p.sourceContainer?.map?.uniqueID ?? -1;
            // 留到下一 tick 供同一设备复用；VisibleAt 只允许本 tick 的连续束显示。
            beam.lifetimeTicks = p.props.continuousBeam ? 2 : p.props.guaranteedHitMissiles ? 7 : BeamLifetimeTicks;
        }

        /// <summary>弹体脚印等效半径（1×1 ≈ 0.71、2×1 ≈ 1.12）。</summary>
        internal static float FootprintRadius(ThingDef def)
        {
            return Mathf.Sqrt(def.Size.x * def.Size.x + def.Size.z * def.Size.z) * 0.5f;
        }

        // ---- 销毁与图池 ----

        /// <summary>
        /// 销毁一枚弹体（帧边界）：注销容器、有图清内容归池。dead 立即置位（本 tick
        /// 后续事件跳过），实际收尾经 QueueFrameAction。
        /// </summary>
        private void DestroyProjectile(Projectile p, string reason)
        {
            if (p.dead) return;
            if (reason != "impact") ReportCannonTrace(p, reason);
            if (!p.props.continuousBeam && DeltaVLog.Enabled(DeltaVLogModule.Combat))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"{p.def?.defName} #{p.containerId} destroyed ({reason})");
            QueueProjectileCleanup(p, destroyContainer: true);
        }

        // Ordinary destruction, reset and external removal share one retirement path.
        // Capture the old map before unregistering; a missing ID does not mean its
        // resources were disposed. Never destroy the replacement representation.
        private void QueueProjectileCleanup(Projectile p, bool destroyContainer)
        {
            if (p.dead) return;
            p.dead = true;
            int expectedId = p.containerId;
            SpaceObject original = p.container;
            int expectedVersion = original?.RepresentationVersion ?? 0;
            Map oldMap = original?.map ?? p.building?.Map;
            SpaceMapLease oldLease = p.mapLease;
            if (!destroyContainer)
            {
                if (p.adoptedShuttle)
                {
                    universe.Autopilot.DetachReference(p.boardingDriver, p.boardingReference);
                    CompSpaceShuttle shuttle = p.building?.TryGetComp<CompSpaceShuttle>();
                    if (shuttle != null) shuttle.intrusionDriveActive = false;
                }
                projectiles.Remove(p);
                if (original != null) original.projectileRoleOverride = false;
            }
            universe.QueueFrameAction(delegate
            {
                // A stale callback must not recycle a pooled record's next occupant.
                if (!p.dead || p.containerId != expectedId) return;
                projectiles.Remove(p);
                SpaceObject container = p.container;
                if (destroyContainer && (container == null || container.RepresentationVersion == expectedVersion))
                {
                    if (container != null) universe.UnregisterObject(container);
                    if (oldMap == null || !oldMap.Disposed)
                    {
                        if (oldLease != null)
                            ReturnProjectileMap(oldLease, p.building);
                        else if (p.building != null && !p.building.Destroyed)
                            p.building.Destroy(DestroyMode.Vanish);
                    }
                }
                RecycleProjectile(p);
            });
        }

        private void RecycleProjectile(Projectile p)
        {
            ReportCannonTrace(p, "removed");
            p.container = null;
            p.def = null;
            p.props = null;
            p.building = null;
            p.mapLease = null;
            p.launcher = null;
            p.sourceContainer = null;
            p.reflectedByShield = false;
            p.target = default;
            p.targetSampleTick = -1;
            p.targetContainerId = 0;
            p.targetPose = default;
            p.adoptedShuttle = false;
            p.boardingDriver = default;
            p.boardingReference = null;
            p.dead = false;
            p.laser = false;
            p.continuousFromLocal = p.continuousToLocal = default;
            p.phase = FlightPhase.ColdLaunch;
            p.coldLaunchEndsTick = p.motorStartedTick = 0;
            p.motorDeltaVelocityPerTick = p.cruiseSpeed = p.rcsTurnSign = 0f;
            p.motorThrusting = false;
            p.damageScale = 1f;
            p.armorPenetrationScale = p.mass = p.beamEnergyScale = 1f;
            p.cannonTrace = null;
            p.body.attacker = null;
            p.body.projectileDef = null;
            p.body.attackerBuilding = null;
            p.body.excludeContainer = null;
            p.body.requiredDefender = null;
            p.body.noseHalfWidth = 0f;
            p.body.noseForwardOffset = 0f;
            p.body.launcher = null;
            p.body.onSelfDestruct = null;
            p.body.onVisualContact = null;
            p.body.onPlace = null;
            p.body.resolvedHitDefender = null;
            p.body.resolvedHitCell = IntVec3.Invalid;
            p.body.dead = false;
            p.body.resolvedHit = false;
            p.body.continued = p.body.didContinue = false;
            if (projectilePool.Count < MaxPooledProjectiles) projectilePool.Push(p);
        }

        /// <summary>殖民者栏等 UI 判断弹体内部图的唯一入口（包含在飞与闲置池）。</summary>
        internal bool IsProjectileMap(Map map)
        {
            if (map == null) return false;
            DeltaVPocketMapMetadata metadata = map.GetComponent<DeltaVPocketMapMetadata>();
            if (metadata?.pooledMap == true && metadata.poolTag == SpaceObjectTag.Projectile) return true;
            SpaceObject obj = universe.SpaceObjectOnMap(map);
            if (obj != null && obj.baseTag == SpaceObjectTag.Projectile) return true;
            return false;
        }

        private void ReturnProjectileMap(SpaceMapLease lease, Building body)
        {
            if (!universe.MapPool.TryReturn(lease, _ =>
                {
                    if (body != null && !body.Destroyed) body.Destroy(DestroyMode.Vanish);
                }) && universe.MapPool.IsCurrent(lease))
                Log.Warning($"[DeltaV] Projectile map {UniverseManager.MapTag(lease.Map)} retained: return rejected; remaining content was not discarded.");
        }

        /// <summary>dev：图池状态摘要。</summary>
        public string PoolStatus()
        {
            return $"in-flight={projectiles.Count} beams={beams.Count} pooledIdleMaps={universe.MapPool.IdleCount}";
        }

        /// <summary>
        /// 读档收尾：写档剔除的是弹体容器，弹体小图随档落盘残留为无容器图——按内容
        /// 回收（清掉弹体 building 后回池）。只动「无承载容器、无人、内容只有弹体
        /// building」的 ShipPocketParent 口袋图，不碰任何真实结构图。
        /// </summary>
        public void SweepOrphanProjectileMaps()
        {
            foreach (Map map in Find.Maps.ToList())
            {
                if (!(map.Parent is ShipPocketParent_DeltaV)) continue;
                if (map == Find.CurrentMap) continue;
                if (universe.SpaceObjectOnMap(map) != null) continue;
                if (map.mapPawns.AllPawns.Count > 0) continue;
                if (!IsProjectileMap(map)) continue;
                bool onlyProjectiles = true;
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Building building
                        && building.def.GetCompProperties<CompProperties_SpaceProjectile>() != null)
                    {
                        continue;
                    }
                    onlyProjectiles = false;
                    break;
                }
                if (!onlyProjectiles) continue;
                SpaceMapLease lease = universe.MapPool.Restore(map);
                if (universe.MapPool.TryReturn(lease, released =>
                    {
                        foreach (Thing thing in released.listerThings.AllThings.ToList())
                            if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                    }) && DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"orphan projectile map {UniverseManager.MapTag(map)} reclaimed into pool after load");
            }
        }

        /// <summary>
        /// 场景重建：销毁全部弹体、清空图池（全部帧边界执行，注册表仍须存活）。
        /// </summary>
        public void ResetFlow()
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                QueueProjectileCleanup(projectiles[i], !projectiles[i].adoptedShuttle);
            }
            beams.Clear();
            trailPuffs.Clear();
            muzzleFlashes.Clear();
            impacts.Clear();
            particles.Clear();
            ResetMotionFeedback();
            soundTicks.Clear();
            visualSequence = 0;
            visualContacts.Clear();
            universe.MapPool.QueueReleaseUnused(SpaceObjectTag.Projectile);
        }

        // ---- 渲染（ShellRenderer 前台层；有图/无图弹体同一路径，图形取自 def）----

        /// <summary>
        /// 弹体与激光线段渲染（前景效果管线，聚焦帧投影）。返回消耗后
        /// 的下一槽位号。弹体朝向按速度方向（UsesVelocityHeading tag 分派）。
        /// </summary>
        internal int TryEmitProjectiles(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            int slot = baseSlot;
            shieldVisualScratch.Clear();
            foreach (SpaceObject obj in universe.objects)
                SpaceCollision.CollectVisibleShieldSegments(obj, shieldVisualScratch);
            if (shieldVisualScratch.Count > 0)
            {
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 11f;
                foreach (SpaceCollision.ShieldVisualSegment segment in shieldVisualScratch)
                {
                    Vector3 a = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                        segment.from, altitude);
                    Vector3 b = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                        segment.to, altitude);
                    Vector3 delta = b - a;
                    float span = new Vector2(delta.x, delta.z).magnitude;
                    if (span < 0.1f) continue;
                    float angle = Mathf.Atan2(delta.z, delta.x) + frame.pose.angle;
                    Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, angle);
                    MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10,
                        Matrix4x4.TRS((a + b) * 0.5f, rotation,
                            new Vector3(span, 1f, 0.8f)), ShieldLineMaterial(segment.mode));
                    ShellLayer.Slot shieldSlot = layer.At(slot++);
                    shieldSlot.layer.Emit(shieldSlot);
                }
            }
            if (trailPuffs.Count > 0)
            {
                EnsureImpactMaterials();
                Quaternion level = SpaceTransform.FocusedMeshRotation(frame, frame.pose.angle);
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 9f;
                int now = Find.TickManager.TicksGame;
                MapOverlayRenderer.BeginQuadBatch();
                foreach (TrailPuff puff in trailPuffs)
                {
                    float age = Mathf.Clamp01((float)(now - puff.bornTick) / puff.lifetimeTicks);
                    Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, puff.pos, altitude);
                    float size = puff.size * (0.65f + age * 1.55f);
                    Material material = exhaustSmokeMaterials[Mathf.Clamp(
                        Mathf.FloorToInt(age * exhaustSmokeMaterials.Length),
                        0, exhaustSmokeMaterials.Length - 1)];
                    MapOverlayRenderer.AddQuadToBatch(MeshPool.plane10,
                        Matrix4x4.TRS(pos, level, new Vector3(size, 1f, size)), material);
                }
                ShellLayer.Slot smokeSlot = layer.At(slot++);
                smokeSlot.layer.Emit(smokeSlot);
            }
            if (projectiles.Count > 0)
            {
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 10f;
                foreach (Projectile p in projectiles)
                {
                    if (p.dead) continue;
                    // 接管的跳帮艇继续由真实 Map 的船壳代理绘制，弹体层只添尾焰。
                    if (!p.laser && !p.adoptedShuttle)
                    {
                        Graphic graphic = p.def.graphicData?.Graphic;
                        Material material = graphic?.MatAt(Rot4.North);
                        if (material == null) continue;
                        EmitTracer(p, frame, frameMap, layer, ref slot);
                        Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                            p.container.pose.position, altitude);
                        Quaternion rot = SpaceTransform.FocusedSpriteRotation(frame, p.container.pose.angle);
                        Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, Vector3.one);
                        MapOverlayRenderer.CollectSingleQuad(graphic.MeshAt(Rot4.North), matrix, material);
                        ShellLayer.Slot s = layer.At(slot++);
                        s.layer.Emit(s);
                    }
                    if (!p.adoptedShuttle && p.props.exhaustTrail && p.motorThrusting)
                    {
                        EnsureImpactMaterials();
                        float ramp = MotorProgress(p, Find.TickManager.TicksGame);
                        float size = p.props.exhaustSize * Mathf.Lerp(0.4f, 1f, ramp);
                        EmitEngineFlame(ExhaustPose(p), size, frame, frameMap,
                            altitude, layer, ref slot);
                    }
                    if (!p.adoptedShuttle && p.props.exhaustTrail && p.rcsTurnSign != 0f)
                    {
                        EnsureImpactMaterials();
                        // 向左折转时朝右喷气；喷口位于弹体前半段的外侧。
                        SpacePose nozzle = SpaceTransform.RigidPointPose(p.container.pose,
                            new Vector2(0.22f, p.props.exhaustOffset.y - p.rcsTurnSign * 0.075f),
                            p.rcsTurnSign * Mathf.PI * 0.5f);
                        EmitEngineFlame(nozzle, p.props.exhaustSize * 0.38f,
                            frame, frameMap, altitude, layer, ref slot);
                    }
                }
            }
            EmitCombatVisuals(frame, frameMap, layer, ref slot);
            return slot;
        }

        private static void EmitEngineFlame(SpacePose nozzle, float size, SpaceObject frame,
            Map frameMap, float altitude, ShellLayer layer, ref int slot)
        {
            float length = size * 2.2f;
            Vector3 center = SpaceTransform.ShipLocalToUniverse(nozzle, new Vector2(-length * 0.5f, 0f));
            Vector3 flame = SpaceTransform.FocusedDrawPosition(frame, frameMap, center,
                altitude + Altitudes.AltInc);
            Quaternion rotation = SpaceTransform.FocusedMeshRotation(frame, nozzle.angle);
            EmitStretchQuad(exhaustFlashMaterials[0], flame, rotation, length, size * 0.85f, layer, ref slot);
            // 白芯也从喷口起始，不能与外焰共用中心而漂在焰尾。
            center = SpaceTransform.ShipLocalToUniverse(nozzle, new Vector2(-size * 0.625f, 0f));
            flame = SpaceTransform.FocusedDrawPosition(frame, frameMap, center, altitude + Altitudes.AltInc * 2f);
            EmitStretchQuad(impactFlashMaterials[0], flame, rotation, size * 1.25f, size * 0.42f, layer, ref slot);
        }

        private static void EmitStretchQuad(Material material, Vector3 pos, Quaternion rot,
            float length, float width, ShellLayer layer, ref int slot)
        {
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(length, 1f, width));
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
            ShellLayer.Slot s = layer.At(slot++);
            s.layer.Emit(s);
        }

        /// <summary>
        /// 命中特效材质（种类 × 透明度档）。贴图与 shader 均按原版 mote def 同款
        /// 配置（发光贴图配 MoteGlow 加法混合——黑底在加法下不可见，配 Transparent 会
        /// 画出黑框；带真实 alpha 的贴图配 MoteBase 默认的 Mote shader）。材质经
        /// MaterialPool.MatFrom（原版材质构造函数）克隆自持以设 renderQueue 3202
        ///（与其余战斗前台效果共用队列）。
        /// </summary>
        private static void EnsureImpactMaterials()
        {
            if (impactFlashMaterials != null) return;
            impactFlashMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.62f, 0.3f));
            impactSmokeMaterials = BuildImpactTiers("Things/Mote/Smoke",
                ShaderDatabase.Mote, new Color(0.35f, 0.35f, 0.38f));
            impactEnergyMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.23f, 0.18f));
            exhaustFlashMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.35f, 0.08f), 3);
            exhaustSmokeMaterials = BuildImpactTiers("Things/Mote/Smoke",
                ShaderDatabase.Mote, new Color(0.47f, 0.46f, 0.45f), 3);
        }

        private static Material[] BuildImpactTiers(string texturePath, Shader shader, Color color, int count = 12)
        {
            var tiers = new Material[count];
            for (int i = 0; i < tiers.Length; i++)
            {
                float alpha = 0.85f * (1f - (float)i / tiers.Length);
                tiers[i] = new Material(MaterialPool.MatFrom(texturePath, shader,
                    new Color(color.r, color.g, color.b, alpha)))
                {
                    name = $"DeltaV_Impact_{texturePath.Substring(texturePath.Length - 10)}_{i}",
                    renderQueue = 3202,
                };
            }
            return tiers;
        }

        private static Material ShieldLineMaterial(SpaceShieldMode mode)
        {
            // Keep separate immutable materials: queued draws retain their references.
            ref Material material = ref (mode == SpaceShieldMode.Electromagnetic
                ? ref electromagneticShieldLineMaterial : ref kineticShieldLineMaterial);
            return material ??= new Material(MaterialPool.MatFrom(
                "Things/Building/SpaceWeapons/ShieldField", ShaderDatabase.Transparent))
            {
                name = $"DeltaV_ShieldLine_{mode}",
                color = mode == SpaceShieldMode.Electromagnetic
                    ? new Color(1f, 0.25f, 0.85f, 0.85f)
                    : new Color(1f, 1f, 1f, 0.72f),
                renderQueue = 3201,
            };
        }

        /// <summary>换档清理静态渲染缓存（UniverseManager.ResetRuntimeServices）。</summary>
        public static void ClearRuntimeCaches()
        {
            if (kineticShieldLineMaterial != null)
            {
                Object.Destroy(kineticShieldLineMaterial);
                kineticShieldLineMaterial = null;
            }
            if (electromagneticShieldLineMaterial != null)
            {
                Object.Destroy(electromagneticShieldLineMaterial);
                electromagneticShieldLineMaterial = null;
            }
            DestroyCombatMaterials();
            MotionParticleRenderer.ClearRuntimeCaches();
            DestroyImpactTiers(ref impactFlashMaterials);
            DestroyImpactTiers(ref impactSmokeMaterials);
            DestroyImpactTiers(ref impactEnergyMaterials);
            DestroyImpactTiers(ref exhaustFlashMaterials);
            DestroyImpactTiers(ref exhaustSmokeMaterials);
        }

        /// <summary>销毁一组特效克隆材质（贴图来自 MaterialPool 共享缓存，不销毁）。</summary>
        private static void DestroyImpactTiers(ref Material[] tiers)
        {
            if (tiers == null) return;
            foreach (Material material in tiers)
            {
                if (material != null) Object.Destroy(material);
            }
            tiers = null;
        }
    }
}

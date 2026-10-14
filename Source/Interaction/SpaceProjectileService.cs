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

        public SpaceProjectileLaunchSpec(float speed, int coldLaunchTicks, float coldLaunchSpeed,
            float damageScale = 1f, float armorPenetrationScale = 1f)
        {
            this.speed = speed;
            this.coldLaunchTicks = coldLaunchTicks;
            this.coldLaunchSpeed = coldLaunchSpeed;
            this.damageScale = damageScale;
            this.armorPenetrationScale = armorPenetrationScale;
        }

        public static SpaceProjectileLaunchSpec FromDef(CompProperties_SpaceProjectile props,
            float damageScale = 1f)
        {
            return new SpaceProjectileLaunchSpec(props.speed, props.coldLaunchTicks,
                props.speed * props.coldLaunchSpeedFactor, damageScale);
        }
    }

    /// <summary>
    /// 弹体服务（太空战架构 §4.1；取代 BoardingPods 槽位，段7 完成接管）：弹体
    /// 创建/注册（图池借出）、制导（导弹速度转向/直射恒速）、超时自毁、碰撞调度
    /// 对接（SpaceCollision 的主动方来源）、渲染（弹体 def 图形投影 + 激光线段）、
    /// 图池管理与读档孤儿图回收、ResetFlow。
    ///
    /// 实例服务（UniverseManager.Projectiles）：不序列化、读档归零（弹体是瞬态成员，
    /// 写档按 tag 剔除，UniverseManager.ExposeData）；积分与被动阻力豁免走主容器
    /// 循环按 tag 分派（§3.3），本服务只做制导写速度与生命周期。
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
    public sealed class SpaceProjectileService
    {
        private const float Dt = 1f / 60f;

        /// <summary>激光线段视觉存留（tick；分档材质近似渐隐）。</summary>
        private const int BeamLifetimeTicks = 15;
        private const int MuzzleFlashLifetimeTicks = 10;
        private const int MaxTrailPuffs = 480;

        private readonly UniverseManager universe;

        private sealed class Projectile
        {
            public SpaceObject container;
            public ThingDef def;
            public CompProperties_SpaceProjectile props;
            public Building building;
            public Thing launcher;
            public SpaceObject sourceContainer;
            public bool reflectedByShield;
            public SpaceObject target;
            public Vector2? targetLocalPoint;
            public bool adoptedShuttle;
            public int spawnedAtTick;
            public bool dead;
            public Vector3 prevPosition;
            public bool laser;
            public float cruiseSpeed;
            public int ignitionTick;
            public bool ignited;
            public Vector3 laserFrom;
            public Vector3 laserTo;
            /// <summary>伤害/穿深倍率（轨道炮 Σ 加速段；随弹体携带到命中结算）。</summary>
            public float damageScale = 1f;
            public float armorPenetrationScale = 1f;
            /// <summary>复用的扫掠查询（每 tick 重填字段，热路径零分配）。</summary>
            public readonly SpaceCollision.SweptBody body = new SpaceCollision.SweptBody();
        }

        private sealed class Beam
        {
            public Vector3 from;
            public Vector3 to;
            public int bornTick;
            public float width;
        }

        private sealed class TrailPuff
        {
            public Vector3 pos;
            public int bornTick;
            public int lifetimeTicks;
            public float size;
        }

        private sealed class MuzzleFlash
        {
            public Vector3 pos;
            public float angle;
            public float size;
            public int bornTick;
        }

        /// <summary>壳上命中特效种类（守方后效选型，见 RegisterImpactFx）。</summary>
        private enum ImpactFxKind : byte
        {
            Explosion,
            Kinetic,
            Energy,
            Shield,
        }

        private sealed class ImpactFx
        {
            public Vector3 pos;
            public int bornTick;
            public ImpactFxKind kind;
            public bool focused;
            public float incomingAngle;
            /// <summary>爆炸半径（非爆炸 = 0；闪光/烟尺寸）。</summary>
            public float radius;
        }

        /// <summary>壳上命中特效存留（tick；分档材质近似渐隐，与激光束同口径）。</summary>
        private const int ImpactLifetimeTicks = 54;

        /// <summary>壳路径音效的近/远衰减界（格；镜像 ThrusterAudio 的 30/120）。</summary>
        private const float ImpactHearNear = 30f;
        private const float ImpactHearFar = 120f;

        private readonly List<Projectile> projectiles = new List<Projectile>();
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

        /// <summary>图池（footprint 哈希 → 空闲小图）。运行时缓存，不序列化。</summary>
        private readonly Dictionary<int, List<Map>> mapPool = new Dictionary<int, List<Map>>();
        // 在飞与闲置池共用同一集合：这些图是弹体实现细节，不能在殖民者栏暴露。
        private readonly HashSet<Map> projectileMaps = new HashSet<Map>();

        public void ForgetMap(Map map)
        {
            if (map == null) return;
            projectileMaps.Remove(map);
            foreach (List<Map> maps in mapPool.Values) maps.Remove(map);
        }

        // 激光线段材质（静态渲染缓存，ClearRuntimeCaches 换档销毁；三档透明度近似渐隐）。
        private static Material[] beamMaterials;
        private static Material kineticShieldLineMaterial;
        private static Material electromagneticShieldLineMaterial;

        // 壳上命中特效材质（静态渲染缓存；种类 × 三档透明度。贴图复用原版 mote 素材，
        // 克隆材质自持以设 renderQueue，ClearRuntimeCaches 只销毁克隆不动共享贴图）。
        private static Material[] impactFlashMaterials;   // 爆炸闪光（BrightFlash 橙）
        private static Material[] impactSparkMaterials;   // 动能火花（SparkThrown 白黄）
        private static Material[] impactShieldMaterials;  // 护盾拦截（BrightFlash 蓝）
        private static Material[] impactSmokeMaterials;   // 爆炸烟（Black 灰）
        private static Material[] impactEnergyMaterials;  // 激光熔蚀（BrightFlash 红）
        private static Material[] exhaustFlashMaterials;
        private static Material[] exhaustSmokeMaterials;
        private static SoundDef impactExplosionShellDef;  // 非聚焦守方（OnCamera + 手工音量）
        private static SoundDef impactHitShellDef;
        private static SoundDef shieldHitShellDef;
        private static SoundDef impactHitMapDef;          // 聚焦守方（InMap 位置化）
        private static SoundDef shieldHitMapDef;

        public SpaceProjectileService(UniverseManager universe)
        {
            this.universe = universe;
        }

        /// <summary>在飞弹体数（诊断/图池状态用）。</summary>
        public int InFlightCount => projectiles.Count;

        /// <summary>成功发射后的表现事件；炮口位置和方向取已冻结的 shot。</summary>
        public void RegisterMuzzleFlash(Vector3 muzzle, float angle, float size)
        {
            muzzleFlashes.Add(new MuzzleFlash
            {
                pos = muzzle,
                angle = angle,
                size = size,
                bornTick = Find.TickManager.TicksGame,
            });
        }

        // ---- 创建 ----

        /// <summary>
        /// 发射一枚弹体。muzzle = 炮口 universe 位置；directionWorld = 炮口处切空间
        /// 单位方向（3D）。初速 = 方向 × 弹速 ⊕ 发射瞬间发射容器速度（弹道继承炮口
        /// 运动；相对速度结算因此对头/追尾天然成立）。导弹传 missileTarget。
        /// damageScale = 轨道炮等的伤害/穿深倍率（随弹体携带到命中结算）。
        /// </summary>
        public bool Launch(ThingDef def, SpaceObject source, Vector3 muzzle, Vector3 directionWorld,
            Faction faction, Thing launcher = null, SpaceObject missileTarget = null, float damageScale = 1f)
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
            Thing launcher, Vector3 hit, Vector2 direction, float signedSpeed)
        {
            OrbitSphere.Basis(hit, out Vector3 east, out Vector3 north);
            Vector3 worldDirection = east * direction.x + north * direction.y;
            Vector2 travel = signedSpeed >= 0f ? direction : -direction;
            Vector3 muzzle = OrbitSphere.Move(hit, travel * 0.06f);
            float speed = Mathf.Abs(signedSpeed);
            if (!Launch(def, null, muzzle, worldDirection, source?.faction, launcher, null,
                new SpaceProjectileLaunchSpec(speed, 0, speed, 1f, 1f))) return;
            Projectile fragment = projectiles[projectiles.Count - 1];
            fragment.sourceContainer = source;
            fragment.reflectedByShield = signedSpeed < 0f;
            fragment.container.pose.velocity = travel * speed
                + SpaceTransform.TransportVelocity(defender.pose, muzzle);
            fragment.container.pose.angle = Mathf.Atan2(travel.y, travel.x);
            fragment.cruiseSpeed = speed;
        }

        public bool Launch(ThingDef def, SpaceObject source, Vector3 muzzle, Vector3 directionWorld,
            Faction faction, Thing launcher, SpaceObject missileTarget, SpaceProjectileLaunchSpec launchSpec)
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
                ? SpaceTransform.TransportVelocity(source.pose, muzzle)
                : Vector2.zero;

            Projectile projectile = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            projectile.def = def;
            projectile.props = props;
            projectile.launcher = launcher;
            projectile.sourceContainer = source;
            projectile.reflectedByShield = false;
            projectile.target = missileTarget;
            projectile.spawnedAtTick = Find.TickManager.TicksGame;
            projectile.laser = props.laserRange > 0f;
            projectile.cruiseSpeed = launchSpec.speed;
            projectile.ignitionTick = Find.TickManager.TicksGame + Mathf.Max(0, launchSpec.coldLaunchTicks);
            projectile.ignited = launchSpec.coldLaunchTicks <= 0;

            if (projectile.laser)
            {
                // 激光：即时链——无图、不积分；容器只为「同一注册表结算链」存在，
                // 速度取发射瞬间双方速度的攻方位（相对速度语义，§4.3）。
                projectile.laserFrom = muzzle;
                projectile.laserTo = OrbitSphere.Move(muzzle, dirTangent * props.laserRange);
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), null,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                projectile.container.pose.velocity = launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            else if (props.hasMap)
            {
                Map map = BorrowMap(def.Size);
                if (map == null)
                {
                    RecycleProjectile(projectile);
                    return false;
                }
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), map,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                float initialSpeed = launchSpec.coldLaunchTicks > 0
                    ? launchSpec.coldLaunchSpeed : launchSpec.speed;
                projectile.container.pose.velocity = dirTangent * initialSpeed + launcherVelocity;
                projectile.building = ThingMaker.MakeThing(def) as Building;
                if (projectile.building != null)
                {
                    GenSpawn.Spawn(projectile.building,
                        new IntVec3((map.Size.x - def.Size.x) / 2, 0, (map.Size.z - def.Size.z) / 2),
                        map, Rot4.North);
                }
                projectile.prevPosition = muzzle;
            }
            else
            {
                projectile.container = new SpaceObject(universe.AllocateOwnerId(), null,
                    new SpacePose(muzzle, Mathf.Atan2(dirTangent.y, dirTangent.x)))
                {
                    baseTag = SpaceObjectTag.Projectile,
                    faction = faction,
                };
                float initialSpeed = launchSpec.coldLaunchTicks > 0
                    ? launchSpec.coldLaunchSpeed : launchSpec.speed;
                projectile.container.pose.velocity = dirTangent * initialSpeed + launcherVelocity;
                projectile.prevPosition = muzzle;
            }
            universe.RegisterObject(projectile.container);
            projectile.damageScale = launchSpec.damageScale;
            projectile.armorPenetrationScale = launchSpec.armorPenetrationScale;
            projectiles.Add(projectile);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"{def.defName} launched from '{source?.label ?? "nowhere"}' " +
                $"(speed {launchSpec.speed:F0}, {(props.hasMap ? "mapped" : "mapless")}" +
                $"{(projectile.laser ? ", laser" : "")})");
            return true;
        }

        /// <summary>把已经起飞的 shuttle 作为一枚有图导弹接入同一制导与碰撞工作表。</summary>
        internal bool AdoptShuttle(SpaceObject shuttle, Building hull, SpaceObject target,
            Vector2? targetLocalPoint)
        {
            CompProperties_SpaceProjectile props = hull?.def.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null || !props.guided || !props.hasMap || shuttle?.map != hull.Map
                || shuttle.baseTag != SpaceObjectTag.Shuttle || target?.map == null
                || target.map.Disposed || projectiles.Any(p => p.container == shuttle)) return false;
            Projectile p = projectilePool.Count > 0 ? projectilePool.Pop() : new Projectile();
            p.container = shuttle;
            p.building = hull;
            p.def = hull.def;
            p.props = props;
            p.launcher = hull;
            p.sourceContainer = null;
            p.reflectedByShield = false;
            p.target = target;
            p.targetLocalPoint = targetLocalPoint;
            p.adoptedShuttle = true;
            p.spawnedAtTick = Find.TickManager.TicksGame;
            p.dead = false;
            p.laser = false;
            p.cruiseSpeed = props.speed;
            p.ignitionTick = p.spawnedAtTick;
            p.ignited = true;
            p.prevPosition = shuttle.pose.position;
            p.damageScale = p.armorPenetrationScale = 1f;
            shuttle.pose.angularVelocity = 0f;
            shuttle.projectileRoleOverride = true;
            projectiles.Add(p);
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"boarding missile adopted shuttle #{shuttle.objectId} target #{target.objectId}");
            return true;
        }

        internal bool ReleaseShuttle(SpaceObject shuttle)
        {
            Projectile p = projectiles.FirstOrDefault(item => item.adoptedShuttle && item.container == shuttle);
            if (p == null) return false;
            p.dead = true;
            projectiles.Remove(p);
            shuttle.projectileRoleOverride = false;
            universe.QueueFrameAction(() => RecycleProjectile(p));
            return true;
        }

        // ---- tick（制导/超时；容器积分之前）----

        /// <summary>
        /// 弹体制导与超时（GameComponentTick 内、容器积分之前调用：本 tick 写速度
        /// 方向、积分随即位移，SpaceCollision 在积分后扫掠——先动后撞，§3.3）。
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
                    if (p.adoptedShuttle && (!TargetAlive(p.target)
                        || p.target.map == null || p.target.map.Disposed))
                    {
                        ReleaseShuttle(p.container);
                        continue;
                    }
                    p.prevPosition = p.container.pose.position;
                    if (!p.laser && !p.ignited && now >= p.ignitionTick)
                    {
                        Vector2 direction = p.container.pose.velocity.sqrMagnitude > 1e-8f
                            ? p.container.pose.velocity.normalized
                            : Vector2.right;
                        p.container.pose.velocity = direction * p.cruiseSpeed;
                        p.container.pose.angle = Mathf.Atan2(direction.y, direction.x);
                        p.ignited = true;
                    }
                    if (p.props.guided && !p.laser && p.ignited && TargetAlive(p.target))
                    {
                        SteerTowards(p);
                    }
                    if (p.props.exhaustTrail && p.ignited && now % 3 == 0)
                    {
                        Vector2 backwards = p.container.pose.velocity.sqrMagnitude > 1e-8f
                            ? -p.container.pose.velocity.normalized : Vector2.left;
                        trailPuffs.Add(new TrailPuff
                        {
                            pos = OrbitSphere.Move(p.container.pose.position, backwards * 0.55f),
                            bornTick = now,
                            lifetimeTicks = Mathf.Max(1, p.props.smokeLifetimeTicks),
                            size = Mathf.Max(0.1f, p.props.exhaustSize),
                        });
                        if (trailPuffs.Count > MaxTrailPuffs) trailPuffs.RemoveAt(0);
                    }
                }
            }
            // 激光线段视觉寿命（纯表现）。
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                if (Find.TickManager.TicksGame - beams[i].bornTick >= BeamLifetimeTicks) beams.RemoveAt(i);
            }
            int tick = Find.TickManager.TicksGame;
            for (int i = trailPuffs.Count - 1; i >= 0; i--)
                if (tick - trailPuffs[i].bornTick >= trailPuffs[i].lifetimeTicks)
                    trailPuffs.RemoveAt(i);
            for (int i = muzzleFlashes.Count - 1; i >= 0; i--)
                if (tick - muzzleFlashes[i].bornTick >= MuzzleFlashLifetimeTicks)
                    muzzleFlashes.RemoveAt(i);
            for (int i = impacts.Count - 1; i >= 0; i--)
                if (tick - impacts[i].bornTick > ImpactLifetimeTicks)
                    impacts.RemoveAt(i);
        }

        /// <summary>纯追踪制导：速度方向每 tick 向目标容器当前位姿转向（转向率受限）。</summary>
        private void SteerTowards(Projectile p)
        {
            Vector3 aimPoint = p.targetLocalPoint.HasValue && p.target.map != null
                ? SpaceTransform.MapPointToUniverse(p.target, p.target.map, p.targetLocalPoint.Value)
                : p.target.pose.position;
            Vector2 toTarget = OrbitSphere.Offset(p.container.pose.position, aimPoint);
            if (toTarget.sqrMagnitude < 1e-8f) return;
            Vector2 desired = toTarget.normalized;
            Vector2 velocity = p.container.pose.velocity;
            Vector2 current = velocity.sqrMagnitude > 1e-8f ? velocity.normalized : desired;
            float signed = Mathf.Sign(current.x * desired.y - current.y * desired.x);
            float angle = Mathf.Acos(Mathf.Clamp(Vector2.Dot(current, desired), -1f, 1f));
            float maxTurn = p.props.turnRateDegPerSec * Mathf.Deg2Rad * Dt;
            float turn = Mathf.Min(angle, maxTurn);
            Vector2 newDir = SpaceTransform.Rotate(current, signed * turn);
            p.container.pose.velocity = newDir * p.cruiseSpeed;
            float facingOffset = p.adoptedShuttle
                ? ShipOrientation.AngleFromRot4(p.building.Rotation) : 0f;
            p.container.pose.angle = SpaceTransform.WrapAngle(
                Mathf.Atan2(newDir.y, newDir.x) - facingOffset);
        }

        /// <summary>
        /// 导弹目标存活判定：目标仍在 objects 权威列表（销毁即注销；漂移目标照常追踪）。
        /// </summary>
        private bool TargetAlive(SpaceObject target)
        {
            return target != null && universe.objects.Contains(target);
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
            foreach (Projectile p in projectiles)
            {
                if (p.dead) continue;
                SpaceCollision.SweptBody body = p.body;
                body.attacker = p.container;
                body.projectileDef = p.def;
                body.attackerBuilding = p.building;
                body.excludeContainer = p.reflectedByShield ? null : p.sourceContainer;
                body.requiredDefender = p.adoptedShuttle ? p.target : null;
                body.ignoreNonHostileProjectiles = !p.reflectedByShield;
                body.launcher = p.launcher;
                body.dead = false;
                body.resolvedHit = false;
                body.didRedirect = false;
                body.shieldResidualFraction = 0f;
                body.sweepRadius = FootprintRadius(p.def);
                body.noseHalfWidth = p.adoptedShuttle ? p.def.Size.x * 0.5f : 0f;
                body.noseForwardOffset = p.adoptedShuttle ? (p.def.Size.z - 1) * 0.5f : 0f;
                body.damageScale = p.damageScale;
                body.armorPenetrationScale = p.armorPenetrationScale;
                if (p.laser)
                {
                    // 专用点防束在结算 tick 追踪导弹现位，避免一 tick 位移把必中特性变成擦边。
                    if (p.props.guaranteedHitMissiles && TargetAlive(p.target)
                        && p.target.map != null && !p.target.map.Disposed)
                    {
                        foreach (Thing thing in p.target.map.listerThings.AllThings)
                        {
                            if (!SpaceImpactEffects.GuaranteedMissileHit(p.def, thing.def)) continue;
                            p.laserTo = p.target.pose.position;
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
                        Vector2 incident = OrbitSphere.Offset(captured.body.from, captured.body.to);
                        Vector2 incidentAtHit = incident.sqrMagnitude > 1e-8f
                            ? SpaceTransform.TransportVelocity(new SpacePose
                            {
                                position = captured.body.from,
                                velocity = incident.normalized
                            }, hit)
                            : Vector2.up;
                        ReleaseShuttle(shuttle);
                        universe.Shuttles.PlaceBoardedShuttle(shuttle, defender, hit, local,
                            incidentAtHit, captured.body.resolvedHitCell,
                            captured.body.resolvedHitShield);
                    }) : null;
                body.onShieldRedirect = delegate (Vector3 from, Vector3 hit)
                {
                    if (captured.laser)
                        beams.Add(new Beam { from = from, to = hit,
                            bornTick = Find.TickManager.TicksGame,
                            width = captured.props.laserBeamWidth });
                };
                body.onShieldShatter = delegate (Vector3 hit, SpaceObject defender,
                    Vector2 direction, float signedSpeed)
                {
                    SpaceShieldProjectileBehavior.SpawnFragments(this, captured.def,
                        captured.sourceContainer, defender, captured.launcher, hit,
                        direction, signedSpeed);
                };
                sweepScratch.Add(p);
                bodyScratch.Add(body);
            }
            SpaceCollision.SweepAndResolve(universe, bodyScratch);
            RegisterImpactFx();
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
                float residual = p.body.shieldResidualFraction;
                if (residual <= 0f || p.dead) continue;
                Vector3 hit = p.body.shieldResidualPoint;
                Vector2 travel = OrbitSphere.Offset(p.body.from, p.body.to);
                if (travel.sqrMagnitude < 1e-8f) continue;
                Vector2 direction = travel.normalized;
                if (p.laser)
                {
                    // 即时光束不能降速续飞；在弧后生成一枚低能破片，走同一弹体/碰撞链。
                    ThingDef fragment = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_ShieldFragment");
                    if (fragment != null)
                    {
                        OrbitSphere.Basis(hit, out Vector3 east, out Vector3 north);
                        Vector3 worldDirection = east * direction.x + north * direction.y;
                        Launch(fragment, p.sourceContainer, OrbitSphere.Move(hit, direction * 0.1f),
                            worldDirection, p.container.faction, p.launcher, null,
                            new SpaceProjectileLaunchSpec(45f, 0, 45f,
                                p.damageScale * residual, p.armorPenetrationScale * residual));
                    }
                    else
                    {
                        Log.ErrorOnce("[DeltaV] Missing shield breach fragment def; residual beam cannot continue.",
                            0x44565346);
                    }
                }
                else
                {
                    // 弹体在弧内侧留下，速度与命中能量下降；导弹巡航速度也同步下降。
                    float speedFactor = Mathf.Sqrt(residual);
                    p.container.pose.position = OrbitSphere.Move(hit, direction * 0.1f);
                    p.container.pose.velocity *= speedFactor;
                    p.cruiseSpeed *= speedFactor;
                    p.damageScale *= residual;
                    p.armorPenetrationScale *= residual;
                    p.ignited = true;
                }
            }
            // 激光当 tick 消费完毕：视觉线段（命中点或全射程）+ 销毁。
            foreach (Projectile p in sweepScratch)
            {
                if (!p.laser) continue;
                Vector3 end = p.body.resolvedHit ? p.body.resolvedHitUniverse : p.body.to;
                beams.Add(new Beam { from = p.body.from, to = end,
                    bornTick = Find.TickManager.TicksGame, width = p.props.laserBeamWidth });
                if (!p.dead) DestroyProjectile(p, "laser spent");
            }
        }

        // ---- 命中特效（壳上视觉 + 音效，2026-09-27；太空战架构 §命中后效）----

        /// <summary>
        /// 命中特效登记（tick 路径，结算后一次遍历）。前台层负责持续烟云与碎片；
        /// 聚焦图主爆炸闪光由原版 GenExplosion 绘制，不重复叠加。非聚焦守方的
        /// MapOverlayRenderer 排除 Mote/Fleck，因此前台层补全闪光。音效两边都补：
        /// 聚焦图爆炸命中由 GenExplosion 原生出声（跳过防重音），动能/护盾补 InMap；
        /// 非聚焦图原版 InMap 声被 CorrectContextNow 硬闸，镜像 ThrusterAudio 壳路径。
        /// </summary>
        private void RegisterImpactFx()
        {
            if (bodyScratch.Count == 0) return;
            bool soundEnabled = DeltaVMod.CurrentSettings?.spaceImpactSound != false;
            int now = Find.TickManager.TicksGame;
            foreach (SpaceCollision.SweptBody body in bodyScratch)
            {
                if (!body.resolvedHit || body.resolvedHitDefender == null) continue;
                ImpactFxKind kind = body.resolvedHitShield ? ImpactFxKind.Shield
                    : body.resolvedHitExplosive ? ImpactFxKind.Explosion
                    : body.projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()?.laserRange > 0f
                        ? ImpactFxKind.Energy
                    : ImpactFxKind.Kinetic;
                bool focused = Find.CurrentMap == body.resolvedHitDefender.map;
                Vector2 incoming = OrbitSphere.Offset(body.from, body.to);
                impacts.Add(new ImpactFx
                {
                    pos = body.resolvedHitUniverse,
                    bornTick = now,
                    kind = kind,
                    focused = focused,
                    incomingAngle = incoming.sqrMagnitude > 1e-8f
                        ? Mathf.Atan2(incoming.y, incoming.x) : 0f,
                    radius = kind == ImpactFxKind.Kinetic
                        ? Mathf.Clamp(0.65f + Mathf.Sqrt(Mathf.Max(0f, body.damageScale))
                            * 0.6f, 0.85f, 3.5f)
                        : kind == ImpactFxKind.Energy ? 1f
                        : body.resolvedHitRadius,
                });
                if (soundEnabled)
                {
                    PlayImpactSound(body, kind, focused);
                }
            }
        }

        /// <summary>命中音效（设置开关默认开——「有人说宇宙中没有空气所以不能传声」）。</summary>
        private void PlayImpactSound(SpaceCollision.SweptBody body, ImpactFxKind kind, bool focused)
        {
            if (focused)
            {
                if (kind == ImpactFxKind.Explosion) return; // GenExplosion 原生出声
                SoundDef def = kind == ImpactFxKind.Shield ? ShieldHitMapDef() : ImpactHitMapDef();
                var cell = new IntVec3(Mathf.FloorToInt(body.resolvedHitLocal.x), 0,
                    Mathf.FloorToInt(body.resolvedHitLocal.y)).ClampInsideMap(body.resolvedHitDefender.map);
                def.PlayOneShot(SoundInfo.InMap(new TargetInfo(cell, body.resolvedHitDefender.map)));
                return;
            }
            SoundDef shellDef = kind switch
            {
                ImpactFxKind.Explosion => ImpactExplosionShellDef(),
                ImpactFxKind.Shield => ShieldHitShellDef(),
                _ => ImpactHitShellDef(),
            };
            // 非聚焦守方：OnCamera one-shot + 手工音量。距离复用船壳绘制同款换算
            //（镜像 ThrusterAudio：30 格内全响、120 格衰减到零，宇宙视野拉远自然无声）。
            Map current = Find.CurrentMap;
            SpaceObject frame = current == null ? null : universe.FrameOf(current);
            if (frame == null || !WorldRendererUtility.DrawingMap) return;
            Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, current, body.resolvedHitUniverse, 0f);
            float distance = Vector3.Distance(Find.Camera.transform.position, drawPos);
            if (distance >= ImpactHearFar) return;
            float volume = distance <= ImpactHearNear
                ? 1f
                : 1f - (distance - ImpactHearNear) / (ImpactHearFar - ImpactHearNear);
            var info = SoundInfo.OnCamera();
            info.volumeFactor = volume;
            shellDef.PlayOneShot(info);
        }

        // 声 def 惰性解析（1.6/Defs/SoundDefs/DeltaV_SpaceCombatSounds.xml；与 ThrusterAudio
        // 的 GetNamed 模式一致）。
        private static SoundDef ImpactExplosionShellDef() =>
            impactExplosionShellDef ??= DefDatabase<SoundDef>.GetNamed("DeltaV_ImpactExplosionShell");
        private static SoundDef ImpactHitShellDef() =>
            impactHitShellDef ??= DefDatabase<SoundDef>.GetNamed("DeltaV_ImpactHitShell");
        private static SoundDef ShieldHitShellDef() =>
            shieldHitShellDef ??= DefDatabase<SoundDef>.GetNamed("DeltaV_ShieldHitShell");
        private static SoundDef ImpactHitMapDef() =>
            impactHitMapDef ??= DefDatabase<SoundDef>.GetNamed("DeltaV_ImpactHitMap");
        private static SoundDef ShieldHitMapDef() =>
            shieldHitMapDef ??= DefDatabase<SoundDef>.GetNamed("DeltaV_ShieldHitMap");

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
            p.dead = true;
            DeltaVLog.Message(DeltaVLogModule.Combat, $"{p.def.defName} #{p.container.objectId} destroyed ({reason})");
            universe.QueueFrameAction(delegate
            {
                projectiles.Remove(p);
                SpaceObject container = p.container;
                if (container == null)
                {
                    RecycleProjectile(p);
                    return;
                }
                if (p.building != null && !p.building.Destroyed)
                {
                    p.building.Destroy(DestroyMode.Vanish);
                }
                p.building = null;
                if (container.map != null)
                {
                    ReturnMapToPool(container.map);
                }
                if (universe.objects.Contains(container))
                {
                    universe.UnregisterObject(container);
                }
                RecycleProjectile(p);
            });
        }

        private void RecycleProjectile(Projectile p)
        {
            p.container = null;
            p.def = null;
            p.props = null;
            p.building = null;
            p.launcher = null;
            p.sourceContainer = null;
            p.reflectedByShield = false;
            p.target = null;
            p.targetLocalPoint = null;
            p.adoptedShuttle = false;
            p.dead = false;
            p.laser = false;
            p.damageScale = 1f;
            p.armorPenetrationScale = 1f;
            p.body.attacker = null;
            p.body.projectileDef = null;
            p.body.attackerBuilding = null;
            p.body.excludeContainer = null;
            p.body.requiredDefender = null;
            p.body.noseHalfWidth = 0f;
            p.body.noseForwardOffset = 0f;
            p.body.launcher = null;
            p.body.onSelfDestruct = null;
            p.body.onPlace = null;
            p.body.resolvedHitDefender = null;
            p.body.resolvedHitCell = IntVec3.Invalid;
            p.body.dead = false;
            p.body.resolvedHit = false;
            if (projectilePool.Count < MaxPooledProjectiles) projectilePool.Push(p);
        }

        private Map BorrowMap(IntVec2 footprint)
        {
            // 尺寸下限 2×2（§15.17 定案）：1×1 图 Area=1，GasGrid 构造的
            // Rand.Range(0, Area/2) 提前返回不消耗 RNG，与原版组件「恰好 1 次」的
            // 校准不符（MapPreview 等按 Rand.iterations 对账的 mod 会误报）。弹体
            // building 始终居中放置，不受下限影响。
            int x = Mathf.Max(2, footprint.x);
            int z = Mathf.Max(2, footprint.z);
            int key = PoolKey(x, z);
            if (mapPool.TryGetValue(key, out List<Map> idle) && idle.Count > 0)
            {
                Map map = idle[idle.Count - 1];
                idle.RemoveAt(idle.Count - 1);
                projectileMaps.Add(map);
                return map;
            }
            Map generated = ShipPocketMapFactory.GenerateImmediate(new IntVec3(x, 1, z), universe);
            if (generated != null)
            {
                DeltaVPocketMapMetadata metadata = generated.GetComponent<DeltaVPocketMapMetadata>();
                if (metadata != null) metadata.projectileMap = true;
                projectileMaps.Add(generated);
                HostColonistBarVisibility.NotifyDockingStateChanged();
            }
            return generated;
        }

        private void ReturnMapToPool(Map map)
        {
            if (map == null || map.Disposed) return;
            // 保险：图上不应再有内容（弹体 building 已毁）；有残留则不入池直接销毁。
            if (map.listerThings.AllThings.Count > 0)
            {
                projectileMaps.Remove(map);
                universe.Lifecycle.DeinitMap(map, false);
                return;
            }
            int key = PoolKey(map.Size.x, map.Size.z);
            if (!mapPool.TryGetValue(key, out List<Map> idle))
            {
                mapPool[key] = idle = new List<Map>();
            }
            idle.Add(map);
            projectileMaps.Add(map);
        }

        /// <summary>殖民者栏等 UI 判断弹体内部图的唯一入口（包含在飞与闲置池）。</summary>
        internal bool IsProjectileMap(Map map)
        {
            if (map == null) return false;
            if (projectileMaps.Contains(map)) return true;
            if (map.GetComponent<DeltaVPocketMapMetadata>()?.projectileMap == true) return true;
            SpaceObject obj = universe.SpaceObjectOnMap(map);
            if (obj != null && obj.baseTag == SpaceObjectTag.Projectile) return true;
            // marker 上线前的存档兼容：普通飞船图最小边长 80，专用宿主为 50；
            // 只有旧弹体池会留下小于宿主边长的 ShipPocketParent 图。
            return map.Parent is ShipPocketParent_DeltaV
                && (map.Size.x < UniverseManager.HostMapEdge || map.Size.z < UniverseManager.HostMapEdge);
        }

        private static int PoolKey(int x, int z) => x * 4096 + z;

        /// <summary>dev：图池状态摘要。</summary>
        public string PoolStatus()
        {
            int pooled = 0;
            foreach (KeyValuePair<int, List<Map>> kv in mapPool) pooled += kv.Value.Count;
            return $"in-flight={projectiles.Count} beams={beams.Count} pooledIdleMaps={pooled}";
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
                if (map.mapPawns.AllPawnsSpawned.Count > 0) continue;
                bool marked = IsProjectileMap(map);
                bool any = false;
                bool onlyProjectiles = true;
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Building building
                        && building.def.GetCompProperties<CompProperties_SpaceProjectile>() != null)
                    {
                        any = true;
                        continue;
                    }
                    onlyProjectiles = false;
                    break;
                }
                // 新存档由持久 marker 识别，空闲池图即使没有 building 也可回收；
                // marker 上线前的旧档仍以「内容只有弹体 building」兼容识别。
                if ((!marked && !any) || !onlyProjectiles) continue;
                foreach (Thing thing in map.listerThings.AllThings.ToList())
                {
                    if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                }
                ReturnMapToPool(map);
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"orphan projectile map {UniverseManager.MapTag(map)} reclaimed into pool after load");
            }
        }

        /// <summary>
        /// 流程清零（dev ResetObjects）：销毁全部弹体、清空图池（全部帧边界执行）。
        /// </summary>
        public void ResetFlow()
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                if (projectiles[i].adoptedShuttle) ReleaseShuttle(projectiles[i].container);
                else DestroyProjectile(projectiles[i], "reset");
            }
            beams.Clear();
            trailPuffs.Clear();
            muzzleFlashes.Clear();
            impacts.Clear();
            // Capture map identities, not pool list references. The queued projectile
            // teardown above can return active maps to those same lists before this runs.
            Map[] resetMaps = projectileMaps.ToArray();
            universe.QueueFrameAction(delegate
            {
                foreach (Map map in resetMaps)
                {
                    if (map == null) continue;
                    ForgetMap(map);
                    if (!map.Disposed) universe.Lifecycle.DeinitMap(map, false);
                }
                // Leave any maps created after ResetFlow intact.
                foreach (int key in mapPool.Keys.ToArray())
                    if (mapPool[key].Count == 0) mapPool.Remove(key);
                HostColonistBarVisibility.NotifyDockingStateChanged();
            });
        }

        // ---- 渲染（ShellRenderer 前台层；有图/无图弹体同一路径，图形取自 def）----

        /// <summary>
        /// 弹体与激光线段渲染（与幽灵船/跳帮舱标记同管线，聚焦帧投影）。返回消耗后
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
                    if (!p.adoptedShuttle)
                    {
                        Graphic graphic = p.def.graphicData?.Graphic;
                        Material material = graphic?.MatAt(Rot4.North);
                        if (material == null) continue;
                        Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                            p.container.pose.position, altitude);
                        Quaternion rot = SpaceTransform.FocusedSpriteRotation(frame, p.container.pose.angle);
                        Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, Vector3.one);
                        MapOverlayRenderer.CollectSingleQuad(graphic.MeshAt(Rot4.North), matrix, material);
                        ShellLayer.Slot s = layer.At(slot++);
                        s.layer.Emit(s);
                    }
                    if (p.props.exhaustTrail && p.ignited)
                    {
                        EnsureImpactMaterials();
                        Vector2 back = p.container.pose.velocity.sqrMagnitude > 1e-8f
                            ? -p.container.pose.velocity.normalized : Vector2.left;
                        Vector3 exhaustPos = OrbitSphere.Move(p.container.pose.position,
                            back * (p.props.exhaustSize * 0.7f));
                        Vector3 flame = SpaceTransform.FocusedDrawPosition(frame, frameMap,
                            exhaustPos, altitude + Altitudes.AltInc);
                        float exhaustHeading = p.adoptedShuttle
                            ? Mathf.Atan2(p.container.pose.velocity.y, p.container.pose.velocity.x)
                            : p.container.pose.angle;
                        Quaternion heading = SpaceTransform.FocusedMeshRotation(frame,
                            exhaustHeading);
                        EmitStretchQuad(exhaustFlashMaterials[0], flame, heading,
                            p.props.exhaustSize * 2.2f, p.props.exhaustSize * 0.85f,
                            layer, ref slot);
                        EmitStretchQuad(impactFlashMaterials[0], flame, heading,
                            p.props.exhaustSize * 1.25f, p.props.exhaustSize * 0.42f,
                            layer, ref slot);
                    }
                }
            }
            if (muzzleFlashes.Count > 0)
            {
                EnsureImpactMaterials();
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 12f;
                foreach (MuzzleFlash fx in muzzleFlashes)
                {
                    float age = Mathf.Clamp01((float)(Find.TickManager.TicksGame - fx.bornTick)
                        / MuzzleFlashLifetimeTicks);
                    Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, fx.pos, altitude);
                    Quaternion heading = SpaceTransform.FocusedMeshRotation(frame, fx.angle);
                    Material glow = impactFlashMaterials[Mathf.Min((int)(age * impactFlashMaterials.Length),
                        impactFlashMaterials.Length - 1)];
                    EmitStretchQuad(glow, pos, heading, fx.size * (1.5f + age),
                        fx.size * 0.7f, layer, ref slot);
                    if (age < 0.5f)
                        EmitStretchQuad(impactSparkMaterials[0], pos, heading,
                            fx.size * 1.15f, fx.size * 0.28f, layer, ref slot);
                }
            }
            if (beams.Count > 0)
            {
                EnsureBeamMaterials();
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 12f;
                foreach (Beam beam in beams)
                {
                    Vector3 a = SpaceTransform.FocusedDrawPosition(frame, frameMap, beam.from, altitude);
                    Vector3 b = SpaceTransform.FocusedDrawPosition(frame, frameMap, beam.to, altitude);
                    Vector3 delta = b - a;
                    float length = new Vector2(delta.x, delta.z).magnitude;
                    if (length < 0.1f) continue;
                    Vector3 mid = (a + b) * 0.5f;
                    // delta 是显示系（FocusedDrawPosition 输出）向量，atan2 得显示角；
                    // FocusedMeshRotation 只吃宇宙角，换基后再走唯一入口（聚焦船旋转
                    // 非零时旧代码会把束方向额外偏掉 pose.angle）。
                    float angle = Mathf.Atan2(delta.z, delta.x) + frame.pose.angle;
                    Quaternion rot = SpaceTransform.FocusedMeshRotation(frame, angle);
                    int age = Find.TickManager.TicksGame - beam.bornTick;
                    Material material = beamMaterials[Mathf.Clamp(age * beamMaterials.Length
                        / BeamLifetimeTicks, 0, beamMaterials.Length - 1)];
                    Matrix4x4 matrix = Matrix4x4.TRS(mid, rot,
                        new Vector3(length, 1f, beam.width));
                    MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
                    ShellLayer.Slot s = layer.At(slot++);
                    s.layer.Emit(s);
                }
            }
            if (impacts.Count > 0)
            {
                EnsureImpactMaterials();
                float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc * 11f;
                int now = Find.TickManager.TicksGame;
                // 特效四边形与聚焦船同宇宙角（与瞄准标记同语义：从聚焦船看恒水平）。
                Quaternion levelRot = SpaceTransform.FocusedMeshRotation(frame, frame.pose.angle);
                foreach (ImpactFx fx in impacts)
                {
                    int age = now - fx.bornTick;
                    if (age < 0 || age > ImpactLifetimeTicks) continue;
                    float age01 = (float)age / ImpactLifetimeTicks;
                    Vector3 pos = SpaceTransform.FocusedDrawPosition(frame, frameMap, fx.pos, altitude);
                    switch (fx.kind)
                    {
                        case ImpactFxKind.Explosion:
                        {
                            // 聚焦图已有原版 GenExplosion 闪光；额外绘制碎片和持续烟云。
                            float flashSize = Mathf.Clamp(fx.radius * 2.8f, 3f, 12f)
                                * (0.6f + 0.8f * age01);
                            if (!fx.focused && age01 < 0.55f)
                                EmitImpactQuad(impactFlashMaterials, pos, levelRot,
                                    flashSize, age01, layer, ref slot);
                            if (age01 < 0.45f)
                            {
                                for (int ray = -1; ray <= 1; ray++)
                                {
                                    Quaternion rayRot = SpaceTransform.FocusedMeshRotation(frame,
                                        fx.incomingAngle + ray * 1.05f);
                                    EmitStretchQuad(impactSparkMaterials[0], pos, rayRot,
                                        flashSize * 0.85f, 0.35f, layer, ref slot);
                                }
                            }
                            float smokeSize = Mathf.Clamp(fx.radius * 3f, 3f, 14f)
                                * (0.65f + age01);
                            EmitImpactQuad(impactSmokeMaterials, pos + new Vector3(0f, Altitudes.AltInc, 0f),
                                levelRot, smokeSize, age01, layer, ref slot);
                            break;
                        }
                        case ImpactFxKind.Shield:
                        {
                            EmitImpactQuad(impactShieldMaterials, pos, levelRot, 3.2f * (0.8f + 0.5f * age01),
                                age01, layer, ref slot);
                            break;
                        }
                        case ImpactFxKind.Energy:
                        {
                            if (age01 < 0.5f)
                                EmitImpactQuad(impactEnergyMaterials, pos, levelRot,
                                    1.6f * (0.7f + age01), age01, layer, ref slot);
                            break;
                        }
                        default:
                        {
                            // 穿甲冲击沿入射方向放出碎片；尺寸随武器效果倍率增长。
                            if (age01 < 0.5f)
                            {
                                EmitImpactQuad(impactFlashMaterials, pos, levelRot,
                                    fx.radius * (0.8f + age01), age01, layer, ref slot);
                                int rayCount = fx.radius >= 1.5f ? 1 : 0;
                                for (int ray = -rayCount; ray <= rayCount; ray++)
                                {
                                    Quaternion rayRot = SpaceTransform.FocusedMeshRotation(frame,
                                        fx.incomingAngle + ray * 0.65f);
                                    EmitStretchQuad(impactSparkMaterials[0], pos, rayRot,
                                        fx.radius * (1.6f - age01), 0.24f,
                                        layer, ref slot);
                                }
                            }
                            EmitImpactQuad(impactSmokeMaterials, pos, levelRot,
                                fx.radius * (0.55f + age01), age01, layer, ref slot);
                            break;
                        }
                    }
                }
            }
            return slot;
        }

        /// <summary>发射一枚命中特效四边形（age01 选渐隐档；仿激光束三档口径）。</summary>
        private static void EmitImpactQuad(Material[] tiers, Vector3 pos, Quaternion rot, float size,
            float age01, ShellLayer layer, ref int slot)
        {
            Material material = tiers[Mathf.Clamp(Mathf.FloorToInt(age01 * tiers.Length), 0, tiers.Length - 1)];
            Matrix4x4 matrix = Matrix4x4.TRS(pos, rot, new Vector3(size, 1f, size));
            MapOverlayRenderer.CollectSingleQuad(MeshPool.plane10, matrix, material);
            ShellLayer.Slot s = layer.At(slot++);
            s.layer.Emit(s);
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
        /// 命中特效材质（种类 × 三档透明度渐隐）。贴图与 shader 均按原版 mote def 同款
        /// 配置（发光贴图配 MoteGlow 加法混合——黑底在加法下不可见，配 Transparent 会
        /// 画出黑框；带真实 alpha 的贴图配 MoteBase 默认的 Mote shader）。材质经
        /// MaterialPool.MatFrom（原版材质构造函数）克隆自持以设 renderQueue 3202
        ///（盖过壳地形 3000 与激光束 3200）。
        /// </summary>
        private static void EnsureImpactMaterials()
        {
            if (impactFlashMaterials != null) return;
            impactFlashMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.62f, 0.3f));
            impactSparkMaterials = BuildImpactTiers("Things/Mote/SparkThrown",
                ShaderDatabase.Mote, new Color(1f, 0.95f, 0.7f));
            impactShieldMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(0.35f, 0.65f, 1f));
            impactSmokeMaterials = BuildImpactTiers("Things/Mote/Smoke",
                ShaderDatabase.Mote, new Color(0.35f, 0.35f, 0.38f));
            impactEnergyMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.23f, 0.18f));
            exhaustFlashMaterials = BuildImpactTiers("Things/Mote/BrightFlash",
                ShaderDatabase.MoteGlow, new Color(1f, 0.35f, 0.08f));
            exhaustSmokeMaterials = BuildImpactTiers("Things/Mote/Smoke",
                ShaderDatabase.Mote, new Color(0.47f, 0.46f, 0.45f));
        }

        private static Material[] BuildImpactTiers(string texturePath, Shader shader, Color color)
        {
            var tiers = new Material[3];
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

        private static void EnsureBeamMaterials()
        {
            if (beamMaterials != null) return;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var pixels = new Color32[4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            // 三档透明度近似渐隐（共享材质不逐束调 alpha；表现占位，视觉细节可后调）。
            beamMaterials = new Material[3];
            for (int i = 0; i < beamMaterials.Length; i++)
            {
                float alpha = 0.9f * (1f - (float)i / beamMaterials.Length);
                beamMaterials[i] = new Material(ShaderDatabase.Transparent)
                {
                    name = $"DeltaV_LaserBeam_{i}",
                    mainTexture = texture,
                    color = new Color(1f, 0.35f, 0.3f, alpha),
                    renderQueue = 3200,
                };
            }
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
            if (beamMaterials != null)
            {
                foreach (Material material in beamMaterials)
                {
                    if (material != null)
                    {
                        if (material.mainTexture != null) Object.Destroy(material.mainTexture);
                        Object.Destroy(material);
                    }
                }
                beamMaterials = null;
            }
            DestroyImpactTiers(ref impactFlashMaterials);
            DestroyImpactTiers(ref impactSparkMaterials);
            DestroyImpactTiers(ref impactShieldMaterials);
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

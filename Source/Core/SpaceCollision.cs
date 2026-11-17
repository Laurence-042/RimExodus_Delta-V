using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 统一容器碰撞核心（太空战架构 §3.3，唯一碰撞实现——弹丸/激光/侵入/PD 四消费方
    /// 共用，不得出现第二份求交或第二份己方排除规则）：
    /// - 碰撞面缓存：每容器「建筑/基架占用格集」（SpaceStructureRules 分类）+ 包围域，
    ///   经地图结构版本失效，弹体小图租用时单独释放缓存，
    ///   容器注销/图销毁释放（Forget/ClearCaches）；
    /// - 参与判定（tag）：主动方 = projectile tag 容器（IsActiveCollider）；被动方 =
    ///   任何有碰撞面的容器（有图即可被撞——飞船被弹体命中、PD 弹打导弹都是此路径）；
    /// - 求交（守方局部坐标，全部经 SpaceTransform 现有坐标入口）：宽相位（扫掠段 ×
    ///   守方包围域圆）→ 盾面与占用格求交 → 按束段距离结算最先接触；
    /// - 命中事件附带几何事实（一次性计算，参与方只消费不重算，§3.3）：命中 universe
    ///   点、两侧 building、入射方向、相对速度标量/矢量、入射角；
    /// - 结算 = 联合效果注册表（SpaceImpactEffects）对称分发，每容器各自后效；
    ///   一切销毁经 SweptBody.onSelfDestruct 回调由服务在帧边界执行——本核心不在
    ///   扫掠循环内注销任何容器；
    /// - tick 驱动、零事件分配（事件为复用实例）；己方排除（顶部炮塔）收口在
    ///   SweptBody.excludeContainer 单参数。
    /// </summary>
    public static partial class SpaceCollision
    {
        internal sealed class HullShape
        {
            internal readonly Vector2 center, half;
            internal readonly HashSet<IntVec3> cells;
            internal HullShape(ShipBase ship)
            {
                center = ship.HullCenter;
                half = CombatWeaponThreat.HullHalfExtents(ship);
                cells = new HashSet<IntVec3>(ship.HullCells);
            }
        }
        internal static bool HullsOverlap(HullShape a, Vector3 center, float angle,
            HullShape b, SpacePose bCenterPose)
        {
            Vector2 offset = SpaceTransform.RotateInverse(OrbitSphere.Offset(bCenterPose.position, center), bCenterPose.angle);
            float relativeAngle = SpaceTransform.WrapAngle(angle - bCenterPose.angle);
            float c = Mathf.Abs(Mathf.Cos(relativeAngle)), s = Mathf.Abs(Mathf.Sin(relativeAngle));
            if (Mathf.Abs(offset.x) > b.half.x + c * a.half.x + s * a.half.y + 1f
                || Mathf.Abs(offset.y) > b.half.y + s * a.half.x + c * a.half.y + 1f) return false;
            foreach (var cell in a.cells)
            {
                var p = b.center + offset + SpaceTransform.Rotate(new Vector2(cell.x + 0.5f, cell.z + 0.5f) - a.center, relativeAngle);
                // Conservative cell envelope; no cover roll or damage side effect.
                for (int x = Mathf.FloorToInt(p.x - 0.7f); x <= Mathf.FloorToInt(p.x + 0.7f); x++)
                for (int z = Mathf.FloorToInt(p.y - 0.7f); z <= Mathf.FloorToInt(p.y + 0.7f); z++)
                    if (b.cells.Contains(new IntVec3(x, 0, z))) return true;
            }
            return false;
        }

        /// <summary>扫掠采样步长（格）。</summary>
        private const float SampleStep = 0.5f;

        /// <summary>
        /// 一次扫掠查询（弹丸/激光/侵入共用）。from→to 是本 tick 的扫掠段（universe
        /// 坐标）：普通弹体 = 积分前后位姿差；激光 = 炮口 → 散布方向 × 最大射程。
        /// </summary>
        public sealed class SweptBody
        {
            public SpaceObject attacker;
            public ThingDef projectileDef;
            /// <summary>有图弹体的 building 实例（无图型为 null；注册表键一律用 def）。</summary>
            public Thing attackerBuilding;
            /// <summary>己方容器排除（顶部炮塔语义：弹不撞发射容器；舰中武器传 null 允许自伤）。</summary>
            public SpaceObject excludeContainer;
            internal DriveSubject selfFire;
            /// <summary>指定撞击对象；侵入式 shuttle 只与已选目标结算，其他弹体为 null。</summary>
            public SpaceObject requiredDefender;
            /// <summary>
            /// 非敌对弹体互相穿过。用于并列发射架齐射：冷发射后的转弯轨迹可以交叉，
            /// 但敌方点防射束与来袭弹仍按派系敌意正常碰撞。
            /// </summary>
            public bool ignoreNonHostileProjectiles;
            public Vector3 from;
            public Vector3 to;
            /// <summary>弹体脚印等效半径（1×1 ≈ 0.71、2×1 ≈ 1.12）。</summary>
            public float sweepRadius = 0.71f;
            /// <summary>侵入艇只以艇首一排触发破拆；0 表示普通圆形弹体。</summary>
            public float noseHalfWidth;
            public float noseForwardOffset;
            /// <summary>伤害归因（TakeDamage/GenExplosion 的 instigator；敌意与战斗日志走原版链路）。</summary>
            public Thing launcher;
            public CompSpaceWeapon weapon;
            /// <summary>「自毁」后效执行回调（弹体服务闭包：标记死亡 + 帧边界销毁/归池）。</summary>
            public System.Action onSelfDestruct;
            /// <summary>结算死亡标记：跳过本弹体的后续事件（§3.5）。</summary>
            public bool dead;
            /// <summary>本 tick 是否产生命中（激光线段视觉取命中点而非全射程）。</summary>
            public bool resolvedHit;
            public Vector3 resolvedHitUniverse;
            /// <summary>命中守方容器（壳上命中特效的归属与聚焦判定）。</summary>
            public SpaceObject resolvedHitDefender;
            /// <summary>命中点守方 Map 连续局部坐标（聚焦图 InMap 音效定位用）。</summary>
            public Vector2 resolvedHitLocal;
            public IntVec3 resolvedHitCell;
            /// <summary>守方后效为爆炸（爆炸半径见 resolvedHitRadius）。</summary>
            public bool resolvedHitExplosive;
            /// <summary>护盾弧拦截命中。</summary>
            public bool resolvedHitShield;
            /// <summary>守方爆炸后效半径（非爆炸 = 0；特效尺寸与音效选型）。</summary>
            public float resolvedHitRadius;
            /// <summary>发射伤害倍率乘剩余冲击比例；随事件进入结算。</summary>
            public float damageScale = 1f;
            public float armorPenetrationScale = 1f;
            public float mass = 1f, beamEnergyScale = 1f;
            /// <summary>「放置」后效回调（侵入 shuttle §7.5）：shuttle 服务闭包——
            /// 舱体放置在撞击位内侧偏移 + 容器销毁（帧边界）。</summary>
            public System.Action<SpaceObject, Vector3, Vector2> onPlace;
            /// <summary>本段击破后的续飞；只消耗 damageScale，不重复按速度或穿甲扣预算。</summary>
            public bool continued;
            public bool didContinue;
            public Vector3 continuePoint;
            public bool redirected;
            public bool didRedirect;
            public bool reflectedByShield;
            public Vector3 redirectPoint;
            public Vector2 redirectDirection;
            public float redirectRemaining;
            public System.Action<Vector3, Vector3, SpaceObject> onShieldRedirect;
            public System.Action<Vector3, SpaceObject, Vector2, Vector2, float, int> onShieldShatter;
            // Consumed synchronously after each resolved contact, before the next sweep.
            public System.Action<VisualContact> onVisualContact;
        }

        /// <summary>Read-only presentation snapshot; geometry and outcomes come from the resolving contact.</summary>
        public struct VisualContact
        {
            public SpaceObject defender;
            public DriveSubject defenderSubject;
            public ThingDef projectileDef;
            public Vector3 position, pathFrom;
            public Vector2 localPoint, localDirection, localNormal;
            public float damageScale, explosionRadius;
            public bool explosive, nativeExplosion, destroyed, penetrated, missileDestroyed, shieldBroken, shieldEmergency;
            public SpaceShieldMode? shieldMode;
            public Vector2 shieldCenter;
            public Vector2 lineStart, lineEnd;
            public float shieldRadius;
        }

        /// <summary>命中事件（几何事实一次性计算；池化复用，调用方不得持有引用）。</summary>
        public sealed class ImpactEvent
        {
            public SpaceObject attacker;
            public ThingDef projectileDef;
            public Thing launcher;
            public CompSpaceWeapon weapon;
            public SpaceObject defender;
            public Thing defenderBuilding;
            public bool defenderIsSubstructure;
            public Vector3 hitUniverse;
            public Vector2 hitDefenderLocal;
            /// <summary>碰撞占用集选中的实际格；扫掠采样点可能位于相邻格。</summary>
            public IntVec3 hitCell;
            public Vector2 incidentDir;
            public Vector2 relativeVelocity;
            public float relativeSpeed;
            public float incidenceAngleRad;
            /// <summary>发射伤害倍率乘剩余冲击比例。</summary>
            public float damageScale = 1f;
            public float armorPenetrationScale = 1f;
            public float mass = 1f, beamEnergyScale = 1f;
        }

        private static readonly ImpactEvent SharedEvent = new ImpactEvent();

        // ---- 碰撞面缓存 ----

        /// <summary>
        /// 一段护盾弧（地图帧几何 + 原版容量 comp 引用；段5）。Active/容量按使用时
        /// 实读（供电/EMP/击破走原版状态机）；几何随容器姿态旋转（地图帧即船体帧）。
        /// </summary>
        public sealed class ShieldArc
        {
            public Vector2 center;
            public float radius;
            public float facing;
            public float span;
            public Building building;
            public CompProjectileInterceptor interceptor;
        }

        private sealed partial class Surface
        {
            public byte[] kinds;
            public Thing[] buildings;
            public bool[] foundations;
            public Vector2 boundsCenter;
            public float boundsRadius;
            public bool anySolid;
            public int structureVersion = -1;
            public float hullRadius;
            public SpaceObject owner;
            public bool incremental;
            public bool shieldsDirty;
            public readonly List<ShieldArc> arcs = new List<ShieldArc>();
            public readonly List<CompSpaceShield> lineShields = new List<CompSpaceShield>();
        }

        private static readonly Dictionary<Map, Surface> Caches = new Dictionary<Map, Surface>();

        /// <summary>读档 / 场景重置时清空（可重建缓存纪律，AGENTS §15）。</summary>
        public static void ClearCaches()
        {
            Caches.Clear();
        }

        /// <summary>图销毁时释放（SpaceLifecycle.Forget 调用）。</summary>
        public static void Forget(Map map)
        {
            if (map != null) Caches.Remove(map);
        }

        public static void InvalidateShield(Map map)
        {
            if (map == null) return;
            if (Caches.TryGetValue(map, out Surface surface)) surface.shieldsDirty = true;
            Current.Game?.GetComponent<UniverseManager>()?.ForgetCombatProtection(map);
        }

        /// <summary>容器的碰撞面（需要炮口外推起点的发射侧也可查询）。</summary>
        private static Surface SurfaceFor(SpaceObject obj)
        {
            Map map = obj.map;
            if (!Caches.TryGetValue(map, out Surface surface))
            {
                surface = new Surface();
                Caches[map] = surface;
            }
            int version = SubstructureOwnership.StructureVersion(map);
            if (surface.owner != obj || surface.kinds == null || surface.structureVersion != version)
            {
                var changed = SubstructureOwnership.TakeCollisionChanges(map, out bool full);
                if (!full && surface.owner == obj && surface.incremental && !obj.IsScattered)
                    surface.RefreshBlocks(map, changed);
                else
                    RebuildHull(obj, surface);
                surface.UpdateHullBounds(map);
                surface.structureVersion = version;
                if (surface.shieldsDirty) RebuildShields(map, surface);
                UpdateShieldBounds(surface);
            }
            else if (surface.shieldsDirty)
            {
                RebuildShields(map, surface);
                UpdateShieldBounds(surface);
            }
            return surface;
        }

        /// <summary>容器的等效碰撞半径（包围域；发射侧外推炮口起点用）。</summary>
        public static float BoundsRadiusOf(SpaceObject obj)
        {
            if (obj?.Payload is AbstractMissile { InterceptionBody: not null } missile)
                return missile.HalfFootprint.magnitude;
            if (obj?.map == null) return 0f;
            return SurfaceFor(obj).boundsRadius;
        }

        /// <summary>以容器原点为圆心的保守包围半径，复用同一碰撞面，供站位留空。</summary>
        internal static float OriginBoundsRadiusOf(SpaceObject obj)
        {
            if (obj?.map == null) return 0f;
            Surface surface = SurfaceFor(obj);
            return surface.boundsRadius + SpaceTransform.MapToShipLocal(obj.map,
                surface.boundsCenter).magnitude;
        }

        /// <summary>
        /// 调试显示读取碰撞系统实际正在使用的缓存，而不是另算一份近似碰撞体。
        /// 输出格为对象 Map 坐标；调用方经 SpaceTransform 投影到当前聚焦参考系。
        /// </summary>
        public static bool CollectDebugSurface(SpaceObject obj, List<IntVec3> occupied,
            out Vector2 boundsCenter, out float boundsRadius)
        {
            occupied.Clear();
            boundsCenter = Vector2.zero;
            boundsRadius = 0f;
            if (obj?.map == null || obj.map.Disposed) return false;
            Surface surface = SurfaceFor(obj);
            boundsCenter = surface.boundsCenter;
            boundsRadius = surface.boundsRadius;
            if (surface.kinds == null) return false;
            occupied.AddRange(surface.OccupiedCells());
            return occupied.Count > 0;
        }

        public readonly struct ShieldVisualSegment
        {
            public readonly Vector3 from;
            public readonly Vector3 to;
            public readonly SpaceShieldMode mode;

            public ShieldVisualSegment(Vector3 from, Vector3 to, SpaceShieldMode mode)
            {
                this.from = from;
                this.to = to;
                this.mode = mode;
            }
        }

        /// <summary>显示直接读取碰撞缓存中的盾实例及同一 GetSegment 几何。</summary>
        public static void CollectVisibleShieldSegments(SpaceObject obj,
            List<ShieldVisualSegment> output)
        {
            if (obj?.map == null || obj.map.Disposed) return;
            Surface surface = SurfaceFor(obj);
            foreach (CompSpaceShield shield in surface.lineShields)
            {
                if (shield.State != SpaceShieldState.LineActive) continue;
                shield.GetSegment(out Vector2 a, out Vector2 b, out _);
                output.Add(new ShieldVisualSegment(
                    SpaceTransform.MapPointToUniverse(obj, obj.map, a),
                    SpaceTransform.MapPointToUniverse(obj, obj.map, b), shield.mode));
            }
        }

        private static void RebuildHull(SpaceObject obj, Surface surface)
        {
            Map map = obj.map;
            int cells = map.cellIndices.NumGridCells;
            if (surface.kinds == null || surface.kinds.Length != cells)
            {
                surface.kinds = new byte[cells];
                surface.buildings = new Thing[cells];
                surface.foundations = new bool[cells];
            }
            else
            {
                // 重建是完整快照；不清空会让已摧毁建筑永久残留在窄相位占用格中。
                System.Array.Clear(surface.kinds, 0, surface.kinds.Length);
                System.Array.Clear(surface.buildings, 0, surface.buildings.Length);
                System.Array.Clear(surface.foundations, 0, surface.foundations.Length);
            }
            surface.ClearOccupancy(map);
            foreach (SpaceCollisionCell geometry in obj.CollisionCells)
            {
                if (geometry.kind != SpaceCellKind.Empty)
                    surface.SetCell(map, geometry.cell, geometry.kind, geometry.building);
            }
            surface.owner = obj;
            // The default map provider classifies the entire map. Custom providers
            // may omit valid cells and must be re-enumerated on invalidation.
            surface.incremental = obj is MapSpaceObject
                && obj.GetType().GetProperty(nameof(SpaceObject.CollisionCells)).DeclaringType == typeof(MapSpaceObject);
            surface.shieldsDirty = true;
        }

        private static void RebuildShields(Map map, Surface surface)
        {
            // 自建发生器在太空模式使用线盾，在地面模式与原版发生器一样
            // 使用拦截器的逐实例半径和状态。两种形态共用此碰撞面。
            surface.arcs.Clear();
            surface.lineShields.Clear();
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is not Building building || building.Destroyed) continue;
                CompProjectileInterceptor interceptor = building.TryGetComp<CompProjectileInterceptor>();
                if (interceptor == null) continue;
                CompSpaceShield shield = building.TryGetComp<CompSpaceShield>();
                if (shield != null && shield.mode != SpaceShieldMode.Guard)
                {
                    surface.lineShields.Add(shield);
                    continue;
                }
                var arc = new ShieldArc
                {
                    center = shield?.GuardCenter ?? new Vector2(building.Position.x + building.def.Size.x * 0.5f,
                        building.Position.z + building.def.Size.z * 0.5f),
                    radius = interceptor.Props.radius,
                    facing = 0f,
                    span = Mathf.PI * 2f,
                    building = building,
                    interceptor = interceptor,
                };
                surface.arcs.Add(arc);
            }
            surface.shieldsDirty = false;
        }

        private static void UpdateShieldBounds(Surface surface)
        {
            surface.boundsRadius = surface.hullRadius;
            float arcBoost = 0f;
            foreach (var shield in surface.lineShields)
            {
                shield.GetSegment(out Vector2 a, out Vector2 b, out _);
                arcBoost = Mathf.Max(arcBoost, (a - surface.boundsCenter).magnitude
                    - surface.boundsRadius, (b - surface.boundsCenter).magnitude - surface.boundsRadius);
            }
            foreach (var arc in surface.arcs)
                arcBoost = Mathf.Max(arcBoost,
                    (arc.center - surface.boundsCenter).magnitude + arc.radius - surface.boundsRadius);
            if (arcBoost > 0f) surface.boundsRadius += arcBoost;
        }

        // ---- 扫掠与结算 ----

        /// <summary>
        /// 对全部在册弹体扫掠段求交并结算（GameComponentTick 容器积分之后调用——
        /// 先动后撞，消除一拍滞后）。注销/销毁不在本循环内发生（回调经帧边界）。
        /// </summary>
        public static void SweepAndResolve(UniverseManager universe, List<SweptBody> bodies)
        {
            if (universe == null || bodies.Count == 0) return;
            List<SpaceObject> objects = universe.objects;
            for (int b = 0; b < bodies.Count; b++)
            {
                SweptBody body = bodies[b];
                if (body.dead || body.attacker == null
                    || body.attacker.Payload is AbstractMissile { InterceptionBody: null }) continue;
                Dictionary<SpaceObject, PassedContacts> passed = null;
                for (int bounce = 0; bounce < 128 && !body.dead; bounce++)
                {
                    if (!TryFindFirstContact(universe, objects, body, ref passed, out SweepContact contact)) break;

                    body.redirected = false;
                    body.continued = false;
                    ResolveContact(body, contact);
                    if (body.dead) break;
                    if (body.continued)
                    {
                        // 不向前跳过距离：同心盾、贴合盾面与紧邻建筑必须逐个结算。
                        body.from = body.continuePoint;
                        body.didContinue = true;
                    }
                    else if (body.redirected)
                    {
                        body.onShieldRedirect?.Invoke(body.from, body.redirectPoint, contact.defender);
                        body.from = OrbitSphere.Move(body.redirectPoint, body.redirectDirection * 0.03f);
                        body.to = OrbitSphere.Move(body.from, body.redirectDirection * body.redirectRemaining);
                        body.didRedirect = true;
                        body.excludeContainer = null;
                        passed?.Clear();
                    }
                    else break;
                    if (bounce == 127) body.to = body.from;
                }
            }
        }

        private static bool TryFindFirstContact(UniverseManager universe, List<SpaceObject> objects,
            SweptBody body, ref Dictionary<SpaceObject, PassedContacts> passed, out SweepContact first)
        {
            SpaceObject source = body.selfFire.key > 0 ? body.excludeContainer : null;
            while (true)
            {
                first = default;
                float nearest = float.PositiveInfinity;
                for (int d = source == null ? 0 : -1; d < objects.Count; d++)
                {
                    bool sourceStructure = d < 0;
                    SpaceObject defender = sourceStructure ? source : objects[d];
                    if (defender.IsScattered) continue;
                    if (defender == body.attacker || !sourceStructure && defender == body.excludeContainer
                        || (body.requiredDefender != null && defender != body.requiredDefender)) continue;
                    if (body.ignoreNonHostileProjectiles && !body.didRedirect && defender.IsActiveCollider
                        && !AreHostile(body.attacker.faction, defender.faction)) continue;
                    Map map = defender.map;
                    PassedContacts missed = null;
                    passed?.TryGetValue(defender, out missed);
                    SweepContact candidate;
                    if (map == null)
                    {
                        if (!TrySweepMissile(body, defender, missed?.buildings, out candidate)) continue;
                    }
                    else if (map.Disposed || universe.Generation?.IsGenerating(map) == true
                        || !TrySweepContact(body, defender, map, sourceStructure, missed, out candidate)) continue;
                    float distance = OrbitSphere.ArcCells(body.from, candidate.universePoint);
                    if (first.defender != null && (distance > nearest
                        || (distance == nearest && defender.objectId >= first.defender.objectId)))
                        continue;
                    nearest = distance;
                    first = candidate;
                }
                if (first.defender == null) return false;

                RayContact ray = first.ray;
                if (ray.kind == SpaceCellKind.Empty
                    || SpaceImpactEffects.HitTest(body.projectileDef, ray.kind, ray.building, first.effect))
                    return true;

                if (first.props?.continuousBeam != true && DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"pass-through: {body.projectileDef?.defName} past " +
                        (ray.building?.def.defName ?? "substructure") + $" at {ray.cell} of '{first.defender.label}'");

                passed ??= new Dictionary<SpaceObject, PassedContacts>();
                if (!passed.TryGetValue(first.defender, out PassedContacts rejected))
                    passed.Add(first.defender, rejected = new PassedContacts());
                if (ray.building != null)
                    (rejected.buildings ??= new HashSet<Thing>()).Add(ray.building);
                else
                    (rejected.foundations ??= new HashSet<IntVec3>()).Add(ray.cell);
            }
        }

        private static bool AreHostile(Faction first, Faction second)
        {
            if (first == null || second == null) return first != second;
            return first.HostileTo(second) || second.HostileTo(first);
        }

        private sealed class PassedContacts
        {
            internal HashSet<Thing> buildings;
            internal HashSet<IntVec3> foundations;
        }

        private struct SweepContact
        {
            internal SpaceObject defender;
            internal RayContact ray;
            internal Vector3 universePoint;
            internal Vector2 direction, lineStart, lineEnd;
            internal float remainingLength;
            internal SpaceImpactEffectDef effect;
            internal CompProperties_SpaceProjectile props;
        }

        private static bool TrySweepMissile(SweptBody body, SpaceObject defender, HashSet<Thing> passed,
            out SweepContact contact)
        {
            contact = default;
            if (defender.Payload is not AbstractMissile { InterceptionBody: not null } missile
                || passed?.Contains(missile.InterceptionBody) == true)
                return false;
            Vector2 from = SpaceTransform.UniverseToShipLocal(defender.pose, body.from);
            Vector2 delta = SpaceTransform.UniverseToShipLocal(defender.pose, body.to) - from;
            float length = delta.magnitude;
            if (length < 1e-6f) return false;
            Vector2 direction = delta / length;
            if (!TryMissileFootprint(missile, from, direction, length, body.sweepRadius, out RayContact ray))
                return false;
            contact = new SweepContact
            {
                defender = defender, ray = ray, direction = direction,
                universePoint = SpaceTransform.ShipLocalToUniverse(defender.pose, ray.point),
                effect = SpaceImpactEffects.Resolve(body.projectileDef, missile.InterceptionBody.def),
                props = body.projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()
            };
            return true;
        }

        internal static bool TryMissileFootprint(AbstractMissile missile, Vector2 from, Vector2 direction,
            float length, float radius, out RayContact ray)
        {
            ray = default;
            if (missile.InterceptionBody == null || length <= 0f || direction.sqrMagnitude < 1e-8f) return false;
            Vector2 half = missile.HalfFootprint;
            float near = 0f, far = length;
            Vector2 extent = half + new Vector2(radius, radius);
            if (!ClipAxis(from.x, direction.x, extent.x, ref near, ref far)
                || !ClipAxis(from.y, direction.y, extent.y, ref near, ref far)) return false;
            float along = radius > 0f ? Mathf.Floor(near / SampleStep) * SampleStep : near;
            float end = Mathf.Min(length, Mathf.Ceil(far / SampleStep) * SampleStep);
            for (;; along = Mathf.Min(along + SampleStep, end))
            {
                Vector2 point = from + direction * along;
                Vector2 nearest = new Vector2(Mathf.Clamp(point.x, -half.x, half.x),
                    Mathf.Clamp(point.y, -half.y, half.y));
                if (radius == 0f || (point - nearest).sqrMagnitude <= radius * radius)
                {
                    Vector2 normal = Mathf.Abs(direction.x) >= Mathf.Abs(direction.y)
                        ? new Vector2(-Mathf.Sign(direction.x), 0f) : new Vector2(0f, -Mathf.Sign(direction.y));
                    ray = new RayContact(point, normal, IntVec3.Invalid, SpaceCellKind.Building, missile.InterceptionBody);
                    return true;
                }
                if (along >= end) return false;
            }
        }

        private static bool ClipAxis(float origin, float direction, float extent, ref float near, ref float far)
        {
            if (Mathf.Abs(direction) < 1e-6f) return Mathf.Abs(origin) <= extent;
            float a = (-extent - origin) / direction;
            float b = (extent - origin) / direction;
            near = Mathf.Max(near, Mathf.Min(a, b));
            far = Mathf.Min(far, Mathf.Max(a, b));
            return near <= far;
        }

        private static bool TrySweepContact(SweptBody body, SpaceObject defender, Map defMap, bool sourceStructure,
            PassedContacts missed, out SweepContact contact)
        {
            contact = default;
            Surface surface = SurfaceFor(defender);
            if (!surface.anySolid) return false;
            Vector2 fromLocal = SpaceTransform.UniverseToMapPoint(defender, defMap, body.from);
            Vector2 toLocal = SpaceTransform.UniverseToMapPoint(defender, defMap, body.to);
            Vector2 delta = toLocal - fromLocal;
            float length = delta.magnitude;
            if (length < 1e-6f) return false;
            Vector2 dir = delta / length;

            Vector2 toCenter = surface.boundsCenter - fromLocal;
            float projection = Vector2.Dot(toCenter, dir);
            float distanceToCenter = (toCenter - dir * Mathf.Clamp(projection, 0f, length)).magnitude;
            if (distanceToCenter > surface.boundsRadius + body.sweepRadius + 0.5f) return false;

            float sampleLength = length;
            var projectileProps = body.projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            RayContact ray = default;
            Vector2 lineStart = default, lineEnd = default;
            if (!sourceStructure && projectileProps?.ignoresShields != true)
            {
                if (TryFindLineHit(surface, fromLocal, dir, length, projectileProps,
                    out CompSpaceShield line, out Vector2 linePoint, out Vector2 lineNormal,
                    out float lineAlong, out lineStart, out lineEnd))
                {
                    sampleLength = lineAlong;
                    ray = new RayContact(linePoint, lineNormal, IntVec3.Invalid, SpaceCellKind.Empty, line.parent, line: line);
                }
                if (TryFindArcHit(surface, fromLocal, dir, length, out ShieldArc arc, out Vector2 point, out float arcAlong)
                    && arcAlong < sampleLength)
                {
                    sampleLength = arcAlong;
                    ray = new RayContact(point, (point - arc.center).normalized, IntVec3.Invalid,
                        SpaceCellKind.Empty, arc.building, guard: arc);
                }
            }

            Vector2 footprintDirection = dir;
            if (body.noseHalfWidth > 0f && body.attackerBuilding != null)
            {
                Vector2 forward = SpaceTransform.Rotate(Vector2.right,
                    ShipOrientation.AngleFromRot4(body.attackerBuilding.Rotation));
                Vector3 nose = SpaceTransform.ShipLocalToUniverse(body.attacker, forward);
                footprintDirection = (SpaceTransform.UniverseToMapPoint(defender, defMap, nose)
                    - SpaceTransform.UniverseToMapPoint(defender, defMap, body.attacker.pose.position)).normalized;
            }
            System.Predicate<IntVec3> scope = null;
            if (sourceStructure)
            {
                var ownership = SubstructureOwnership.TryGet(defMap);
                if (ownership == null || body.selfFire.objectId != defender.objectId) return false;
                var muzzleFootprint = body.launcher?.Map == defMap ? body.launcher.OccupiedRect() : CellRect.Empty;
                scope = cell => !muzzleFootprint.Contains(cell) && ownership.OwnerAt(cell) == body.selfFire.key;
            }
            float near = Mathf.Max(0f, projection - surface.boundsRadius - body.sweepRadius - SampleStep);
            if (TryFindHullContact(body, defMap, surface, fromLocal, dir, footprintDirection,
                near, sampleLength, missed?.buildings, missed?.foundations, scope, false, out RayContact hull))
                ray = hull;
            if (ray.kind == SpaceCellKind.Empty && ray.line == null && ray.guard == null) return false;

            contact = new SweepContact
            {
                defender = defender,
                ray = ray,
                universePoint = SpaceTransform.MapPointToUniverse(defender, defMap, ray.point),
                direction = dir,
                lineStart = lineStart,
                lineEnd = lineEnd,
                remainingLength = length - sampleLength,
                effect = ray.line == null ? SpaceImpactEffects.Resolve(body.projectileDef, ray.building?.def,
                    shieldSurface: ray.guard != null) : null,
                props = projectileProps
            };
            return true;
        }

        private static void ResolveContact(SweptBody body, SweepContact contact)
        {
            SpaceObject defender = contact.defender;
            Map defMap = defender.map;
            RayContact ray = contact.ray;
            Vector2 dir = contact.direction;
            Vector2 p = ray.point;
            var projectileProps = contact.props;
            if (ray.kind != SpaceCellKind.Empty)
            {
                IntVec3 bestCell = ray.cell;
                SpaceCellKind bestKind = ray.kind;
                Thing bestThing = ray.building;
                SpaceImpactEffectDef effect = contact.effect;
                ImpactEvent evt = SharedEvent;
                evt.attacker = body.attacker;
                evt.projectileDef = body.projectileDef;
                evt.launcher = body.launcher;
                evt.weapon = body.weapon;
                evt.defender = defender;
                evt.defenderBuilding = bestThing;
                evt.defenderIsSubstructure = bestKind == SpaceCellKind.Substructure;
                evt.hitDefenderLocal = p;
                evt.hitCell = bestCell;
                evt.hitUniverse = contact.universePoint;
                evt.incidentDir = dir;
                evt.relativeVelocity = SpaceTransform.RelativeVelocityAtContact(body.attacker.pose, defender.pose, evt.hitUniverse);
                evt.relativeSpeed = evt.relativeVelocity.magnitude;
                if (SpaceShieldProjectileBehavior.IsKinetic(projectileProps) && evt.relativeSpeed > .0001f)
                    evt.incidentDir = defMap != null
                        ? SpaceTransform.UniverseTangentToMapDirection(defender, defMap, evt.hitUniverse, evt.relativeVelocity)
                        : SpaceTransform.Rotate(SpaceTransform.TransportTangent(evt.relativeVelocity,
                            evt.hitUniverse, defender.pose.position), -defender.pose.angle).normalized;
                evt.damageScale = body.damageScale;
                evt.armorPenetrationScale = body.armorPenetrationScale;
                evt.mass = body.mass; evt.beamEnergyScale = body.beamEnergyScale;

                // 入射角：按扫掠进入面推导法线（法线指向来侧；基架格同规则，不设第二套约定）。
                Vector2 normal = Mathf.Abs(evt.incidentDir.x) >= Mathf.Abs(evt.incidentDir.y)
                    ? new Vector2(-Mathf.Sign(evt.incidentDir.x), 0f)
                    : new Vector2(0f, -Mathf.Sign(evt.incidentDir.y));
                evt.incidenceAngleRad = SpaceTransform.IncidenceAngle(evt.incidentDir, normal);

                // 对称结算（§3.5）：每个参与容器各自解析自己的后效。
                body.resolvedHit = true;
                body.resolvedHitUniverse = evt.hitUniverse;
                body.resolvedHitDefender = defender;
                body.resolvedHitLocal = p;
                body.resolvedHitCell = bestCell;
                var defenderPart = effect?.defenderEffect;
                body.resolvedHitExplosive = defenderPart != null
                    && defenderPart.kind == ImpactEffectKind.DamageMap
                    && (defenderPart.explosionRadius > 0f
                        || defenderPart.penetrationLayers?.Count > 0
                        && defenderPart.penetrationLayers[0].halfWidth > 0);
                body.resolvedHitShield = false;
                body.resolvedHitRadius = body.resolvedHitExplosive
                    ? Mathf.Max(defenderPart.explosionRadius,
                        defenderPart.penetrationLayers?.Count > 0
                            ? defenderPart.penetrationLayers[0].halfWidth : 0f) : 0f;
                if (projectileProps?.continuousBeam != true && DeltaVLog.Enabled(DeltaVLogModule.Combat)) DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"impact: {body.projectileDef?.defName} -> " +
                    (evt.defenderIsSubstructure
                        ? $"substructure {bestCell}"
                        : $"'{evt.defenderBuilding?.def.defName ?? "rock"}' @{bestCell}") +
                    $" on '{defender.label}' (v_rel {evt.relativeSpeed:F1}, incidence " +
                    $"{evt.incidenceAngleRad * Mathf.Rad2Deg:F0}°)");
                float durabilityScale = SpaceImpactEffects.DurabilityScale(effect, evt.defenderBuilding);
                // 毁伤会清掉基架归属，声音所需身份必须在结算前捕获。
                ShipBase struckShip = defMap != null && bestThing != null ? ShipRegistry.ShipOf(bestThing) : ShipBase.Invalid;
                DriveSubject struckSubject = struckShip.IsValid ? struckShip.Identity
                    : new DriveSubject(defender.objectId, defMap?.GetComponent<SubstructureOwnership>()?.OwnerAt(bestCell) ?? 0);
                float remaining = SpaceImpactEffects.ApplyToDefender(evt, effect?.defenderEffect, durabilityScale);
                body.onVisualContact?.Invoke(new VisualContact
                {
                    defender = defender, projectileDef = body.projectileDef,
                    defenderSubject = struckSubject,
                    position = evt.hitUniverse, pathFrom = body.from,
                    localPoint = p, localDirection = dir, localNormal = normal,
                    damageScale = body.damageScale, explosive = body.resolvedHitExplosive,
                    nativeExplosion = defMap != null && defenderPart?.explosionRadius > 0f
                        && !(defenderPart.penetrationLayers?.Count > 0),
                    explosionRadius = body.resolvedHitRadius, penetrated = remaining > 0f,
                    destroyed = bestThing?.Destroyed == true
                        || bestKind == SpaceCellKind.Substructure && !SubstructureOwnership.HasFoundation(defMap, bestCell),
                    missileDestroyed = bestThing?.Destroyed == true
                        && bestThing.def.GetCompProperties<CompProperties_SpaceProjectile>()?.guided == true
                });
                if (ContinueAfterImpact(body, remaining, evt.hitUniverse)) return;
                ApplyAttackerOutcome(body, effect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                return;
            }

            if (ray.line is CompSpaceShield lineShield)
            {
                Vector3 hit = contact.universePoint;
                Vector2 linePoint = ray.point;
                Vector2 lineNormal = ray.normal;
                if (lineShield.mode == SpaceShieldMode.Kinetic)
                {
                    Vector2 relative = SpaceTransform.RelativeVelocityAtContact(body.attacker.pose, defender.pose, hit);
                    Vector2 worldNormal = SpaceTransform.MapDirectionToUniverseTangent(defender, defMap, linePoint, lineNormal);
                    float impulse = lineShield.ConsumeKineticImpulse(body.mass, Vector2.Dot(relative, worldNormal),
                        SpaceKinetics.ShieldImpulse(lineShield.kineticStrengthPercent), projectileProps.shieldFragmentCost);
                    if (impulse <= 0f)
                    {
                        body.continued = true;
                        body.continuePoint = hit;
                        return;
                    }
                    body.resolvedHit = true;
                    body.resolvedHitShield = true;
                    body.resolvedHitUniverse = hit;
                    body.resolvedHitDefender = defender;
                    body.resolvedHitLocal = linePoint;
                    body.onVisualContact?.Invoke(new VisualContact
                    {
                        defender = defender, projectileDef = body.projectileDef, position = hit, pathFrom = body.from,
                        localPoint = linePoint, localDirection = dir, localNormal = lineNormal,
                        damageScale = body.damageScale, shieldMode = SpaceShieldMode.Kinetic,
                        lineStart = contact.lineStart, lineEnd = contact.lineEnd
                    });
                    int fragmentCount = SpaceKinetics.ShieldFragmentCount(body.mass, relative, worldNormal, impulse, projectileProps);
                    if (fragmentCount > 0)
                    {
                        body.onShieldShatter?.Invoke(hit, defender, relative, worldNormal, impulse, fragmentCount);
                        body.dead = true;
                        body.onSelfDestruct?.Invoke();
                        return;
                    }
                    // Surviving rounds retain their identity, lifetime, mass and remaining damage scale.
                    Vector2 outgoingRelative = SpaceKinetics.ApplyShieldImpulse(body.mass, relative, worldNormal, impulse);
                    body.reflectedByShield |= Vector2.Dot(outgoingRelative, worldNormal) > 0f;
                    Vector2 outgoingWorld = outgoingRelative + SpaceTransform.VelocityAtUniversePoint(defender.pose, hit);
                    float beforeSpeed = body.attacker.pose.velocity.magnitude;
                    body.attacker.pose.velocity = SpaceTransform.TransportTangent(outgoingWorld, hit, body.attacker.pose.position);
                    body.redirected = true;
                    body.redirectPoint = hit;
                    body.redirectDirection = outgoingWorld.sqrMagnitude > 1e-8f ? outgoingWorld.normalized : worldNormal;
                    body.redirectRemaining = beforeSpeed > .0001f ? contact.remainingLength * outgoingWorld.magnitude / beforeSpeed : 0f;
                    return;
                }
                int strength = lineShield.electromagneticStrengthPercent;
                SpaceShieldRefraction.Plan plan = SpaceShieldRefraction.Calculate(dir, lineNormal, strength);
                float cost = projectileProps.shieldLaserTurnEnergy * body.beamEnergyScale * plan.addedDeg / 180f;
                float funded = lineShield.ConsumePulseFraction(cost);
                Vector2 outgoing = plan.Direction(funded);
                body.onVisualContact?.Invoke(new VisualContact
                {
                    defender = defender, projectileDef = body.projectileDef, position = hit, pathFrom = body.from,
                    localPoint = linePoint, localDirection = dir, localNormal = lineNormal,
                    damageScale = body.damageScale, shieldMode = SpaceShieldMode.Electromagnetic,
                    lineStart = contact.lineStart, lineEnd = contact.lineEnd
                });
                body.redirected = true;
                body.redirectPoint = hit;
                body.redirectDirection = SpaceTransform.MapDirectionToUniverseTangent(defender,
                    defMap, linePoint, outgoing);
                body.redirectRemaining = contact.remainingLength;
                return;
            }

            // 弧事件（格采样未先命中）：护盾吸收——守方容量消耗 + 攻方自毁。
            if (ray.guard is ShieldArc shieldArc)
            {
                ImpactEvent evt = SharedEvent;
                evt.attacker = body.attacker;
                evt.projectileDef = body.projectileDef;
                evt.launcher = body.launcher;
                evt.weapon = body.weapon;
                evt.defender = defender;
                evt.defenderBuilding = shieldArc.building;
                evt.defenderIsSubstructure = false;
                Vector2 shieldPoint = ray.point;
                SpaceImpactEffectDef shieldEffect = contact.effect;
                evt.hitDefenderLocal = shieldPoint;
                evt.hitCell = IntVec3.Invalid;
                evt.hitUniverse = contact.universePoint;
                evt.incidentDir = dir;
                evt.relativeVelocity = SpaceTransform.RelativeVelocityAtContact(body.attacker.pose, defender.pose, evt.hitUniverse);
                evt.relativeSpeed = evt.relativeVelocity.magnitude;
                if (SpaceShieldProjectileBehavior.IsKinetic(projectileProps) && evt.relativeSpeed > .0001f)
                    evt.incidentDir = SpaceTransform.UniverseTangentToMapDirection(defender, defMap, evt.hitUniverse, evt.relativeVelocity);
                evt.incidenceAngleRad = SpaceTransform.IncidenceAngle(evt.incidentDir, ray.normal);
                evt.damageScale = body.damageScale;
                evt.armorPenetrationScale = body.armorPenetrationScale;
                evt.mass = body.mass; evt.beamEnergyScale = body.beamEnergyScale;
                body.resolvedHit = true;
                body.resolvedHitUniverse = evt.hitUniverse;
                body.resolvedHitDefender = defender;
                body.resolvedHitLocal = shieldPoint;
                body.resolvedHitCell = IntVec3.Invalid;
                body.resolvedHitExplosive = false;
                body.resolvedHitShield = true;
                body.resolvedHitRadius = 0f;
                float capacityBefore = CompSpaceShield.AvailableGuardCapacity(shieldArc.interceptor);
                var guardShield = shieldArc.building.TryGetComp<CompSpaceShield>();
                bool emergencyBefore = guardShield != null && !guardShield.UsesRegular;
                float residual = SpaceImpactEffects.ApplyToDefender(evt, shieldEffect?.defenderEffect);
                SpaceImpactEffects.ApplyToDefender(evt, shieldEffect?.shieldSurfaceEffect);
                if (shieldEffect?.shieldSurfaceEffect?.kind == ImpactEffectKind.DamageMap)
                {
                    body.resolvedHitExplosive = true;
                    body.resolvedHitRadius = shieldEffect.shieldSurfaceEffect.explosionRadius;
                }
                body.onVisualContact?.Invoke(new VisualContact
                {
                    defender = defender, projectileDef = body.projectileDef, position = evt.hitUniverse, pathFrom = body.from,
                    localPoint = shieldPoint, localDirection = dir,
                    localNormal = ray.normal,
                    damageScale = body.damageScale, explosive = body.resolvedHitExplosive,
                    nativeExplosion = body.resolvedHitExplosive,
                    explosionRadius = body.resolvedHitRadius, shieldMode = SpaceShieldMode.Guard,
                    shieldCenter = shieldArc.center, shieldRadius = shieldArc.radius,
                    shieldEmergency = emergencyBefore,
                    shieldBroken = capacityBefore > 0 && CompSpaceShield.AvailableGuardCapacity(shieldArc.interceptor) <= 0
                });
                if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"shield intercept: {body.projectileDef?.defName} on '{defender.label}' " +
                        $"(hp {capacityBefore}→{CompSpaceShield.AvailableGuardCapacity(shieldArc.interceptor)}, " +
                        $"residual {residual:P0})");
                if (shieldEffect?.shieldSurfaceEffect != null
                    || shieldEffect?.attackerEffect?.kind == ImpactEffectKind.PlaceBody)
                {
                    ApplyAttackerOutcome(body, shieldEffect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                }
                else if (!ContinueAfterImpact(body, residual, evt.hitUniverse))
                {
                    ApplyAttackerOutcome(body, shieldEffect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                }
            }
        }

        private static bool ContinueAfterImpact(SweptBody body, float remaining, Vector3 hit)
        {
            if (remaining <= 0f) return false;
            body.damageScale *= remaining;
            body.continued = true;
            body.continuePoint = hit;
            return true;
        }

        /// <summary>攻方后效执行（DestroySelf 经服务回调帧边界销毁；PlaceBody 经放置回调）。</summary>
        private static void ApplyAttackerOutcome(SweptBody body, SpaceImpactEffectDef effect,
            SpaceObject defender, Vector3 hitUniverse, Vector2 hitLocal)
        {
            if (effect?.attackerEffect == null) return;
            switch (effect.attackerEffect.kind)
            {
                case ImpactEffectKind.DestroySelf:
                    body.dead = true;
                    body.onSelfDestruct?.Invoke();
                    break;
                case ImpactEffectKind.PlaceBody:
                    // 侵入 shuttle 的放置后效（§7.5）：执行服务回调（含容器销毁）。
                    if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"boarding impact outcome tick={Find.TickManager.TicksGame} " +
                            $"shuttle=#{body.attacker?.objectId} defender=#{defender?.objectId} " +
                            $"effect={effect.defName} hitLocal={hitLocal} " +
                            $"defenderMap={UniverseManager.MapTag(defender?.map)}");
                    body.dead = true;
                    body.onPlace?.Invoke(defender, hitUniverse, hitLocal);
                    break;
                case ImpactEffectKind.None:
                    break;
                default:
                    // ShieldDrain（守方侧）不是攻方后效；收到未支持的攻方后效说明 def
                    // 配置超前于实现，显式报错不静默。
                    Log.ErrorOnce($"[DeltaV] Attacker effect {effect.attackerEffect.kind} for " +
                                  $"{body.projectileDef?.defName} is not implemented yet.", 0x51504154);
                    break;
            }
        }

        /// <summary>
        /// 扫掠段 × 弧环求交（GenGeo.IntersectLineCircleOutline 同款语义 + 弧角限制）：
        /// 取 from 起最近进入交点；命中点须落在弧跨内。段外/弧内出发不算。
        /// </summary>
        private static bool TryFindArcHit(Surface surface, Vector2 from, Vector2 dir, float length,
            out ShieldArc hitArc, out Vector2 hitPoint, out float along,
            System.Predicate<ShieldArc> include = null)
        {
            hitArc = null;
            hitPoint = default;
            along = float.MaxValue;
            for (int i = 0; i < surface.arcs.Count; i++)
            {
                ShieldArc arc = surface.arcs[i];
                if (include != null ? !include(arc)
                    : !CompSpaceShield.CanAbsorbGuard(arc.interceptor) || !arc.interceptor.Active) continue;
                Vector2 toCenter = arc.center - from;
                float proj = Vector2.Dot(toCenter, dir);
                float distSqr = toCenter.sqrMagnitude - proj * proj;
                if (distSqr > arc.radius * arc.radius) continue;
                float offset = Mathf.Sqrt(Mathf.Max(0f, arc.radius * arc.radius - distSqr));
                float t = proj - offset;
                if (t < -0.001f || t > length) continue;
                t = Mathf.Max(0f, t);
                Vector2 point = from + dir * t;
                float angle = Mathf.Atan2(point.y - arc.center.y, point.x - arc.center.x);
                if (Mathf.Abs(SpaceTransform.WrapAngle(angle - arc.facing)) > arc.span * 0.5f) continue;
                if (t < along)
                {
                    along = t;
                    hitArc = arc;
                    hitPoint = point;
                }
            }
            return hitArc != null;
        }

        private static bool TryFindLineHit(Surface surface, Vector2 from, Vector2 dir,
            float length, CompProperties_SpaceProjectile projectileProps,
            out CompSpaceShield hitShield, out Vector2 hitPoint,
            out Vector2 hitNormal, out float along, out Vector2 lineStart, out Vector2 lineEnd,
            System.Predicate<CompSpaceShield> include = null)
        {
            hitShield = null;
            hitPoint = default;
            hitNormal = default;
            lineStart = lineEnd = default;
            along = float.MaxValue;
            foreach (CompSpaceShield shield in surface.lineShields)
            {
                if (include != null ? !include(shield) : shield.State != SpaceShieldState.LineActive) continue;
                if (shield.mode == SpaceShieldMode.Kinetic
                    ? !SpaceShieldProjectileBehavior.IsKinetic(projectileProps)
                    : !SpaceShieldProjectileBehavior.IsLaser(projectileProps)) continue;
                shield.GetSegment(out Vector2 a, out Vector2 b, out Vector2 normal);
                if (Vector2.Dot(dir, normal) >= -0.0001f) continue;
                Vector2 edge = b - a;
                float cross = dir.x * edge.y - dir.y * edge.x;
                if (Mathf.Abs(cross) < 1e-6f) continue;
                Vector2 difference = a - from;
                float t = (difference.x * edge.y - difference.y * edge.x) / cross;
                float u = (difference.x * dir.y - difference.y * dir.x) / cross;
                if (t < 0.001f || t > length || u < 0f || u > 1f || t >= along) continue;
                along = t;
                hitShield = shield;
                hitPoint = from + dir * t;
                hitNormal = normal;
                lineStart = a;
                lineEnd = b;
            }
            return hitShield != null;
        }

        private static bool TryFindHullContact(SweptBody body, Map map, Surface surface, Vector2 from,
            Vector2 dir, Vector2 footprintDirection, float near, float end, HashSet<Thing> passedBuildings,
            HashSet<IntVec3> passedCells, System.Predicate<IntVec3> include, bool skipSubstructure,
            out RayContact contact, HashSet<Thing> coverBuildings = null, HashSet<IntVec3> coverCells = null)
        {
            contact = default;
            Vector2 normal = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y)
                ? new Vector2(-Mathf.Sign(dir.x), 0f) : new Vector2(0f, -Mathf.Sign(dir.y));
            if (body.sweepRadius == 0f && body.noseHalfWidth == 0f)
                return TryFindGridContact(map, surface, from, dir, near, end, normal, out contact,
                    include, passedBuildings, passedCells, skipSubstructure, coverBuildings, coverCells);

            int first = Mathf.FloorToInt(near / SampleStep);
            int last = Mathf.CeilToInt(end / SampleStep);
            for (int step = first; step <= last; step++)
            {
                Vector2 p = from + dir * Mathf.Min(end, step * SampleStep);
                if (!FindBestCell(body, map, surface, from, p, footprintDirection, passedBuildings, passedCells,
                    out IntVec3 cell, out SpaceCellKind kind, out Thing building, include, skipSubstructure,
                    coverBuildings, coverCells)) continue;
                contact = new RayContact(p, normal, cell, kind, building);
                return true;
            }
            return false;
        }

        private static bool TryFindGridContact(Map map, Surface surface, Vector2 from, Vector2 dir,
            float near, float end, Vector2 projectileNormal, out RayContact contact,
            System.Predicate<IntVec3> include = null, HashSet<Thing> passedBuildings = null,
            HashSet<IntVec3> passedCells = null, bool skipSubstructure = false,
            HashSet<Thing> coverBuildings = null, HashSet<IntVec3> coverCells = null, bool hullContact = false)
        {
            contact = default;
            var ray = new GridRay(from, dir, near);
            IntVec3 previous = ray.Cell;
            for (; ray.Along <= end; ray.Advance())
            {
                IntVec3 cell = ray.Cell;
                Vector2 face = projectileNormal;
                if (hullContact)
                {
                    // An already occupied start cell is not a new impact. At an exact
                    // corner both crossed edges are available; choose a stable face.
                    int dx = previous.x - cell.x, dz = previous.z - cell.z;
                    previous = cell;
                    if (dx == 0 && dz == 0) continue;
                    face = dz == 0 || dx != 0 && Mathf.Abs(dir.x) >= Mathf.Abs(dir.y)
                        ? new Vector2(dx, 0f) : new Vector2(0f, dz);
                    if (!HullContactCell(map, surface, cell, out Thing hull)) continue;
                    contact = new RayContact(from + dir * ray.Along, face, cell,
                        hull == null ? SpaceCellKind.Substructure : SpaceCellKind.Building, hull as Building);
                    return true;
                }
                if (!cell.InBounds(map) || include?.Invoke(cell) == false
                    || !TryGetHullCell(map, surface, cell, passedBuildings, passedCells, skipSubstructure,
                        out SpaceCellKind kind, out Thing building, coverBuildings, coverCells)) continue;
                contact = new RayContact(from + dir * ray.Along, face, cell, kind, building);
                return true;
            }
            return false;
        }

        private static bool TryGetHullCell(Map map, Surface surface, IntVec3 cell, HashSet<Thing> passedBuildings,
            HashSet<IntVec3> passedCells, bool skipSubstructure, out SpaceCellKind kind, out Thing building,
            HashSet<Thing> coverBuildings, HashSet<IntVec3> coverCells)
        {
            int index = map.cellIndices.CellToIndex(cell);
            kind = (SpaceCellKind)surface.kinds[index];
            building = surface.buildings[index];
            if (building != null && !building.Destroyed && passedBuildings?.Contains(building) != true
                && coverBuildings?.Contains(building) != true) return true;
            kind = SpaceCellKind.Substructure;
            building = null;
            return !skipSubstructure && surface.foundations[index] && passedCells?.Contains(cell) != true
                && coverCells?.Contains(cell) != true;
        }

        /// <summary>
        /// 找采样点周围（扫掠块半径内）「建筑优先、沿弹道更早」的占用格。
        /// 已放行的建筑/基架不重掷；建筑放行后同格基架仍可被判定。
        /// </summary>
        private static bool FindBestCell(SweptBody body, Map defMap, Surface surface, Vector2 fromLocal,
            Vector2 p, Vector2 dir, HashSet<Thing> passedBuildings, HashSet<IntVec3> passedSubstructure,
            out IntVec3 bestCell, out SpaceCellKind bestKind, out Thing bestThing,
            System.Predicate<IntVec3> include, bool skipSubstructure,
            HashSet<Thing> coverBuildings, HashSet<IntVec3> coverCells)
        {
            bestCell = IntVec3.Invalid;
            bestKind = SpaceCellKind.Empty;
            bestThing = null;
            int reach = Mathf.CeilToInt(body.sweepRadius);
            int cx = Mathf.FloorToInt(p.x);
            int cz = Mathf.FloorToInt(p.y);
            Vector2 side = new Vector2(-dir.y, dir.x);
            float bestRank = float.MaxValue;
            for (int ox = -reach; ox <= reach; ox++)
            for (int oz = -reach; oz <= reach; oz++)
            {
                var cell = new IntVec3(cx + ox, 0, cz + oz);
                if (!cell.InBounds(defMap)) continue;
                if (include != null && !include(cell)) continue;
                // 格矩形到采样点的最近点距离 ≤ 扫掠半径才算被脚印覆盖。
                float nearestX = Mathf.Clamp(p.x, cell.x, cell.x + 1f);
                float nearestZ = Mathf.Clamp(p.y, cell.z, cell.z + 1f);
                if ((nearestX - p.x) * (nearestX - p.x) + (nearestZ - p.y) * (nearestZ - p.y)
                    > body.sweepRadius * body.sweepRadius)
                {
                    continue;
                }
                if (body.noseHalfWidth > 0f)
                {
                    // 侧滑时按真实艇首朝向定位，不能把速度方向当成船首。
                    // 与落位用的 3×5 脚印一致，仅让前端一排 3×1 的矩形触发破拆。
                    Vector2 offset = new Vector2(cell.x + 0.5f, cell.z + 0.5f)
                        - (p + dir * body.noseForwardOffset);
                    float forwardReach = 0.5f + 0.5f * (Mathf.Abs(dir.x) + Mathf.Abs(dir.y));
                    float sideReach = body.noseHalfWidth
                        + 0.5f * (Mathf.Abs(side.x) + Mathf.Abs(side.y));
                    if (Mathf.Abs(Vector2.Dot(offset, dir)) > forwardReach
                        || Mathf.Abs(Vector2.Dot(offset, side)) > sideReach
                        || Mathf.Abs(offset.x) > 0.5f + 0.5f * Mathf.Abs(dir.x)
                            + body.noseHalfWidth * Mathf.Abs(side.x)
                        || Mathf.Abs(offset.y) > 0.5f + 0.5f * Mathf.Abs(dir.y)
                            + body.noseHalfWidth * Mathf.Abs(side.y)) continue;
                }
                if (!TryGetHullCell(defMap, surface, cell, passedBuildings, passedSubstructure, skipSubstructure,
                    out SpaceCellKind kind, out Thing thing, coverBuildings, coverCells)) continue;
                // 建筑高于同一带内的基架；同类再按沿弹道距离排序。
                float rank = (new Vector2(cell.x + 0.5f, cell.z + 0.5f) - fromLocal).sqrMagnitude;
                bool buildingWins = bestKind == SpaceCellKind.Substructure
                    && kind != SpaceCellKind.Substructure;
                bool sameLayer = bestKind == SpaceCellKind.Empty
                    || (bestKind == SpaceCellKind.Substructure) == (kind == SpaceCellKind.Substructure);
                if (buildingWins || sameLayer && rank < bestRank)
                {
                    bestRank = rank;
                    bestCell = cell;
                    bestKind = kind;
                    bestThing = thing;
                }
            }
            return bestKind != SpaceCellKind.Empty;
        }
    }
}

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
    ///   经 GlobalStructureVersion + 周期重扫失效（ShipPhysics 质量报告同风格），
    ///   容器注销/图销毁释放（Forget/ClearCaches）；
    /// - 参与判定（tag）：主动方 = projectile tag 容器（IsActiveCollider）；被动方 =
    ///   任何有碰撞面的容器（有图即可被撞——飞船被弹体命中、PD 弹打导弹都是此路径）；
    /// - 求交（守方局部坐标，全部经 SpaceTransform 现有坐标入口）：宽相位（扫掠段 ×
    ///   守方包围域圆）→（段5：护盾弧先于格）→ 0.5 格步进占用采样；
    /// - 命中事件附带几何事实（一次性计算，参与方只消费不重算，§3.3）：命中 universe
    ///   点、两侧 building、入射方向、相对速度标量/矢量、入射角；
    /// - 结算 = 联合效果注册表（SpaceImpactEffects）对称分发，每容器各自后效；
    ///   一切销毁经 SweptBody.onSelfDestruct 回调由服务在帧边界执行——本核心不在
    ///   扫掠循环内注销任何容器；
    /// - tick 驱动、零事件分配（事件为复用实例）；己方排除（顶部炮塔）收口在
    ///   SweptBody.excludeContainer 单参数。
    /// </summary>
    public static class SpaceCollision
    {
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
            /// <summary>「放置」后效回调（侵入 shuttle §7.5）：shuttle 服务闭包——
            /// 舱体放置在撞击位内侧偏移 + 容器销毁（帧边界）。</summary>
            public System.Action<SpaceObject, Vector3, Vector2> onPlace;
            /// <summary>护盾挡停语义（侵入 shuttle）：活动弧截获时速度清零，
            /// 不按普通弹体生成残余。</summary>
            public bool haltOnShield;
            /// <summary>本段击破后的续飞；只消耗 damageScale，不重复按速度或穿甲扣预算。</summary>
            public bool continued;
            public bool didContinue;
            public Vector3 continuePoint;
            public bool redirected;
            public bool didRedirect;
            public Vector3 redirectPoint;
            public Vector2 redirectDirection;
            public float redirectRemaining;
            public System.Action<Vector3, Vector3> onShieldRedirect;
            public System.Action<Vector3, SpaceObject, Vector2, float> onShieldShatter;
            // Consumed synchronously after each resolved contact, before the next sweep.
            public System.Action<VisualContact> onVisualContact;
        }

        /// <summary>Read-only presentation snapshot; geometry and outcomes come from the resolving contact.</summary>
        public struct VisualContact
        {
            public SpaceObject defender;
            public ThingDef projectileDef;
            public Vector3 position, pathFrom;
            public Vector2 localPoint, localDirection, localNormal;
            public float damageScale, explosionRadius;
            public bool explosive, nativeExplosion, destroyed, penetrated, missileDestroyed, shieldBroken;
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

        private sealed class Surface
        {
            public byte[] kinds;
            public Thing[] buildings;
            public Vector2 boundsCenter;
            public float boundsRadius;
            public bool anySolid;
            public int structureVersion = -1;
            public readonly List<IntVec3> occupiedCells = new List<IntVec3>();
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
            if (map != null && Caches.TryGetValue(map, out Surface surface))
                surface.structureVersion = -1;
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
            int version = SubstructureOwnership.GlobalStructureVersion;
            if (surface.kinds == null || SubstructureOwnership.CacheNeedsRefresh(surface.structureVersion))
            {
                Rebuild(obj, surface, version);
            }
            return surface;
        }

        /// <summary>容器的等效碰撞半径（包围域；发射侧外推炮口起点用）。</summary>
        public static float BoundsRadiusOf(SpaceObject obj)
        {
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
            occupied.AddRange(surface.occupiedCells);
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

        private static void Rebuild(SpaceObject obj, Surface surface, int version)
        {
            Map map = obj.map;
            int cells = map.cellIndices.NumGridCells;
            if (surface.kinds == null || surface.kinds.Length != cells)
            {
                surface.kinds = new byte[cells];
                surface.buildings = new Thing[cells];
            }
            else
            {
                // 重建是完整快照；不清空会让已摧毁建筑永久残留在窄相位占用格中。
                System.Array.Clear(surface.kinds, 0, surface.kinds.Length);
                System.Array.Clear(surface.buildings, 0, surface.buildings.Length);
            }
            bool any = false;
            surface.occupiedCells.Clear();
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (SpaceCollisionCell geometry in obj.CollisionCells)
            {
                IntVec3 cell = geometry.cell;
                SpaceCellKind kind = geometry.kind;
                Thing thing = geometry.building;
                if (kind == SpaceCellKind.Empty) continue;
                int index = map.cellIndices.CellToIndex(cell);
                surface.kinds[index] = (byte)kind;
                surface.buildings[index] = thing;
                surface.occupiedCells.Add(cell);
                any = true;
                minX = Mathf.Min(minX, cell.x); maxX = Mathf.Max(maxX, cell.x);
                minZ = Mathf.Min(minZ, cell.z); maxZ = Mathf.Max(maxZ, cell.z);
            }
            surface.anySolid = any;
            if (any)
            {
                surface.boundsCenter = new Vector2((minX + maxX + 1) * 0.5f, (minZ + maxZ + 1) * 0.5f);
                float width = maxX - minX + 1;
                float height = maxZ - minZ + 1;
                // 宽相位圆必须包住脚印四角；最大边长的一半会漏掉矩形/方形角部。
                surface.boundsRadius = 0.5f * Mathf.Sqrt(width * width + height * height);
            }
            else
            {
                surface.boundsCenter = SpaceTransform.ShipLocalOrigin(map);
                surface.boundsRadius = 0f;
            }
            // 自建发生器在太空模式使用线盾，在地面模式与原版发生器一样
            // 使用拦截器的逐实例半径和状态。两种形态共用此碰撞面。
            surface.arcs.Clear();
            surface.lineShields.Clear();
            float arcBoost = 0f;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is not Building building || building.Destroyed) continue;
                CompProjectileInterceptor interceptor = building.TryGetComp<CompProjectileInterceptor>();
                if (interceptor == null) continue;
                CompSpaceShield shield = building.TryGetComp<CompSpaceShield>();
                if (shield != null && shield.mode != SpaceShieldMode.Guard)
                {
                    surface.lineShields.Add(shield);
                    shield.GetSegment(out Vector2 a, out Vector2 b, out _);
                    arcBoost = Mathf.Max(arcBoost, (a - surface.boundsCenter).magnitude
                        - surface.boundsRadius, (b - surface.boundsCenter).magnitude
                        - surface.boundsRadius);
                    continue;
                }
                var arc = new ShieldArc
                {
                    center = new Vector2(building.Position.x + building.def.Size.x * 0.5f,
                        building.Position.z + building.def.Size.z * 0.5f),
                    radius = interceptor.Props.radius,
                    facing = 0f,
                    span = Mathf.PI * 2f,
                    building = building,
                    interceptor = interceptor,
                };
                surface.arcs.Add(arc);
                arcBoost = Mathf.Max(arcBoost,
                    (arc.center - surface.boundsCenter).magnitude + arc.radius - surface.boundsRadius);
            }
            if (arcBoost > 0f) surface.boundsRadius += arcBoost;
            surface.structureVersion = version;
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
                if (body.dead || body.attacker == null) continue;
                for (int bounce = 0; bounce < 128 && !body.dead; bounce++)
                {
                    bool redirected = false;
                    for (int d = 0; d < objects.Count; d++)
                    {
                        SpaceObject defender = objects[d];
                        if (defender == body.attacker || defender == body.excludeContainer
                            || (body.requiredDefender != null && defender != body.requiredDefender)) continue;
                        if (body.ignoreNonHostileProjectiles && !body.didRedirect && defender.IsActiveCollider
                            && !AreHostile(body.attacker.faction, defender.faction)) continue;
                        Map defMap = defender.map;
                        if (defMap == null || defMap.Disposed) continue;
                        if (universe.Generation?.IsGenerating(defMap) == true) continue;
                        body.redirected = false;
                        body.continued = false;
                        if (!SweepOne(body, defender, defMap)) continue;
                        if (body.dead) break;
                        if (body.continued)
                        {
                            // 不向前跳过距离：同心盾、贴合盾面与紧邻建筑必须逐个结算。
                            body.from = body.continuePoint;
                            body.didContinue = true;
                            if (bounce == 127) body.to = body.from;
                            redirected = true;
                            break;
                        }
                        if (!body.redirected) continue;
                        Vector3 oldFrom = body.from;
                        body.onShieldRedirect?.Invoke(oldFrom, body.redirectPoint);
                        body.from = OrbitSphere.Move(body.redirectPoint,
                            body.redirectDirection * 0.03f);
                        body.to = OrbitSphere.Move(body.from,
                            body.redirectDirection * body.redirectRemaining);
                        body.didRedirect = true;
                        body.excludeContainer = null;
                        if (bounce == 127) body.to = body.from;
                        redirected = true;
                        break;
                    }
                    if (!redirected) break;
                }
            }
        }

        private static bool AreHostile(Faction first, Faction second)
        {
            if (first == null || second == null) return first != second;
            return first.HostileTo(second) || second.HostileTo(first);
        }

        /// <summary>单个 body 对单个守方的窄相位求交与结算；返回是否产生命中事件。</summary>
        private static bool SweepOne(SweptBody body, SpaceObject defender, Map defMap)
        {
            Surface surface = SurfaceFor(defender);
            if (!surface.anySolid) return false;
            // 守方局部（地图连续坐标）扫掠段：universe ⇄ map 只经 SpaceTransform 入口。
            Vector2 fromLocal = SpaceTransform.UniverseToMapPoint(defender, defMap, body.from);
            Vector2 toLocal = SpaceTransform.UniverseToMapPoint(defender, defMap, body.to);
            Vector2 delta = toLocal - fromLocal;
            float length = delta.magnitude;
            if (length < 1e-6f) return false;
            Vector2 dir = delta / length;

            // 宽相位：扫掠段到包围域圆心的最近距离。
            Vector2 toCenter = surface.boundsCenter - fromLocal;
            float along = Mathf.Clamp(Vector2.Dot(toCenter, dir), 0f, length);
            float distanceToCenter = (toCenter - dir * along).magnitude;
            if (distanceToCenter > surface.boundsRadius + body.sweepRadius + 0.5f) return false;

            // 先求最近盾交，再只采样交点前的建筑；折射后由外层继续同一求交链。
            float sampleLength = length;
            CompSpaceShield lineShield = null;
            Vector2 linePoint = default;
            Vector2 lineNormal = default;
            Vector2 lineStart = default, lineEnd = default;
            float lineAlong = float.MaxValue;
            CompProperties_SpaceProjectile projectileProps = body.projectileDef
                ?.GetCompProperties<CompProperties_SpaceProjectile>();
            bool shieldsApply = projectileProps?.ignoresShields != true;
            if (shieldsApply && TryFindLineHit(surface, fromLocal, dir, length, projectileProps,
                out lineShield, out linePoint, out lineNormal, out lineAlong, out lineStart, out lineEnd))
                sampleLength = lineAlong;
            ShieldArc shieldArc = null;
            Vector2 shieldPoint = default;
            float shieldIncidence = 0f;
            SpaceImpactEffectDef shieldEffect = null;
            float relativeSpeed = SpaceTransform.RelativeVelocity(body.attacker.pose, defender.pose).magnitude;
            if (shieldsApply && surface.arcs.Count > 0
                && TryFindArcHit(surface, fromLocal, dir, length, out ShieldArc arc, out Vector2 point, out float arcAlong))
            {
                Vector2 radial = (point - arc.center).normalized;
                float incidence = SpaceTransform.IncidenceAngle(dir, radial);
                SpaceImpactEffectDef effect = SpaceImpactEffects.Resolve(body.projectileDef, arc.building.def, shieldSurface: true);
                if (arc.interceptor.currentHitPoints > 0 && arcAlong < sampleLength)
                {
                    sampleLength = arcAlong;
                    shieldArc = arc;
                    shieldPoint = point;
                    shieldIncidence = incidence;
                    shieldEffect = effect;
                }
            }

            // 窄相位：0.5 格步进采样占用集（至多采到弧交点）。
            int steps = Mathf.Max(1, Mathf.CeilToInt(sampleLength / SampleStep));
            HashSet<Thing> passedBuildings = null;
            HashSet<IntVec3> passedSubstructure = null;
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = fromLocal + dir * Mathf.Min(sampleLength, SampleStep * s);
                if (!FindBestCell(body, defMap, surface, fromLocal, p, dir,
                    passedBuildings, passedSubstructure,
                    out IntVec3 bestCell, out SpaceCellKind bestKind, out Thing bestThing))
                {
                    continue;
                }

                SpaceImpactEffectDef effect = SpaceImpactEffects.Resolve(body.projectileDef,
                    bestKind == SpaceCellKind.Substructure ? null : bestThing?.def);
                ImpactEvent evt = SharedEvent;
                evt.attacker = body.attacker;
                evt.projectileDef = body.projectileDef;
                evt.launcher = body.launcher;
                evt.defender = defender;
                evt.defenderBuilding = bestThing;
                evt.defenderIsSubstructure = bestKind == SpaceCellKind.Substructure;
                evt.hitDefenderLocal = p;
                evt.hitCell = bestCell;
                evt.hitUniverse = SpaceTransform.MapPointToUniverse(defender, defMap, p);
                evt.incidentDir = dir;
                evt.relativeVelocity = SpaceTransform.RelativeVelocity(body.attacker.pose, defender.pose);
                evt.relativeSpeed = evt.relativeVelocity.magnitude;
                evt.damageScale = body.damageScale;
                evt.armorPenetrationScale = body.armorPenetrationScale;

                // 入射角：按扫掠进入面推导法线（法线指向来侧；基架格同规则，不设第二套约定）。
                Vector2 normal = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y)
                    ? new Vector2(-Mathf.Sign(dir.x), 0f)
                    : new Vector2(0f, -Mathf.Sign(dir.y));
                evt.incidenceAngleRad = SpaceTransform.IncidenceAngle(dir, normal);

                // 命中判定（二值）：放行 = 弹体穿过、本 tick 后续采样继续（下次碰撞再判定）。
                if (!SpaceImpactEffects.HitTest(body.projectileDef, bestKind, bestThing, effect))
                {
                    if (projectileProps?.continuousBeam != true && DeltaVLog.Enabled(DeltaVLogModule.Combat))
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"pass-through: {body.projectileDef?.defName} past " +
                            (bestThing?.def.defName ?? "substructure") + $" at {bestCell} " +
                            $"of '{defender.label}' (v_rel {evt.relativeSpeed:F1})");
                    if (bestThing != null)
                        (passedBuildings ??= new HashSet<Thing>()).Add(bestThing);
                    else
                        (passedSubstructure ??= new HashSet<IntVec3>()).Add(bestCell);
                    continue;
                }

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
                float durabilityScale = 1f;
                if (evt.defenderBuilding != null && effect != null
                    && effect.projectileDefs.NullOrEmpty() && effect.targetThingDefs.NullOrEmpty()
                    && !effect.targetsSubstructure)
                {
                    // 全局兜底条目（同等耐久舱壁规则）：耐久只换参数——基准耐久 / 守方耐久。
                    durabilityScale = Mathf.Clamp(effect.fallbackBaselineHitPoints
                        / Mathf.Max(1f, SpaceImpactEffects.MaxHitPointsOf(evt.defenderBuilding)), 0.2f, 2f);
                }
                float remaining = SpaceImpactEffects.ApplyToDefender(evt, effect?.defenderEffect, durabilityScale);
                body.onVisualContact?.Invoke(new VisualContact
                {
                    defender = defender, projectileDef = body.projectileDef,
                    position = evt.hitUniverse, pathFrom = body.from,
                    localPoint = p, localDirection = dir, localNormal = normal,
                    damageScale = body.damageScale, explosive = body.resolvedHitExplosive,
                    nativeExplosion = defenderPart?.explosionRadius > 0f
                        && !(defenderPart.penetrationLayers?.Count > 0),
                    explosionRadius = body.resolvedHitRadius, penetrated = remaining > 0f,
                    destroyed = bestThing?.Destroyed == true
                        || bestKind == SpaceCellKind.Substructure && !SubstructureOwnership.HasFoundation(defMap, bestCell),
                    missileDestroyed = bestThing?.Destroyed == true
                        && bestThing.def.GetCompProperties<CompProperties_SpaceProjectile>()?.guided == true
                });
                if (ContinueAfterImpact(body, remaining, evt.hitUniverse)) return true;
                ApplyAttackerOutcome(body, effect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                return true;
            }

            if (lineShield != null && lineAlong <= sampleLength + 0.001f)
            {
                Vector3 hit = SpaceTransform.MapPointToUniverse(defender, defMap, linePoint);
                if (lineShield.mode == SpaceShieldMode.Kinetic)
                {
                    float speed = relativeSpeed;
                    float requested = lineShield.ShieldProps.kineticMaxDeltaV
                        * lineShield.kineticStrengthPercent / 100f;
                    float baseCost = projectileProps.shieldFragmentCost;
                    float extra = SpaceShieldProjectileBehavior.KineticCost(projectileProps, requested, speed)
                        - baseCost;
                    float paid = lineShield.ConsumePulseFraction(baseCost + extra)
                        * (baseCost + extra);
                    float actualDeltaV = extra <= 0f ? 0f
                        : requested * Mathf.Clamp01((paid - baseCost) / extra);
                    body.resolvedHit = true;
                    body.resolvedHitShield = true;
                    body.resolvedHitUniverse = hit;
                    body.resolvedHitDefender = defender;
                    body.resolvedHitLocal = linePoint;
                    Vector2 worldDirection = SpaceTransform.MapDirectionToUniverseTangent(
                        defender, defMap, linePoint, dir);
                    body.onVisualContact?.Invoke(new VisualContact
                    {
                        defender = defender, projectileDef = body.projectileDef, position = hit, pathFrom = body.from,
                        localPoint = linePoint, localDirection = dir, localNormal = lineNormal,
                        damageScale = body.damageScale, shieldMode = SpaceShieldMode.Kinetic,
                        lineStart = lineStart, lineEnd = lineEnd
                    });
                    body.onShieldShatter?.Invoke(hit, defender, worldDirection, speed - actualDeltaV);
                    body.dead = true;
                    body.onSelfDestruct?.Invoke();
                    return true;
                }
                int strength = lineShield.electromagneticStrengthPercent;
                SpaceShieldRefraction.Plan plan = SpaceShieldRefraction.Calculate(dir, lineNormal, strength);
                float cost = projectileProps.shieldLaserTurnEnergy * plan.addedDeg / 180f;
                float funded = lineShield.ConsumePulseFraction(cost);
                Vector2 outgoing = plan.Direction(funded);
                body.onVisualContact?.Invoke(new VisualContact
                {
                    defender = defender, projectileDef = body.projectileDef, position = hit, pathFrom = body.from,
                    localPoint = linePoint, localDirection = dir, localNormal = lineNormal,
                    damageScale = body.damageScale, shieldMode = SpaceShieldMode.Electromagnetic,
                    lineStart = lineStart, lineEnd = lineEnd
                });
                body.redirected = true;
                body.redirectPoint = hit;
                body.redirectDirection = SpaceTransform.MapDirectionToUniverseTangent(defender,
                    defMap, linePoint, outgoing);
                body.redirectRemaining = length - lineAlong;
                return true;
            }

            // 弧事件（格采样未先命中）：护盾吸收——守方容量消耗 + 攻方自毁。
            if (shieldArc != null)
            {
                ImpactEvent evt = SharedEvent;
                evt.attacker = body.attacker;
                evt.projectileDef = body.projectileDef;
                evt.launcher = body.launcher;
                evt.defender = defender;
                evt.defenderBuilding = shieldArc.building;
                evt.defenderIsSubstructure = false;
                evt.hitDefenderLocal = shieldPoint;
                evt.hitCell = IntVec3.Invalid;
                evt.hitUniverse = SpaceTransform.MapPointToUniverse(defender, defMap, shieldPoint);
                evt.incidentDir = dir;
                evt.relativeVelocity = SpaceTransform.RelativeVelocity(body.attacker.pose, defender.pose);
                evt.relativeSpeed = evt.relativeVelocity.magnitude;
                evt.incidenceAngleRad = shieldIncidence;
                evt.damageScale = body.damageScale;
                evt.armorPenetrationScale = body.armorPenetrationScale;
                body.resolvedHit = true;
                body.resolvedHitUniverse = evt.hitUniverse;
                body.resolvedHitDefender = defender;
                body.resolvedHitLocal = shieldPoint;
                body.resolvedHitCell = IntVec3.Invalid;
                body.resolvedHitExplosive = false;
                body.resolvedHitShield = true;
                body.resolvedHitRadius = 0f;
                int capacityBefore = shieldArc.interceptor.currentHitPoints;
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
                    localNormal = (shieldPoint - shieldArc.center).normalized,
                    damageScale = body.damageScale, explosive = body.resolvedHitExplosive,
                    nativeExplosion = body.resolvedHitExplosive,
                    explosionRadius = body.resolvedHitRadius, shieldMode = SpaceShieldMode.Guard,
                    shieldCenter = shieldArc.center, shieldRadius = shieldArc.radius,
                    shieldBroken = capacityBefore > 0 && shieldArc.interceptor.currentHitPoints <= 0
                });
                if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"shield intercept: {body.projectileDef?.defName} on '{defender.label}' " +
                        $"(hp {capacityBefore}→{shieldArc.interceptor.currentHitPoints}, " +
                        $"residual {residual:P0})");
                if (shieldEffect?.shieldSurfaceEffect != null
                    || shieldEffect?.attackerEffect?.kind == ImpactEffectKind.PlaceBody)
                {
                    ApplyAttackerOutcome(body, shieldEffect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                }
                else if (body.haltOnShield)
                {
                    // 侵入 shuttle 的护盾挡停：有容量 → 速度清零、容量照耗、
                    // 不摧毁；侵入舱独立使用挡停语义。玩家可重入（目标保留）。
                    evt.attacker.pose.velocity = Vector2.zero;
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"intrusion halted by shield: '{body.projectileDef?.defName}' stopped at '{defender.label}' arc");
                }
                else if (!ContinueAfterImpact(body, residual, evt.hitUniverse))
                {
                    ApplyAttackerOutcome(body, shieldEffect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                }
                return true;
            }
            return false;
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
            out ShieldArc hitArc, out Vector2 hitPoint, out float along)
        {
            hitArc = null;
            hitPoint = default;
            along = float.MaxValue;
            for (int i = 0; i < surface.arcs.Count; i++)
            {
                ShieldArc arc = surface.arcs[i];
                if (arc.interceptor.currentHitPoints <= 0 || !arc.interceptor.Active) continue;
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
            out Vector2 hitNormal, out float along, out Vector2 lineStart, out Vector2 lineEnd)
        {
            hitShield = null;
            hitPoint = default;
            hitNormal = default;
            lineStart = lineEnd = default;
            along = float.MaxValue;
            foreach (CompSpaceShield shield in surface.lineShields)
            {
                if (shield.State != SpaceShieldState.LineActive) continue;
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

        /// <summary>
        /// 找采样点周围（扫掠块半径内）「建筑优先、沿弹道更早」的占用格。
        /// 已放行的建筑/基架不重掷；建筑放行后同格基架仍可被判定。
        /// </summary>
        private static bool FindBestCell(SweptBody body, Map defMap, Surface surface, Vector2 fromLocal,
            Vector2 p, Vector2 dir, HashSet<Thing> passedBuildings, HashSet<IntVec3> passedSubstructure,
            out IntVec3 bestCell, out SpaceCellKind bestKind, out Thing bestThing)
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
                    // 原先的外接圆会把艇身侧后方的墙算作艇首撞击。
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
                int index = defMap.cellIndices.CellToIndex(cell);
                SpaceCellKind kind = (SpaceCellKind)surface.kinds[index];
                if (kind == SpaceCellKind.Empty) continue;
                Thing thing = surface.buildings[index];
                if (thing != null && thing.Destroyed) continue;
                if (thing != null && passedBuildings?.Contains(thing) == true)
                {
                    // 掩体放行只让弹体穿过建筑，不暴露仍被建筑覆盖的基架。
                    continue;
                }
                if (kind == SpaceCellKind.Substructure
                    && passedSubstructure?.Contains(cell) == true) continue;
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

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 统一容器碰撞核心（太空战架构 §3.3，唯一碰撞实现——弹丸/激光/侵入/PD 四消费方
    /// 共用，不得出现第二份求交或第二份己方排除规则）：
    /// - 碰撞面缓存：每容器「占用格集」（SpaceStructureRules 唯一分类器）+ 包围域，
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
        /// <summary>碰撞面重扫间隔（tick）：已知写入经结构版本即时失效，其余允许至多 2s 过期。</summary>
        private const int RescanIntervalTicks = 120;

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
            /// <summary>
            /// 非敌对弹体互相穿过。用于并列发射架齐射：冷发射后的转弯轨迹可以交叉，
            /// 但敌方点防射束与来袭弹仍按派系敌意正常碰撞。
            /// </summary>
            public bool ignoreNonHostileProjectiles;
            public Vector3 from;
            public Vector3 to;
            /// <summary>弹体脚印等效半径（1×1 ≈ 0.71、2×1 ≈ 1.12）。</summary>
            public float sweepRadius = 0.71f;
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
            /// <summary>守方后效为爆炸（爆炸半径见 resolvedHitRadius）。</summary>
            public bool resolvedHitExplosive;
            /// <summary>护盾弧拦截命中。</summary>
            public bool resolvedHitShield;
            /// <summary>守方爆炸后效半径（非爆炸 = 0；特效尺寸与音效选型）。</summary>
            public float resolvedHitRadius;
            /// <summary>伤害/穿深倍率（轨道炮 Σ 加速段；随事件进入结算）。</summary>
            public float damageScale = 1f;
            public float armorPenetrationScale = 1f;
            /// <summary>「放置」后效回调（侵入 shuttle §7.5）：shuttle 服务闭包——
            /// 舱体放置在撞击位内侧偏移 + 容器销毁（帧边界）。</summary>
            public System.Action<SpaceObject, Vector3, Vector2> onPlace;
            /// <summary>护盾挡停语义（侵入 shuttle，§15.2 默认）：容量未破时速度清零
            /// 而非吸收摧毁；破弧放行撞格。</summary>
            public bool haltOnShield;
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
            /// <summary>伤害/穿深倍率（发射侧携带，如轨道炮 Σ 加速段）。</summary>
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
            public int measuredAtTick = -1;
            public readonly List<IntVec3> occupiedCells = new List<IntVec3>();
            public readonly List<ShieldArc> arcs = new List<ShieldArc>();
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
            int now = Find.TickManager.TicksGame;
            if (surface.kinds == null || surface.structureVersion != version
                || now - surface.measuredAtTick >= RescanIntervalTicks)
            {
                Rebuild(map, surface, version, now);
            }
            return surface;
        }

        /// <summary>容器的等效碰撞半径（包围域；发射侧外推炮口起点用）。</summary>
        public static float BoundsRadiusOf(SpaceObject obj)
        {
            if (obj?.map == null) return 0f;
            return SurfaceFor(obj).boundsRadius;
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

        private static void Rebuild(Map map, Surface surface, int version, int now)
        {
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
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                SpaceCellKind kind = SpaceStructureRules.ClassifyCell(map, cell, out Thing thing);
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
            // 护盾弧（段5）：全部 CompGravshipShieldGenerator 建筑各注册一段弧；
            // 弧跨/朝向经 CompSpaceShieldArc（def patch），缺省全圆环。弧半径计入
            // 包围域（宽相位必须覆盖弧）。
            surface.arcs.Clear();
            float arcBoost = 0f;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is not Building building || building.Destroyed) continue;
                CompProjectileInterceptor interceptor = building.TryGetComp<CompProjectileInterceptor>();
                if (interceptor == null) continue;
                CompSpaceShieldArc arcProps = building.TryGetComp<CompSpaceShieldArc>();
                var arc = new ShieldArc
                {
                    center = new Vector2(building.Position.x + building.def.Size.x * 0.5f,
                        building.Position.z + building.def.Size.z * 0.5f),
                    radius = interceptor.Props.radius,
                    facing = arcProps?.FacingRad ?? 0f,
                    span = arcProps?.SpanRad ?? Mathf.PI * 2f,
                    building = building,
                    interceptor = interceptor,
                };
                surface.arcs.Add(arc);
                arcBoost = Mathf.Max(arcBoost,
                    (arc.center - surface.boundsCenter).magnitude + arc.radius - surface.boundsRadius);
            }
            if (arcBoost > 0f) surface.boundsRadius += arcBoost;
            surface.structureVersion = version;
            surface.measuredAtTick = now;
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
                for (int d = 0; d < objects.Count; d++)
                {
                    SpaceObject defender = objects[d];
                    if (defender == body.attacker || defender == body.excludeContainer) continue;
                    if (body.ignoreNonHostileProjectiles && defender.IsActiveCollider
                        && !AreHostile(body.attacker.faction, defender.faction))
                    {
                        continue;
                    }
                    Map defMap = defender.map;
                    if (defMap == null || defMap.Disposed) continue;
                    if (universe.Generation?.IsGenerating(defMap) == true) continue;
                    if (!SweepOne(body, defender, defMap)) continue;
                    if (body.dead) break;
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

            // 先弧后格（段5）：扫掠段 × 弧环求交——Active ∧ 容量足够 → 护盾事件
            //（采样只到弧交点）；弧未激活/容量破 → 放行，继续对占用格采样。
            float sampleLength = length;
            ShieldArc shieldArc = null;
            Vector2 shieldPoint = default;
            float shieldIncidence = 0f;
            float shieldDurabilityScale = 1f;
            SpaceImpactEffectDef shieldEffect = null;
            float relativeSpeed = SpaceTransform.RelativeVelocity(body.attacker.pose, defender.pose).magnitude;
            if (surface.arcs.Count > 0
                && TryFindArcHit(surface, fromLocal, dir, length, out ShieldArc arc, out Vector2 point, out float arcAlong))
            {
                Vector2 radial = (point - arc.center).normalized;
                float incidence = SpaceTransform.IncidenceAngle(dir, radial);
                SpaceImpactEffectDef effect = SpaceImpactEffects.Resolve(body.projectileDef, arc.building.def);
                float drain = SpaceImpactEffects.ComputeDamage(effect?.defenderEffect, relativeSpeed, incidence);
                if (arc.interceptor.currentHitPoints >= Mathf.Max(1f, drain))
                {
                    sampleLength = arcAlong;
                    shieldArc = arc;
                    shieldPoint = point;
                    shieldIncidence = incidence;
                    shieldEffect = effect;
                }
                else
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"shield broken through: {body.projectileDef?.defName} over '{defender.label}' arc " +
                        $"(hp {arc.interceptor.currentHitPoints} < drain {drain:F0}); projectile continues to grid");
                }
            }

            // 窄相位：0.5 格步进采样占用集（至多采到弧交点）。
            int steps = Mathf.Max(1, Mathf.CeilToInt(sampleLength / SampleStep));
            var lastPassed = new IntVec3(int.MinValue, 0, int.MinValue);
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = fromLocal + dir * (SampleStep * s);
                if (!FindBestCell(body, defMap, surface, fromLocal, p, lastPassed,
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
                if (!SpaceImpactEffects.HitTest(effect, evt.relativeSpeed))
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"pass-through: {body.projectileDef?.defName} through substructure at {bestCell} " +
                        $"of '{defender.label}' (v_rel {evt.relativeSpeed:F1})");
                    lastPassed = bestCell;
                    continue;
                }

                // 对称结算（§3.5）：每个参与容器各自解析自己的后效。
                body.resolvedHit = true;
                body.resolvedHitUniverse = evt.hitUniverse;
                body.resolvedHitDefender = defender;
                body.resolvedHitLocal = p;
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
                DeltaVLog.Message(DeltaVLogModule.Combat,
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
                SpaceImpactEffects.ApplyToDefender(evt, effect?.defenderEffect, durabilityScale);
                ApplyAttackerOutcome(body, effect, defender, evt.hitUniverse, evt.hitDefenderLocal);
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
                body.resolvedHitExplosive = false;
                body.resolvedHitShield = true;
                body.resolvedHitRadius = 0f;
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"shield intercept: {body.projectileDef?.defName} absorbed by '{shieldArc.building.def.defName}' " +
                    $"on '{defender.label}' (drain {SpaceImpactEffects.ComputeDamage(shieldEffect?.defenderEffect, evt.relativeSpeed, shieldIncidence):F0}, " +
                    $"hp {shieldArc.interceptor.currentHitPoints}/{shieldArc.interceptor.HitPointsMax})");
                SpaceImpactEffects.ApplyToDefender(evt, shieldEffect?.defenderEffect, shieldDurabilityScale);
                if (body.haltOnShield)
                {
                    // 侵入 shuttle 的护盾挡停（§15.2 默认）：容量未破 → 速度清零、容量照耗、
                    // 不摧毁；破弧已被上一步放行撞格。玩家可重入（目标保留）。
                    evt.attacker.pose.velocity = Vector2.zero;
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"intrusion halted by shield: '{body.projectileDef?.defName}' stopped at '{defender.label}' arc");
                }
                else
                {
                    ApplyAttackerOutcome(body, shieldEffect, defender, evt.hitUniverse, evt.hitDefenderLocal);
                }
                return true;
            }
            return false;
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
                if (!arc.interceptor.Active) continue; // 供电/EMP/击破走原版状态机
                Vector2 toCenter = arc.center - from;
                float proj = Vector2.Dot(toCenter, dir);
                float distSqr = toCenter.sqrMagnitude - proj * proj;
                if (distSqr > arc.radius * arc.radius) continue;
                float offset = Mathf.Sqrt(Mathf.Max(0f, arc.radius * arc.radius - distSqr));
                float t = proj - offset;
                if (t < 0f || t > length) continue;
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

        /// <summary>
        /// 找采样点周围（扫掠块半径内）「沿弹道更早、实心优先」的占用格。lastPassed
        /// 是刚放行穿过的基架格（跳过，防同一格反复掷骰）。
        /// </summary>
        private static bool FindBestCell(SweptBody body, Map defMap, Surface surface, Vector2 fromLocal,
            Vector2 p, IntVec3 lastPassed, out IntVec3 bestCell, out SpaceCellKind bestKind, out Thing bestThing)
        {
            bestCell = IntVec3.Invalid;
            bestKind = SpaceCellKind.Empty;
            bestThing = null;
            int reach = Mathf.CeilToInt(body.sweepRadius);
            int cx = Mathf.FloorToInt(p.x);
            int cz = Mathf.FloorToInt(p.y);
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
                int index = defMap.cellIndices.CellToIndex(cell);
                SpaceCellKind kind = (SpaceCellKind)surface.kinds[index];
                if (kind == SpaceCellKind.Empty) continue;
                if (kind == SpaceCellKind.Substructure && cell == lastPassed) continue;
                // 同一采样点内取「沿弹道更早、实心优先」的格子（墙旁甲板先打墙）。
                float rank = (new Vector2(cell.x + 0.5f, cell.z + 0.5f) - fromLocal).sqrMagnitude
                    - (kind == SpaceCellKind.Substructure ? 0.25f : 0f);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    bestCell = cell;
                    bestKind = kind;
                    bestThing = surface.buildings[index];
                }
            }
            return bestKind != SpaceCellKind.Empty;
        }
    }
}

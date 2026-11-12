using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>命中后效的种类（太空战架构 §3.4；每个参与容器各自解析自己的一侧）。</summary>
    public enum ImpactEffectKind : byte
    {
        /// <summary>无后效。</summary>
        None = 0,

        /// <summary>毁伤：对自己的内部图施加（原版原语 TakeDamage / GenExplosion）。</summary>
        DamageMap = 1,

        /// <summary>自毁：弹体容器销毁（有图则清图归池；执行经服务回调，帧边界）。</summary>
        DestroySelf = 2,

        /// <summary>护盾容量消耗（段5：写原版 CompProjectileInterceptor.currentHitPoints）。</summary>
        ShieldDrain = 3,

        /// <summary>放置：侵入 shuttle 的后效——舱体 building 放置在撞击位内侧（段7）。</summary>
        PlaceBody = 4,

        CoupleMotion = 5,
        Salvage = 6,
        TractorPull = 7,
        CutStructure = 8,
        Weld = 9
    }

    /// <summary>从命中格起沿弹道前进一格的结构毁伤；伤害倍率与横向半宽分别配置。</summary>
    public class ImpactPenetrationLayer
    {
        public float damageFactor = 1f;
        public int halfWidth;
    }

    /// <summary>一侧的后效参数（数值全部 XML 可调；伤害随相对速度/入射角缩放）。</summary>
    public class ImpactOutcomePart
    {
        public ImpactEffectKind kind = ImpactEffectKind.None;

        // ---- DamageMap 参数 ----

        /// <summary>伤害类型（爆炸用爆炸系 DamageDef、直射用弹系）。</summary>
        public DamageDef damageDef;

        /// <summary>基准相对速度下的伤害。</summary>
        public float damage = 20f;

        /// <summary>基准相对速度（格/s）。</summary>
        public float referenceSpeed = 60f;

        /// <summary>伤害 ∝ (v/v_ref)^exponent（1 = 线性；默认 0 = 不随速度缩放，
        /// 速度缩放是动能弹语义，需在 XML 中显式启用）。</summary>
        public float speedExponent = 0f;
        public bool kineticMassScaling;

        /// <summary>入射角权重：正入射 ×1 → 掠射（90°）×此值。</summary>
        public float incidenceMinFactor = 0.35f;

        /// <summary>原版护甲穿透值（与基础伤害同系数缩放）；不决定穿过几层结构。</summary>
        public float armorPenetration = 10f;

        /// <summary>按顺序列出各深度格的毁伤。空表只命中表层；几何穿透与护甲穿透独立。</summary>
        public List<ImpactPenetrationLayer> penetrationLayers;

        /// <summary>击破后按实际吸收量扣除本弹预算，由统一碰撞链继续扫掠；不展开局部穿层。</summary>
        public bool penetrates;

        /// <summary>爆炸半径（>0 = GenExplosion 波及；0 = 单格直击或逐层结构毁伤）。</summary>
        public float explosionRadius = 0f;

        /// <summary>破片摧毁基架的半径（>0 生效；中心概率按距离线性衰减到 0）。</summary>
        public float stripSubstructureRadius = 0f;

        /// <summary>破片在命中格处摧毁基架的概率（随距离线性衰减）。</summary>
        public float stripSubstructureChance = 0.5f;

        /// <summary>基架被直接命中后，伤害达到此值时必定摧毁命中格；不累计损伤。</summary>
        public float substructureBreakThreshold = 60f;

        /// <summary>跳弹门槛：守方耐久 ≥ 此值才可能跳弹（0 = 该后效不判跳弹）。</summary>
        public float ricochetMinHitPoints = 0f;

        /// <summary>跳弹所需最小入射角（度；入射角 ≥ 此值且耐久达标 → 消灭无伤）。</summary>
        public float ricochetMinIncidenceDeg = 60f;

        /// <summary>耦合每脉冲趋同量及冲量上限；不是对宇宙绝对速度施加阻力。</summary>
        public float couplingFraction = 0.15f;
        public float maxCouplingImpulse = 400f;
        public float maxCouplingAngularImpulse = 4000f;
        public float tractorForce = 500f;

        /// <summary>采矿光束矿物回收率；人造建筑与基架沿用原版拆除产出。</summary>
        public float miningYieldFactor = 0.5f;
    }

    /// <summary>
    /// 联合效果注册表条目（太空战架构 §3.4）：键 =（弹体 building def × 对面
    /// building/基架），记录命中后的攻守两侧效果；命中概率由目标类型统一判定。
    /// XML 可扩展；未注册的 def 对按「同等耐久舱壁」兜底条目结算（fallback 特例：
    /// projectileDefs 留空的条目即全局兜底）。本 def 族不持有运行时状态，纯数据。
    /// </summary>
    public class SpaceImpactEffectDef : Def
    {
        public const float DefaultSubstructureHitChance = 0.02f;

        /// <summary>匹配的弹体 building def（空 = 任意弹体 → 全局兜底条目）。</summary>
        public List<ThingDef> projectileDefs;

        /// <summary>本条目以基架格为目标（命中判定走 Substructure 语义）。</summary>
        public bool targetsSubstructure;

        /// <summary>弹体遇到无建筑遮盖的基架格时发生碰撞的概率。</summary>
        public float substructureHitChance = DefaultSubstructureHitChance;

        /// <summary>负数沿用建筑掩体；工程束可显式指定结构命中概率。</summary>
        public float buildingHitChance = -1f;

        /// <summary>匹配的守方 building def（空 = 该弹体的任意目标兜底）。</summary>
        public List<ThingDef> targetThingDefs;

        /// <summary>攻方（弹体）一侧的后效。</summary>
        public ImpactOutcomePart attackerEffect;

        /// <summary>守方（被撞容器）一侧的后效。</summary>
        public ImpactOutcomePart defenderEffect;

        /// <summary>护盾容量结算之后在盾面执行的地图后效（导弹爆炸）。</summary>
        public ImpactOutcomePart shieldSurfaceEffect;

        /// <summary>
        /// 兜底条目专用：基准舱壁耐久。守方伤害按「基准耐久 / 守方耐久」缩放
        ///（同等耐久舱壁规则：耐久只换参数，不换规则）。
        /// </summary>
        public float fallbackBaselineHitPoints = 100f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (targetsSubstructure && (substructureHitChance < 0f || substructureHitChance > 1f))
                yield return $"{defName}: substructureHitChance must be between 0 and 1";
            if (buildingHitChance != -1f && (buildingHitChance < 0f || buildingHitChance > 1f))
                yield return $"{defName}: buildingHitChance must be -1 or between 0 and 1";
            if (defenderEffect?.kind == ImpactEffectKind.CoupleMotion
                && (defenderEffect.couplingFraction <= 0f || defenderEffect.couplingFraction > 1f
                    || defenderEffect.maxCouplingImpulse < 0f || defenderEffect.maxCouplingAngularImpulse < 0f))
                yield return $"{defName}: invalid coupling strength or impulse limits";
            if (defenderEffect?.kind == ImpactEffectKind.Salvage
                && (defenderEffect.damage <= 0f || defenderEffect.miningYieldFactor < 0f
                    || defenderEffect.miningYieldFactor > 1f))
                yield return $"{defName}: invalid cutting amount or mining yield";
            if (defenderEffect?.kind == ImpactEffectKind.DamageMap
                && defenderEffect.substructureBreakThreshold <= 0f)
                yield return $"{defName}: substructureBreakThreshold must be positive";
            if (defenderEffect?.penetrates == true
                && (defenderEffect.penetrationLayers?.Count > 0 || defenderEffect.explosionRadius > 0f))
                yield return $"{defName}: budget penetration cannot also use a local penetration profile or explosion";
            if (attackerEffect == null && defenderEffect == null)
            {
                yield return $"{defName}: neither attackerEffect nor defenderEffect configured";
            }
        }
    }

    /// <summary>
    /// 联合效果注册表的解析与应用（单实现）：查表（精确 def 对 → 该弹体任意目标 →
    /// 全局兜底）、命中判定（二值）、守方毁伤应用（原版伤害原语单点封装）。
    /// 攻方的 DestroySelf / PlaceBody 等后效由碰撞核心经 SweptBody 回调执行
    ///（服务闭包），注册表保持纯函数式。
    /// </summary>
    public static partial class SpaceImpactEffects
    {
        /// <summary>艇首两排保证通路，艇身其余格只收集会妨碍放置的建筑。</summary>
        internal static bool TryCollectBoardingBreach(Map map, IntVec3 nose, Rot4 facing,
            CellRect footprint, ThingDef shuttle, out HashSet<Building> buildings)
        {
            IntVec3 forward = facing.FacingCell;
            IntVec3 side = facing == Rot4.North || facing == Rot4.South
                ? IntVec3.East : IntVec3.North;
            buildings = new HashSet<Building>();
            for (int depth = 0; depth < 2; depth++)
            for (int lateral = -1; lateral <= 1; lateral++)
            {
                IntVec3 cell = nose + forward * depth + side * lateral;
                if (!cell.InBounds(map)) return false;
                foreach (Thing thing in cell.GetThingList(map))
                    if (thing is Building building) buildings.Add(building);
            }
            foreach (IntVec3 cell in footprint)
            {
                if (!cell.InBounds(map)) return false;
                foreach (Thing thing in cell.GetThingList(map))
                    if (thing is Building building && GenSpawn.SpawningWipes(shuttle, building.def, ignoreDestroyable: true))
                        buildings.Add(building);
            }
            foreach (Building building in buildings)
                if (!building.def.destroyable) return false;
            return true;
        }

        internal static void BreachBoardingCorridor(Map map, HashSet<Building> buildings, Thing launcher)
        {
            foreach (Building building in buildings)
            {
                if (building.Destroyed) continue;
                building.TakeDamage(new DamageInfo(DamageDefOf.Bomb,
                    building.MaxHitPoints, 100f, -1f, launcher));
                if (!building.Destroyed) building.Destroy(DestroyMode.KillFinalize);
            }
            if (buildings.Count == 0) return;
            SubstructureOwnership.BumpStructureVersion(map);
            PlanetGlobeRenderer.InvalidateSilhouette(map);
        }

        private static Dictionary<ThingDef, Dictionary<ThingDef, SpaceImpactEffectDef>> byTarget;
        private static Dictionary<ThingDef, SpaceImpactEffectDef> substructureFor;
        private static Dictionary<ThingDef, SpaceImpactEffectDef> anyTargetFor;
        private static SpaceImpactEffectDef globalFallback;
        private static int cachedDefCount = -1;

        /// <summary>
        /// 解析一条 def 对的效果。targetDef = null 表示命中基架格。
        /// 查表链：精确对 → 该弹体 × 基架 → 该弹体 × 任意目标 → 全局兜底（同等耐久
        /// 舱壁规则；兜底条目在 XML 以空 projectileDefs 声明，耐久缩放在应用时进行）。
        /// ShieldDrain 精确条目只供真实盾面查询，发生器实体使用普通建筑后效。
        /// </summary>
        public static SpaceImpactEffectDef Resolve(ThingDef projectile, ThingDef targetDef, bool shieldSurface = false)
        {
            EnsureBuilt();
            if (projectile != null)
            {
                if (targetDef != null
                    && byTarget.TryGetValue(projectile, out Dictionary<ThingDef, SpaceImpactEffectDef> targets)
                    && targets.TryGetValue(targetDef, out SpaceImpactEffectDef exact)
                    && (exact.defenderEffect?.kind == ImpactEffectKind.ShieldDrain) == shieldSurface)
                {
                    return exact;
                }
                if (shieldSurface) return null;
                if (targetDef == null && substructureFor.TryGetValue(projectile, out SpaceImpactEffectDef sub))
                {
                    return sub;
                }
                if (anyTargetFor.TryGetValue(projectile, out SpaceImpactEffectDef any))
                {
                    return any;
                }
            }
            return globalFallback;
        }

        /// <summary>
        /// 命中判定（二值）：true = 产生后效；false = 放行穿过。
        /// 建筑按掩体效果，基架按本弹体的联合效果条目判定。
        /// </summary>
        public static bool HitTest(ThingDef projectileDef,
            SpaceCellKind kind, Thing building, SpaceImpactEffectDef effect)
        {
            if (kind == SpaceCellKind.Substructure)
                return Rand.Chance(HitChance(projectileDef, kind, building, effect));
            if (kind != SpaceCellKind.Substructure && building != null)
            {
                if (effect?.buildingHitChance >= 0f)
                    return Rand.Chance(HitChance(projectileDef, kind, building, effect));
                if (GuaranteedMissileHit(projectileDef, building.def)) return true;
                if (building.def.IsWall) return true;
                float chance = SpaceStructureRules.BuildingHitChance(building);
                if (chance <= 0f || chance < 1f && !Rand.Chance(chance)) return false;
            }
            return true;
        }

        /// <summary>点防专用弹体与有图制导导弹的配对规则，瞄准和命中共用。</summary>
        public static bool GuaranteedMissileHit(ThingDef projectileDef, ThingDef targetDef)
        {
            var projectile = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            var target = targetDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            return projectile?.guaranteedHitMissiles == true && target?.guided == true
                && target.hasMap;
        }

        /// <summary>
        /// 后效量计算（单实现）：基准 × 速度因子 × 入射角因子（× 兜底耐久缩放）。
        /// DamageMap 的伤害与 ShieldDrain 的容量消耗共用同一缩放口径。
        /// </summary>
        public static float ComputeDamage(ImpactOutcomePart part, float relativeSpeed,
            float incidenceAngleRad, float durabilityScale = 1f, float mass = 1f)
        {
            if (part == null) return 0f;
            float speedFactor = part.kineticMassScaling ? SpaceKinetics.ImpactFactor(mass, relativeSpeed) : Mathf.Pow(
                Mathf.Max(relativeSpeed, 0.01f) / Mathf.Max(1f, part.referenceSpeed), part.speedExponent);
            float incidenceFactor = Mathf.Lerp(1f, part.incidenceMinFactor,
                Mathf.Clamp01(incidenceAngleRad / (Mathf.PI * 0.5f)));
            return part.damage * speedFactor * incidenceFactor * durabilityScale;
        }

        /// <summary>
        /// 守方后效应用（原版伤害原语单点）：伤害 = 基准 × 速度因子 × 入射角因子
        ///（× 兜底耐久缩放）。跳弹（高耐久 ∧ 斜入射）= 消灭无伤——由攻方 DestroySelf
        /// 后效完成「消灭」，本方法不施加伤害。基架格由单次伤害决定摧毁概率，
        /// 不累计耐久；爆炸及破片仍可破坏周围基架。护盾容量消耗 = 直写原版 comp。
        /// 返回可续飞的剩余比例；调用方只乘一次弹体 damageScale。
        /// </summary>
        public static float ApplyToDefender(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part,
            float durabilityScale = 1f)
        {
            if (part == null || evt.defender?.map == null) return 0f;
            Map map = evt.defender.map;

            if (part.kind == ImpactEffectKind.CoupleMotion || part.kind == ImpactEffectKind.TractorPull)
            {
                ApplyEngineeringForce(evt, part);
                return 0f;
            }
            if (part.kind == ImpactEffectKind.Salvage)
            {
                Salvage(evt, part);
                return 0f;
            }

            if (part.kind == ImpactEffectKind.CutStructure)
            { CutStructure(evt, part); return 0f; }
            if (part.kind == ImpactEffectKind.Weld)
            { Weld(evt, part); return 0f; }

            if (part.kind == ImpactEffectKind.ShieldDrain)
            {
                CompProjectileInterceptor interceptor = evt.defenderBuilding?.TryGetComp<CompProjectileInterceptor>();
                if (interceptor == null) return 0f;
                float drain = ComputeDamage(part, evt.relativeSpeed, evt.incidenceAngleRad, durabilityScale, evt.mass)
                    * evt.damageScale;
                var shield = evt.defenderBuilding.TryGetComp<CompSpaceShield>();
                if (shield != null)
                {
                    float absorbed = shield.ConsumeGuard(drain);
                    return part.penetrates ? RemainingFraction(drain, absorbed) : 0f;
                }
                int capacity = interceptor.currentHitPoints;
                interceptor.currentHitPoints = Mathf.Max(0,
                    capacity - Mathf.Max(0, Mathf.RoundToInt(drain)));
                return part.penetrates ? RemainingFraction(drain, capacity) : 0f;
            }
            if (part.kind != ImpactEffectKind.DamageMap) return 0f;

            IntVec3 cell = evt.hitCell.IsValid ? evt.hitCell : new IntVec3(
                Mathf.FloorToInt(evt.hitDefenderLocal.x), 0,
                Mathf.FloorToInt(evt.hitDefenderLocal.y)).ClampInsideMap(map);

            // 跳弹（当前默认实现 = 消灭无伤；可反弹类型留扩展位，太空战架构 §3.4）：
            // 守方耐久达门槛 ∧ 入射角够歪 → 不施加伤害（弹体的 DestroySelf 照常执行）。
            if (Ricochets(part, evt.defenderBuilding, evt.incidenceAngleRad))
            {
                float maxHitPoints = MaxHitPointsOf(evt.defenderBuilding);
                if (DeltaVLog.Enabled(DeltaVLogModule.Combat)) DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"ricochet: {evt.projectileDef?.defName} off '{evt.defenderBuilding.def.defName}' " +
                    $"(hp {maxHitPoints:F0}, incidence {evt.incidenceAngleRad * Mathf.Rad2Deg:F0}°, v_rel {evt.relativeSpeed:F1})");
                return 0f;
            }

            float amount = ComputeDamage(part, evt.relativeSpeed, evt.incidenceAngleRad, durabilityScale, evt.mass)
                * evt.damageScale;
            float ap = part.armorPenetration * (amount / Mathf.Max(1f, part.damage))
                * evt.armorPenetrationScale;
            DamageDef damType = part.damageDef ?? DamageDefOf.Bullet;
            bool buildingGeometryChanged = false;
            float remaining = 0f;

            if (evt.defenderIsSubstructure && SubstructureOwnership.HasFoundation(map, cell)
                && Rand.Chance(Mathf.Clamp01(amount / part.substructureBreakThreshold)))
            {
                if (SubstructureDamageUtility.TryDestroyHitFoundation(map, cell, instigator: evt.launcher) && part.penetrates)
                    remaining = RemainingFraction(amount, part.substructureBreakThreshold);
            }

            if (part.penetrates && evt.defenderBuilding != null && !evt.defenderBuilding.Destroyed)
            {
                // 预算弹使用无建筑倍率的直击 DamageDef。实际耐久消耗由原版 TakeDamage 返回；
                // 未击毁（包括免伤/吸收）的结构截停，不能凭名义伤害越过。
                DamageWorker.DamageResult result = evt.defenderBuilding.TakeDamage(
                    new DamageInfo(damType, amount, ap, -1f, evt.launcher, null, evt.projectileDef));
                buildingGeometryChanged = evt.defenderBuilding.Destroyed;
                if (buildingGeometryChanged)
                    remaining = RemainingFraction(amount, result.totalDamageDealt);
            }
            else if (part.penetrationLayers != null && part.penetrationLayers.Count > 0
                && evt.defenderBuilding != null)
            {
                buildingGeometryChanged = ApplyDirectionalPenetration(evt, part, map, cell,
                    amount, ap, damType);
            }
            else if (part.explosionRadius > 0f)
            {
                // 爆炸：波及（人员杀伤/建筑毁伤）交原版 GenExplosion 原生处理。
                GenExplosion.DoExplosion(cell, map, part.explosionRadius, damType, evt.launcher,
                    Mathf.Max(1, Mathf.RoundToInt(amount)), ap, projectile: evt.projectileDef);
                // Explosion can destroy structures outside the directly hit cell.
                buildingGeometryChanged = true;
            }
            else if (evt.defenderBuilding != null && !evt.defenderBuilding.Destroyed)
            {
                evt.defenderBuilding.TakeDamage(new DamageInfo(damType, amount, ap, -1f,
                    evt.launcher, null, evt.projectileDef));
                buildingGeometryChanged = evt.defenderBuilding.Destroyed;
            }

            if (part.stripSubstructureRadius > 0f)
            {
                // 破片溅射：按递减概率摧毁命中点周围基架（人员杀伤已由爆炸原生覆盖）。
                foreach (IntVec3 nearby in GenRadial.RadialCellsAround(cell, part.stripSubstructureRadius, true))
                {
                    if (!nearby.InBounds(map) || nearby == cell) continue;
                    float falloff = 1f - (nearby - cell).LengthHorizontal / part.stripSubstructureRadius;
                    if (Rand.Chance(part.stripSubstructureChance * falloff))
                    {
                        SubstructureDamageUtility.TryDestroy(map, nearby, instigator: evt.launcher);
                    }
                }
            }
            if (buildingGeometryChanged)
            {
                SubstructureOwnership.BumpStructureVersion(map);
                PlanetGlobeRenderer.InvalidateSilhouette(map);
            }
            return remaining;
        }

        private static float RemainingFraction(float budget, float absorbed) =>
            budget > 0f ? Mathf.Clamp01((budget - Mathf.Max(0f, absorbed)) / budget) : 0f;

        /// <summary>
        /// 每层先伤中央结构，再伤两侧；中央结构尚存时弹芯在该层停止。
        /// 空格可跨越，同一多格建筑只结算一次，避免同一物体被一发重复击中。
        /// </summary>
        private static bool ApplyDirectionalPenetration(SpaceCollision.ImpactEvent evt,
            ImpactOutcomePart part, Map map, IntVec3 entry, float amount, float ap, DamageDef damType)
        {
            bool destroyedBuilding = false;
            WalkPenetration(part, map, entry, evt.incidentDir, c => SolidAt(map, c),
                (target, factor) =>
                {
                    target.TakeDamage(new DamageInfo(damType, amount * factor,
                        ap, -1f, evt.launcher, null, evt.projectileDef));
                    destroyedBuilding |= target.Destroyed;
                }, target => !target.Destroyed);
            return destroyedBuilding;
        }

        internal static void WalkPenetration(ImpactOutcomePart part, Map map, IntVec3 entry,
            Vector2 direction, Func<IntVec3, Thing> solidAt, Action<Thing, float> damage, Func<Thing, bool> survives)
        {
            Vector2 dir = direction.normalized;
            if (dir.sqrMagnitude < 0.001f) dir = Vector2.up;
            Vector2 side = new Vector2(-dir.y, dir.x);
            Vector2 origin = new Vector2(entry.x + 0.5f, entry.z + 0.5f);
            var hitThings = new HashSet<Thing>();
            var hitCells = new HashSet<IntVec3>();
            IntVec3 center = entry;
            float travel = 0f;
            for (int depth = 0; depth < part.penetrationLayers.Count; depth++)
            {
                ImpactPenetrationLayer layer = part.penetrationLayers[depth];
                if (layer == null || layer.damageFactor <= 0f) continue;
                if (depth > 0)
                {
                    // 逐格穿越，斜入射不会因固定距离采样把同一格当作两层。
                    IntVec3 previous = center;
                    do
                    {
                        travel += 0.1f;
                        center = CellAt(origin + dir * travel);
                    } while (center == previous && travel < 2f * (depth + 1));
                }
                if (!center.InBounds(map)) break;
                Vector2 centerPoint = new Vector2(center.x + 0.5f, center.z + 0.5f);
                Thing centerThing = solidAt(center);
                bool centerStopped = false;
                for (int lane = 0; lane <= Mathf.Max(0, layer.halfWidth) * 2; lane++)
                {
                    int offset = lane == 0 ? 0 : (lane + 1) / 2 * (lane % 2 == 1 ? -1 : 1);
                    IntVec3 targetCell = offset == 0 ? center : CellAt(centerPoint + side * offset);
                    if (!targetCell.InBounds(map)) continue;
                    Thing target = offset == 0 ? centerThing : solidAt(targetCell);
                    if (target == null) continue;
                    if (hitCells.Add(targetCell) && hitThings.Add(target))
                    {
                        damage(target, layer.damageFactor);
                    }
                    if (offset == 0 && survives(target)) centerStopped = true;
                }
                if (centerStopped) break;
            }
        }

        private static IntVec3 CellAt(Vector2 point) => new IntVec3(
            Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));

        private static Thing SolidAt(Map map, IntVec3 cell) =>
            SpaceStructureRules.ClassifyCell(map, cell, out Thing solid) == SpaceCellKind.Empty
                ? null : solid;

        /// <summary>守方 building 的基准耐久（跳弹门槛与兜底耐久缩放的口径）。</summary>
        public static float MaxHitPointsOf(Thing thing)
        {
            return thing?.def.GetStatValueAbstract(StatDefOf.MaxHitPoints) ?? 100f;
        }

        private static void EnsureBuilt()
        {
            List<SpaceImpactEffectDef> defs = DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading;
            if (cachedDefCount == defs.Count) return;
            byTarget = new Dictionary<ThingDef, Dictionary<ThingDef, SpaceImpactEffectDef>>();
            substructureFor = new Dictionary<ThingDef, SpaceImpactEffectDef>();
            anyTargetFor = new Dictionary<ThingDef, SpaceImpactEffectDef>();
            globalFallback = null;
            for (int i = 0; i < defs.Count; i++)
            {
                SpaceImpactEffectDef def = defs[i];
                bool anyProjectile = def.projectileDefs.NullOrEmpty();
                bool anyTarget = def.targetThingDefs.NullOrEmpty();
                if (anyProjectile && anyTarget && !def.targetsSubstructure)
                {
                    globalFallback = def; // 空×空 = 全局兜底（同等耐久舱壁）
                    continue;
                }
                if (anyProjectile)
                {
                    Log.ErrorOnce($"[DeltaV] SpaceImpactEffectDef {def.defName}: wildcard projectile entries " +
                                  "must be the global fallback (both lists empty).", def.defName.GetHashCode());
                    continue;
                }
                foreach (ThingDef projectile in def.projectileDefs)
                {
                    if (def.targetsSubstructure)
                    {
                        substructureFor[projectile] = def;
                    }
                    else if (anyTarget)
                    {
                        anyTargetFor[projectile] = def;
                    }
                    else
                    {
                        foreach (ThingDef target in def.targetThingDefs)
                        {
                            if (!byTarget.TryGetValue(projectile, out Dictionary<ThingDef, SpaceImpactEffectDef> targets))
                            {
                                byTarget[projectile] = targets = new Dictionary<ThingDef, SpaceImpactEffectDef>();
                            }
                            targets[target] = def;
                        }
                    }
                }
            }
            cachedDefCount = defs.Count;
        }

        /// <summary>dev：重建查表（热重载 def 后）。</summary>
        public static void ResetCache()
        {
            cachedDefCount = -1;
        }
    }
}

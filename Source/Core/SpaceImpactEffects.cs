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
        PlaceBody = 4
    }

    /// <summary>命中判定的种类（二值：产生后效 / 放行穿过）。</summary>
    public enum ImpactHitTestKind : byte
    {
        /// <summary>恒产生后效。</summary>
        Always = 0,

        /// <summary>按相对速度的概率放行（射弹 × 基架：越快越难穿过；放行则弹体继续飞行）。</summary>
        SpeedChance = 1
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

        /// <summary>伤害 ∝ (v/v_ref)^exponent（1 = 线性）。</summary>
        public float speedExponent = 1f;

        /// <summary>入射角权重：正入射 ×1 → 掠射（90°）×此值。</summary>
        public float incidenceMinFactor = 0.35f;

        /// <summary>基准穿深（与伤害同系数缩放）。</summary>
        public float armorPenetration = 10f;

        /// <summary>爆炸半径（>0 = GenExplosion 对整图波及；0 = 只对被撞建筑 TakeDamage）。</summary>
        public float explosionRadius = 0f;

        /// <summary>破片摧毁基架的半径（>0 生效；中心概率按距离线性衰减到 0）。</summary>
        public float stripSubstructureRadius = 0f;

        /// <summary>破片在命中格处摧毁基架的概率（随距离线性衰减）。</summary>
        public float stripSubstructureChance = 0.5f;

        /// <summary>跳弹门槛：守方耐久 ≥ 此值才可能跳弹（0 = 该后效不判跳弹）。</summary>
        public float ricochetMinHitPoints = 0f;

        /// <summary>跳弹所需最小入射角（度；入射角 ≥ 此值且耐久达标 → 消灭无伤）。</summary>
        public float ricochetMinIncidenceDeg = 60f;
    }

    /// <summary>
    /// 联合效果注册表条目（太空战架构 §3.4）：键 =（弹体 building def × 对面
    /// building/基架），两段契约 = 命中判定（二值）+ 命中后效（攻守两侧各一份）。
    /// XML 可扩展；未注册的 def 对按「同等耐久舱壁」兜底条目结算（fallback 特例：
    /// projectileDefs 留空的条目即全局兜底）。本 def 族不持有运行时状态，纯数据。
    /// </summary>
    public class SpaceImpactEffectDef : Def
    {
        /// <summary>匹配的弹体 building def（空 = 任意弹体 → 全局兜底条目）。</summary>
        public List<ThingDef> projectileDefs;

        /// <summary>本条目以基架格为目标（命中判定走 Substructure 语义）。</summary>
        public bool targetsSubstructure;

        /// <summary>匹配的守方 building def（空 = 该弹体的任意目标兜底）。</summary>
        public List<ThingDef> targetThingDefs;

        public ImpactHitTestKind hitTest = ImpactHitTestKind.Always;

        // ---- SpeedChance 放行参数 ----

        /// <summary>基准速度下的放行概率（0–1；放行 = 弹体穿过、下次碰撞再判定）。</summary>
        public float passThroughChance = 0.5f;

        /// <summary>放行判定的基准相对速度（格/s）。</summary>
        public float passThroughReferenceSpeed = 60f;

        /// <summary>放行 ∝ (v/v_ref)^exponent（负值 = 越快越难穿）。</summary>
        public float passThroughSpeedExponent = -1f;

        /// <summary>攻方（弹体）一侧的后效。</summary>
        public ImpactOutcomePart attackerEffect;

        /// <summary>守方（被撞容器）一侧的后效。</summary>
        public ImpactOutcomePart defenderEffect;

        /// <summary>
        /// 兜底条目专用：基准舱壁耐久。守方伤害按「基准耐久 / 守方耐久」缩放
        ///（同等耐久舱壁规则：耐久只换参数，不换规则）。
        /// </summary>
        public float fallbackBaselineHitPoints = 100f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
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
    public static class SpaceImpactEffects
    {
        private static Dictionary<ThingDef, Dictionary<ThingDef, SpaceImpactEffectDef>> byTarget;
        private static Dictionary<ThingDef, SpaceImpactEffectDef> substructureFor;
        private static Dictionary<ThingDef, SpaceImpactEffectDef> anyTargetFor;
        private static SpaceImpactEffectDef globalFallback;
        private static int cachedDefCount = -1;

        /// <summary>
        /// 解析一条 def 对的效果。targetDef = null 表示命中基架格。
        /// 查表链：精确对 → 该弹体 × 基架 → 该弹体 × 任意目标 → 全局兜底（同等耐久
        /// 舱壁规则；兜底条目在 XML 以空 projectileDefs 声明，耐久缩放在应用时进行）。
        /// </summary>
        public static SpaceImpactEffectDef Resolve(ThingDef projectile, ThingDef targetDef)
        {
            EnsureBuilt();
            if (projectile != null)
            {
                if (targetDef != null
                    && byTarget.TryGetValue(projectile, out Dictionary<ThingDef, SpaceImpactEffectDef> targets)
                    && targets.TryGetValue(targetDef, out SpaceImpactEffectDef exact))
                {
                    return exact;
                }
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
        /// 命中判定（二值）：true = 产生后效；false = 放行穿过（弹体继续飞行，下次
        /// 碰撞再判定）。SpeedChance 按相对速度联合判定。
        /// </summary>
        public static bool HitTest(SpaceImpactEffectDef effect, float relativeSpeed)
        {
            if (effect == null) return true;
            if (effect.hitTest != ImpactHitTestKind.SpeedChance) return true;
            float passChance = effect.passThroughChance
                * Mathf.Pow(relativeSpeed / Mathf.Max(1f, effect.passThroughReferenceSpeed),
                    effect.passThroughSpeedExponent);
            return !Rand.Chance(Mathf.Clamp01(passChance));
        }

        /// <summary>
        /// 后效量计算（单实现）：基准 × 速度因子 × 入射角因子（× 兜底耐久缩放）。
        /// DamageMap 的伤害与 ShieldDrain 的容量消耗共用同一缩放口径。
        /// </summary>
        public static float ComputeDamage(ImpactOutcomePart part, float relativeSpeed,
            float incidenceAngleRad, float durabilityScale = 1f)
        {
            if (part == null) return 0f;
            float speedFactor = Mathf.Pow(
                Mathf.Max(relativeSpeed, 0.01f) / Mathf.Max(1f, part.referenceSpeed), part.speedExponent);
            float incidenceFactor = Mathf.Lerp(1f, part.incidenceMinFactor,
                Mathf.Clamp01(incidenceAngleRad / (Mathf.PI * 0.5f)));
            return part.damage * speedFactor * incidenceFactor * durabilityScale;
        }

        /// <summary>
        /// 守方后效应用（原版伤害原语单点）：伤害 = 基准 × 速度因子 × 入射角因子
        ///（× 兜底耐久缩放）。跳弹（高耐久 ∧ 斜入射）= 消灭无伤——由攻方 DestroySelf
        /// 后效完成「消灭」，本方法不施加伤害。基架格的「伤害」= 摧毁命中处基架
        ///（含破片递减半径）。护盾容量消耗 = 直写原版 comp（EMP/击破/重充沿用原版状态机）。
        /// </summary>
        public static void ApplyToDefender(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part,
            float durabilityScale = 1f)
        {
            if (part == null || evt.defender?.map == null) return;
            Map map = evt.defender.map;

            if (part.kind == ImpactEffectKind.ShieldDrain)
            {
                // 护盾容量消耗（段5）：写原版 comp 字段；重充/击破语义由原版
                // CompGravshipShieldGenerator 的充能节拍继续推进。
                CompProjectileInterceptor interceptor = evt.defenderBuilding?.TryGetComp<CompProjectileInterceptor>();
                if (interceptor == null) return;
                float drain = ComputeDamage(part, evt.relativeSpeed, evt.incidenceAngleRad, durabilityScale)
                    * evt.damageScale;
                interceptor.currentHitPoints = Mathf.Max(0,
                    interceptor.currentHitPoints - Mathf.Max(1, Mathf.RoundToInt(drain)));
                return;
            }
            if (part.kind != ImpactEffectKind.DamageMap) return;

            var cell = new IntVec3(Mathf.FloorToInt(evt.hitDefenderLocal.x), 0,
                Mathf.FloorToInt(evt.hitDefenderLocal.y)).ClampInsideMap(map);

            // 跳弹（当前默认实现 = 消灭无伤；可反弹类型留扩展位，太空战架构 §3.4）：
            // 守方耐久达门槛 ∧ 入射角够歪 → 不施加伤害（弹体的 DestroySelf 照常执行）。
            if (part.ricochetMinHitPoints > 0f && evt.defenderBuilding != null && !evt.defenderBuilding.Destroyed)
            {
                float maxHitPoints = MaxHitPointsOf(evt.defenderBuilding);
                if (maxHitPoints >= part.ricochetMinHitPoints
                    && evt.incidenceAngleRad >= part.ricochetMinIncidenceDeg * Mathf.Deg2Rad)
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"ricochet: {evt.projectileDef?.defName} off '{evt.defenderBuilding.def.defName}' " +
                        $"(hp {maxHitPoints:F0}, incidence {evt.incidenceAngleRad * Mathf.Rad2Deg:F0}°, v_rel {evt.relativeSpeed:F1})");
                    return;
                }
            }

            float amount = ComputeDamage(part, evt.relativeSpeed, evt.incidenceAngleRad, durabilityScale)
                * evt.damageScale;
            float ap = part.armorPenetration * (amount / Mathf.Max(1f, part.damage));
            DamageDef damType = part.damageDef ?? DamageDefOf.Bullet;

            if (part.explosionRadius > 0f)
            {
                // 爆炸：波及（人员杀伤/建筑毁伤）交原版 GenExplosion 原生处理。
                GenExplosion.DoExplosion(cell, map, part.explosionRadius, damType, evt.launcher,
                    Mathf.Max(1, Mathf.RoundToInt(amount)), ap, projectile: evt.projectileDef);
            }
            else if (evt.defenderBuilding != null && !evt.defenderBuilding.Destroyed)
            {
                evt.defenderBuilding.TakeDamage(new DamageInfo(damType, amount, ap, -1f,
                    evt.launcher, null, evt.projectileDef));
            }
            else if (evt.defenderIsSubstructure)
            {
                // 基架单体命中（无爆炸参数时的「固定伤害」语义）：摧毁命中格基架。
                StripSubstructureAt(map, cell);
            }

            if (part.stripSubstructureRadius > 0f)
            {
                // 破片溅射：按递减概率摧毁命中点周围基架（人员杀伤已由爆炸原生覆盖）。
                foreach (IntVec3 nearby in GenRadial.RadialCellsAround(cell, part.stripSubstructureRadius, true))
                {
                    if (!nearby.InBounds(map)) continue;
                    float falloff = 1f - (nearby - cell).LengthHorizontal / part.stripSubstructureRadius;
                    if (Rand.Chance(part.stripSubstructureChance * falloff))
                    {
                        StripSubstructureAt(map, nearby);
                    }
                }
            }
            SubstructureOwnership.BumpStructureVersion();
            PlanetGlobeRenderer.InvalidateSilhouette(map);
        }

        /// <summary>守方 building 的基准耐久（跳弹门槛与兜底耐久缩放的口径）。</summary>
        public static float MaxHitPointsOf(Thing thing)
        {
            return thing?.def.GetStatValueAbstract(StatDefOf.MaxHitPoints) ?? 100f;
        }

        /// <summary>
        /// 摧毁命中格基架。原版 SetFoundation 是「放置」API（null 只红字不动网格），
        /// 移除唯一入口是 RemoveFoundation（doLeavings:false——战斗剥离不掉资源，破片
        /// 溅射逐格掉落会刷屏）。归属同步由 RemoveFoundation postfix 完成（含版本 bump）。
        /// </summary>
        private static void StripSubstructureAt(Map map, IntVec3 cell)
        {
            if (map.terrainGrid.FoundationAt(cell)?.IsSubstructure == true)
            {
                map.terrainGrid.RemoveFoundation(cell, doLeavings: false);
            }
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

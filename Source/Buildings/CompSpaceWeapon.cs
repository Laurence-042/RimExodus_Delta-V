using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>武器组套别（主/副，太空战架构 §5.5）。</summary>
    public enum SpaceWeaponSalvoSet : byte
    {
        Main = 0,
        Secondary = 1
    }

    /// <summary>武器瞄准模式：齐射（随驾驶瞄准模式射击）/ 点防御（自动索敌可拦截容器）。</summary>
    public enum SpaceWeaponAimMode : byte
    {
        Volley = 0,
        PointDefense = 1
    }

    public enum SpaceWeaponEngagementMode : byte
    {
        Space = 0,
        Ground = 1
    }

    /// <summary>插件建造格与视觉参数。偏移以朝北时的格心为原点，单位为地图格。</summary>
    public class SpaceWeaponAttachmentSlot
    {
        public IntVec3 cell;
        public Vector2 drawOffset = Vector2.zero;
        public float drawScale = 1f;
    }

    /// <summary>
    /// 太空武器 comp（太空战架构 §5.1）：可切换炮塔在地面模式走原版炮塔 Verb，
    /// 太空模式走本 comp + 弹体服务；两种模式互斥。弹道类型由
    /// projectileDef 引用的弹体 building def 决定（直射/激光/导弹，§5.1 单一事实源）。
    /// 组别（套别+组号）、PD 模式与安全限制是本 comp 的存档事实；炮手预留同存本 comp，
    /// 弹仓优先级属于 Magazine；冷却持久化，本 tick 发射为运行时。
    /// 武器必须位于基架格上（含墙下基架）——归属随基架、随控制域捕获搬运。
    /// </summary>
    public class CompProperties_SpaceWeapon : CompProperties
    {
        /// <summary>发射的弹体 building def（联合效果注册表键与弹道参数来源）。</summary>
        public ThingDef projectileDef;

        /// <summary>默认瞄准模式（齐射/PD；玩家可经 gizmo 切换，当前值随 comp 存档）。</summary>
        public SpaceWeaponAimMode aimMode = SpaceWeaponAimMode.Volley;

        /// <summary>固定为自动点防御，不提供齐射模式。</summary>
        public bool pointDefenseOnly;
        public bool casemate;
        public int projectilesPerShot = 1;
        public float projectileFanHalfAngle;
        public SpaceObjectTag defaultPdTargets = SpaceObjectTag.Projectile | SpaceObjectTag.Shuttle;

        /// <summary>工程设备只消费玩家手动齐射，不参加自动攻击和点防。</summary>
        public bool manualOnly;
        public bool tractor;

        public SpaceWeaponSalvoSet defaultSalvoSet = SpaceWeaponSalvoSet.Main;
        public int defaultGroupNumber = 1;

        /// <summary>跨墙感知：true = 顶部炮塔（弹排除己方容器）；false = 舰中固定（可自伤）。</summary>
        public bool topTurret = true;

        /// <summary>射界（度，绕挂载朝向对称；360 = 全向）。</summary>
        public float fireArcDeg = 360f;

        /// <summary>目标锁定范围；负数表示与 fireArcDeg 相同。冷发射导弹可固定轴、全向锁定。</summary>
        public float lockArcDeg = -1f;

        /// <summary>基础散射角（度；实际偏移 × 技能偏差系数，§5.4）。</summary>
        public float baseSpreadDeg = 2f;

        /// <summary>发射冷却（tick）。</summary>
        public int cooldownTicks = 90;

        /// <summary>射程（格；PD 索敌与导弹沿射线锁定目标的范围）。</summary>
        public float range = 120f;

        /// <summary>太空弹道从炮口算起的最小有效距离（格）。</summary>
        public float minRange;

        /// <summary>是否允许在原版同图炮塔和跨容器弹道之间切换。</summary>
        public bool groundModeAvailable;

        /// <summary>逐发从本武器所在电网蓄电池提取的能量（W·d）；持续功耗仍由 CompPowerTrader 负责。</summary>
        public float shotEnergy;
        public float launchKineticEnergy;

        /// <summary>本武器对联合命中效果的倍率；弹体 Def 仍是效果注册表键。</summary>
        public float impactScale = 1f;

        /// <summary>内置火控技能；负数表示仍需炮手或自瞄插件。</summary>
        public int intrinsicAimSkill = -1;

        /// <summary>朝北局部坐标中的炮口连续偏移（相对脚印中心）。</summary>
        public Vector2 muzzleOffset = Vector2.zero;

        /// <summary>旋转炮身沿当前朝向从脚印中心到炮口的距离（格）。</summary>
        public float turretMuzzleDistance;

        /// <summary>成功发射时的炮口闪光尺寸；0 表示无。</summary>
        public float muzzleFlashSize;
        /// <summary>Visual top recoil only; the building, muzzle solution and physics remain fixed.</summary>
        public float recoilDistance;
        public int recoilTicks = 18;

        /// <summary>覆盖挂件槽：同一条目决定建造格及该格上的插件贴图中心和缩放。</summary>
        public List<SpaceWeaponAttachmentSlot> attachmentSlots = new List<SpaceWeaponAttachmentSlot>();

        /// <summary>
        /// 可安装的挂件 Def。空列表表示开放槽，供其他 Mod 直接通过挂件 comp 接入；
        /// 非空时既约束放置，也约束运行时发现。
        /// </summary>
        public List<ThingDef> allowedAttachmentDefs = new List<ThingDef>();

        /// <summary>通用加速链第一部件的 root 位置；空列表表示不接受串联加速件。</summary>
        public List<IntVec3> acceleratorStarts = new List<IntVec3>();

        public List<ThingDef> allowedAcceleratorDefs = new List<ThingDef>();

        /// <summary>默认启用自身结构发射路径预检。</summary>
        public bool safeFireByDefault = true;
        public bool requireHullClearance;

        /// <summary>自动火控发射方向与目标方向允许的最大夹角（度）。</summary>
        public float defaultFireDeviationDeg = 3f;

        /// <summary>手控默认夹角；负数沿用自动默认值。</summary>
        public float defaultManualFireDeviationDeg = -1f;
        public float ManualFireDeviationDefault => defaultManualFireDeviationDeg >= 0f
            ? defaultManualFireDeviationDeg : defaultFireDeviationDeg;

        /// <summary>覆盖弹体默认冷发射时长；负数表示使用弹体 Def。</summary>
        public int ignitionDelayTicks = -1;

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            var instance = req.Thing?.TryGetComp<CompSpaceWeapon>();
            bool continuous = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()?.continuousBeam == true;
            if (impactScale != 1f)
                yield return SpaceCombatStats.Entry("DeltaV_StatImpactScale",
                    impactScale.ToString("0.##") + "×", 5110);
            if (shotEnergy > 0f)
                yield return SpaceCombatStats.Entry(continuous ? "DeltaV_StatBeamEnergy" : "DeltaV_StatShotEnergy",
                    SpaceCombatStats.WattDays(continuous ? shotEnergy * 60f : instance?.ShotEnergy ?? shotEnergy), 5100);
            if (!continuous)
                yield return SpaceCombatStats.Entry("DeltaV_StatCooldown",
                    SpaceCombatStats.Seconds(instance?.ShotInterval ?? cooldownTicks), 5090);
            yield return SpaceCombatStats.Entry("DeltaV_StatRange", range.ToString("0"), 5080);
            if (minRange > 0f)
                yield return SpaceCombatStats.Entry("DeltaV_StatMinRange", minRange.ToString("0"), 5079);
            if (!attachmentSlots.NullOrEmpty())
                yield return SpaceCombatStats.Entry("DeltaV_StatAttachmentSlots",
                    attachmentSlots.Count.ToString(), 5070);
        }

        public CompProperties_SpaceWeapon()
        {
            compClass = typeof(CompSpaceWeapon);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>()?.continuousBeam == true
                && cooldownTicks != 1)
                yield return $"{parentDef.defName}: continuous beams require cooldownTicks = 1";
            var projectile = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (projectilesPerShot < 1 || projectileFanHalfAngle < 0f
                || projectilesPerShot > 1 && (projectile == null || projectile.hasMap || projectile.guided || projectile.laserRange > 0f))
                yield return $"{parentDef.defName}: projectile fans require positive counts and mapless unguided projectiles";
        }
    }

    public partial class CompSpaceWeapon : ThingComp
    {
        // ---- 持久配置（存档事实；gizmo 修改）----

        public SpaceWeaponSalvoSet salvoSet = SpaceWeaponSalvoSet.Main;
        public int groupNumber = 1;
        public SpaceWeaponAimMode aimMode = SpaceWeaponAimMode.Volley;
        public SpaceWeaponEngagementMode engagementMode = SpaceWeaponEngagementMode.Space;
        public bool safeFire = true;
        internal SpaceObjectTag pdTargets = SpaceObjectTag.Projectile | SpaceObjectTag.Shuttle;
        internal bool AcceptsPointDefenseTarget(SpaceObject target)
            => target?.InterceptionEligible == true && (target.InterceptionTargetTag & pdTargets) != 0;
        public bool keepGunnerWhenUnavailable = true;
        public Pawn reservedGunner;
        public float fireDeviationDeg;
        public float manualFireDeviationDeg;
        private bool safeFireInitialized;
        private bool alignmentConfigurationInitialized;
        private bool configurationInitialized;
        private int lastMagazineThingId = -1;
        private int visualShotTick = -10000;
        private float visualShotAngle;
        public void NotifyVisualShot(float angle)
        {
            visualShotTick = Find.TickManager.TicksGame;
            visualShotAngle = angle;
        }

        public Vector3 VisualRecoilOffset
        {
            get
            {
                if (!SpaceModeEnabled || WeaponProps.recoilDistance <= 0f) return Vector3.zero;
                int age = Find.TickManager.TicksGame - visualShotTick;
                if (age < 0 || age >= WeaponProps.recoilTicks) return Vector3.zero;
                float amount = age < 2 ? (age + 1f) / 2f
                    : Mathf.Pow(1f - (age - 2f) / Mathf.Max(1, WeaponProps.recoilTicks - 2), 2f);
                Vector2 back = SpaceTransform.Rotate(Vector2.left, visualShotAngle)
                    * (WeaponProps.recoilDistance * amount);
                return new Vector3(back.x, 0f, back.y);
            }
        }

        public void ResetPrefabIdentity()
        {
            lastMagazineThingId = -1;
            cooldownUntilTick = 0;
            committedCooldownTicks = 0;
            visualShotTick = -10000;
        }

        public int cooldownUntilTick;
        private int committedCooldownTicks;
        internal int CommittedCooldownTicks => committedCooldownTicks;
        internal int lastBeamTriggerTick = -1;
        public CompWeaponTuning Tuning => Component<CompWeaponTuning>();
        public int ShotInterval
        {
            get
            {
                if (Tuning is { } tuning) return tuning.ShotInterval;
                int baseline = WeaponProps.cooldownTicks;
                if (WeaponProps.attachmentSlots.NullOrEmpty()) return baseline;
                float multiplier = 1f;
                foreach (var thing in Layout.attachments)
                    if (thing.TryGetComp<CompSpaceWeaponAttachment>() is { Enabled: true } attachment)
                        multiplier *= attachment.ShotIntervalMultiplier;
                return multiplier == 1f ? baseline : Mathf.Max(1, Mathf.RoundToInt(baseline * multiplier));
            }
        }

        internal void CommitCooldown(int tick, int interval)
        {
            committedCooldownTicks = interval;
            cooldownUntilTick = tick + interval;
        }
        public float ShotEnergy => WeaponProps.shotEnergy * (Tuning?.power ?? 1);
        public int CooldownTicksLeft => Tuning?.RemainingTicks ?? Mathf.Max(0, cooldownUntilTick - Find.TickManager.TicksGame);

        public CompProperties_SpaceWeapon WeaponProps => (CompProperties_SpaceWeapon)props;

        public bool SpaceModeEnabled => !WeaponProps.groundModeAvailable
            || engagementMode == SpaceWeaponEngagementMode.Space;

        public bool GroundModeEnabled => WeaponProps.groundModeAvailable
            && engagementMode == SpaceWeaponEngagementMode.Ground;

        public UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        /// <summary>武器所在图的承载容器（非太空图为 null = 太空射击不适用）。</summary>
        public SpaceObject Container => parent?.Map != null ? Universe?.FrameOf(parent.Map) : null;

        /// <summary>武器挂载朝向的地图帧角（射界基准 = 建筑朝向）。</summary>
        public float FacingAngle => Component<CompSpaceWeaponTraverse>()?.CurrentAngle
            ?? ShipOrientation.AngleFromRot4(parent.Rotation);

        public CompMannable Mannable => Component<CompMannable>();

        private ShipBase cachedOwner;
        private Map ownerMap;
        private IntVec3 ownerPosition;
        private Rot4 ownerRotation;
        private int ownerVersion;
        private bool ownerCacheValid, ownerWasValid;

        /// <summary>
        /// 本武器所属船：船体内置武器归于自身，独立建筑按基架域解析；无船则 Invalid。
        /// </summary>
        public ShipBase OwnerShip
        {
            get
            {
                Map map = parent?.Map;
                if (map == null || map.Disposed || parent.Destroyed) return ShipBase.Invalid;
                int version = SubstructureOwnership.StructureVersion(map);
                if (!ownerCacheValid || ownerMap != map || ownerVersion != version
                    || ownerPosition != parent.Position || ownerRotation != parent.Rotation
                    || ownerWasValid && !cachedOwner.IsValid)
                {
                    // 独立于连接布局：纯指向不能因归属查询发现挂件、弹仓或加速链。
                    cachedOwner = ShipRegistry.ShipOf(parent, requireWholeFootprint: true);
                    ownerMap = map; ownerVersion = version;
                    ownerPosition = parent.Position; ownerRotation = parent.Rotation;
                    ownerWasValid = cachedOwner.IsValid;
                    // 未注册地图没有完整的结构通知，不能缓存其归属结论。
                    ownerCacheValid = Container != null;
                }
                return cachedOwner;
            }
        }

        /// <summary>本武器的组是否激活（读取所属船的共同火控状态，随身份锚搬运）。</summary>
        public bool GroupActive
        {
            get
            {
                ShipBase ship = OwnerShip;
                if (!ship.IsValid) return false;
                int mask = ship.Combat?.WeaponGroupMask(salvoSet) ?? 0;
                return GroupEnabled(mask);
            }
        }

        internal bool GroupEnabled(int mask) => (mask & (1 << (groupNumber - 1))) != 0;

        /// <summary>当前炮手（原版 CompMannable 在册者）。</summary>
        public Pawn CurrentGunner => Mannable?.ManningPawn;

        /// <summary>所属船的武装总开关；跟随船体身份锚，不绑定当前 Map。</summary>
        public bool Armed => OwnerShip.IsValid && OwnerShip.Combat?.weaponsArmed == true;

        /// <summary>炮手、自瞄或内置火控中的任一真实来源。</summary>
        public bool HasFireControl => Mannable == null || CurrentGunner != null || AutoAimEffective;

        /// <summary>待机耗电意图不把 PowerOn 当输入，避免“先有电还是先申请负载”的循环。</summary>
        public bool WantsStandbyPower => Armed && HasFireControl
            && parent.TryGetComp<CompFlickable>() is not { SwitchIsOn: false };

        /// <summary>转向和射击共用的硬件启用口径；弹药、冷却、射界与武器组另行判定。</summary>
        public bool Operational => WantsStandbyPower
            && parent.TryGetComp<CompPowerTrader>() is not { PowerOn: false };

        internal void SyncStandbyPower()
        {
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            if (power == null) return;
            float output = WantsStandbyPower ? -power.Props.PowerConsumption : 0f;
            if (Mathf.Abs(power.PowerOutput - output) > 0.01f) power.PowerOutput = output;
        }

        public SpaceWeaponAttachmentSlot AttachmentSlotAt(IntVec3 cell)
        {
            foreach (SpaceWeaponAttachmentSlot slot in WeaponProps.attachmentSlots)
                if (SpaceWeaponGeometry.RotatedCell(parent, slot.cell) == cell) return slot;
            return null;
        }

        private SpaceWeaponLayout layout;
        internal readonly SpaceWeaponEnergy QueryEnergy = new SpaceWeaponEnergy();
        internal readonly SpaceWeaponEnergy ExecutionEnergy = new SpaceWeaponEnergy();
        internal bool executingControl;
        internal SpaceWeaponLayout Layout
        {
            get
            {
                if (layout == null || !layout.Matches(this))
                {
                    var perf = Universe?.Performance;
                    long start = perf?.Start() ?? 0L;
                    layout = new SpaceWeaponLayout(this);
                    perf?.Record(PerformanceStage.WeaponLayout, start);
                }
                return layout;
            }
        }
        internal void ClearRuntimeModel()
        {
            layout = null; cachedOwner = null; ownerMap = null; ownerCacheValid = ownerWasValid = false;
            QueryEnergy.Clear(); ExecutionEnergy.Clear();
        }
        public IEnumerable<ThingWithComps> AttachedThings() => Layout.attachments;

        internal IEnumerable<ThingWithComps> DiscoverAttachedThings()
        {
            if (parent.Map == null || WeaponProps.attachmentSlots.NullOrEmpty()) yield break;
            int owner = SpaceWeaponGeometry.OwnerIdOf(parent);
            var seen = new HashSet<int>();
            foreach (SpaceWeaponAttachmentSlot slot in WeaponProps.attachmentSlots)
            {
                IntVec3 cell = SpaceWeaponGeometry.RotatedCell(parent, slot.cell);
                if (!parent.OccupiedRect().Contains(cell)) continue;
                if (!cell.InBounds(parent.Map)) continue;
                foreach (Thing thing in parent.Map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing == parent || thing is not ThingWithComps comps || thing.Destroyed) continue;
                    CompSpaceWeaponAttachment attachment = comps.TryGetComp<CompSpaceWeaponAttachment>();
                    if (attachment == null || attachment.HostWeapon != this
                        || !attachment.CompatibleWith(parent.def)) continue;
                    if (!WeaponProps.allowedAttachmentDefs.NullOrEmpty()
                        && !WeaponProps.allowedAttachmentDefs.Contains(thing.def)) continue;
                    if (owner > 0 && SpaceWeaponGeometry.OwnerIdOf(thing) != owner) continue;
                    if (!seen.Add(thing.thingIDNumber)) continue;
                    yield return comps;
                }
            }
        }

        public bool AutoAimEffective
        {
            get
            {
                if (WeaponProps.intrinsicAimSkill >= 0) return true;
                foreach (ThingWithComps attached in AttachedThings())
                {
                    CompSpaceWeaponAttachment attachment = attached.TryGetComp<CompSpaceWeaponAttachment>();
                    if (attachment?.AimSkillFor(this) != null) return true;
                }
                return false;
            }
        }

        // ---- 炮塔状态变更事件源（事件驱动补分配，见 ShipControlService.NotifyGunnerAvailabilityChanged）----

        /// <summary>
        /// 供电边沿登记到 controller；下一 tick 释放断电岗位并重新分配空闲炮手。
        /// </summary>
        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff")
            {
                Universe?.Controls?.NotifyGunnerAvailabilityChanged(parent.Map);
            }
        }

        /// <summary>
        /// 重放置完成（起飞/对接搬运 StripDomain → PlaceIntoMap 的 GenSpawn 收尾；
        /// DeSpawn 已使 FailOnDespawnedNullOrForbidden 丢掉炮手 job，原版无召回）。
        /// 读档重建不主动派遣；持久武装状态由 controller 重建后的状态事件接回。
        /// </summary>
        public override void Initialize(CompProperties properties)
        {
            base.Initialize(properties);
            pdTargets = WeaponProps.defaultPdTargets;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            ClearRuntimeModel();
            base.PostSpawnSetup(respawningAfterLoad);
            if (!configurationInitialized)
            {
                salvoSet = WeaponProps.defaultSalvoSet;
                groupNumber = Mathf.Clamp(WeaponProps.defaultGroupNumber, 1, 6);
                aimMode = WeaponProps.aimMode;
                configurationInitialized = true;
            }
            if (!safeFireInitialized)
            {
                safeFire = WeaponProps.safeFireByDefault;
                safeFireInitialized = true;
            }
            if (!alignmentConfigurationInitialized)
            {
                fireDeviationDeg = Mathf.Clamp(WeaponProps.defaultFireDeviationDeg, 0f, 180f);
                manualFireDeviationDeg = Mathf.Clamp(WeaponProps.ManualFireDeviationDefault, 0f, 180f);
                alignmentConfigurationInitialized = true;
            }
            Universe?.Weapons?.RegisterWeapon(this);
            SyncStandbyPower();
            if (respawningAfterLoad) return;
            Universe?.Controls?.NotifyGunnerAvailabilityChanged(parent.Map);
        }

        public override void CompTick()
        {
            base.CompTick();
            SyncStandbyPower();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            ClearRuntimeModel();
            base.PostDeSpawn(map, mode);
            Universe?.Weapons?.UnregisterWeapon(this, map);
            Universe?.Controls?.NotifyGunnerAvailabilityChanged(map);
        }

        /// <summary>
        /// 开火技能来源（§5.4）：自动瞄准插件与炮手 shooting 取高者；
        /// 两者皆无且存在 Mannable 时不满足开火条件。插件必须覆盖声明槽位并与武器
        /// 同归属基架（生效判定收口 CompSpaceAutoAimPlugin）。
        /// </summary>
        public bool TryGetSkillLevel(out int skillLevel)
        {
            int? gunnerSkill = null;
            Pawn gunner = CurrentGunner;
            if (gunner?.skills != null)
            {
                gunnerSkill = gunner.skills.GetSkill(SkillDefOf.Shooting).Level;
            }
            int? pluginSkill = WeaponProps.intrinsicAimSkill >= 0
                ? WeaponProps.intrinsicAimSkill : null;
            if (parent.Map != null)
            {
                foreach (ThingWithComps attached in AttachedThings())
                {
                    CompSpaceWeaponAttachment attachment = attached.TryGetComp<CompSpaceWeaponAttachment>();
                    int? aimSkill = attachment?.AimSkillFor(this);
                    if (aimSkill.HasValue) pluginSkill = Mathf.Max(pluginSkill ?? 0, aimSkill.Value);
                }
            }
            // Mannable 不存在表示武器自带基础火控；第三方只加核心 Comp 即可工作。
            if (Mannable == null && pluginSkill == null && gunnerSkill == null)
            {
                skillLevel = 10;
                return true;
            }
            if (gunnerSkill == null && pluginSkill == null)
            {
                skillLevel = 0;
                return false;
            }
            skillLevel = Mathf.Max(gunnerSkill ?? 0, pluginSkill ?? 0);
            return true;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref coverage, "combatCoverage");
            coverage ??= new CombatWeaponCoverage();
            Scribe_Values.Look(ref salvoSet, "salvoSet", WeaponProps.defaultSalvoSet);
            Scribe_Values.Look(ref groupNumber, "groupNumber", WeaponProps.defaultGroupNumber);
            Scribe_Values.Look(ref aimMode, "aimMode", WeaponProps.aimMode);
            Scribe_Values.Look(ref engagementMode, "engagementMode", SpaceWeaponEngagementMode.Space);
            Scribe_Values.Look(ref configurationInitialized, "configurationInitialized", true);
            Scribe_Values.Look(ref safeFire, "safeFire", true);
            Scribe_Values.Look(ref pdTargets, "pdTargets", WeaponProps.defaultPdTargets);
            Scribe_Values.Look(ref keepGunnerWhenUnavailable, "keepGunnerWhenUnavailable", true);
            Scribe_References.Look(ref reservedGunner, "reservedGunner");
            Scribe_Values.Look(ref safeFireInitialized, "safeFireInitialized", true);
            Scribe_Values.Look(ref fireDeviationDeg, "fireDeviationDeg",
                WeaponProps.defaultFireDeviationDeg);
            Scribe_Values.Look(ref manualFireDeviationDeg, "manualFireDeviationDeg",
                WeaponProps.ManualFireDeviationDefault);
            Scribe_Values.Look(ref alignmentConfigurationInitialized,
                "alignmentConfigurationInitialized", true);
            Scribe_Values.Look(ref lastMagazineThingId, "lastMagazineThingId", -1);
            Scribe_Values.Look(ref cooldownUntilTick, "cooldownUntilTick", 0);
            Scribe_Values.Look(ref committedCooldownTicks, "committedCooldownTicks", 0);
        }

        public CompSpaceWeaponMagazine SelectMagazine()
        {
            var perf = Universe?.Performance;
            long start = perf?.Start() ?? 0L;
            try
            {
                CompSpaceWeaponMagazine first = null, next = null;
                int priority = int.MinValue;
                foreach (var magazine in Layout.magazines)
                {
                    var attachment = magazine.parent == parent ? null
                        : magazine.parent.TryGetComp<CompSpaceWeaponAttachment>();
                    if (!magazine.HasHeads(magazine.RequiredHeads(this)) || attachment != null && !attachment.Enabled) continue;
                    if (magazine.Priority < priority) continue;
                    if (magazine.Priority > priority)
                    { priority = magazine.Priority; first = next = null; }
                    int id = magazine.parent.thingIDNumber;
                    if (first == null || id < first.parent.thingIDNumber) first = magazine;
                    if (id > lastMagazineThingId && (next == null || id < next.parent.thingIDNumber)) next = magazine;
                }
                return next ?? first;
            }
            finally { perf?.Record(PerformanceStage.WeaponAmmo, start); }
        }

        internal bool MagazineAvailable(CompSpaceWeaponMagazine selected) =>
            (Component<CompSpaceWeaponMagazine>() == null || selected != null)
            && (Tuning?.Settings.chemical != true || Component<CompSpaceWeaponMagazine>()?.HasPropellant(Tuning.charge) == true);

        public void NotifyMagazineConsumed(CompSpaceWeaponMagazine magazine)
        {
            if (magazine != null) lastMagazineThingId = magazine.parent.thingIDNumber;
            var next = SelectMagazine();
            if (next?.SelectedProjectileDef != magazine?.SelectedProjectileDef
                || next?.MagazineProps.unitMass != magazine?.MagazineProps.unitMass
                || next?.MagazineProps.armorPenetrationScale != magazine?.MagazineProps.armorPenetrationScale)
                NotifyParametersChanged();
        }

        internal void NotifyParametersChanged() => Universe?.Controls.ExistingFor(OwnerShip)?.FireControl.WeaponParametersChanged(this);

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Universe;
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (universe == null || !parent.Spawned || !playerSide)
            {
                yield break;
            }

            yield return new Command_BatchSetting<CompSpaceWeapon>("WeaponCoverage", this,
                weapons => Find.WindowStack.Add(new Dialog_WeaponCoverage(weapons)))
            {
                defaultLabel = "DeltaV_WeaponCoverage".Translate(), defaultDesc = "DeltaV_WeaponCoverageHelp".Translate(),
                icon = TexCommand.Attack
            };

            if (WeaponProps.groundModeAvailable) yield return new Command_BatchToggle<CompSpaceWeapon>(
                "GroundMode", this, w => w.GroundModeEnabled, (w, active) =>
                {
                    w.engagementMode = active
                        ? SpaceWeaponEngagementMode.Ground : SpaceWeaponEngagementMode.Space;
                    (w.parent as Building_SpaceTurretGun)?.NotifyEngagementModeChanged();
                    w.Universe?.Weapons?.NotifyWeaponModeChanged(w);
                })
            {
                defaultLabel = "DeltaV_GroundMode".Translate(),
                defaultDesc = "DeltaV_GroundModeTip".Translate(),
                icon = TexCommand.FireAtWill,
            };

            // 武器组指派（主/副两套 × 1–6）：弹出菜单选择，当前项打勾（取代旧循环
            // 步进——12 个组合不该靠单击轮转）。组的激活位在状态面板切换。
            yield return new Command_WeaponGroup(this);

            // 专用点防武器的模式由 Def 固定；其他武器仍可切换。
            if (WeaponProps.tractor && AllowsPointDefense) yield return new Command_BatchSetting<CompSpaceWeapon>(
                "TractorMode", this, weapons =>
                {
                    SpaceWeaponAimMode next = aimMode == SpaceWeaponAimMode.PointDefense
                        ? SpaceWeaponAimMode.Volley : SpaceWeaponAimMode.PointDefense;
                    foreach (CompSpaceWeapon weapon in weapons)
                    {
                        weapon.aimMode = next;
                        weapon.Universe?.Weapons?.NotifyWeaponModeChanged(weapon);
                    }
                })
            {
                defaultLabel = (aimMode == SpaceWeaponAimMode.PointDefense
                    ? "DeltaV_TractorModeCollect" : "DeltaV_TractorModeStructure").Translate(),
                defaultDesc = "DeltaV_TractorModeDesc".Translate(),
                icon = TexCommand.FireAtWill,
            };
            if (!WeaponProps.pointDefenseOnly && !WeaponProps.manualOnly)
                yield return new Command_BatchToggle<CompSpaceWeapon>("WeaponPointDefense",
                    this, w => w.aimMode == SpaceWeaponAimMode.PointDefense, (w, active) =>
                    {
                        w.aimMode = active ? SpaceWeaponAimMode.PointDefense : SpaceWeaponAimMode.Volley;
                        w.Universe?.Weapons?.NotifyWeaponModeChanged(w);
                    })
            {
                defaultLabel = "DeltaV_WeaponPdgizmo".Translate(),
                defaultDesc = "DeltaV_WeaponPdgizmoTip".Translate(),
                icon = TexCommand.FireAtWill,
            };

            if (aimMode == SpaceWeaponAimMode.PointDefense && AllowsPointDefense && !WeaponProps.tractor)
                yield return new Command_BatchSetting<CompSpaceWeapon>("PointDefenseTargets", this,
                    weapons => Find.WindowStack.Add(new Dialog_PointDefenseTargets(this, weapons)))
                {
                    defaultLabel = "DeltaV_PointDefenseTargets".Translate(),
                    icon = TexCommand.FireAtWill,
                };

            // 太空自动火控、点防和地面原版炮塔共用自动角度；玩家手控独立设置。
            if (!WeaponProps.manualOnly || AllowsPointDefense)
                yield return new Command_BatchSetting<CompSpaceWeapon>("WeaponAutoFireDeviation", this,
                weapons => Find.WindowStack.Add(new Dialog_Slider(
                    value => "DeltaV_AutoFireDeviation".Translate(value.ToString()),
                    0, 180, value =>
                    {
                        foreach (CompSpaceWeapon weapon in weapons)
                            if (weapon.parent.Spawned && !weapon.parent.Destroyed)
                                weapon.fireDeviationDeg = value;
                    }, Mathf.RoundToInt(fireDeviationDeg))))
            {
                defaultLabel = "DeltaV_AutoFireDeviation".Translate(fireDeviationDeg.ToString("0.##")),
                defaultDesc = "DeltaV_AutoFireDeviationTip".Translate(),
                icon = TexCommand.FireAtWill,
            };
            if (!WeaponProps.pointDefenseOnly)
                yield return new Command_BatchSetting<CompSpaceWeapon>("WeaponManualFireDeviation", this,
                weapons => Find.WindowStack.Add(new Dialog_Slider(
                    value => "DeltaV_ManualFireDeviation".Translate(value.ToString()),
                    0, 180, value =>
                    {
                        foreach (CompSpaceWeapon weapon in weapons)
                            if (weapon.parent.Spawned && !weapon.parent.Destroyed)
                                weapon.manualFireDeviationDeg = value;
                    }, Mathf.RoundToInt(manualFireDeviationDeg))))
            {
                defaultLabel = "DeltaV_ManualFireDeviation".Translate(manualFireDeviationDeg.ToString("0.##")),
                defaultDesc = "DeltaV_ManualFireDeviationTip".Translate(),
                icon = TexCommand.FireAtWill,
            };

            // 复用原版设施关联按钮语义：选中炮塔时直接给出兼容附件的建造 designator。
            foreach (Gizmo gizmo in SpaceWeaponAttachmentSlots.BuildGizmos(parent,
                WeaponProps.attachmentSlots, WeaponProps.allowedAttachmentDefs)) yield return gizmo;
            foreach (Gizmo gizmo in SpaceRailgunConstruction.BuildGizmos(parent)) yield return gizmo;

            if (Mannable == null) yield break;

            if (WeaponProps.intrinsicAimSkill < 0)
                yield return new Command_BatchToggle<CompSpaceWeapon>("KeepGunner", this,
                    w => w.keepGunnerWhenUnavailable, (w, active) =>
                    {
                        w.keepGunnerWhenUnavailable = active;
                        w.Universe?.Controls?.NotifyGunnerAvailabilityChanged(w.parent.Map);
                    })
            {
                defaultLabel = "DeltaV_KeepGunnerWhenUnavailable".Translate(),
                defaultDesc = "DeltaV_KeepGunnerWhenUnavailableTip".Translate(),
                icon = TexCommand.Install,
            };

            if (WeaponProps.casemate)
                yield return new Command_BatchToggle<CompSpaceWeapon>("SafeFire", this,
                    w => w.safeFire, (w, active) => w.safeFire = active)
                {
                    defaultLabel = "DeltaV_SafeFire".Translate(),
                    defaultDesc = "DeltaV_SafeFireTip".Translate(),
                    icon = TexCommand.FireAtWill,
                };

            // 炮手预留（含「不指定」）：浮单列本图殖民者。
            yield return new Command_BatchSetting<CompSpaceWeapon>(
                "ReserveGunner:" + parent.Map.GetHashCode(), this, weapons =>
                {
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("DeltaV_WeaponReserveNone".Translate(), () =>
                        {
                            foreach (CompSpaceWeapon weapon in weapons) weapon.reservedGunner = null;
                        })
                    };
                    foreach (Pawn pawn in parent.Map.mapPawns.FreeColonistsSpawned)
                    {
                        Pawn captured = pawn;
                        options.Add(new FloatMenuOption(captured.LabelShortCap, () =>
                        {
                            foreach (CompSpaceWeapon weapon in weapons)
                                if (weapon.parent.Spawned && !weapon.parent.Destroyed)
                                    weapon.reservedGunner = captured;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                })
            {
                defaultLabel = "DeltaV_WeaponReserveGizmo".Translate(
                    reservedGunner == null
                        ? "DeltaV_WeaponReserveNone".Translate()
                        : reservedGunner.LabelShort),
                defaultDesc = "DeltaV_WeaponReserveGizmoTip".Translate(),
                icon = TexCommand.Install,
            };
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            if (parent.Map == null) return;

            SpaceWeaponAttachmentSlots.DrawSelection(parent, WeaponProps.attachmentSlots, AttachedThings());

            if (!WeaponProps.acceleratorStarts.NullOrEmpty()) SpaceRailgunConstruction.DrawBarrierCell(parent);
            foreach (IntVec3 offset in WeaponProps.acceleratorStarts)
            {
                IntVec3 root = SpaceWeaponGeometry.RotatedCell(parent, offset);
                ThingDef nextDef = !WeaponProps.allowedAcceleratorDefs.NullOrEmpty()
                    ? WeaponProps.allowedAcceleratorDefs[0]
                    : null;
                var cells = new List<IntVec3>();
                if (nextDef == null) cells.Add(root);
                else foreach (IntVec3 cell in GenAdj.OccupiedRect(root, parent.Rotation, nextDef.Size))
                    cells.Add(cell);
                GenDraw.DrawFieldEdges(cells,
                    CompSpaceRailgun.SegmentAtRoot(parent.Map, root, parent.Rotation) != null
                        ? Color.green : Color.yellow);
            }
        }

    }
}

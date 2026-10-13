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
    /// 组别（套别+组号）、PD 模式与安全限制是本 comp 的存档事实；炮手预留属于 Crew，
    /// 弹仓优先级属于 Magazine；冷却与本 tick 发射为运行时。
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

        /// <summary>发射方向与目标方向允许的最大夹角（度）。</summary>
        public float defaultFireDeviationDeg = 3f;

        /// <summary>覆盖弹体默认冷发射时长；负数表示使用弹体 Def。</summary>
        public int ignitionDelayTicks = -1;

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            if (impactScale != 1f)
                yield return SpaceCombatStats.Entry("DeltaV_StatImpactScale",
                    impactScale.ToString("0.##") + "×", 5110);
            if (shotEnergy > 0f)
                yield return SpaceCombatStats.Entry("DeltaV_StatShotEnergy",
                    SpaceCombatStats.WattDays(shotEnergy), 5100);
            yield return SpaceCombatStats.Entry("DeltaV_StatCooldown",
                SpaceCombatStats.Ticks(cooldownTicks), 5090);
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
    }

    public class CompSpaceWeapon : ThingComp
    {
        // ---- 持久配置（存档事实；gizmo 修改）----

        public SpaceWeaponSalvoSet salvoSet = SpaceWeaponSalvoSet.Main;
        public int groupNumber = 1;
        public SpaceWeaponAimMode aimMode = SpaceWeaponAimMode.Volley;
        public SpaceWeaponEngagementMode engagementMode = SpaceWeaponEngagementMode.Space;
        public bool safeFire = true;
        public bool keepGunnerWhenUnavailable = true;
        public float fireDeviationDeg;
        private bool safeFireInitialized;
        private bool alignmentConfigurationInitialized;
        private bool configurationInitialized;
        private int lastMagazineThingId = -1;

        public void ResetPrefabIdentity() => lastMagazineThingId = -1;

        // ---- 运行时（不存档）----

        [Unsaved] public int cooldownUntilTick;
        [Unsaved] public SpaceObject pdTarget;
        [Unsaved] public int pdRetargetAtTick;

        public CompProperties_SpaceWeapon WeaponProps => (CompProperties_SpaceWeapon)props;

        public bool SpaceModeEnabled => !WeaponProps.groundModeAvailable
            || engagementMode == SpaceWeaponEngagementMode.Space;

        public bool GroundModeEnabled => WeaponProps.groundModeAvailable
            && engagementMode == SpaceWeaponEngagementMode.Ground;

        public UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        /// <summary>武器所在图的承载容器（非太空图为 null = 太空射击不适用）。</summary>
        public SpaceObject Container => parent?.Map != null ? Universe?.FrameOf(parent.Map) : null;

        /// <summary>炮口 universe 位置（武器格中心）。</summary>
        public Vector3 MuzzleUniverse
        {
            get
            {
                SpaceObject container = Container;
                return SpaceTransform.MapPointToUniverse(container, parent.Map,
                    SpaceWeaponGeometry.MuzzleMapPoint(this));
            }
        }

        /// <summary>武器挂载朝向的地图帧角（射界基准 = 建筑朝向）。</summary>
        public float FacingAngle => parent.TryGetComp<CompSpaceWeaponTraverse>()?.CurrentAngle
            ?? ShipOrientation.AngleFromRot4(parent.Rotation);

        public CompMannable Mannable => parent.TryGetComp<CompMannable>();

        /// <summary>
        /// 本武器所属船（归属 = 武器脚下基架归属；无归属/残骸归属 → Invalid，无组激活）。
        /// </summary>
        public Ship OwnerShip
        {
            get
            {
                Map map = parent?.Map;
                if (map == null) return Ship.Invalid;
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
                int owner = SpaceWeaponGeometry.OwnerIdOf(parent);
                if (owner <= 0) return Ship.Invalid;
                Ship ship = ShipRegistry.ShipByOwner(owner);
                return ship.IsValid && ship.map == map ? ship : Ship.Invalid;
            }
        }

        /// <summary>本武器的组是否激活（激活位存主驾驶台 comp，随捕获搬运）。</summary>
        public bool GroupActive
        {
            get
            {
                Ship ship = OwnerShip;
                if (!ship.IsValid) return false;
                int mask = ship.console?.WeaponGroupMask(salvoSet) ?? 0;
                return (mask & (1 << (groupNumber - 1))) != 0;
            }
        }

        /// <summary>当前炮手（原版 CompMannable 在册者）。</summary>
        public CompSpaceWeaponCrew Crew => parent.TryGetComp<CompSpaceWeaponCrew>();

        public Pawn CurrentGunner => Crew != null ? Crew.ManningPawn : Mannable?.ManningPawn;

        public SpaceWeaponAttachmentSlot AttachmentSlotAt(IntVec3 cell)
        {
            foreach (SpaceWeaponAttachmentSlot slot in WeaponProps.attachmentSlots)
                if (SpaceWeaponGeometry.RotatedCell(parent, slot.cell) == cell) return slot;
            return null;
        }

        public IEnumerable<ThingWithComps> AttachedThings()
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
                    CompSpaceAutoAimPlugin plugin = attached.TryGetComp<CompSpaceAutoAimPlugin>();
                    if (plugin != null && plugin.EffectiveFor(this)) return true;
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
        /// 读档重建不触发（battleStations 运行时已清零，事件空转）。
        /// </summary>
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
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
                alignmentConfigurationInitialized = true;
            }
            Universe?.Weapons?.RegisterWeapon(this);
            if (respawningAfterLoad) return;
            Universe?.Controls?.NotifyGunnerAvailabilityChanged(parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            Universe?.Weapons?.UnregisterWeapon(this, map);
            Universe?.Controls?.NotifyGunnerAvailabilityChanged(map);
        }

        /// <summary>
        /// 开火技能来源（§5.4）：自动瞄准插件与炮手 shooting 取高者；
        /// 两者皆无且存在 Crew 时不满足开火条件。插件必须覆盖声明槽位并与武器
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
                    CompSpaceAutoAimPlugin plugin = attached.TryGetComp<CompSpaceAutoAimPlugin>();
                    if (plugin != null && plugin.EffectiveFor(this))
                    {
                        pluginSkill = Mathf.Max(pluginSkill ?? 0, plugin.SkillLevel);
                    }
                }
            }
            // Crew 不存在表示武器自带基础火控；第三方只加核心 Comp 即可工作。
            if (Crew == null && Mannable == null && pluginSkill == null && gunnerSkill == null)
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
            Scribe_Values.Look(ref salvoSet, "salvoSet", WeaponProps.defaultSalvoSet);
            Scribe_Values.Look(ref groupNumber, "groupNumber", WeaponProps.defaultGroupNumber);
            Scribe_Values.Look(ref aimMode, "aimMode", WeaponProps.aimMode);
            Scribe_Values.Look(ref engagementMode, "engagementMode", SpaceWeaponEngagementMode.Space);
            Scribe_Values.Look(ref configurationInitialized, "configurationInitialized", true);
            Scribe_Values.Look(ref safeFire, "safeFire", true);
            Scribe_Values.Look(ref keepGunnerWhenUnavailable, "keepGunnerWhenUnavailable", true);
            Scribe_Values.Look(ref safeFireInitialized, "safeFireInitialized", true);
            Scribe_Values.Look(ref fireDeviationDeg, "fireDeviationDeg",
                WeaponProps.defaultFireDeviationDeg);
            Scribe_Values.Look(ref alignmentConfigurationInitialized,
                "alignmentConfigurationInitialized", true);
            Scribe_Values.Look(ref lastMagazineThingId, "lastMagazineThingId", -1);
        }

        public CompSpaceWeaponMagazine SelectMagazine()
        {
            var candidates = new List<CompSpaceWeaponMagazine>();
            CompSpaceWeaponMagazine own = parent.TryGetComp<CompSpaceWeaponMagazine>();
            if (own != null && own.HasShot) candidates.Add(own);
            foreach (ThingWithComps attached in AttachedThings())
            {
                CompSpaceWeaponAttachment attachment = attached.TryGetComp<CompSpaceWeaponAttachment>();
                CompSpaceWeaponMagazine magazine = attached.TryGetComp<CompSpaceWeaponMagazine>();
                if (magazine != null && magazine.HasShot && (attachment == null || attachment.Enabled))
                {
                    candidates.Add(magazine);
                }
            }
            if (candidates.Count == 0) return null;
            int max = int.MinValue;
            foreach (CompSpaceWeaponMagazine candidate in candidates) max = Mathf.Max(max, candidate.Priority);
            candidates.RemoveAll(m => m.Priority != max);
            candidates.Sort((a, b) => a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber));
            foreach (CompSpaceWeaponMagazine candidate in candidates)
            {
                if (candidate.parent.thingIDNumber > lastMagazineThingId) return candidate;
            }
            return candidates[0];
        }

        internal bool MagazineAvailable(CompSpaceWeaponMagazine selected) =>
            parent.TryGetComp<CompSpaceWeaponMagazine>() == null || selected != null;

        public void NotifyMagazineConsumed(CompSpaceWeaponMagazine magazine)
        {
            if (magazine != null) lastMagazineThingId = magazine.parent.thingIDNumber;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Universe;
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (universe == null || !parent.Spawned || !playerSide)
            {
                yield break;
            }

            if (WeaponProps.groundModeAvailable) yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_GroundMode".Translate(),
                defaultDesc = "DeltaV_GroundModeTip".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => GroundModeEnabled,
                toggleAction = delegate
                {
                    engagementMode = GroundModeEnabled
                        ? SpaceWeaponEngagementMode.Space : SpaceWeaponEngagementMode.Ground;
                    (parent as Building_SpaceTurretGun)?.NotifyEngagementModeChanged();
                    pdTarget = null;
                    Universe?.Weapons?.NotifyWeaponModeChanged(this);
                }
            };

            // 武器组指派（主/副两套 × 1–6）：弹出菜单选择，当前项打勾（取代旧循环
            // 步进——12 个组合不该靠单击轮转）。组的激活位在状态面板切换。
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_WeaponGroupGizmo".Translate(
                    ("DeltaV_WeaponSet" + salvoSet).Translate(), groupNumber.ToString()),
                defaultDesc = "DeltaV_WeaponGroupGizmoTip".Translate(),
                icon = TexCommand.Attack,
                action = delegate
                {
                    var options = new List<FloatMenuOption>();
                    var sets = new[] { SpaceWeaponSalvoSet.Main, SpaceWeaponSalvoSet.Secondary };
                    foreach (SpaceWeaponSalvoSet set in sets)
                    {
                        for (int group = 1; group <= 6; group++)
                        {
                            SpaceWeaponSalvoSet capturedSet = set;
                            int capturedGroup = group;
                            bool current = set == salvoSet && group == groupNumber;
                            options.Add(new FloatMenuOption(
                                "DeltaV_WeaponGroupEntry".Translate(
                                    ("DeltaV_WeaponSet" + capturedSet).Translate(),
                                    capturedGroup.ToString()) + (current ? " ✓" : string.Empty),
                                delegate
                                {
                                    // FloatMenu 不暂停：菜单打开期间对象可动/可失效，
                                    // 点击时按既有契约复核（同炮手预留/对接目标菜单）。
                                    if (!parent.Spawned || parent.Destroyed) return;
                                    salvoSet = capturedSet;
                                    groupNumber = capturedGroup;
                                }));
                        }
                    }
                    Find.WindowStack.Add(new FloatMenu(options, "DeltaV_WeaponGroupMenuTitle".Translate()));
                }
            };

            // 专用点防武器的模式由 Def 固定；其他武器仍可切换。
            if (!WeaponProps.pointDefenseOnly) yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_WeaponPdgizmo".Translate(),
                defaultDesc = "DeltaV_WeaponPdgizmoTip".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => aimMode == SpaceWeaponAimMode.PointDefense,
                toggleAction = delegate
                {
                    aimMode = aimMode == SpaceWeaponAimMode.PointDefense
                        ? SpaceWeaponAimMode.Volley
                        : SpaceWeaponAimMode.PointDefense;
                    Universe?.Weapons?.NotifyWeaponModeChanged(this);
                }
            };

            // 同一角度配置约束旋转炮塔当前炮口和固定轴武器安装轴。
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_FireDeviation".Translate(fireDeviationDeg.ToString("0")),
                defaultDesc = "DeltaV_FireDeviationTip".Translate(),
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_Slider(
                        value => "DeltaV_FireDeviation".Translate(value.ToString()),
                        0, 180, value =>
                        {
                            if (!parent.Spawned || parent.Destroyed) return;
                            fireDeviationDeg = value;
                            // 原切换钮支持多选；滑条确认同样应用到已选武器。
                            foreach (object selected in Find.Selector.SelectedObjectsListForReading)
                            {
                                if (selected is ThingWithComps other && other != parent
                                    && other.Spawned && !other.Destroyed)
                                {
                                    CompSpaceWeapon selectedWeapon = other.TryGetComp<CompSpaceWeapon>();
                                    if (selectedWeapon != null) selectedWeapon.fireDeviationDeg = value;
                                }
                            }
                        }, Mathf.RoundToInt(fireDeviationDeg)));
                }
            };

            // 复用原版设施关联按钮语义：选中炮塔时直接给出兼容附件的建造 designator。
            foreach (Gizmo gizmo in SpaceWeaponAttachmentSlots.BuildGizmos(parent,
                WeaponProps.attachmentSlots, WeaponProps.allowedAttachmentDefs)) yield return gizmo;
            foreach (Gizmo gizmo in SpaceRailgunConstruction.BuildGizmos(parent)) yield return gizmo;

            CompSpaceWeaponCrew crew = Crew;
            if (crew == null) yield break;

            if (WeaponProps.intrinsicAimSkill < 0) yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_KeepGunnerWhenUnavailable".Translate(),
                defaultDesc = "DeltaV_KeepGunnerWhenUnavailableTip".Translate(),
                icon = TexCommand.Install,
                isActive = () => keepGunnerWhenUnavailable,
                toggleAction = delegate
                {
                    keepGunnerWhenUnavailable = !keepGunnerWhenUnavailable;
                    Universe?.Controls?.NotifyGunnerAvailabilityChanged(parent.Map);
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_SafeFire".Translate(),
                defaultDesc = "DeltaV_SafeFireTip".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => safeFire,
                toggleAction = delegate { safeFire = !safeFire; }
            };

            // 炮手预留（含「不指定」）：浮单列本图殖民者。
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_WeaponReserveGizmo".Translate(
                    crew.reservedGunner == null
                        ? "DeltaV_WeaponReserveNone".Translate()
                        : crew.reservedGunner.LabelShort),
                defaultDesc = "DeltaV_WeaponReserveGizmoTip".Translate(),
                icon = TexCommand.Install,
                action = delegate
                {
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("DeltaV_WeaponReserveNone".Translate(), delegate
                        {
                            crew.reservedGunner = null;
                        })
                    };
                    foreach (Pawn pawn in parent.Map.mapPawns.FreeColonistsSpawned)
                    {
                        Pawn captured = pawn;
                        options.Add(new FloatMenuOption(captured.LabelShortCap, delegate
                        {
                            crew.reservedGunner = captured;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            if (parent.Map == null) return;

            SpaceWeaponAttachmentSlots.DrawSelection(parent, WeaponProps.attachmentSlots, AttachedThings());

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

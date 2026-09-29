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

    /// <summary>
    /// 太空武器 comp（太空战架构 §5.1，双模）：地表/同图战斗完全走原版炮塔 comp
    ///（本 comp 零介入）；太空跨容器射击 = 本 comp + 弹体服务。弹道类型由
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

        /// <summary>朝北局部坐标中的炮口连续偏移（相对脚印中心）。</summary>
        public Vector2 muzzleOffset = Vector2.zero;

        /// <summary>朝北局部坐标中的覆盖挂件槽；旋转后精确匹配槽内 Thing。</summary>
        public List<IntVec3> attachmentSlots = new List<IntVec3>();

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

        /// <summary>
        /// 默认允许炮管未对准瞄点时沿当前炮管/固定安装轴开火。轨道炮等固定轴武器
        /// 依此工作；转速慢、射速快的炮可用同一实例开关在转动中持续射击。
        /// </summary>
        public bool canFireWithoutAlignmentByDefault;

        /// <summary>覆盖弹体默认冷发射时长；负数表示使用弹体 Def。</summary>
        public int ignitionDelayTicks = -1;

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
        public bool safeFire = true;
        public bool canFireWithoutAlignment;
        private bool safeFireInitialized;
        private bool alignmentConfigurationInitialized;
        private bool configurationInitialized;
        private int lastMagazineThingId = -1;

        // ---- 运行时（不存档）----

        [Unsaved] public int cooldownUntilTick;
        [Unsaved] public SpaceObject pdTarget;
        [Unsaved] public int pdRetargetAtTick;

        public CompProperties_SpaceWeapon WeaponProps => (CompProperties_SpaceWeapon)props;

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

        public IEnumerable<ThingWithComps> AttachedThings()
        {
            if (parent.Map == null || WeaponProps.attachmentSlots.NullOrEmpty()) yield break;
            int owner = SpaceWeaponGeometry.OwnerIdOf(parent);
            var seen = new HashSet<int>();
            foreach (IntVec3 offset in WeaponProps.attachmentSlots)
            {
                IntVec3 cell = SpaceWeaponGeometry.RotatedCell(parent, offset);
                if (!parent.OccupiedRect().Contains(cell)) continue;
                if (!cell.InBounds(parent.Map)) continue;
                foreach (Thing thing in parent.Map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing == parent || thing is not ThingWithComps comps || thing.Destroyed) continue;
                    CompSpaceWeaponAttachment attachment = comps.TryGetComp<CompSpaceWeaponAttachment>();
                    if (attachment == null || attachment.HostWeapon != this) continue;
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
                foreach (ThingWithComps attached in AttachedThings())
                {
                    CompSpaceAutoAimPlugin plugin = attached.TryGetComp<CompSpaceAutoAimPlugin>();
                    if (plugin != null && plugin.EffectiveFor(this)) return true;
                }
                return false;
            }
        }

        // ---- 炮塔状态变更事件源（事件驱动补分配，见 SpaceWeaponService.NotifyWeaponAvailabilityChanged）----

        /// <summary>
        /// 供电恢复事件（原版 CompPowerTrader 的 PowerOn setter 切换时广播
        /// "PowerTurnedOn"）：短路损毁后重连、起飞/对接搬运后电网恢复等一切停电窗口
        /// 的恢复端。登记到武器服务，下一 tick 在战斗位置激活的图上补分配炮手
        /// （JobDriver_ManTurret 无供电检查、断电不丢 job，但转移 DeSpawn 会丢）。
        /// </summary>
        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "PowerTurnedOn")
            {
                Universe?.Weapons?.NotifyWeaponAvailabilityChanged(parent.Map);
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
                canFireWithoutAlignment = WeaponProps.canFireWithoutAlignmentByDefault;
                alignmentConfigurationInitialized = true;
            }
            if (respawningAfterLoad) return;
            Universe?.Weapons?.NotifyWeaponAvailabilityChanged(parent.Map);
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
            int? pluginSkill = null;
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
            Scribe_Values.Look(ref configurationInitialized, "configurationInitialized", true);
            Scribe_Values.Look(ref safeFire, "safeFire", true);
            Scribe_Values.Look(ref safeFireInitialized, "safeFireInitialized", true);
            Scribe_Values.Look(ref canFireWithoutAlignment, "canFireWithoutAlignment",
                WeaponProps.canFireWithoutAlignmentByDefault);
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

            // 瞄准模式：齐射 ⇄ 点防御。
            yield return new Command_Toggle
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
                }
            };

            // 对准门禁是实例配置：关闭时仍持续追踪瞄点，但每发沿炮管当前方向射出。
            yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_FireWithoutAlignment".Translate(),
                defaultDesc = "DeltaV_FireWithoutAlignmentTip".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => canFireWithoutAlignment,
                toggleAction = delegate { canFireWithoutAlignment = !canFireWithoutAlignment; }
            };

            // 复用原版设施关联按钮语义：选中炮塔时直接给出兼容附件的建造 designator。
            if (!WeaponProps.attachmentSlots.NullOrEmpty())
            {
                foreach (ThingDef attachmentDef in CompatibleAttachmentDefs())
                {
                    Designator_Build designator = BuildCopyCommandUtility.FindAllowedDesignator(attachmentDef);
                    if (designator != null) yield return designator;
                }
            }

            CompSpaceWeaponCrew crew = Crew;
            if (crew == null) yield break;

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

            // 覆盖式挂件槽：黄色 = 空槽，绿色 = 已安装兼容挂件。
            foreach (IntVec3 offset in WeaponProps.attachmentSlots)
            {
                IntVec3 cell = SpaceWeaponGeometry.RotatedCell(parent, offset);
                if (!parent.OccupiedRect().Contains(cell)) continue;
                bool occupied = false;
                foreach (ThingWithComps attached in AttachedThings())
                {
                    if (attached.OccupiedRect().Contains(cell)) { occupied = true; break; }
                }
                GenDraw.DrawFieldEdges(new List<IntVec3> { cell },
                    occupied ? Color.green : Color.yellow);
            }

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

        private IEnumerable<ThingDef> CompatibleAttachmentDefs()
        {
            if (!WeaponProps.allowedAttachmentDefs.NullOrEmpty())
            {
                foreach (ThingDef def in WeaponProps.allowedAttachmentDefs) yield return def;
                yield break;
            }
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.GetCompProperties<CompProperties_SpaceWeaponAttachment>() != null)
                    yield return def;
            }
        }
    }
}

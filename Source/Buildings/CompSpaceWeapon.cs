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
    /// 组别（套别+组号）、PD 模式、炮手预留是存档事实；冷却与本 tick 发射为运行时。
    /// 武器必须位于基架格上（含墙下基架）——归属随基架、随控制域捕获搬运。
    /// </summary>
    public class CompProperties_SpaceWeapon : CompProperties
    {
        /// <summary>发射的弹体 building def（联合效果注册表键与弹道参数来源）。</summary>
        public ThingDef projectileDef;

        /// <summary>默认瞄准模式（齐射/PD；玩家可经 gizmo 切换，当前值随 comp 存档）。</summary>
        public SpaceWeaponAimMode aimMode = SpaceWeaponAimMode.Volley;

        /// <summary>跨墙感知：true = 顶部炮塔（弹排除己方容器）；false = 舰中固定（可自伤）。</summary>
        public bool topTurret = true;

        /// <summary>射界（度，绕挂载朝向对称；360 = 全向）。</summary>
        public float fireArcDeg = 360f;

        /// <summary>基础散射角（度；实际偏移 × 技能偏差系数，§5.4）。</summary>
        public float baseSpreadDeg = 2f;

        /// <summary>发射冷却（tick）。</summary>
        public int cooldownTicks = 90;

        /// <summary>射程（格；PD 索敌与导弹沿射线锁定目标的范围）。</summary>
        public float range = 120f;

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
        public Pawn reservedGunner;

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
                    new Vector2(parent.Position.x + 0.5f, parent.Position.z + 0.5f));
            }
        }

        /// <summary>武器挂载朝向的地图帧角（射界基准 = 建筑朝向）。</summary>
        public float FacingAngle => ShipOrientation.AngleFromRot4(parent.Rotation);

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
                int owner = ownership?.OwnerAt(parent.Position) ?? 0;
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
        public Pawn CurrentGunner => Mannable?.ManningPawn;

        /// <summary>
        /// 开火技能来源（§5.4 优先级：自动瞄准插件的等价技能 → 炮手 shooting；
        /// 与炮手并存取高者；两者皆无 → 不满足开火条件）。插件需四邻接本武器
        /// 且同归属基架（生效判定收口 CompSpaceAutoAimPlugin）。
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
                foreach (IntVec3 cell in parent.OccupiedRect().ExpandedBy(1).Cells)
                {
                    if (parent.OccupiedRect().Contains(cell) || !cell.InBounds(parent.Map)) continue;
                    foreach (Thing thing in parent.Map.thingGrid.ThingsListAtFast(cell))
                    {
                        if (thing is Building building && !building.Destroyed
                            && building.TryGetComp<CompSpaceAutoAimPlugin>() is { } plugin
                            && plugin.EffectiveFor(this))
                        {
                            pluginSkill = Mathf.Max(pluginSkill ?? 0, plugin.SkillLevel);
                        }
                    }
                }
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
            Scribe_Values.Look(ref salvoSet, "salvoSet", SpaceWeaponSalvoSet.Main);
            Scribe_Values.Look(ref groupNumber, "groupNumber", 1);
            Scribe_Values.Look(ref aimMode, "aimMode", SpaceWeaponAimMode.Volley);
            Scribe_References.Look(ref reservedGunner, "reservedGunner");
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            UniverseManager universe = Universe;
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (universe == null || !parent.Spawned || !playerSide)
            {
                yield break;
            }

            // 武器组指派：主1 → … → 主6 → 副1 → … → 副6 → 主1（套别+组号一并循环）。
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_WeaponGroupGizmo".Translate(
                    ("DeltaV_WeaponSet" + salvoSet).Translate(), groupNumber.ToString()),
                defaultDesc = "DeltaV_WeaponGroupGizmoTip".Translate(),
                icon = TexCommand.Attack,
                action = delegate
                {
                    groupNumber = groupNumber >= 6 ? 1 : groupNumber + 1;
                    if (groupNumber == 1)
                    {
                        salvoSet = salvoSet == SpaceWeaponSalvoSet.Main
                            ? SpaceWeaponSalvoSet.Secondary
                            : SpaceWeaponSalvoSet.Main;
                    }
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

            // 炮手预留（含「不指定」）：浮单列本图殖民者。
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_WeaponReserveGizmo".Translate(
                    reservedGunner == null
                        ? "DeltaV_WeaponReserveNone".Translate()
                        : reservedGunner.LabelShort),
                defaultDesc = "DeltaV_WeaponReserveGizmoTip".Translate(),
                icon = TexCommand.Install,
                action = delegate
                {
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("DeltaV_WeaponReserveNone".Translate(), delegate
                        {
                            reservedGunner = null;
                        })
                    };
                    foreach (Pawn pawn in parent.Map.mapPawns.FreeColonistsSpawned)
                    {
                        Pawn captured = pawn;
                        options.Add(new FloatMenuOption(captured.LabelShortCap, delegate
                        {
                            reservedGunner = captured;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }
    }
}

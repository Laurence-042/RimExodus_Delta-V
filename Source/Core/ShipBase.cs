using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>所有飞船的共同事实与能力；结构布局由并列实现提供，运动属于 SpaceObject。</summary>
    public abstract class ShipBase
    {
        public static readonly ShipBase Invalid = new FoundationShip(Ship.Invalid);
        public abstract bool IsValid { get; }
        public abstract Map Map { get; }
        public abstract DriveSubject Identity { get; }
        public abstract Thing Anchor { get; }
        public abstract float FrameAngle { get; }
        public abstract Vector2 HullCenter { get; }
        public abstract IEnumerable<IntVec3> HullCells { get; }
        public abstract Faction Faction { get; }
        public abstract string Label { get; }
        public abstract ShipDerivedState State { get; }
        public abstract ShipCombatState Combat { get; }
        public abstract bool CanDriveIn(SpaceObject container);
        public abstract bool Contains(Thing thing);
        /// <summary>数值布局使用容器 Map 的局部坐标；物理报告重建与出力前能力复核共用。</summary>
        public abstract IEnumerable<ShipPhysics.Thruster> BuildThrusters();
        private Map weaponMap;
        private int weaponVersion = -1, weaponMeasuredAt = -1;
        private IReadOnlyList<CompSpaceWeapon> weapons = System.Array.Empty<CompSpaceWeapon>();
        public IEnumerable<CompSpaceWeapon> Weapons
        {
            get
            {
                if (!IsValid) return System.Array.Empty<CompSpaceWeapon>();
                if (weaponMap != Map || SubstructureOwnership.CacheNeedsRefresh(weaponVersion, weaponMeasuredAt))
                {
                    weapons = CollectWeapons().Distinct().ToArray();
                    weaponMap = Map;
                    weaponVersion = SubstructureOwnership.GlobalStructureVersion;
                    weaponMeasuredAt = Find.TickManager.TicksGame;
                }
                return weapons;
            }
        }
        protected abstract IEnumerable<CompSpaceWeapon> CollectWeapons();
        /// <summary>包含 Empty 格，以明确船体负责的区域；求交仍由 SpaceCollision 执行。</summary>
        public abstract IEnumerable<SpaceCollisionCell> CollisionCells { get; }
        /// <summary>船体在当前承载关系下贡献的对象角色；容器缓存结果供索敌快速过滤。</summary>
        public abstract SpaceObjectTag RolesIn(SpaceObject container);
        public abstract bool PrepareThrust(UniverseManager universe, bool playerInitiated);
        public abstract ShipTransferSpec Transfer { get; }
        public virtual bool CanBoard => false;
        public virtual void NotifyLanded() { }
    }

    /// <summary>船体提供的 Gravship 搬运参数；捕获、预览和放置仍使用同一条链。</summary>
    public readonly struct ShipTransferSpec
    {
        public readonly Building console, body;
        public readonly int ownerId;
        public readonly bool includeTerrain;
        public readonly IntVec2? pocketSize;
        public ShipTransferSpec(Building console, Building body, int ownerId,
            bool includeTerrain, IntVec2? pocketSize = null)
        {
            this.console = console;
            this.body = body;
            this.ownerId = ownerId;
            this.includeTerrain = includeTerrain;
            this.pocketSize = pocketSize;
        }
    }

    /// <summary>驾驶台认领的基架域；Ship 仅为现有结构查询快照。</summary>
    public class FoundationShip : ShipBase
    {
        public readonly Ship Structure;
        public FoundationShip(Ship structure) { Structure = structure; }
        public override bool IsValid => Structure.IsValid;
        public override Map Map => Structure.map;
        public override DriveSubject Identity => new DriveSubject(0, Structure.ownerId);
        public override Thing Anchor => Structure.console?.parent;
        public override float FrameAngle => ShipRegistry.LocalFrameAngle(Structure);
        public override Vector2 HullCenter => ShipRegistry.HullCenter(Structure);
        public override IEnumerable<IntVec3> HullCells => ShipRegistry.ControlDomain(Structure);
        public override Faction Faction => ShipRegistry.FactionOf(Structure);
        public override string Label => Current.Game?.GetComponent<UniverseManager>()?.LabelForOwner(Identity.key) ?? "";
        public override ShipDerivedState State => ShipRegistry.StateOf(Structure);
        public override ShipCombatState Combat => Structure.console?.combat;
        public override bool CanDriveIn(SpaceObject container) => IsValid && container?.map == Map;
        public override bool Contains(Thing thing) => ShipRegistry.Contains(Structure, thing);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            if (!IsValid) yield break;
            foreach (Thing thing in Map.listerThings.AllThings)
                if (ThrusterPower.IsThruster(thing) && Contains(thing))
                    yield return ShipPhysics.BuildInstalledThruster(thing);
        }
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons() => IsValid
            ? SpaceWeaponService.InstalledWeaponsOn(Map).Where(w => w.OwnerShip.Identity == Identity)
            : Enumerable.Empty<CompSpaceWeapon>();
        public override IEnumerable<SpaceCollisionCell> CollisionCells =>
            SpaceStructureRules.CollisionCells(Map, HullCells);
        public override ShipTransferSpec Transfer => new ShipTransferSpec(
            Anchor as Building, null, Structure.ownerId, true);
        public override SpaceObjectTag RolesIn(SpaceObject container) => SpaceObjectTag.Ship;
        public override bool PrepareThrust(UniverseManager universe, bool playerInitiated) =>
            universe.ShipTransitions.RequestFoundationSeparation(universe, Structure, playerInitiated);
    }

    /// <summary>单建筑船；原版 transporter 与 launchable 仍由 CompSpaceShuttle 适配。</summary>
    public class ShuttleShip : ShipBase
    {
        public readonly CompSpaceShuttle Hull;
        private Map driveMap;
        private int driveVersion = -1, driveMeasuredAt = -1;
        private bool independentHull;
        public ShuttleShip(CompSpaceShuttle hull) { Hull = hull; }
        public override bool IsValid => Hull.parent is Building && Hull.parent.Spawned
            && !Hull.parent.Destroyed && Map?.Disposed == false;
        public override Map Map => Hull.parent.Map;
        public override DriveSubject Identity => new DriveSubject(0, Hull.parent.thingIDNumber, true);
        public override Thing Anchor => Hull.parent;
        public override float FrameAngle => ShipOrientation.AngleFromRot4(Anchor.Rotation);
        public override Vector2 HullCenter => SpaceTransform.BoundsCenterOfCells(HullCells);
        public override IEnumerable<IntVec3> HullCells => Anchor.OccupiedRect().Cells;
        public override Faction Faction => Anchor.Faction;
        public override string Label => Anchor.LabelCap;
        public override ShipDerivedState State => CanDriveIn(Current.Game?.GetComponent<UniverseManager>()?.FrameOf(Map))
            ? ShipDerivedState.FreeFlight : ShipDerivedState.Docked;
        public override ShipCombatState Combat => Hull.combat;
        public override bool CanDriveIn(SpaceObject container)
        {
            if (!IsValid || container?.map != Map || Map.Parent is not ShipPocketParent_DeltaV) return false;
            if (driveMap != Map || SubstructureOwnership.CacheNeedsRefresh(driveVersion, driveMeasuredAt))
            {
                driveMap = Map;
                driveVersion = SubstructureOwnership.GlobalStructureVersion;
                driveMeasuredAt = Find.TickManager.TicksGame;
                independentHull = !SubstructureOwnership.CellsOnMap(Map).Any()
                    && !Map.listerThings.AllThings.Any(t => t is Building && t != Anchor && !t.Destroyed)
                    && !HullCells.Any(c => Map.terrainGrid.BaseTerrainAt(c) is TerrainDef terrain
                        && terrain != TerrainDefOf.Space);
            }
            return independentHull;
        }
        public override bool Contains(Thing thing) => IsValid && thing == Anchor;
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            if (!IsValid) yield break;
            Vector2 center = HullCenter - SpaceTransform.ShipLocalOrigin(Map);
            IReadOnlyList<ShuttleThrusterConfig> layout = Hull.ThrusterLayout;
            if (layout == null) yield break;
            foreach (ShuttleThrusterConfig configured in layout)
            {
                if (configured == null || configured.force.sqrMagnitude <= 1e-6f) continue;
                Vector2 force = SpaceTransform.Rotate(configured.force, FrameAngle);
                yield return new ShipPhysics.Thruster
                {
                    localPos = center + SpaceTransform.Rotate(configured.position, FrameAngle),
                    dir = force.normalized, thrust = force.magnitude, operational = true
                };
            }
        }
        // 默认 Def 没有武器；追加 Comp 的扩展仍进入同一武器事务。
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons() => IsValid
            ? Hull.parent.AllComps.OfType<CompSpaceWeapon>() : Enumerable.Empty<CompSpaceWeapon>();
        public override IEnumerable<SpaceCollisionCell> CollisionCells =>
            SpaceStructureRules.CollisionCells(Map, HullCells);
        public override SpaceObjectTag RolesIn(SpaceObject container) => SpaceObjectTag.Ship
            | (CanDriveIn(container) ? SpaceObjectTag.Shuttle : 0);
        public override bool PrepareThrust(UniverseManager universe, bool playerInitiated) => true;
        public override ShipTransferSpec Transfer => new ShipTransferSpec(null, Anchor as Building, 0, false,
            new IntVec2(Anchor.def.Size.x + 8, Anchor.def.Size.z + 8));
        public override bool CanBoard => Hull.CanBoard;
        public override void NotifyLanded() =>
            SpaceShuttleService.StartLaunchCooldown(Hull.parent.TryGetComp<CompLaunchable>());
    }

    /// <summary>火控与武器组的唯一持久模型，由船体身份锚保存。</summary>
    public sealed class ShipCombatState : IExposable
    {
        public ShipFireControlMode fireControlMode = ShipFireControlMode.Automatic;
        public int weaponGroupsMain = 1, weaponGroupsSecondary = 1;
        public int WeaponGroupMask(SpaceWeaponSalvoSet set) =>
            set == SpaceWeaponSalvoSet.Main ? weaponGroupsMain : weaponGroupsSecondary;
        public void SetWeaponGroupMask(SpaceWeaponSalvoSet set, int mask)
        {
            if (set == SpaceWeaponSalvoSet.Main) weaponGroupsMain = mask;
            else weaponGroupsSecondary = mask;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref fireControlMode, "fireControlMode", ShipFireControlMode.Automatic);
            Scribe_Values.Look(ref weaponGroupsMain, "weaponGroupsMain", 1);
            Scribe_Values.Look(ref weaponGroupsSecondary, "weaponGroupsSecondary", 1);
        }
    }
}

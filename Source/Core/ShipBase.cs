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
        private ShipStructure structuralState;
        internal ShipStructure StructureInstance => structuralState ??= new ShipStructure(this);

        internal ShipStructure StructuralState
        {
            get
            {
                if (!IsValid) return null;
                ShipStructure state = StructureInstance;
                if (state.Map != Map)
                    Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.BindIdentity(this);
                return state;
            }
        }
        public abstract float FrameAngle { get; }
        public abstract Vector2 HullCenter { get; }
        public abstract IEnumerable<IntVec3> HullCells { get; }
        public abstract Faction Faction { get; }
        public virtual bool IsPublicFacility => false;
        internal PlayerAccessLevel PlayerAccessLevel => PlayerAccess.For(this);
        public bool PlayerMayOperate => IsValid && PlayerAccess.CanOperate(PlayerAccessLevel);
        public abstract string Label { get; }
        public abstract ShipDerivedState State { get; }
        public abstract ShipCombatState Combat { get; }
        public CompShipCombatAI CombatAI => (Anchor as ThingWithComps)?.TryGetComp<CompShipCombatAI>();
        public abstract bool CanDriveIn(SpaceObject container);
        public abstract bool Contains(Thing thing);
        /// <summary>数值布局使用容器 Map 的局部坐标；物理报告重建与出力前能力复核共用。</summary>
        public abstract IEnumerable<ShipPhysics.Thruster> BuildThrusters();
        private Map weaponMap;
        private int weaponVersion = -1;
        private IReadOnlyList<CompSpaceWeapon> weapons = System.Array.Empty<CompSpaceWeapon>();
        public IEnumerable<CompSpaceWeapon> Weapons
        {
            get
            {
                if (!IsValid) return System.Array.Empty<CompSpaceWeapon>();
                if (GetType() == typeof(FoundationShip) && StructuralState is { } structure)
                    return structure.InstalledWeapons;
                if (GetType() == typeof(ShuttleShip)) return CompSpaceWeapon.On(((ShuttleShip)this).Hull.parent);
                int revision = ShipStructureIndex.ReadRevision(Map, ShipStructureAspect.Membership
                    | ShipStructureAspect.Footprint | ShipStructureAspect.Foundation | ShipStructureAspect.Primary
                    | ShipStructureAspect.Ownership | ShipStructureAspect.Representation);
                if (weaponMap != Map || weaponVersion != revision)
                {
                    weapons = CollectWeapons().Distinct().ToArray();
                    weaponMap = Map;
                    weaponVersion = revision;
                }
                return weapons;
            }
        }
        protected abstract IEnumerable<CompSpaceWeapon> CollectWeapons();
        /// <summary>包含 Empty 格，以明确船体负责的区域；求交仍由 SpaceCollision 执行。</summary>
        public abstract IEnumerable<SpaceCollisionCell> CollisionCells { get; }
        /// <summary>船体在当前承载关系下贡献的对象角色；容器缓存结果供索敌快速过滤。</summary>
        public abstract SpaceObjectTag RolesIn(SpaceObject container);
        public abstract ShipTransferSpec Transfer { get; }
        /// <summary>是否随船搬运脚印内的地面物品；内部货舱始终随所属建筑。</summary>
        public virtual bool MoveColocatedItems => true;
        public virtual AcceptanceReport CheckDeparture()
        {
            if (!IsValid || !BuildThrusters().Any(t => t.operational && t.thrust > 0f))
                return "DeltaV_LaunchFailNoThruster".Translate();
            if (HullCells.Any(c => Map.roofGrid.RoofAt(c)?.isNatural == true))
                return "CommandLaunchGroupFailUnderRoof".Translate();
            return true;
        }
        public virtual bool CanBoard => false;
        public virtual bool RequiresHeadingAlignment => true;
        public virtual bool RequiresAngularVelocityMatch => true;
        public virtual bool MatchDockingPointVelocity => false;
        public virtual bool AllowsAngularMotion => true;
        public virtual bool UsesCameraInput => false;
        public virtual int FlightMapMargin => ShipPocketMapFactory.StructureMargin;
        public virtual bool ReserveRotationBounds => false;
        public virtual bool HasGunnerStations => true;
        public virtual ShipFireControlMode DefaultFireControlMode => ShipFireControlMode.Automatic;
        public virtual bool SupportsFireControlMode(ShipFireControlMode mode) => true;
        public ShipFireControlMode EffectiveFireControlMode => Combat != null && SupportsFireControlMode(Combat.fireControlMode)
            ? Combat.fireControlMode : DefaultFireControlMode;
        public virtual ShipAimHandler CreateAimHandler(UniverseManager universe) => new ShipAimHandler(universe, this);
        public virtual void NotifyLanded() { }

        // 船体是面板的共同入口；控制器仍唯一提供权限、驾驶和侵入等操作。
        private ShipController PanelController => IsValid
            ? Current.Game?.GetComponent<UniverseManager>()?.Controls.For(this) : null;
        internal void CollectPanelActions(List<PilotPanelAction> actions) =>
            PanelController?.CollectPanelActions(PilotTarget.Of(this), actions);
        internal void CollectCombatPanelActions(bool hasWeapons,
            List<PilotPanelAction> beforeGroups, List<PilotPanelAction> afterGroups) =>
            PanelController?.CollectCombatPanelActions(this, hasWeapons, beforeGroups, afterGroups);
        internal void CollectHeadingPanelActions(List<HeadingPanelAction> actions) =>
            PanelController?.CollectHeadingPanelActions(this, actions);
    }

    /// <summary>船体提供的 Gravship 搬运参数；捕获、预览和放置仍使用同一条链。</summary>
    public readonly struct ShipTransferSpec
    {
        public readonly Building console, body;
        public readonly int ownerId;
        public readonly bool includeTerrain;
        public ShipTransferSpec(Building console, Building body, int ownerId,
            bool includeTerrain)
        {
            this.console = console;
            this.body = body;
            this.ownerId = ownerId;
            this.includeTerrain = includeTerrain;
        }
    }

    /// <summary>驾驶台认领的基架域；Ship 仅为现有结构查询快照。</summary>
    public class FoundationShip : ShipBase
    {
        private readonly int ownerId;
        private CompDeltaVPilotConsole console;
        public Ship Structure => new Ship(ownerId, console?.parent is { Spawned: true } parent
            ? parent.Map : null, console);
        private SpaceObject checkedContainer;
        private int hostVersion = -1;
        private bool hasSeparateHost;
        public FoundationShip(Ship structure)
        {
            ownerId = structure.ownerId;
            console = structure.console;
        }

        internal void Reanchor(CompDeltaVPilotConsole next)
        {
            console = next;
            hostVersion = -1;
            checkedContainer = null;
        }
        public override bool IsValid => Structure.IsValid;
        public override Map Map => Structure.map;
        public override DriveSubject Identity => new DriveSubject(0, ownerId);
        public override Thing Anchor => console?.parent;
        public override float FrameAngle => ShipRegistry.LocalFrameAngle(Structure);
        public override Vector2 HullCenter => ShipRegistry.HullCenter(Structure);
        public override IEnumerable<IntVec3> HullCells => ShipRegistry.ControlDomain(Structure);
        public override Faction Faction => ShipRegistry.FactionOf(Structure);
        public override bool IsPublicFacility => Structure.console?.isPublicFacility == true;
        public override string Label => Current.Game?.GetComponent<UniverseManager>()?.LabelForOwner(Identity.key) ?? "";
        public override ShipDerivedState State => ShipRegistry.StateOf(Structure);
        public override ShipCombatState Combat => Structure.console?.combat;
        public override bool CanDriveIn(SpaceObject container) => IsValid && container?.map == Map;
        internal bool RequiresSeparatePocket(SpaceObject container, FoundationTopologyService topology)
        {
            int version = ShipStructureIndex.ReadRevision(Map, ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                | ShipStructureAspect.Membership | ShipStructureAspect.Footprint | ShipStructureAspect.Terrain
                | ShipStructureAspect.Primary | ShipStructureAspect.Representation);
            if (checkedContainer != container || hostVersion != version)
            {
                hasSeparateHost = ShipRegistry.HasSeparateHost(Structure, container, topology);
                hostVersion = version;
                checkedContainer = container;
            }
            return hasSeparateHost;
        }
        public override bool Contains(Thing thing) => ShipRegistry.Contains(Structure, thing);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            if (!IsValid) yield break;
            ShipStructure state = StructuralState;
            foreach (Building thing in state.Thrusters)
                yield return ShipPhysics.InstalledThruster(state, thing);
        }
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons() => IsValid
            ? SpaceWeaponService.WeaponsRegisteredOrMapped(Map).Where(w => w.OwnerShip.Identity == Identity)
            : Enumerable.Empty<CompSpaceWeapon>();
        public override IEnumerable<SpaceCollisionCell> CollisionCells =>
            SpaceStructureRules.CollisionCells(Map, HullCells);
        public override ShipTransferSpec Transfer => new ShipTransferSpec(
            Anchor as Building, null, Structure.ownerId, true);
        public override SpaceObjectTag RolesIn(SpaceObject container) => SpaceObjectTag.Ship;
    }

    /// <summary>单建筑船；原版 transporter 与 launchable 仍由 CompSpaceShuttle 适配。</summary>
    public class ShuttleShip : ShipBase
    {
        public readonly CompSpaceShuttle Hull;
        private Map driveMap;
        private int driveVersion = -1;
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
        public override ShipDerivedState State => Current.Game?.GetComponent<UniverseManager>()?.FrameOf(Map)?.IsWorksite != true
            && CanDriveIn(Current.Game?.GetComponent<UniverseManager>()?.FrameOf(Map))
            ? ShipDerivedState.FreeFlight : ShipDerivedState.Docked;
        public override ShipCombatState Combat => Hull.combat;
        public override bool CanDriveIn(SpaceObject container)
        {
            if (!IsValid || container?.map != Map || Map.Parent is not ShipPocketParent_DeltaV) return false;
            int revision = ShipStructureIndex.ReadRevision(Map, ShipStructureAspect.Foundation | ShipStructureAspect.Membership
                | ShipStructureAspect.Footprint | ShipStructureAspect.Terrain | ShipStructureAspect.Representation);
            if (driveMap != Map || driveVersion != revision)
            {
                driveMap = Map;
                driveVersion = revision;
                var topology = Current.Game.GetComponent<UniverseManager>().FoundationTopology;
                independentHull = topology.DomainCount(Map) == 0
                    && SubstructureOwnership.For(Map).SpawnedBuildingCount == 1
                    && !HullCells.Any(c => Map.terrainGrid.BaseTerrainAt(c) is TerrainDef terrain
                        && terrain != TerrainDefOf.Space);
            }
            return independentHull;
        }
        public override bool Contains(Thing thing) => IsValid && thing == Anchor;
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters()
        {
            foreach (var thruster in StructuralState?.VirtualThrusters ?? System.Array.Empty<ShipPhysics.Thruster>())
                yield return thruster.NominalSnapshot();
        }

        internal IEnumerable<ShipPhysics.Thruster> CreateThrusters()
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
                    dir = force.normalized, thrust = force.magnitude, operational = true,
                    shuttle = Hull, baseThrust = force.magnitude,
                    boostable = configured.force.x > 0f && Mathf.Abs(configured.force.y) < 0.001f,
                    visuals = configured.visuals ?? Hull.ShuttleProps.exhaust,
                    normalVisuals = configured.visuals ?? Hull.ShuttleProps.exhaust
                };
            }
        }
        // 默认 Def 没有武器；追加 Comp 的扩展仍进入同一武器事务。
        protected override IEnumerable<CompSpaceWeapon> CollectWeapons() => IsValid
            ? CompSpaceWeapon.On(Hull.parent) : Enumerable.Empty<CompSpaceWeapon>();
        public override IEnumerable<SpaceCollisionCell> CollisionCells =>
            SpaceStructureRules.CollisionCells(Map, HullCells);
        public override SpaceObjectTag RolesIn(SpaceObject container) => SpaceObjectTag.Ship
            | (CanDriveIn(container) ? SpaceObjectTag.Shuttle : 0);
        public override ShipTransferSpec Transfer => new ShipTransferSpec(null, Anchor as Building, 0, false);
        public override bool MoveColocatedItems => false;
        public override AcceptanceReport CheckDeparture()
        {
            AcceptanceReport report = base.CheckDeparture();
            if (!report) return report;
            report = Hull.Launchable?.CanLaunch() ?? AcceptanceReport.WasRejected;
            if (!report) return report;
            // 原版只检查根格；多格艇的整片脚印也必须可穿过屋顶。
            if (HullCells.Any(c => Map.roofGrid.Roofed(c) && !MoonPoolUtility.IsMoonPoolAt(Map, c)))
                return "CommandLaunchGroupFailUnderRoof".Translate();
            return true;
        }
        public override bool CanBoard => Hull.CanBoard;
        public override void NotifyLanded()
        {
            Hull.intrusionDriveActive = false;
            SpaceShuttleService.StartLaunchCooldown(Hull.parent.TryGetComp<CompLaunchable>());
        }
    }

    /// <summary>火控与武器组的唯一持久模型，由船体身份锚保存。</summary>
    public sealed class ShipCombatState : IExposable
    {
        private const int AllWeaponGroups = (1 << 6) - 1;

        public bool weaponsArmed;
        public ShipFireControlMode fireControlMode = ShipFireControlMode.Automatic;
        public int weaponGroupsMain = AllWeaponGroups, weaponGroupsSecondary = AllWeaponGroups;
        public int WeaponGroupMask(SpaceWeaponSalvoSet set) =>
            set == SpaceWeaponSalvoSet.Main ? weaponGroupsMain : weaponGroupsSecondary;
        public void SetWeaponGroupMask(SpaceWeaponSalvoSet set, int mask)
        {
            if (set == SpaceWeaponSalvoSet.Main) weaponGroupsMain = mask;
            else weaponGroupsSecondary = mask;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref weaponsArmed, "weaponsArmed", false);
            Scribe_Values.Look(ref fireControlMode, "fireControlMode", ShipFireControlMode.Automatic);
            Scribe_Values.Look(ref weaponGroupsMain, "weaponGroupsMain", AllWeaponGroups);
            Scribe_Values.Look(ref weaponGroupsSecondary, "weaponGroupsSecondary", AllWeaponGroups);
        }
    }
}

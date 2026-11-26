using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class MapSpaceObject : SpaceObject
    {
        private Map interior;
        private SpaceObjectTag memberTags;
        private bool independentShuttle;
        private IReadOnlyList<ShipBase> members = System.Array.Empty<ShipBase>();
        private int membersVersion = -1;
        private int rolesVersion = -1;
        private Pawn personnelPawn;
        private PawnSpaceflightSnapshot personnelSnapshot;
        [Unsaved] private PawnSpaceflightShip personnelShip;
        internal Pawn PersonnelPawn => personnelPawn;
        internal PawnSpaceflightSnapshot PersonnelSnapshot => personnelSnapshot;
        internal PawnSpaceflightShip PersonnelShip => personnelPawn == null || personnelSnapshot == null
            ? null : personnelShip ??= new PawnSpaceflightShip(personnelPawn, personnelSnapshot);
        public override Map map
        {
            get => interior;
            internal set { interior = value; rolesVersion = membersVersion = -1; members = System.Array.Empty<ShipBase>(); personnelShip = null; }
        }
        public MapSpaceObject() { }
        public MapSpaceObject(int id, Map map, SpacePose pose) : base(id, pose) { interior = map; }
        internal void SetPersonnel(Pawn pawn, PawnSpaceflightSnapshot snapshot)
        {
            personnelPawn = pawn;
            personnelSnapshot = snapshot;
            personnelShip = null;
            ClearMemberCaches();
        }
        public override IEnumerable<ShipBase> Ships
        {
            get
            {
                if (interior == null || interior.Disposed) return System.Array.Empty<ShipBase>();
                var index = Current.Game.GetComponent<UniverseManager>().ShipStructure;
                int revision = index.ShipRevision(interior);
                if (membersVersion != revision)
                {
                    members = ShipRegistry.DiscoverShipsOnMap(interior).ToArray();
                    membersVersion = index.ShipRevision(interior);
                }
                return members;
            }
        }
        internal void ClearMemberCaches()
        {
            members = System.Array.Empty<ShipBase>();
            membersVersion = rolesVersion = -1;
        }
        protected override SpaceObjectTag MemberTags
        {
            get
            {
                if (interior == null || interior.Disposed) return 0;
                int revision = ShipStructureIndex.ReadRevision(interior, ShipStructureAspect.Foundation
                    | ShipStructureAspect.Membership | ShipStructureAspect.Footprint | ShipStructureAspect.Terrain
                    | ShipStructureAspect.Primary | ShipStructureAspect.Representation);
                if (rolesVersion != revision)
                {
                    memberTags = 0;
                    independentShuttle = false;
                    foreach (ShipBase ship in Ships)
                    {
                        memberTags |= ship.RolesIn(this);
                        if (ship is ShuttleShip shuttle && shuttle.CanDriveIn(this)) independentShuttle = true;
                    }
                    rolesVersion = revision;
                }
                return memberTags;
            }
        }
        // 点防读取与成员角色共用同一份缓存，不另行扫描船体。
        protected override bool HasIndependentShuttle { get { _ = MemberTags; return independentShuttle; } }
        protected override IEnumerable<Faction> ShipFactions => Ships.Select(ship => ship.Faction);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters() =>
            Ships.Where(ship => ship.CanDriveIn(this)).SelectMany(ship => ship.BuildThrusters());
        public override IEnumerable<SpaceCollisionCell> CollisionCells
        {
            get
            {
                if (interior == null || interior.Disposed) yield break;
                if (IsScattered) yield break;
                var structure = ShipStructureIndex.Read(interior);
                if (structure == null) yield break;
                var occupied = new HashSet<IntVec3>(SubstructureOwnership.CellsOnMap(interior));
                occupied.UnionWith(structure.OccupiedBuildingCells);
                var custom = new HashSet<IntVec3>();
                foreach (ShipBase ship in Ships)
                {
                    if (ship.GetType() == typeof(FoundationShip) || ship.GetType() == typeof(ShuttleShip)) continue;
                    foreach (SpaceCollisionCell cell in ship.CollisionCells)
                        if (cell.cell.InBounds(interior) && custom.Add(cell.cell)) yield return cell;
                }
                occupied.ExceptWith(custom);
                foreach (SpaceCollisionCell cell in SpaceStructureRules.CollisionCells(interior, occupied))
                    yield return cell;
            }
        }
        protected override ShipPhysics.MassReport MotionReport
        {
            get
            {
                if (interior == null || interior.Disposed) return ShipPhysics.PointMassReport;
                return ShipPhysics.ApplyPersonnelMass(ShipPhysics.GetReport(this), personnelPawn);
            }
        }
        // Pure drift needs mass/COM but does not consume real-time thruster availability.
        protected override ShipPhysics.MassReport DriftReport =>
            interior == null || interior.Disposed ? ShipPhysics.PointMassReport
                : ShipPhysics.ApplyPersonnelMass(ShipPhysics.GetReport(this), personnelPawn);
        private PilotTarget ResolvePilot(DriveSubject subject)
        {
            if (interior == null || interior.Disposed || subject.key <= 0) return default;
            foreach (ShipBase ship in Ships)
            {
                PilotTarget pilot = PilotTarget.Of(ship);
                if (ship.Identity.Equals(subject)
                    && ship.CanDriveIn(this)) return pilot;
            }
            return default;
        }
        public override DriveSubject PrimaryDriver
        {
            get
            {
                if (interior == null || interior.Disposed) return default;
                var ships = Ships
                    .Where(ship => ship.CanDriveIn(this) && ship.State == ShipDerivedState.FreeFlight).ToList();
                return ships.Count == 1
                    ? ships[0].Identity.InContainer(objectId) : default;
            }
        }
        public override bool TryGetDrive(DriveSubject subject, out DriveInfo info)
        {
            var pilot = ResolvePilot(subject);
            info = pilot.IsValid ? ReadDriveInfo(pilot.FrameAngle) : null;
            return info != null;
        }
        public override DriveResult ApplyDrive(DriveSubject subject, DriveControl control, float dt, out float allocatedTorque)
        {
            allocatedTorque = 0f;
            var pilot = ResolvePilot(subject);
            if (!pilot.IsValid) { ClearDrive(); return DriveResult.InvalidSubject; }
            ShipPhysics.MassReport report = MotionReport;
            DriveResult result = ExecuteDrive(report, pilot.FrameAngle, subject, control, dt, out allocatedTorque);
            if (result == DriveResult.Executed && subject.shuttle)
                pilot.Vessel.Anchor?.TryGetComp<CompSpaceShuttle>()?.ConsumeBoostTick(report);
            return result;
        }
        protected override DriveResult AuthorizeDrive(DriveSubject subject, ShipPhysics.Command command, bool playerInitiated)
        {
            var universe = Current.Game.GetComponent<UniverseManager>();
            long authorizeStart = universe.Performance.Start();
            if ((Mathf.Abs(command.fx) > 1e-6f || Mathf.Abs(command.fz) > 1e-6f || Mathf.Abs(command.torque) > 1e-6f)
                && !universe.ShipTransitions.RequestThrust(universe, ResolvePilot(subject), playerInitiated))
            {
                universe.Performance.Record(PerformanceStage.DriveAuthorize, authorizeStart);
                return DriveResult.WaitingForSeparation;
            }
            universe.Performance.Record(PerformanceStage.DriveAuthorize, authorizeStart);
            return DriveResult.Executed;
        }
        protected override void AllocateDrive(ShipPhysics.MassReport report, ShipPhysics.Command command,
            out Vector2 force, out float torque)
        {
            var performance = Current.Game.GetComponent<UniverseManager>().Performance;
            long allocateStart = performance.Start();
            ShipPhysics.Allocate(report, command, out force, out torque);
            performance.Record(PerformanceStage.DriveAllocate, allocateStart);
        }
        public override void ClearDrive() { base.ClearDrive(); ShipPhysics.ClearThrottles(interior); }
        internal ArchivedSpaceObject CaptureArchive(string data)
        {
            var archived = new ArchivedSpaceObject();
            CopyFactsTo(archived);
            archived.ambientLabel = label;
            archived.mapData = data; archived.mapId = interior.uniqueID;
            archived.mapArea = interior.Size.x * interior.Size.z;
            archived.parent = (PocketMapParent)interior.Parent;
            archived.memberFactions = ShipFactions.ToList();
            archived.globe = Current.Game.GetComponent<UniverseManager>().GlobeDisplay.Capture(this);
            DriveSubject subject = PrimaryDriver;
            DriveInfo info = TryGetDrive(subject, out var drive) ? drive : MotionInfo;
            archived.dynamics = DriveSnapshot.Capture(info, subject);
            return archived;
        }
        public override string label => personnelPawn?.LabelCap ?? base.label;
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) personnelSnapshot?.CaptureWeaponRuntime();
            base.ExposeData();
            Scribe_References.Look(ref interior, "map");
            Scribe_References.Look(ref personnelPawn, "personnelPawn");
            Scribe_Deep.Look(ref personnelSnapshot, "personnelFlight");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) personnelShip = null;
        }
    }

    public sealed class ArchivedSpaceObject : SpaceObject
    {
        public string mapData;
        public int mapId;
        internal int mapArea;
        public PocketMapParent parent;
        public DriveSnapshot dynamics;
        public GlobeDisplaySummary globe;
        internal List<Faction> memberFactions = new List<Faction>();
        protected override IEnumerable<Faction> ShipFactions => memberFactions;
        public float observedThrust, observedFirepower;
        public int observedMainConsoleId;
        public List<NpcShipCombatSummary> npcShips = new List<NpcShipCombatSummary>();
        /// <summary>本归档按下「强制保留」的死者；释放时只松开这些，不替其他持有者做主。</summary>
        internal List<Pawn> pinnedDeadPawns = new List<Pawn>();
        protected override ShipPhysics.MassReport MotionReport => dynamics?.Report ?? ShipPhysics.PointMassReport;
        public override DriveSubject PrimaryDriver => dynamics?.Identity.InContainer(objectId) ?? default;
        public override bool TryGetDrive(DriveSubject subject, out DriveInfo info)
        {
            info = dynamics != null && dynamics.ownerId > 0 && subject.Equals(PrimaryDriver)
                ? ReadDriveInfo(dynamics.frameAngle) : null;
            return info != null;
        }
        public override DriveResult ApplyDrive(DriveSubject subject, DriveControl control, float dt, out float allocatedTorque)
        {
            allocatedTorque = 0f;
            if (dynamics == null || dynamics.ownerId <= 0 || !subject.Equals(PrimaryDriver))
                return DriveResult.InvalidSubject;
            return ExecuteDrive(MotionReport, dynamics.frameAngle, subject, control, dt, out allocatedTorque);
        }
        /// <summary>
        /// 原版 WorldPawn GC 只在尸体仍连着死者时保留它（CorpseExists）。尸体只存在于归档后，
        /// 读档会断开这条连接，下一轮 GC 就丢弃死者。归档期间用原版「强制保留」集合钉住；
        /// 已在集合里的不重复记录，释放时也就不会移除。
        /// </summary>
        internal static List<Pawn> PinCorpsePawns(IEnumerable<Thing> things)
        {
            var pins = new List<Pawn>();
            WorldPawns world = Find.WorldPawns;
            if (world == null) return pins;
            HashSet<Pawn> kept = world.ForcefullyKeptPawns, seen = new HashSet<Pawn>();
            foreach (Thing thing in things)
            {
                if (!(thing is Corpse corpse) || corpse.Bugged || !(corpse.InnerPawn is Pawn pawn)
                    || pawn.Discarded || !seen.Add(pawn) || kept.Contains(pawn))
                    continue;
                if (!world.Contains(pawn)) world.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
                else kept.Add(pawn);
                pins.Add(pawn);
            }
            return pins;
        }
        /// <summary>归档离开对象表（恢复、退役或注销）时调用：之后这些死者重新交由原版 GC 处置。</summary>
        internal void ReleaseDeadPawnPins()
        {
            if (pinnedDeadPawns == null || pinnedDeadPawns.Count == 0) return;
            HashSet<Pawn> kept = Find.WorldPawns?.ForcefullyKeptPawns;
            if (kept != null)
                foreach (Pawn pawn in pinnedDeadPawns) kept.Remove(pawn);
            pinnedDeadPawns.Clear();
        }
        /// <summary>恢复时接受的缺席：原版装载本身会丢弃的污渍，以及内部 pawn 已不在游戏里的尸体。</summary>
        internal static bool TolerateUnspawned(Thing thing)
        {
            if (thing?.def == null) return false;
            if (thing.def.category == ThingCategory.Filth) return true;
            return thing is Corpse corpse && corpse.Bugged;
        }
        internal MapSpaceObject RestoreRepresentation(Map restored)
        {
            var result = new MapSpaceObject(objectId, restored, pose);
            CopyFactsTo(result);
            return result;
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mapData, "mapData"); Scribe_Values.Look(ref mapId, "mapId");
            Scribe_Values.Look(ref mapArea, "mapArea");
            Scribe_References.Look(ref parent, "parent"); Scribe_Deep.Look(ref dynamics, "dynamics");
            Scribe_Deep.Look(ref globe, "globe");
            Scribe_Collections.Look(ref memberFactions, "shipFactions", LookMode.Reference);
            if (memberFactions == null) memberFactions = new List<Faction>();
            Scribe_Values.Look(ref observedThrust, "observedThrust");
            Scribe_Values.Look(ref observedFirepower, "observedFirepower");
            Scribe_Values.Look(ref observedMainConsoleId, "observedMainConsoleId");
            Scribe_Collections.Look(ref npcShips, "npcShipCombatSummaries", LookMode.Deep);
            npcShips ??= new List<NpcShipCombatSummary>();
            Scribe_Collections.Look(ref pinnedDeadPawns, "pinnedDeadPawns", saveDestroyedThings: true,
                LookMode.Reference);
            pinnedDeadPawns ??= new List<Pawn>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) pinnedDeadPawns.RemoveAll(pawn => pawn == null);
        }
    }

    public sealed class NpcShipCombatSummary : IExposable
    {
        public int shipId, mainConsoleId, initialMainConsoleId;
        public float initialThrust, initialFirepower, observedThrust, observedFirepower;
        public bool retreating;

        public void ExposeData()
        {
            Scribe_Values.Look(ref shipId, "shipId");
            Scribe_Values.Look(ref mainConsoleId, "mainConsoleId");
            Scribe_Values.Look(ref initialMainConsoleId, "initialMainConsoleId");
            Scribe_Values.Look(ref initialThrust, "initialThrust");
            Scribe_Values.Look(ref initialFirepower, "initialFirepower");
            Scribe_Values.Look(ref observedThrust, "observedThrust");
            Scribe_Values.Look(ref observedFirepower, "observedFirepower");
            Scribe_Values.Look(ref retreating, "retreating");
        }
    }
}

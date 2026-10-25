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
        private IReadOnlyList<ShipBase> members = System.Array.Empty<ShipBase>();
        private int membersVersion = -1;
        private int rolesVersion = -1;
        public override Map map
        {
            get => interior;
            internal set { interior = value; rolesVersion = membersVersion = -1; members = System.Array.Empty<ShipBase>(); }
        }
        public MapSpaceObject() { }
        public MapSpaceObject(int id, Map map, SpacePose pose) : base(id, pose) { interior = map; }
        public override IEnumerable<ShipBase> Ships
        {
            get
            {
                if (interior == null || interior.Disposed) return System.Array.Empty<ShipBase>();
                if (SubstructureOwnership.CacheNeedsRefresh(membersVersion))
                {
                    members = ShipRegistry.DiscoverShipsOnMap(interior).ToArray();
                    membersVersion = SubstructureOwnership.GlobalStructureVersion;
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
                if (SubstructureOwnership.CacheNeedsRefresh(rolesVersion))
                {
                    memberTags = 0;
                    foreach (ShipBase ship in Ships) memberTags |= ship.RolesIn(this);
                    rolesVersion = SubstructureOwnership.GlobalStructureVersion;
                }
                return memberTags;
            }
        }
        protected override IEnumerable<Faction> ShipFactions => Ships.Select(ship => ship.Faction);
        public override IEnumerable<ShipPhysics.Thruster> BuildThrusters() =>
            Ships.Where(ship => ship.CanDriveIn(this)).SelectMany(ship => ship.BuildThrusters());
        public override IEnumerable<SpaceCollisionCell> CollisionCells
        {
            get
            {
                if (interior == null || interior.Disposed) yield break;
                if (IsScattered) yield break;
                var covered = new HashSet<IntVec3>();
                foreach (ShipBase ship in Ships)
                    foreach (SpaceCollisionCell cell in ship.CollisionCells)
                        if (cell.cell.InBounds(interior) && covered.Add(cell.cell)) yield return cell;
                // 无主残骸、天然岩石与船体范围外的建筑仍由同一地图分类入口提供。
                foreach (SpaceCollisionCell cell in SpaceStructureRules.CollisionCells(interior,
                    CellRect.WholeMap(interior).Where(cell => !covered.Contains(cell)))) yield return cell;
            }
        }
        protected override ShipPhysics.MassReport MotionReport
        {
            get
            {
                if (interior == null || interior.Disposed) return ShipPhysics.PointMassReport;
                var report = ShipPhysics.GetReport(this);
                ShipPhysics.RefreshOperational(report);
                return report;
            }
        }
        // Pure drift needs mass/COM but does not consume real-time thruster availability.
        protected override ShipPhysics.MassReport DriftReport =>
            interior == null || interior.Disposed ? ShipPhysics.PointMassReport : ShipPhysics.GetReport(this);
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
            DriveResult result = ExecuteDrive(MotionReport, pilot.FrameAngle, subject, control, dt, out allocatedTorque);
            if (result == DriveResult.Executed && subject.shuttle)
                pilot.Vessel.Anchor?.TryGetComp<CompSpaceShuttle>()?.ConsumeBoostTick(ReadDriveInfo(pilot.FrameAngle));
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
            archived.parent = (PocketMapParent)interior.Parent;
            archived.memberFactions = ShipFactions.ToList();
            archived.globe = Current.Game.GetComponent<UniverseManager>().GlobeDisplay.Capture(this);
            DriveSubject subject = PrimaryDriver;
            DriveInfo info = TryGetDrive(subject, out var drive) ? drive : MotionInfo;
            archived.dynamics = DriveSnapshot.Capture(info, subject);
            return archived;
        }
        public override void ExposeData() { base.ExposeData(); Scribe_References.Look(ref interior, "map"); }
    }

    public sealed class ArchivedSpaceObject : SpaceObject
    {
        public string mapData;
        public int mapId;
        public PocketMapParent parent;
        public DriveSnapshot dynamics;
        public GlobeDisplaySummary globe;
        internal List<Faction> memberFactions = new List<Faction>();
        protected override IEnumerable<Faction> ShipFactions => memberFactions;
        public float observedThrust, observedFirepower;
        public int observedMainConsoleId;
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
            if (!TryGetDrive(subject, out var info)) return DriveResult.InvalidSubject;
            return ExecuteDrive(MotionReport, info.frameAngle, subject, control, dt, out allocatedTorque);
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
            Scribe_References.Look(ref parent, "parent"); Scribe_Deep.Look(ref dynamics, "dynamics");
            Scribe_Deep.Look(ref globe, "globe");
            Scribe_Collections.Look(ref memberFactions, "shipFactions", LookMode.Reference);
            if (memberFactions == null) memberFactions = new List<Faction>();
            Scribe_Values.Look(ref observedThrust, "observedThrust");
            Scribe_Values.Look(ref observedFirepower, "observedFirepower");
            Scribe_Values.Look(ref observedMainConsoleId, "observedMainConsoleId");
        }
    }
}

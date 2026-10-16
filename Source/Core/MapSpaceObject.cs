using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class MapSpaceObject : SpaceObject
    {
        private Map interior;
        private ShipPhysics.Thruster[] virtualThrusters;
        public override Map map { get => interior; internal set => interior = value; }
        public MapSpaceObject() { }
        public MapSpaceObject(int id, Map map, SpacePose pose) : base(id, pose) { interior = map; }
        protected override ShipPhysics.MassReport MotionReport
        {
            get
            {
                if (interior == null || interior.Disposed) return ShipPhysics.PointMassReport;
                EnsureVirtualLayout();
                var report = ShipPhysics.GetReport(interior, virtualThrusters);
                ShipPhysics.RefreshOperational(report);
                return report;
            }
        }
        /// <summary>XML 布局在有图对象内重建；运行时缓存不落档，不另设全局虚拟动力表。</summary>
        private void EnsureVirtualLayout()
        {
            if (baseTag != SpaceObjectTag.Shuttle || virtualThrusters != null) return;
            Building hull = SpaceShuttleService.HullOf(this);
            CompSpaceShuttle comp = hull?.TryGetComp<CompSpaceShuttle>();
            if (comp == null) return;
            Map pocket = interior;
            Vector2 origin = SpaceTransform.ShipLocalOrigin(pocket);
            Vector2 hullCenter = SpaceTransform.BoundsCenterOfCells(hull.OccupiedRect().Cells);
            float frameAngle = ShipOrientation.AngleFromRot4(hull.Rotation);
            IReadOnlyList<ShuttleThrusterConfig> layout = comp.ThrusterLayout;
            var thrusters = new List<ShipPhysics.Thruster>(layout?.Count ?? 0);
            if (layout != null)
            {
                for (int i = 0; i < layout.Count; i++)
                {
                    ShuttleThrusterConfig configured = layout[i];
                    if (configured == null || configured.force.sqrMagnitude <= 1e-6f) continue;
                    Vector2 mapForce = SpaceTransform.Rotate(configured.force, frameAngle);
                    float thrust = mapForce.magnitude;
                    thrusters.Add(new ShipPhysics.Thruster
                    {
                        thing = null,
                        localPos = hullCenter - origin
                            + SpaceTransform.Rotate(configured.position, frameAngle),
                        dir = mapForce / thrust,
                        operational = true,
                        thrust = thrust,
                    });
                }
            }
            virtualThrusters = thrusters.ToArray();
            ShipPhysics.Forget(interior);
        }

        private PilotTarget ResolvePilot(DriveSubject subject)
        {
            if (interior == null || interior.Disposed || subject.key <= 0) return default;
            if (!subject.shuttle)
            {
                var ship = ShipRegistry.ShipByOwner(subject.key);
                return ship.IsValid && ship.map == interior ? PilotTarget.Of(ship) : default;
            }
            var body = SpaceShuttleService.HullOf(this);
            return body?.thingIDNumber == subject.key && baseTag == SpaceObjectTag.Shuttle ? PilotTarget.Of(body) : default;
        }
        public override DriveSubject PrimaryDriver
        {
            get
            {
                if (interior == null || interior.Disposed) return default;
                var ships = ShipRegistry.ShipsOnMap(interior);
                return ships.Count == 1 && ShipRegistry.IsFreeFlight(ships[0])
                    ? new DriveSubject(objectId, ships[0].ownerId) : default;
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
            return ExecuteDrive(MotionReport, pilot.FrameAngle, subject, control, dt, out allocatedTorque);
        }
        protected override DriveResult AuthorizeDrive(DriveSubject subject, ShipPhysics.Command command, bool playerInitiated)
        {
            var universe = Current.Game.GetComponent<UniverseManager>();
            if ((Mathf.Abs(command.fx) > 1e-6f || Mathf.Abs(command.fz) > 1e-6f || Mathf.Abs(command.torque) > 1e-6f)
                && !universe.ShipTransitions.RequestThrust(universe, ResolvePilot(subject), playerInitiated))
                return DriveResult.WaitingForSeparation;
            return DriveResult.Executed;
        }
        protected override void AllocateDrive(ShipPhysics.MassReport report, ShipPhysics.Command command,
            out Vector2 force, out float torque) => ShipPhysics.Allocate(report, command, out force, out torque);
        public override void ClearDrive() { base.ClearDrive(); ShipPhysics.ClearThrottles(interior); }
        internal ArchivedSpaceObject CaptureArchive(string data)
        {
            var archived = new ArchivedSpaceObject();
            CopyFactsTo(archived);
            archived.ambientLabel = label;
            archived.mapData = data; archived.mapId = interior.uniqueID;
            archived.parent = (PocketMapParent)interior.Parent;
            DriveSubject subject = PrimaryDriver;
            DriveInfo info = TryGetDrive(subject, out var drive) ? drive : MotionInfo;
            archived.dynamics = DriveSnapshot.Capture(info, subject.key);
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
        public float observedThrust, observedFirepower;
        public int observedMainConsoleId;
        protected override ShipPhysics.MassReport MotionReport => dynamics?.Report ?? ShipPhysics.PointMassReport;
        public override DriveSubject PrimaryDriver => new DriveSubject(objectId, dynamics?.ownerId ?? 0);
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
            Scribe_Values.Look(ref observedThrust, "observedThrust");
            Scribe_Values.Look(ref observedFirepower, "observedFirepower");
            Scribe_Values.Look(ref observedMainConsoleId, "observedMainConsoleId");
        }
    }
}

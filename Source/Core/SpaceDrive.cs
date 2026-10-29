using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>驾驶主体身份与容器身份分开；分离后由主体重新解析当前容器。</summary>
    public readonly struct DriveSubject : IEquatable<DriveSubject>
    {
        public readonly int objectId, key;
        public readonly bool shuttle;
        public DriveSubject(int objectId, int key, bool shuttle = false)
        { this.objectId = objectId; this.key = key; this.shuttle = shuttle; }
        public DriveSubject InContainer(int id) => new DriveSubject(id, key, shuttle);
        public static DriveSubject Of(Ship ship) => Of(PilotTarget.Of(ship));
        public static DriveSubject Of(ShipBase ship) => Of(PilotTarget.Of(ship));
        public static DriveSubject Of(PilotTarget target) => !target.IsValid ? default
            : new DriveSubject(Current.Game?.GetComponent<UniverseManager>()?.FrameOf(target.Map)?.objectId ?? 0,
                target.Identity.key, target.Identity.shuttle);
        public SpaceObject Resolve(UniverseManager universe)
        {
            SpaceObject current = universe?.ObjectById(objectId);
            if (current?.TryGetDrive(this, out _) == true) return current;
            // 船分离会换容器；ownerId 只查船，再经真实 Map 查询容器。
            ShipBase ship = ShipRegistry.ShipByIdentity(this);
            SpaceObject frame = ship != null ? universe?.FrameOf(ship.Map) : null;
            return ship?.CanDriveIn(frame) == true ? frame : null;
        }
        public bool Equals(DriveSubject other) => key == other.key && shuttle == other.shuttle;
        public override bool Equals(object obj) => obj is DriveSubject other && Equals(other);
        public override int GetHashCode() => (key * 397) ^ (shuttle ? 1 : 0);
        public static bool operator ==(DriveSubject a, DriveSubject b) => a.Equals(b);
        public static bool operator !=(DriveSubject a, DriveSubject b) => !a.Equals(b);
        public override string ToString() => (shuttle ? "thing:" : "structure:") + key;
    }

    public enum DriveResult { Executed, WaitingForSeparation, InvalidSubject, AlreadyAdvanced }
    public struct DriveControl
    {
        public Vector2 force;
        public float torque, voidBlend;
        public bool playerInitiated, assistX, assistZ, assistW;
        internal bool hasInput;
        internal ShipPhysics.Command input;
        public static DriveControl FromInput(ShipPhysics.Command command, bool assistX, bool assistZ, bool assistW)
            => new DriveControl { hasInput = true, input = command, playerInitiated = true,
                assistX = assistX, assistZ = assistZ, assistW = assistW };
    }

    /// <summary>只读物理视图；预览只写求解工作区，不写真实油门或表现时间。</summary>
    public sealed class DriveInfo
    {
        private readonly ShipPhysics.MassReport report;
        public readonly float frameAngle;
        public SpacePose pose { get; internal set; }
        internal DriveInfo(ShipPhysics.MassReport report, float frameAngle = 0f)
        { this.report = report; this.frameAngle = frameAngle; }
        public float mass => report.mass;
        public float inertia => report.inertia;
        public Vector2 comLocal => report.comLocal;
        public int version => report.version;
        public float throttlesWrittenAt => report.throttlesWrittenAt;
        public IReadOnlyList<ThrusterInfo> thrusters => views ?? (views = BuildViews());
        private IReadOnlyList<ThrusterInfo> views;
        private IReadOnlyList<ThrusterInfo> BuildViews()
        {
            var result = new List<ThrusterInfo>(report.thrusters.Count);
            foreach (var thruster in report.thrusters) result.Add(new ThrusterInfo(thruster));
            return result.AsReadOnly();
        }
        public float AvailableThrust
        {
            get { float sum = 0f; foreach (var thruster in report.thrusters)
                if (thruster.operational) sum += thruster.thrust; return sum; }
        }
        public ShipPhysics.ControlModel PreviewModel(ShipPhysics.ControlModel reuse = null)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewModel);
            return ShipPhysics.BuildControlModel(report, reuse);
        }
        private ShipPhysics.MassReport unboostedReport;
        private int unboostedVersion = -1;
        internal float BoostSecondsRemaining
        {
            get
            {
                foreach (var thruster in report.thrusters)
                    if (thruster.boostable && thruster.shuttle?.BoostMultiplier > 1f)
                        return thruster.shuttle.BoostTicksRemaining / 60f;
                return 0f;
            }
        }
        internal ShipPhysics.ControlModel PreviewUnboostedModel(ShipPhysics.ControlModel reuse)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewModel);
            if (unboostedReport == null || unboostedVersion != report.version)
            {
                unboostedReport = new ShipPhysics.MassReport
                    { mass = report.mass, inertia = report.inertia, comLocal = report.comLocal };
                foreach (var t in report.thrusters)
                    unboostedReport.thrusters.Add(new ShipPhysics.Thruster
                    { localPos = t.localPos, dir = t.dir, operational = t.operational,
                        thrust = t.shuttle != null ? t.baseThrust : t.thrust });
                unboostedVersion = report.version;
            }
            return ShipPhysics.BuildControlModel(unboostedReport, reuse);
        }
        public float PreviewBraking(Vector2 direction)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewBraking);
            return ShipPhysics.PreviewBrakingAcceleration(report, direction);
        }
        public float PreviewAngular(int direction)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewAngular);
            return ShipPhysics.PreviewAngularAcceleration(report, direction);
        }
        public void Preview(ShipPhysics.Command command, out Vector2 force, out float torque)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewAllocation);
            ShipPhysics.PreviewAllocation(report, command, out force, out torque);
        }
        public void Preview(Vector2 desiredForce, float desiredTorque, out Vector2 force, out float torque)
            => Preview(ShipPhysics.ForceToCommand(report, desiredForce, desiredTorque), out force, out torque);
    }

    public readonly struct ThrusterInfo
    {
        private readonly ShipPhysics.Thruster source;
        internal ThrusterInfo(ShipPhysics.Thruster source) { this.source = source; }
        public Thing thing => source?.thing;
        public Vector2 localPos => source.localPos;
        public Vector2 dir => source.dir;
        public bool operational => source.operational;
        public float thrust => source.thrust;
        public float throttle => source.throttle;
        public ThrusterVisualProfile visuals => thing?.TryGetComp<CompThrusterVisuals>()?.Profile ?? source.visuals;
    }

    /// <summary>整图卸载后无法重建的数值布局；不保存 Thing、油门、求解工作区。</summary>
    public sealed class DriveSnapshot : IExposable
    {
        public float mass, inertia, frameAngle;
        public Vector2 comLocal;
        public int ownerId;
        public bool shuttle;
        public List<Vector2> positions = new List<Vector2>(), directions = new List<Vector2>();
        public List<float> forces = new List<float>();
        private ShipPhysics.MassReport report;
        internal ShipPhysics.MassReport Report
        {
            get
            {
                if (report != null) return report;
                report = new ShipPhysics.MassReport { mass = mass, inertia = inertia, comLocal = comLocal };
                for (int i = 0; i < forces.Count; i++) report.thrusters.Add(new ShipPhysics.Thruster
                { localPos = positions[i], dir = directions[i], thrust = forces[i], operational = forces[i] > 0f });
                return report;
            }
        }
        internal DriveSubject Identity => new DriveSubject(0, ownerId, shuttle);
        internal static DriveSnapshot Capture(DriveInfo info, DriveSubject subject) =>
            Capture(info, subject.key, subject.shuttle);
        internal static DriveSnapshot Capture(DriveInfo info, int ownerId, bool shuttle = false)
        {
            var result = new DriveSnapshot { mass = info.mass, inertia = info.inertia,
                comLocal = info.comLocal, frameAngle = info.frameAngle, ownerId = ownerId, shuttle = shuttle };
            foreach (var t in info.thrusters)
            { result.positions.Add(t.localPos); result.directions.Add(t.dir); result.forces.Add(t.operational ? t.thrust : 0f); }
            return result;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref mass, "mass"); Scribe_Values.Look(ref inertia, "inertia");
            Scribe_Values.Look(ref frameAngle, "frameAngle"); Scribe_Values.Look(ref comLocal, "comLocal");
            Scribe_Values.Look(ref ownerId, "ownerId");
            Scribe_Values.Look(ref shuttle, "shuttle");
            Scribe_Collections.Look(ref positions, "positions", LookMode.Value);
            Scribe_Collections.Look(ref directions, "directions", LookMode.Value);
            Scribe_Collections.Look(ref forces, "forces", LookMode.Value);
        }
    }
}

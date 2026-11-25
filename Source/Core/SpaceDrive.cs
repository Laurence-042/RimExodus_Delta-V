using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum DriveSubjectKind : byte
    {
        Foundation,
        Shuttle,
        Personnel
    }

    /// <summary>驾驶主体身份与容器身份分开；分离后由主体重新解析当前容器。</summary>
    public readonly struct DriveSubject : IEquatable<DriveSubject>
    {
        public readonly int objectId, key;
        public readonly DriveSubjectKind kind;
        public bool shuttle => kind == DriveSubjectKind.Shuttle;
        public bool personnel => kind == DriveSubjectKind.Personnel;
        public DriveSubject(int objectId, int key, bool shuttle = false)
            : this(objectId, key, shuttle ? DriveSubjectKind.Shuttle : DriveSubjectKind.Foundation) { }
        public DriveSubject(int objectId, int key, DriveSubjectKind kind)
        { this.objectId = objectId; this.key = key; this.kind = kind; }
        public DriveSubject InContainer(int id) => new DriveSubject(id, key, kind);
        public static DriveSubject Of(Ship ship) => Of(PilotTarget.Of(ship));
        public static DriveSubject Of(ShipBase ship) => Of(PilotTarget.Of(ship));
        public static DriveSubject Of(PilotTarget target) => !target.IsValid ? default
            : new DriveSubject(Current.Game?.GetComponent<UniverseManager>()?.FrameOf(target.Map)?.objectId ?? 0,
                target.Identity.key, target.Identity.kind);
        public SpaceObject Resolve(UniverseManager universe)
        {
            SpaceObject current = universe?.ObjectById(objectId);
            if (current?.TryGetDrive(this, out _) == true) return current;
            // 船分离会换容器；ownerId 只查船，再经真实 Map 查询容器。
            ShipBase ship = ShipRegistry.ShipByIdentity(this);
            SpaceObject frame = ship != null ? universe?.FrameOf(ship.Map) : null;
            return ship?.CanDriveIn(frame) == true ? frame : null;
        }
        public bool Equals(DriveSubject other) => key == other.key && kind == other.kind;
        public override bool Equals(object obj) => obj is DriveSubject other && Equals(other);
        public override int GetHashCode() => (key * 397) ^ (int)kind;
        public static bool operator ==(DriveSubject a, DriveSubject b) => a.Equals(b);
        public static bool operator !=(DriveSubject a, DriveSubject b) => !a.Equals(b);
        public override string ToString() => (kind == DriveSubjectKind.Shuttle ? "thing:"
            : kind == DriveSubjectKind.Personnel ? "pawn:" : "structure:") + key;
    }

    /// <summary>Map-plane input axes, including cameras whose projected axes are not perpendicular.</summary>
    public readonly struct DriveInputBasis
    {
        public readonly Vector2 Forward, Left;
        public DriveInputBasis(Vector2 forward, Vector2 left) { Forward = forward.normalized; Left = left.normalized; }
        public static DriveInputBasis ForAngle(float angle) => new DriveInputBasis(
            SpaceTransform.Rotate(Vector2.right, angle), SpaceTransform.Rotate(Vector2.up, angle));
        public Vector2 ToMap(Vector2 input) => Forward * input.x + Left * input.y;
        public Vector2 FromMap(Vector2 map)
        {
            float determinant = Forward.x * Left.y - Forward.y * Left.x;
            return new Vector2(map.x * Left.y - map.y * Left.x,
                Forward.x * map.y - Forward.y * map.x) / determinant;
        }
    }

    public enum DriveResult { Executed, WaitingForSeparation, InvalidSubject, AlreadyAdvanced }
    public struct DriveControl
    {
        public Vector2 force;
        public float torque, voidBlend;
        public bool playerInitiated, assistX, assistZ, assistW;
        public DriveInputBasis? inputBasis;
        internal bool hasInput;
        internal ShipPhysics.Command input;
        public static DriveControl FromInput(ShipPhysics.Command command, bool assistX, bool assistZ, bool assistW, DriveInputBasis? inputBasis = null)
            => new DriveControl { hasInput = true, input = command, playerInitiated = true, inputBasis = inputBasis,
                assistX = assistX, assistZ = assistZ, assistW = assistW };
    }

    /// <summary>只读物理视图；预览只写求解工作区，不写真实油门或表现时间。</summary>
    public sealed class DriveInfo
    {
        private readonly ShipPhysics.MassReport report;
        public readonly float frameAngle;
        public SpacePose pose { get; internal set; }
        private PreviewMemo previewMemo;
        // Exact-input memoization is limited to a synchronous read-only planning call.
        // It cannot survive a tick, a power change, or a new structural observation.
        internal IDisposable ReusePreviews() => new PreviewMemo(this);
        private sealed class PreviewMemo : IDisposable
        {
            private readonly DriveInfo owner;
            private readonly PreviewMemo previous;
            internal readonly Dictionary<Vector2, float> braking = new Dictionary<Vector2, float>();
            internal readonly Dictionary<int, float> angular = new Dictionary<int, float>();
            internal PreviewMemo(DriveInfo owner)
            { this.owner = owner; previous = owner.previewMemo; owner.previewMemo = this; }
            public void Dispose() { owner.previewMemo = previous; }
        }
        internal DriveInfo(ShipPhysics.MassReport report, float frameAngle = 0f)
        { this.report = report; this.frameAngle = frameAngle; }
        public bool AllowsAngularMotion => report.allowsAngularMotion;
        public float mass => report.mass;
        public float inertia => report.inertia;
        public Vector2 comLocal => report.comLocal;
        public int version => report.version;
        /// <summary>火焰、排气与工作音共用 15 游戏 tick 的油门新鲜期（0.25 游戏秒）。</summary>
        public bool HasFreshThrottles
        {
            get
            {
                int age = (Find.TickManager?.TicksGame ?? -1) - report.throttlesWrittenTick;
                return report.throttlesWrittenTick >= 0 && age >= 0 && age <= 15;
            }
        }
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
            if (previewMemo != null && previewMemo.braking.TryGetValue(direction, out float cached)) return cached;
            float result = ShipPhysics.PreviewBrakingAcceleration(report, direction);
            if (previewMemo != null && previewMemo.braking.Count < 2048) previewMemo.braking[direction] = result;
            return result;
        }
        public float PreviewAngular(int direction)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.DrivePreviewAngular);
            if (previewMemo != null && previewMemo.angular.TryGetValue(direction, out float cached)) return cached;
            float result = ShipPhysics.PreviewAngularAcceleration(report, direction);
            if (previewMemo != null) previewMemo.angular[direction] = result;
            return result;
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
        public DriveSubjectKind kind;
        public bool allowsAngularMotion = true;
        public List<Vector2> positions = new List<Vector2>(), directions = new List<Vector2>();
        public List<float> forces = new List<float>();
        private ShipPhysics.MassReport report;
        internal ShipPhysics.MassReport Report
        {
            get
            {
                if (report != null) return report;
                report = new ShipPhysics.MassReport { mass = mass, inertia = inertia, comLocal = comLocal, allowsAngularMotion = allowsAngularMotion };
                for (int i = 0; i < forces.Count; i++) report.thrusters.Add(new ShipPhysics.Thruster
                { localPos = positions[i], dir = directions[i], thrust = forces[i], operational = forces[i] > 0f });
                return report;
            }
        }
        internal DriveSubject Identity => new DriveSubject(0, ownerId,
            kind == DriveSubjectKind.Foundation && shuttle ? DriveSubjectKind.Shuttle : kind);
        internal static DriveSnapshot Capture(DriveInfo info, DriveSubject subject) =>
            Capture(info, subject.key, subject.kind);
        internal static DriveSnapshot Capture(DriveInfo info, int ownerId, bool shuttle = false)
            => Capture(info, ownerId, shuttle ? DriveSubjectKind.Shuttle : DriveSubjectKind.Foundation);
        internal static DriveSnapshot Capture(DriveInfo info, int ownerId, DriveSubjectKind kind)
        {
            var result = new DriveSnapshot { mass = info.mass, inertia = info.inertia, allowsAngularMotion = info.AllowsAngularMotion,
                comLocal = info.comLocal, frameAngle = info.frameAngle, ownerId = ownerId,
                shuttle = kind == DriveSubjectKind.Shuttle, kind = kind };
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
            Scribe_Values.Look(ref kind, "kind", DriveSubjectKind.Foundation);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && kind == DriveSubjectKind.Foundation && shuttle)
                kind = DriveSubjectKind.Shuttle;
            Scribe_Values.Look(ref allowsAngularMotion, "allowsAngularMotion", true);
            Scribe_Collections.Look(ref positions, "positions", LookMode.Value);
            Scribe_Collections.Look(ref directions, "directions", LookMode.Value);
            Scribe_Collections.Look(ref forces, "forces", LookMode.Value);
        }
    }
}

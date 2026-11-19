using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        private sealed class HullSource
        {
            internal DriveSubject subject;
            internal SpaceObject owner;
            internal HullSource next;
            internal Map map;
            internal int representation, version = -1;
            internal readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
        }
        private readonly Dictionary<DriveSubject, HullSource> hullSources = new Dictionary<DriveSubject, HullSource>();
        private readonly Dictionary<SpaceObject, HullSource> hullSourcesByOwner = new Dictionary<SpaceObject, HullSource>();
        private readonly List<HullSource> hullSourceBuffer = new List<HullSource>();
        private readonly Dictionary<SpaceObject, (SpaceCollision.TargetView view, SpacePose pose)> hullStarts =
            new Dictionary<SpaceObject, (SpaceCollision.TargetView, SpacePose)>();
        private readonly List<SpaceCollision.HullMotion> hullMotions = new List<SpaceCollision.HullMotion>();
        private readonly List<SpaceCollision.HullContact> pendingHullContacts = new List<SpaceCollision.HullContact>();
        private readonly Stack<List<(IntVec3 a, IntVec3 b)>> freeHullLayers = new Stack<List<(IntVec3, IntVec3)>>();
        private List<(IntVec3 a, IntVec3 b)> activeHullLayer;
        private readonly HashSet<(int, int)> crushedHullPairs = new HashSet<(int, int)>();
        private readonly SpaceCollision.SweptBody contactBody = new SpaceCollision.SweptBody();
        private readonly SpaceCollision.HullImpactBatch hullImpact = new SpaceCollision.HullImpactBatch();
        private Func<SpaceObject, IntVec3, bool> hullEnabled;
        private bool hullSourcesDirty;
        internal long HullRequests { get; private set; }

        internal void RefreshHullSource(DriveSubject subject)
        {
            hullSourcesDirty = true;
            ShipBase ship = ShipRegistry.ShipByIdentity(subject);
            if (ship?.IsValid != true || universe.Controls.CollisionAvoidanceEnabled(ship))
            {
                hullSources.Remove(subject);
                if (hullSources.Count == 0) hullSourcesByOwner.Clear();
                return;
            }
            if (!hullSources.ContainsKey(subject)) hullSources.Add(subject, new HullSource { subject = subject });
        }

        internal void NotifyHullCellsChanged(Map map, CellRect? range)
        {
            if (hullSources.Count != 0) hullSourcesDirty = true;
        }

        private void RefreshHullSources()
        {
            if (!hullSourcesDirty) return;
            hullSourcesDirty = false;
            hullSourcesByOwner.Clear();
            hullSourceBuffer.Clear();
            hullSourceBuffer.AddRange(hullSources.Values);
            foreach (var source in hullSourceBuffer)
            {
                source.next = null;
                ShipBase ship = ShipRegistry.ShipByIdentity(source.subject);
                if (ship?.IsValid != true || universe.Controls.CollisionAvoidanceEnabled(ship))
                {
                    hullSources.Remove(source.subject);
                    continue;
                }
                var owner = universe.FrameOf(ship.Map);
                if (owner == null || owner.IsArchived || owner.projectileRoleOverride
                    || !FreeFlightBootstrap.IsZeroGravity(universe, ship.Map))
                {
                    source.owner = null;
                    source.map = null;
                    source.cells.Clear();
                    source.version = -1;
                    continue;
                }
                int version = SubstructureOwnership.StructureVersion(ship.Map);
                if (source.owner != owner || source.map != ship.Map || source.representation != owner.RepresentationVersion
                    || source.version != version)
                {
                    source.owner = owner;
                    source.map = ship.Map;
                    source.representation = owner.RepresentationVersion;
                    source.cells.Clear();
                    source.cells.UnionWith(ship.HullCells);
                    source.version = version;
                }
                if (source.cells.Count == 0) continue;
                hullSourcesByOwner.TryGetValue(owner, out source.next);
                hullSourcesByOwner[owner] = source;
            }
            hullSourceBuffer.Clear();
        }

        private void PrepareHullContacts()
        {
            using var timing = universe.Performance.Measure(PerformanceStage.HullPrepare);
            hullStarts.Clear();
            if (hullSources.Count == 0 && projectiles.Count == 0) return;
            RefreshHullSources();
            foreach (var frame in universe.objects)
            {
                var view = SpaceCollision.TargetView.Of(frame);
                if (!view.HasRigidStructure) continue;
                hullStarts.Add(frame, (view, frame.pose));
            }
        }

        private bool HullEnabledAt(SpaceObject owner, IntVec3 cell) => HullSourceAt(owner, cell) != null;

        private HullSource HullSourceAt(SpaceObject owner, IntVec3 cell)
        {
            hullSourcesByOwner.TryGetValue(owner, out HullSource source);
            while (source != null && !source.cells.Contains(cell)) source = source.next;
            return source;
        }

        private SpacePose? MapContactPoseOf(SpaceObject owner, float fraction)
        {
            if (!hullStarts.TryGetValue(owner, out var start) || !start.view.Valid) return null;
            return SpaceTransform.LerpPose(start.pose, owner.pose, fraction);
        }

        private bool HullMotionCurrent(SpaceCollision.HullMotion motion) =>
            motion.live && hullStarts.ContainsKey(motion.body);

        private void ResolveHullContacts()
        {
            if (hullSources.Count == 0 || hullStarts.Count < 2) return;
            RefreshHullSources();
            if (hullSourcesByOwner.Count == 0) return;
            bool moved = false;
            foreach (var entry in hullStarts)
            {
                var frame = entry.Key;
                var (view, start) = entry.Value;
                if (!view.CanHullContact || !FreeFlightBootstrap.IsZeroGravity(universe, view.map)) continue;
                SpacePose end = frame.pose;
                moved |= SpaceCollision.ChangedHullPose(start, end);
                start.velocity = SpaceTransform.TransportVelocity(end, start.position);
                start.angularVelocity = end.angularVelocity;
                hullMotions.Add(new SpaceCollision.HullMotion(view, start, end));
            }
            if (!moved || hullMotions.Count < 2) return;
            hullMotions.Sort((a, b) => a.body.objectId.CompareTo(b.body.objectId));
            for (int a = 0; a < hullMotions.Count - 1; a++)
            for (int b = a + 1; b < hullMotions.Count; b++)
                QueueHullPair(a, b, 0f);

            int impulseBudget = 16 + hullMotions.Count * 4;
            while (pendingHullContacts.Count > 0)
            {
                ReleaseHullLayer(activeHullLayer);
                activeHullLayer = null;
                int first = 0;
                for (int i = 1; i < pendingHullContacts.Count; i++)
                    if (Earlier(pendingHullContacts[i], pendingHullContacts[first])) first = i;
                var hit = pendingHullContacts[first];
                if (impulseBudget == 0)
                {
                    StopHullMotion(hit.time);
                    break;
                }

                pendingHullContacts.RemoveAt(first);
                activeHullLayer = hit.layer;
                var a = hullMotions[hit.a];
                var b = hullMotions[hit.b];
                if (!HullMotionCurrent(a) || !HullMotionCurrent(b)
                    || !a.Supports(hit.cellA) || !b.Supports(hit.cellB)) continue;
                RefreshHullSources();
                var source = hit.reverse ? b : a;
                var target = hit.reverse ? a : b;
                IntVec3 sourceCell = hit.reverse ? hit.cellB : hit.cellA;
                IntVec3 targetCell = hit.reverse ? hit.cellA : hit.cellB;
                var subject = HullSourceAt(source.body, sourceCell);
                bool reverse = subject == null;
                subject ??= HullSourceAt(target.body, targetCell);
                if (subject == null) continue;
                Thing launcher = ShipRegistry.ShipByIdentity(subject.subject)?.Anchor;

                a.body.pose = a.At(hit.time);
                b.body.pose = b.At(hit.time);
                if (reverse)
                {
                    var from = source.view.AtPose(source.body.pose);
                    var to = target.view.AtPose(target.body.pose);
                    Vector3 point = to.ToUniverse(hit.localPoint);
                    Vector2 normal = to.DirectionToUniverse(hit.localPoint, hit.localNormal);
                    hit.localPoint = from.FromUniverse(point);
                    hit.localNormal = -from.DirectionFromUniverse(point, normal);
                    (source, target) = (target, source);
                    (sourceCell, targetCell) = (targetCell, sourceCell);
                }
                hullImpact.Prepare(source, target, !crushedHullPairs.Contains((hit.a, hit.b)),
                    hit.layer, hit.reverse ^ reverse);
                using (universe.Performance.Measure(PerformanceStage.HullOutcome))
                {
                    HullRequests++;
                    universe.Performance.RecordHullRequest();
                    SpaceCollision.ResolveHullContact(hullImpact, sourceCell, targetCell, hit,
                        DeltaVDefOf.DeltaV_Projectile_HullContact, contactBody, launcher, QueueVisualContact);
                }
                bool closed = hullImpact.closing > 0f;
                if (hullImpact.crushed)
                {
                    crushedHullPairs.Add((hit.a, hit.b));
                    universe.Performance.RecordHullCrushBatch();
                }
                if (hullImpact.continues) a.deferDrift = b.deferDrift = true;
                hullImpact.Clear();
                if (!closed)
                {
                    if (HullMotionCurrent(a)) a.body.pose = a.to;
                    if (HullMotionCurrent(b)) b.body.pose = b.to;
                    continue;
                }

                impulseBudget--;
                universe.Performance.RecordHullImpulse();
                ResumeHullMotion(ref a, hit.time);
                ResumeHullMotion(ref b, hit.time);
                hullMotions[hit.a] = a;
                hullMotions[hit.b] = b;
                for (int i = pendingHullContacts.Count - 1; i >= 0; i--)
                {
                    var candidate = pendingHullContacts[i];
                    if (candidate.a == hit.a || candidate.b == hit.a || candidate.a == hit.b || candidate.b == hit.b)
                    {
                        ReleaseHullLayer(candidate.layer);
                        pendingHullContacts.RemoveAt(i);
                    }
                }
                RefreshHullSources();
                for (int i = 0; i < hullMotions.Count; i++)
                {
                    if (i == hit.a || i == hit.b) continue;
                    QueueHullPair(hit.a, i, hit.time);
                    QueueHullPair(hit.b, i, hit.time);
                }
            }
        }

        private void QueueHullPair(int a, int b, float time)
        {
            if (a > b) (a, b) = (b, a);
            var own = hullMotions[a];
            var other = hullMotions[b];
            if (!HullMotionCurrent(own) || !HullMotionCurrent(other)
                || !hullSourcesByOwner.ContainsKey(own.body) && !hullSourcesByOwner.ContainsKey(other.body)) return;
            if (!SpaceCollision.ChangedHullPose(own.At(time), own.to)
                && !SpaceCollision.ChangedHullPose(other.At(time), other.to)) return;
            if (own.surface == null) { own.RefreshSurface(); hullMotions[a] = own; }
            if (other.surface == null) { other.RefreshSurface(); hullMotions[b] = other; }
            using var timing = universe.Performance.Measure(PerformanceStage.HullBroad);
            universe.Performance.RecordHullPair();
            var layer = freeHullLayers.Count == 0 ? new List<(IntVec3, IntVec3)>() : freeHullLayers.Pop();
            if (!SpaceCollision.TryHullPair(own, other, time, hullEnabled ??= HullEnabledAt, out var hit, layer))
            {
                ReleaseHullLayer(layer);
                return;
            }
            hit.a = a;
            hit.b = b;
            pendingHullContacts.Add(hit);
        }

        private static bool Earlier(SpaceCollision.HullContact a, SpaceCollision.HullContact b) =>
            a.time < b.time || a.time == b.time && (a.a < b.a || a.a == b.a && a.b < b.b);

        private void ResumeHullMotion(ref SpaceCollision.HullMotion motion, float time)
        {
            motion.from = motion.body.pose;
            motion.start = time;
            motion.to = motion.from;
            if (!HullMotionCurrent(motion)) return;
            // A breached pair can take other impulses this tick while its remaining travel is deferred.
            if (!motion.deferDrift && ShipPhysics.GetReport(motion.body) is { } report)
                motion.to = ShipPhysics.DriftAfterContact(motion.from, report, (1f - time) * Dt);
            motion.body.pose = motion.to;
            motion.RefreshSurface();
        }

        private void StopHullMotion(float time)
        {
            universe.Performance.RecordHullBudgetStop(time);
            foreach (var hit in pendingHullContacts)
            {
                var a = hullMotions[hit.a];
                var b = hullMotions[hit.b];
                if (HullMotionCurrent(a)) a.body.pose = a.At(time);
                if (HullMotionCurrent(b)) b.body.pose = b.At(time);
            }
        }

        private void ForgetHullFrame(SpaceObject frame, Map map = null, bool removeSource = true)
        {
            frame ??= map != null ? universe.FrameOf(map) : null;
            if (frame != null)
            {
                hullStarts.Remove(frame);
                hullSourcesByOwner.Remove(frame);
            }
            hullSourcesDirty = true;
            hullSourceBuffer.Clear();
            hullSourceBuffer.AddRange(hullSources.Values);
            foreach (var source in hullSourceBuffer)
            {
                if ((frame == null || source.owner != frame) && (map == null || source.map != map)) continue;
                if (source.owner != null) hullSourcesByOwner.Remove(source.owner);
                if (frame != null && removeSource) hullSources.Remove(source.subject);
                source.owner = null;
                source.next = null;
                source.map = null;
                source.cells.Clear();
                source.version = -1;
            }
            hullSourceBuffer.Clear();
        }

        private void ReleaseHullLayer(List<(IntVec3 a, IntVec3 b)> layer)
        {
            if (layer == null) return;
            layer.Clear();
            freeHullLayers.Push(layer);
        }

        internal void FinishHullContacts()
        {
            ReleaseHullLayer(activeHullLayer);
            activeHullLayer = null;
            foreach (var hit in pendingHullContacts) ReleaseHullLayer(hit.layer);
            pendingHullContacts.Clear();
            hullMotions.Clear();
            hullStarts.Clear();
            crushedHullPairs.Clear();
            hullImpact.Clear();
            contactBody.attacker = contactBody.hullSource = contactBody.resolvedHitDefender = null;
            contactBody.hullSourceMap = null;
            contactBody.onVisualContact = null;
            contactBody.launcher = null;
        }

        internal bool HullRouteClear(ShipBase ship, CombatTrajectoryIntent intent, float elapsed,
            out DriveSubject blocker, out int blockedSecond)
        {
            blocker = default; blockedSecond = 0;
            if (!universe.Controls.CollisionAvoidanceEnabled(ship)) return true;
            var target = universe.FrameOf(ship.Map);
            if (target == null) return true;
            using var timing = universe.Performance.Measure(PerformanceStage.CombatRoute);
            RefreshHullSources();
            var own = SpaceCollision.TargetView.Of(target);
            foreach (var source in hullSources.Values)
            {
                var other = source.owner;
                if (other == null || other == target || other.projectileRoleOverride) continue;
                var view = SpaceCollision.TargetView.Of(other);
                float reach = 4f * (other.pose.velocity.magnitude + target.pose.velocity.magnitude)
                    + own.OriginBoundsRadius + view.OriginBoundsRadius + 10f;
                if (OrbitSphere.ArcCells(other.pose.position, target.pose.position) > reach) continue;
                intent.Sample(elapsed, out var position, out _, out float angle, out _);
                var previous = new SpacePose(position, angle);
                var from = other.pose;
                for (int i = 1; i <= 40; i++)
                {
                    float seconds = i * .1f;
                    intent.Sample(elapsed + seconds, out position, out _, out angle, out _);
                    var next = new SpacePose(position, angle);
                    var to = other.pose;
                    to.position = OrbitSphere.Move(to.position, to.velocity * seconds);
                    to.angle += to.angularVelocity * seconds;
                    var approaching = new SpaceCollision.HullMotion(view, from, to);
                    var candidate = new SpaceCollision.HullMotion(own, previous, next);
                    approaching.RefreshSurface();
                    candidate.RefreshSurface();
                    if (SpaceCollision.TryHullPair(approaching, candidate, 0f, hullEnabled ??= HullEnabledAt, out _))
                    { blocker = source.subject; blockedSecond = Mathf.CeilToInt(seconds); return false; }
                    previous = next; from = to;
                }
            }
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        private const float EdgeInset = .0002f;
        private const float SameTime = .0001f;

        private static bool HullContactCell(Map map, Surface surface, IntVec3 cell, out Thing building)
        {
            building = null;
            if (!cell.InBounds(map)) return false;
            int index = map.cellIndices.CellToIndex(cell);
            if (surface.buildings[index] is Building occupant && occupant.Spawned && !occupant.Destroyed
                && occupant.def.useHitPoints && occupant.def.destroyable)
            {
                building = occupant;
                return true;
            }
            return surface.foundations[index];
        }

        internal struct HullMotion
        {
            internal TargetView view;
            internal SpaceObject body => view.owner;
            internal Map map => view.map;
            internal SpacePose from, to;
            internal float start, radius;
            internal bool deferDrift;
            internal Surface surface;
            internal bool live => view.CanHullContact;

            internal HullMotion(TargetView view, SpacePose from, SpacePose to)
            {
                this = default;
                this.view = view;
                this.from = from;
                this.to = to;
            }

            internal SpacePose At(float time)
            {
                if (time >= 1f) return to;
                if (time <= start) return from;
                return SpaceTransform.LerpPose(to, from, (1f - time) / (1f - start));
            }

            internal void RefreshSurface()
            {
                if (!live) return;
                surface = SurfaceFor(body);
                radius = surface.hullRadius + view.ToLocal(surface.boundsCenter).magnitude;
            }

            internal bool Supports(IntVec3 cell) => live && HullContactCell(map, surface, cell, out _);
        }

        internal struct HullContact
        {
            internal int a, b, samples;
            internal float time;
            internal IntVec3 cellA, cellB;
            internal Vector2 localPoint, localNormal;
            internal bool reverse;
            internal List<(IntVec3 a, IntVec3 b)> layer;
        }

        internal static bool ChangedHullPose(SpacePose from, SpacePose to) =>
            from.angle != to.angle || !from.direction.Equals(to.direction);

        internal static bool TryHullPair(HullMotion a, HullMotion b, float time,
            Func<SpaceObject, IntVec3, bool> enabled, out HullContact hit, List<(IntVec3 a, IntVec3 b)> layer = null)
        {
            hit = new HullContact { layer = layer };
            if (!a.live || !b.live || a.map == b.map) return false;
            SpacePose aFrom = a.At(time), bFrom = b.At(time);
            if (!ChangedHullPose(aFrom, a.to) && !ChangedHullPose(bFrom, b.to)) return false;
            if (!a.surface.anySolid || !b.surface.anySolid) return false;
            float reach = a.radius + b.radius
                + OrbitSphere.Offset(aFrom.direction, a.to.direction).magnitude
                + OrbitSphere.Offset(bFrom.direction, b.to.direction).magnitude;
            if (OrbitSphere.Offset(aFrom.direction, bFrom.direction).sqrMagnitude > reach * reach) return false;

            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.HullNarrow);
            hit.time = float.PositiveInfinity;
            float turn = Mathf.Abs(SpaceTransform.WrapAngle(a.to.angle - aFrom.angle))
                + Mathf.Abs(SpaceTransform.WrapAngle(b.to.angle - bFrom.angle));
            int segments = Mathf.Max(1, Mathf.CeilToInt(turn / (2f * Mathf.Deg2Rad)));
            for (int segment = 0; segment < segments; segment++)
            {
                float start = Mathf.Lerp(time, 1f, (float)segment / segments);
                float end = Mathf.Lerp(time, 1f, (float)(segment + 1) / segments);
                Sweep(a, b, start, end, false, enabled, ref hit);
                Sweep(b, a, start, end, true, enabled, ref hit);
                if (hit.samples > 0) return true;
            }
            return false;
        }

        private static void Sweep(HullMotion a, HullMotion b, float start, float end, bool reverse,
            Func<SpaceObject, IntVec3, bool> enabled, ref HullContact best)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.HullSweep);
            SpacePose a0 = a.At(start), a1 = a.At(end), b0 = b.At(start), b1 = b.At(end);
            Vector2 aOrigin = SpaceTransform.ShipLocalOrigin(a.map);
            Vector2 bOrigin = SpaceTransform.ShipLocalOrigin(b.map);
            Vector2 begin = aOrigin + SpaceTransform.UniverseToShipLocal(a0, b0.direction);
            Vector2 finish = aOrigin + SpaceTransform.UniverseToShipLocal(a1, b1.direction);
            float margin = b.radius + 2f;
            var window = CellRect.FromLimits(Mathf.FloorToInt(Mathf.Min(begin.x, finish.x) - margin),
                Mathf.FloorToInt(Mathf.Min(begin.y, finish.y) - margin),
                Mathf.CeilToInt(Mathf.Max(begin.x, finish.x) + margin),
                Mathf.CeilToInt(Mathf.Max(begin.y, finish.y) + margin));
            foreach (IntVec3 cell in a.surface.OccupiedCells(window))
            {
                if (!HullContactCell(a.map, a.surface, cell, out _)) continue;
                for (int edge = 0; edge < 4; edge++)
                {
                    IntVec3 adjacent = edge switch
                    {
                        0 => IntVec3.East, 1 => IntVec3.West, 2 => IntVec3.North, _ => IntVec3.South
                    };
                    if (HullContactCell(a.map, a.surface, cell + adjacent, out _)) continue;
                    var outward = new Vector2(adjacent.x, adjacent.z);
                    var side = new Vector2(-outward.y, outward.x);
                    Vector2 edgeCenter = new Vector2(cell.x + .5f, cell.z + .5f) - aOrigin
                        + outward * (.5f - EdgeInset);
                    for (int sample = -1; sample <= 1; sample++)
                    {
                        Vector2 local = edgeCenter + side * (sample * (.5f - EdgeInset));
                        Vector2 from = bOrigin + InOtherMap(a0, b0, local);
                        Vector2 delta = bOrigin + InOtherMap(a1, b1, local) - from;
                        float length = delta.magnitude;
                        if (length < .00001f) continue;
                        Vector2 direction = delta / length;
                        // A third body's impulse can drive an already touching
                        // pair together again within the same tick.
                        float lead = EdgeInset * 2f;
                        if (!TryHullGridSegment(b.map, b.surface, from - direction * lead,
                            direction, length + lead, out RayContact ray)) continue;
                        if (!enabled(a.body, cell) && !enabled(b.body, ray.cell)) continue;
                        float fraction = Vector2.Dot(ray.point - from, direction) / length;
                        float time = Mathf.Lerp(start, end, Mathf.Clamp01(fraction));
                        Vector2 point = ray.point - bOrigin;
                        float timeDelta = Mathf.Abs(time - best.time);
                        bool simultaneous = timeDelta <= SameTime && timeDelta * length <= .003f * (end - start);
                        var cells = reverse ? (ray.cell, cell) : (cell, ray.cell);
                        if (time < best.time && !simultaneous)
                        {
                            best.layer?.Clear();
                            best.layer?.Add(cells);
                            best.time = time;
                            best.cellA = reverse ? ray.cell : cell;
                            best.cellB = reverse ? cell : ray.cell;
                            best.localPoint = point;
                            best.localNormal = ray.normal;
                            best.reverse = reverse;
                            best.samples = 1;
                        }
                        else if (simultaneous && reverse == best.reverse && ray.normal == best.localNormal
                            && Mathf.Abs(Vector2.Dot(point - best.localPoint, ray.normal)) < EdgeInset)
                        {
                            best.layer?.Add(cells);
                            best.localPoint += (point - best.localPoint) / ++best.samples;
                        }
                    }
                }
            }
        }

        private static Vector2 InOtherMap(SpacePose from, SpacePose target, Vector2 local) =>
            SpaceTransform.UniverseToShipLocal(target,
                OrbitSphere.Move(from.direction, SpaceTransform.Rotate(local, from.angle)));
    }
}

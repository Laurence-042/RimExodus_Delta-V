using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        private static bool HullContactCell(Map map, Surface surface, IntVec3 cell, out Thing building)
        {
            building = null;
            if (!cell.InBounds(map)) return false;
            int index = map.cellIndices.CellToIndex(cell);
            if (surface.kinds[index] == (byte)SpaceCellKind.Substructure) return true;
            if (surface.buildings[index] is not Building occupant || !occupant.Spawned || occupant.Destroyed
                || !occupant.def.useHitPoints || !occupant.def.destroyable
                || occupant.TryGetComp<CompSpaceShuttle>() != null) return false;
            building = occupant;
            return true;
        }

        /// <summary>Transient trajectories for the current tick. The maps still own all structural state.</summary>
        internal sealed class HullContacts
        {
            private const float EdgeInset = .0002f;
            private const float SameTime = .0001f;

            private struct Motion
            {
                internal SpaceObject body;
                internal Map map;
                internal int representation;
                internal SpacePose from, to;
                internal float start, radius;
                internal Surface surface;

                internal bool Live => body.RepresentationVersion == representation
                    && body.map == map && !map.Disposed && body.CanRelocateMap;
                internal SpacePose At(float time)
                {
                    if (time >= 1f) return to;
                    if (time <= start) return from;
                    return SpaceTransform.LerpPose(to, from, (1f - time) / (1f - start));
                }

                internal void RefreshSurface()
                {
                    surface = SurfaceFor(body);
                    radius = surface.hullRadius + SpaceTransform.MapToShipLocal(map, surface.boundsCenter).magnitude;
                }
            }

            private struct Contact
            {
                internal int a, b, samples;
                internal float time;
                internal IntVec3 cellA, cellB;
                internal Vector2 localPoint, localNormal;
                internal bool reverse;
            }

            private struct CrushRay
            {
                private GridRay ray;
                private readonly float limit;
                private float lastOccupied;
                private bool foundStructure;

                internal CrushRay(Vector2 at, Vector2 inward, Map map)
                {
                    ray = new GridRay(at - inward, inward, 0f);
                    limit = map.Size.x + map.Size.z + 2f;
                    lastOccupied = 0f;
                    foundStructure = false;
                }

                internal bool Read(Motion motion, out IntVec3 cell, out Thing building, out float depth)
                {
                    while (ray.Along <= limit && (!foundStructure || ray.Along - lastOccupied <= 2f))
                    {
                        cell = ray.Cell;
                        if (HullContactCell(motion.map, motion.surface, cell, out building) && building != null
                            || LiveFoundation(motion, cell))
                        {
                            foundStructure = true;
                            lastOccupied = ray.Along;
                            depth = Mathf.Max(0f, ray.Along - 1f);
                            return true;
                        }
                        ray.Advance();
                    }
                    cell = default;
                    building = null;
                    depth = 0f;
                    return false;
                }

                private static bool LiveFoundation(Motion motion, IntVec3 cell)
                {
                    if (!cell.InBounds(motion.map)) return false;
                    int index = motion.map.cellIndices.CellToIndex(cell);
                    // The tick's collision surface still points to the old building.
                    // Recheck its underlying foundation without rebuilding blocks inside the crush.
                    return motion.surface.buildings[index] is not Thing building || building.Destroyed
                        ? motion.surface.foundations[index] && SubstructureOwnership.HasFoundation(motion.map, cell)
                        : false;
                }
            }

            private struct CrushLane
            {
                internal CrushRay a, b;
                internal bool ended;
            }

            private readonly List<Motion> motions = new List<Motion>();
            private readonly List<Contact> pending = new List<Contact>();
            private readonly HashSet<(int, int)> damaged = new HashSet<(int, int)>();
            private readonly List<CrushLane> crushLanes = new List<CrushLane>();

            internal void Capture(UniverseManager universe)
            {
                motions.Clear();
                foreach (SpaceObject body in universe.objects)
                {
                    Map map = body.map;
                    if (!body.CanRelocateMap || map.Disposed
                        || !FreeFlightBootstrap.IsZeroGravity(universe, map)) continue;
                    motions.Add(new Motion { body = body, map = map, representation = body.RepresentationVersion,
                        from = body.pose });
                }
            }

            internal void Resolve(float dt)
            {
                try { ResolveCurrentTick(dt); }
                finally { motions.Clear(); pending.Clear(); damaged.Clear(); }
            }

            private void ResolveCurrentTick(float dt)
            {
                bool moved = false;
                for (int i = motions.Count - 1; i >= 0; i--)
                {
                    Motion motion = motions[i];
                    if (!motion.Live) { motions.RemoveAt(i); continue; }
                    motion.to = motion.body.pose;
                    moved |= ChangedPose(motion.from, motion.to);
                    motion.from.velocity = SpaceTransform.TransportVelocity(motion.to, motion.from.position);
                    motion.from.angularVelocity = motion.to.angularVelocity;
                    motions[i] = motion;
                }
                if (!moved || motions.Count < 2) return;
                motions.Sort((a, b) => a.body.objectId.CompareTo(b.body.objectId));
                for (int a = 0; a < motions.Count - 1; a++)
                for (int b = a + 1; b < motions.Count; b++)
                    Queue(a, b, 0f);

                int impulseBudget = 16 + motions.Count * 4;
                while (pending.Count > 0)
                {
                    int first = 0;
                    for (int i = 1; i < pending.Count; i++)
                        if (Earlier(pending[i], pending[first])) first = i;
                    Contact hit = pending[first];
                    if (impulseBudget == 0)
                    {
                        // Stop the unresolved motion at the first remaining contact.
                        // Keep the velocities already settled for the next tick.
                        foreach (Contact remaining in pending)
                        {
                            Motion left = motions[remaining.a], right = motions[remaining.b];
                            if (left.Live) left.body.pose = left.At(hit.time);
                            if (right.Live) right.body.pose = right.At(hit.time);
                        }
                        break;
                    }
                    pending.RemoveAt(first);
                    Motion a = motions[hit.a], b = motions[hit.b];
                    if (!a.Live || !b.Live
                        || !HullContactCell(a.map, a.surface, hit.cellA, out _)
                        || !HullContactCell(b.map, b.surface, hit.cellB, out _)) continue;

                    a.body.pose = a.At(hit.time);
                    b.body.pose = b.At(hit.time);
                    Motion defender = hit.reverse ? a : b;
                    Vector3 point = SpaceTransform.ShipLocalToUniverse(defender.body.pose, hit.localPoint);
                    Vector2 normal = SpaceTransform.TransportTangent(
                        SpaceTransform.Rotate(hit.localNormal, defender.body.pose.angle), defender.body.pose.position, point);
                    if (hit.reverse) normal = -normal;
                    float closing = ShipPhysics.ApplyHullContactImpulse(a.body, ShipPhysics.GetReport(a.body),
                        b.body, ShipPhysics.GetReport(b.body), point, normal, out float normalEnergy);
                    if (closing <= 0f)
                    {
                        a.body.pose = a.to;
                        b.body.pose = b.to;
                        continue;
                    }

                    impulseBudget--;
                    int crushBudget = SpaceImpactEffects.HullCrushBudget(closing, normalEnergy);
                    if (crushBudget > 0 && damaged.Add((hit.a, hit.b)))
                        Crush(a, b, hit, point, normal, crushBudget);
                    Resume(ref a, hit.time, dt);
                    Resume(ref b, hit.time, dt);
                    motions[hit.a] = a;
                    motions[hit.b] = b;
                    pending.RemoveAll(candidate => candidate.a == hit.a || candidate.b == hit.a
                        || candidate.a == hit.b || candidate.b == hit.b);
                    for (int i = 0; i < motions.Count; i++)
                    {
                        if (i == hit.a || i == hit.b) continue;
                        Queue(hit.a, i, hit.time);
                        Queue(hit.b, i, hit.time);
                    }
                }
            }

            private void Crush(Motion a, Motion b, Contact hit, Vector3 point, Vector2 normal, int budget)
            {
                Vector2 atA = SpaceTransform.UniverseToMapPoint(a.body, a.map, point);
                Vector2 atB = SpaceTransform.UniverseToMapPoint(b.body, b.map, point);
                Vector2 tangent = new Vector2(-normal.y, normal.x);
                Vector2 InMap(Motion motion, Vector2 direction) => SpaceTransform.RotateInverse(
                    SpaceTransform.TransportTangent(direction, point, motion.body.pose.position), motion.body.pose.angle);
                Vector2 sideA = InMap(a, tangent), sideB = InMap(b, tangent);
                Vector2 inwardA = InMap(a, normal), inwardB = InMap(b, -normal);
                Vector2 CenterOf(IntVec3 cell) => new Vector2(cell.x + .5f, cell.z + .5f);
                float alignment = .5f * (Vector2.Dot(CenterOf(hit.cellA) - atA, sideA)
                    + Vector2.Dot(CenterOf(hit.cellB) - atB, sideB));
                float midA = Vector2.Dot(a.surface.boundsCenter - atA, sideA);
                float midB = Vector2.Dot(b.surface.boundsCenter - atB, sideB);
                int min = Mathf.CeilToInt(Mathf.Max(midA - a.surface.hullRadius, midB - b.surface.hullRadius) - alignment);
                int max = Mathf.FloorToInt(Mathf.Min(midA + a.surface.hullRadius, midB + b.surface.hullRadius) - alignment);
                crushLanes.Clear();
                for (int i = min; i <= max; i++)
                {
                    float across = i + alignment;
                    crushLanes.Add(new CrushLane
                    {
                        a = new CrushRay(atA + across * sideA, inwardA, a.map),
                        b = new CrushRay(atB + across * sideB, inwardB, b.map)
                    });
                }

                var damage = new SpaceImpactEffects.HullCrushDamage();
                while (budget > 0)
                {
                    int first = -1;
                    float firstDepth = float.PositiveInfinity;
                    IntVec3 cellA = default, cellB = default;
                    Thing buildingA = null, buildingB = null;
                    for (int i = 0; i < crushLanes.Count; i++)
                    {
                        CrushLane lane = crushLanes[i];
                        if (lane.ended) continue;
                        if (!lane.a.Read(a, out var left, out Thing leftBuilding, out float leftDepth)
                            || !lane.b.Read(b, out var right, out Thing rightBuilding, out float rightDepth))
                            lane.ended = true;
                        else if (leftDepth + rightDepth < firstDepth)
                        {
                            first = i;
                            firstDepth = leftDepth + rightDepth;
                            cellA = left;
                            cellB = right;
                            buildingA = leftBuilding;
                            buildingB = rightBuilding;
                        }
                        crushLanes[i] = lane;
                    }
                    if (first == -1) break;
                    if (!damage.Exchange(ref budget, a.map, cellA, buildingA, b.map, cellB, buildingB))
                    {
                        CrushLane lane = crushLanes[first];
                        lane.ended = true;
                        crushLanes[first] = lane;
                    }
                }
                damage.Finish();
                crushLanes.Clear();
            }

            private static void Resume(ref Motion motion, float time, float dt)
            {
                motion.from = motion.body.pose;
                motion.start = time;
                motion.to = motion.from;
                if (!motion.Live) return;
                motion.to = ShipPhysics.DriftAfterContact(motion.from, ShipPhysics.GetReport(motion.body), (1f - time) * dt);
                motion.body.pose = motion.to;
                motion.RefreshSurface();
            }

            private void Queue(int a, int b, float time)
            {
                if (a > b) (a, b) = (b, a);
                Motion own = motions[a], other = motions[b];
                if (!own.Live || !other.Live || own.map == other.map) return;
                SpacePose aFrom = own.At(time), bFrom = other.At(time);
                if (!ChangedPose(aFrom, own.to) && !ChangedPose(bFrom, other.to)) return;
                if (own.surface == null) { own.RefreshSurface(); motions[a] = own; }
                if (other.surface == null) { other.RefreshSurface(); motions[b] = other; }
                if (!own.surface.anySolid || !other.surface.anySolid) return;
                float reach = own.radius + other.radius
                    + OrbitSphere.Offset(aFrom.direction, own.to.direction).magnitude
                    + OrbitSphere.Offset(bFrom.direction, other.to.direction).magnitude;
                if (OrbitSphere.Offset(aFrom.direction, bFrom.direction).sqrMagnitude > reach * reach) return;

                var best = new Contact { a = a, b = b, time = float.PositiveInfinity };
                float turn = Mathf.Abs(SpaceTransform.WrapAngle(own.to.angle - aFrom.angle))
                    + Mathf.Abs(SpaceTransform.WrapAngle(other.to.angle - bFrom.angle));
                int segments = Mathf.Max(1, Mathf.CeilToInt(turn / (2f * Mathf.Deg2Rad)));
                for (int segment = 0; segment < segments; segment++)
                {
                    float start = Mathf.Lerp(time, 1f, (float)segment / segments);
                    float end = Mathf.Lerp(time, 1f, (float)(segment + 1) / segments);
                    Sweep(own, other, start, end, false, ref best);
                    Sweep(other, own, start, end, true, ref best);
                    if (best.samples > 0) { pending.Add(best); break; }
                }
            }

            private static bool ChangedPose(SpacePose from, SpacePose to) =>
                from.angle != to.angle || from.direction.x != to.direction.x
                || from.direction.y != to.direction.y || from.direction.z != to.direction.z;

            private static bool Earlier(Contact a, Contact b) => a.time < b.time
                || a.time == b.time && (a.a < b.a || a.a == b.a && a.b < b.b);

            private static void Sweep(Motion a, Motion b, float start, float end, bool reverse, ref Contact best)
            {
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
                            if (!TryFindGridContact(b.map, b.surface, from - direction * lead,
                                direction, 0f, length + lead, default, out RayContact ray, hullContact: true)) continue;
                            float fraction = Vector2.Dot(ray.point - from, direction) / length;
                            float time = Mathf.Lerp(start, end, fraction);
                            Vector2 point = ray.point - bOrigin;
                            if (time < best.time - SameTime)
                            {
                                best.time = time;
                                best.cellA = reverse ? ray.cell : cell;
                                best.cellB = reverse ? cell : ray.cell;
                                best.localPoint = point;
                                best.localNormal = ray.normal;
                                best.reverse = reverse;
                                best.samples = 1;
                            }
                            else if (Mathf.Abs(time - best.time) <= SameTime && reverse == best.reverse
                                && ray.normal == best.localNormal
                                && Mathf.Abs(Vector2.Dot(point - best.localPoint, ray.normal)) < EdgeInset)
                            {
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
}

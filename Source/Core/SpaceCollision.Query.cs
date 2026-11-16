using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
        private struct GridRay
        {
            private readonly Vector2 from, direction;
            private readonly int stepX, stepZ;
            private int x, z;
            internal float Along { get; private set; }
            internal IntVec3 Cell => new IntVec3(x, 0, z);

            internal GridRay(Vector2 from, Vector2 direction, float near)
            {
                this.from = from;
                this.direction = direction;
                Along = near;
                Vector2 start = from + direction * near;
                x = Mathf.FloorToInt(start.x);
                z = Mathf.FloorToInt(start.y);
                stepX = direction.x > 0f ? 1 : -1;
                stepZ = direction.y > 0f ? 1 : -1;
                if (direction.x < 0f && start.x == x) x--;
                if (direction.y < 0f && start.y == z) z--;
            }

            internal void Advance()
            {
                float nextX = direction.x == 0f ? float.PositiveInfinity
                    : (x + (stepX > 0 ? 1 : 0) - from.x) / direction.x;
                float nextZ = direction.y == 0f ? float.PositiveInfinity
                    : (z + (stepZ > 0 ? 1 : 0) - from.y) / direction.y;
                if (nextX <= nextZ) x += stepX;
                if (nextZ <= nextX) z += stepZ;
                Along = Mathf.Min(nextX, nextZ);
            }
        }

        internal readonly struct RayContact
        {
            internal readonly Vector2 point, normal;
            internal readonly IntVec3 cell;
            internal readonly SpaceCellKind kind;
            internal readonly Thing building;
            internal readonly CompSpaceShield line;
            internal readonly ShieldArc guard;
            internal RayContact(Vector2 point, Vector2 normal, IntVec3 cell,
                SpaceCellKind kind, Thing building, CompSpaceShield line = null, ShieldArc guard = null)
            {
                this.point = point; this.normal = normal; this.cell = cell;
                this.kind = kind; this.building = building; this.line = line; this.guard = guard;
            }
        }

        // One decision's query view. Geometry comes from the production collision cache;
        // hypothetical damage/energy remain in the caller's private evaluation ledger.
        internal sealed class RayQuery
        {
            private readonly Surface surface;
            private readonly Map map;
            private readonly HashSet<IntVec3> cells;
            private readonly HashSet<Thing> hullBuildings = new HashSet<Thing>();
            internal readonly List<IntVec3> foundationCells = new List<IntVec3>();
            private readonly SweptBody footprint = new SweptBody();
            private readonly Predicate<IntVec3> includeBody;
            internal RayQuery(SpaceObject target, ShipBase scope = null, bool collectCover = true)
            {
                map = target.map; surface = SurfaceFor(target);
                includeBody = Includes;
                if (scope != null) cells = new HashSet<IntVec3>(scope.HullCells);
                if (!collectCover) return;
                foreach (IntVec3 cell in surface.OccupiedCells())
                {
                    if (!Includes(cell)) continue;
                    int index = map.cellIndices.CellToIndex(cell);
                    if (surface.foundations[index]) foundationCells.Add(cell);
                    Thing building = surface.buildings[index];
                    if (building != null) hullBuildings.Add(building);
                }
            }
            private bool Includes(IntVec3 cell) => cells == null || cells.Contains(cell);
            internal IEnumerable<ThingDef> GuardDefinitions()
            {
                foreach (var arc in surface.arcs)
                    if (Includes(arc.building.Position)) yield return arc.building.def;
            }
            internal void ScanProtection(Vector2 endpoint, Vector2 incoming, Action<IntVec3, Thing, bool> visit)
            {
                var ray = new GridRay(endpoint, -incoming, 0f);
                for (; ray.Cell.InBounds(map); ray.Advance())
                {
                    var cell = ray.Cell;
                    if (!Includes(cell)) continue;
                    int index = map.cellIndices.CellToIndex(cell);
                    visit(cell, surface.buildings[index], surface.foundations[index]);
                }
            }

            internal void ScanGuardSurfaces(Vector2 endpoint, Vector2 incoming, List<ShieldArc> hits)
            {
                if (surface.arcs.Count == 0) return;
                float length = map.Size.x + map.Size.z;
                Vector2 from = endpoint - incoming * length;
                while (TryFindArcHit(surface, from, incoming, length, out var arc, out _, out _,
                    item => Includes(item.building.Position) && !hits.Contains(item)))
                    hits.Add(arc);
            }
            internal bool AllBuildingsPass(HashSet<Thing> coverBuildings) => hullBuildings.IsSubsetOf(coverBuildings);
            private void NextLine(Vector2 from, Vector2 dir, float length, CompProperties_SpaceProjectile props,
                Predicate<CompSpaceShield> include, out CompSpaceShield hit, out Vector2 point,
                out Vector2 normal, out float along) => TryFindLineHit(surface, from, dir, length, props,
                    out hit, out point, out normal, out along, out _, out _,
                    shield => Includes(shield.parent.Position) && (include?.Invoke(shield) ?? shield.State == SpaceShieldState.LineActive));
            private void NextGuard(Vector2 from, Vector2 dir, float length, Predicate<ShieldArc> include,
                out ShieldArc hit, out Vector2 point, out float along) => TryFindArcHit(surface, from, dir, length,
                    out hit, out point, out along, shield => Includes(shield.building.Position) && (include?.Invoke(shield)
                        ?? (CompSpaceShield.CanAbsorbGuard(shield.interceptor) && shield.interceptor.Active)));
            internal bool Next(Vector2 from, Vector2 direction, float length, ThingDef projectile,
                float radius, HashSet<Thing> passed, HashSet<IntVec3> passedCells,
                Predicate<CompSpaceShield> includeLine, Predicate<ShieldArc> includeGuard,
                out RayContact contact, bool skipSubstructure = false,
                HashSet<Thing> coverBuildings = null, HashSet<IntVec3> coverCells = null, bool skipHull = false)
            {
                contact = default;
                if (length <= 0 || direction.sqrMagnitude < 1e-8f) return false;
                Vector2 dir = direction.normalized;
                var props = projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
                CompSpaceShield line = null; ShieldArc guard = null;
                Vector2 linePoint = default, lineNormal = default, guardPoint = default;
                float lineAlong = float.MaxValue, guardAlong = float.MaxValue;
                if (props?.ignoresShields != true)
                {
                    if (surface.lineShields.Count > 0)
                        NextLine(from, dir, length, props, includeLine, out line, out linePoint, out lineNormal, out lineAlong);
                    if (surface.arcs.Count > 0)
                        NextGuard(from, dir, length, includeGuard, out guard, out guardPoint, out guardAlong);
                }
                float end = Mathf.Min(length, lineAlong, guardAlong);
                footprint.sweepRadius = Mathf.Max(0, radius);
                Vector2 toCenter = surface.boundsCenter - from;
                float projection = Vector2.Dot(toCenter, dir);
                if ((toCenter - dir * Mathf.Clamp(projection, 0, length)).magnitude
                    > surface.boundsRadius + radius + .5f) return false;
                if (!skipHull)
                {
                    float near = Mathf.Max(0, toCenter.magnitude - surface.boundsRadius - radius - 1);
                    float hullEnd = Mathf.Min(end, projection + surface.boundsRadius + radius + 1);
                    if (TryFindHullContact(footprint, map, surface, from, dir, dir,
                        near, hullEnd, passed, passedCells, includeBody, skipSubstructure, out contact,
                        coverBuildings, coverCells)) return true;
                }
                if (guard != null && guardAlong <= lineAlong)
                    contact = new RayContact(guardPoint, (guardPoint - guard.center).normalized,
                        IntVec3.Invalid, SpaceCellKind.Empty, guard.building, guard: guard);
                else if (line != null)
                    contact = new RayContact(linePoint, lineNormal, IntVec3.Invalid,
                        SpaceCellKind.Empty, line.parent, line: line);
                else return false;
                return true;
            }
        }
    }
}

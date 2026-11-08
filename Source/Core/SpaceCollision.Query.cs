using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class SpaceCollision
    {
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
            private readonly HashSet<Thing> buildings = new HashSet<Thing>();
            internal readonly List<IntVec3> foundationCells = new List<IntVec3>();
            private readonly SweptBody footprint = new SweptBody();
            private readonly Predicate<IntVec3> includeBody, includeSolid;
            internal RayQuery(SpaceObject target, ShipBase scope = null)
            {
                map = target.map; surface = SurfaceFor(target);
                includeBody = Includes; includeSolid = IncludesSolid;
                if (scope != null) cells = new HashSet<IntVec3>(scope.HullCells);
                foreach (IntVec3 cell in surface.occupiedCells)
                {
                    if (!Includes(cell)) continue;
                    Thing building = surface.buildings[map.cellIndices.CellToIndex(cell)];
                    if (building != null) buildings.Add(building);
                    else if (surface.kinds[map.cellIndices.CellToIndex(cell)] == (byte)SpaceCellKind.Substructure)
                        foundationCells.Add(cell);
                }
            }
            private bool BuildingsPassed(HashSet<Thing> passed)
            {
                foreach (Thing building in buildings) if (!passed.Contains(building)) return false;
                return true;
            }
            private bool Includes(IntVec3 cell) => cells == null || cells.Contains(cell);
            private bool IncludesSolid(IntVec3 cell) => Includes(cell)
                && surface.kinds[map.cellIndices.CellToIndex(cell)] != (byte)SpaceCellKind.Substructure;
            internal float Obstruction(Vector2 source, Vector2 end, Thing target, ISet<Thing> removed) =>
                TargetObstruction(map, source, end, target, removed, surface);
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
                out RayContact contact, bool skipSubstructure = false)
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
                float near = Mathf.Max(0, (surface.boundsCenter - from).magnitude - surface.boundsRadius - radius - 1);
                int first = Mathf.Max(0, Mathf.FloorToInt(near / SampleStep));
                int last = Mathf.CeilToInt(Mathf.Min(end, projection + surface.boundsRadius + radius + 1) / SampleStep);
                bool emptyBody = skipSubstructure && BuildingsPassed(passed);
                for (int sample = first; !emptyBody && sample <= last; sample++)
                {
                    Vector2 p = from + dir * Mathf.Min(end, sample * SampleStep);
                    if (!FindBestCell(footprint, map, surface, from, p, dir, passed, passedCells,
                        out IntVec3 cell, out SpaceCellKind kind, out Thing building,
                        skipSubstructure ? includeSolid : includeBody)) continue;
                    Vector2 normal = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y)
                        ? new Vector2(-Mathf.Sign(dir.x), 0) : new Vector2(0, -Mathf.Sign(dir.y));
                    contact = new RayContact(p, normal, cell, kind, building);
                    return true;
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

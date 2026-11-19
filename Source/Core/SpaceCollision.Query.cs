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

        internal static RayContact ConfirmedStructureContact(Map map, RayContact contact,
            HashSet<Thing> destroyed = null)
        {
            if (map == null || contact.kind != SpaceCellKind.Substructure) return contact;
            SpaceCellKind kind = SpaceStructureRules.ClassifyCell(map, contact.cell, out var building, destroyed);
            return building == null ? contact : new RayContact(contact.point, contact.normal, contact.cell, kind, building);
        }

        internal static bool TrySameCellContinuation(Map map, RayContact contact, out RayContact next,
            HashSet<Thing> destroyed = null, HashSet<IntVec3> brokenFoundation = null)
        {
            next = default;
            if (map == null || contact.building == null || !contact.cell.InBounds(map)
                || brokenFoundation?.Contains(contact.cell) == true
                || !SubstructureOwnership.HasFoundation(map, contact.cell))
                return false;
            SpaceCellKind kind = SpaceStructureRules.ClassifyCell(map, contact.cell, out var building, destroyed);
            if (building == contact.building) return false;
            next = new RayContact(contact.point, contact.normal, contact.cell, kind, building);
            return true;
        }

        private static bool TryHullGridSegment(Map map, Surface surface, Vector2 from,
            Vector2 direction, float length, out RayContact contact)
        {
            contact = default;
            var ray = new GridRay(from, direction, 0f);
            IntVec3 previous = ray.Cell;
            // Start inside a cell is an existing overlap. Only a crossed face is a new impact.
            for (ray.Advance(); ray.Along <= length; ray.Advance())
            {
                IntVec3 cell = ray.Cell;
                int dx = previous.x - cell.x, dz = previous.z - cell.z;
                previous = cell;
                if (!HullContactCell(map, surface, cell, out Thing building)) continue;
                if (dx != 0 && HullContactCell(map, surface, cell + new IntVec3(dx, 0, 0), out _)) dx = 0;
                if (dz != 0 && HullContactCell(map, surface, cell + new IntVec3(0, 0, dz), out _)) dz = 0;
                if (dx == 0 && dz == 0) continue;
                Vector2 normal = dz == 0 || dx != 0 && Mathf.Abs(direction.x) >= Mathf.Abs(direction.y)
                    ? new Vector2(dx, 0f) : new Vector2(0f, dz);
                contact = new RayContact(from + direction * ray.Along, normal, cell,
                    building == null ? SpaceCellKind.Substructure : SpaceCellKind.Building, building);
                return true;
            }
            return false;
        }

        // One decision's query view. Geometry comes from the production collision cache;
        // hypothetical damage/energy remain in the caller's private evaluation ledger.
        internal sealed class RayQuery
        {
            private readonly TargetView target;
            private readonly Surface surface;
            private readonly Map map;
            private readonly HashSet<IntVec3> cells;
            private readonly HashSet<Thing> hullBuildings = new HashSet<Thing>();
            internal readonly List<IntVec3> foundationCells = new List<IntVec3>();
            private readonly SweptBody footprint = new SweptBody();
            private readonly Predicate<IntVec3> includeBody;
            internal RayQuery(SpaceObject target, ShipBase scope = null, bool collectCover = true)
            {
                this.target = TargetView.Of(target);
                map = this.target.map;
                surface = map != null && this.target.Valid ? SurfaceFor(target) : null;
                includeBody = Includes;
                if (scope != null) cells = new HashSet<IntVec3>(scope.HullCells);
                if (surface == null)
                {
                    if (this.target.Valid && this.target.entity != null) hullBuildings.Add(this.target.entity);
                    return;
                }
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
                if (surface == null || !target.Valid) yield break;
                foreach (var arc in surface.arcs)
                    if (Includes(arc.building.Position)) yield return arc.building.def;
            }
            internal void ScanProtection(Vector2 endpoint, Vector2 incoming, Action<IntVec3, Thing, bool> visit)
            {
                if (!target.Valid) return;
                if (surface == null) { visit(IntVec3.Invalid, target.entity, false); return; }
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
                if (surface == null || !target.Valid) return;
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
                if (!NextLocal(target.ToLocal(from), direction, length, projectile, radius, passed, passedCells,
                    includeLine, includeGuard, out contact, skipSubstructure, coverBuildings, coverCells, skipHull)) return false;
                // Existing map consumers keep their explicit backend-coordinate interface.
                contact = new RayContact(target.ToBackend(contact.point), contact.normal, contact.cell,
                    contact.kind, contact.building, contact.line, contact.guard);
                return true;
            }

            internal bool NextLocal(Vector2 from, Vector2 direction, float length, ThingDef projectile,
                float radius, HashSet<Thing> passed, HashSet<IntVec3> passedCells,
                Predicate<CompSpaceShield> includeLine, Predicate<ShieldArc> includeGuard,
                out RayContact contact, bool skipSubstructure = false,
                HashSet<Thing> coverBuildings = null, HashSet<IntVec3> coverCells = null, bool skipHull = false)
                => target.TryRay(this, from, direction, length, projectile, radius, passed, passedCells,
                    includeLine, includeGuard, out contact, skipSubstructure, coverBuildings, coverCells, skipHull);

            internal bool NextMap(Vector2 from, Vector2 dir, float length, ThingDef projectile,
                float radius, HashSet<Thing> passed, HashSet<IntVec3> passedCells,
                Predicate<CompSpaceShield> includeLine, Predicate<ShieldArc> includeGuard,
                out RayContact contact, bool skipSubstructure,
                HashSet<Thing> coverBuildings, HashSet<IntVec3> coverCells, bool skipHull)
            {
                contact = default;
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

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal enum PersonnelAccessKind
    {
        MoonPool,
        DockingPort,
        OpenDeck,
        VacuumEdge
    }

    internal readonly struct PersonnelExitSite
    {
        internal readonly Map map;
        internal readonly IntVec3 cell;
        internal readonly IntVec3 outward;
        internal readonly PersonnelAccessKind kind;
        internal readonly int portThingId;
        internal bool Vertical => kind == PersonnelAccessKind.MoonPool
            || kind == PersonnelAccessKind.OpenDeck;

        internal PersonnelExitSite(Map map, IntVec3 cell, IntVec3 outward,
            PersonnelAccessKind kind, int portThingId = 0)
        {
            this.map = map;
            this.cell = cell;
            this.outward = outward;
            this.kind = kind;
            this.portThingId = portThingId;
        }
    }

    internal readonly struct PersonnelLandingSite
    {
        internal readonly Map map;
        internal readonly IntVec3 cell;
        internal readonly PersonnelAccessKind kind;
        internal readonly int portThingId;

        internal PersonnelLandingSite(Map map, IntVec3 cell, PersonnelAccessKind kind,
            int portThingId = 0)
        {
            this.map = map;
            this.cell = cell;
            this.kind = kind;
            this.portThingId = portThingId;
        }
    }

    /// <summary>
    /// On-demand personnel access resolver. Gizmo enumeration must never call this;
    /// commands resolve once on click and revalidate the frozen result at the frame boundary.
    /// </summary>
    internal static class PersonnelAccess
    {
        internal static bool TryResolveExit(Pawn pawn, out PersonnelExitSite site)
        {
            site = default;
            Map map = pawn?.Map;
            IntVec3 cell = pawn?.Position ?? IntVec3.Invalid;
            if (pawn?.Spawned != true || map == null || map.Disposed || !cell.InBounds(map)) return false;

            if (MoonPoolUtility.IsMoonPoolAt(map, cell))
            {
                site = new PersonnelExitSite(map, cell, IntVec3.Zero,
                    PersonnelAccessKind.MoonPool);
                return true;
            }

            Building_DockingPort port = map.thingGrid.ThingsListAtFast(cell)
                .OfType<Building_DockingPort>().FirstOrDefault(p => p.Open);
            if (port != null && OpenVacuum(map, cell + port.Rotation.FacingCell))
            {
                site = new PersonnelExitSite(map, cell, port.Rotation.FacingCell,
                    PersonnelAccessKind.DockingPort, port.thingIDNumber);
                return true;
            }

            if (SupportedOpenCell(map, cell) && map.roofGrid.RoofAt(cell) == null)
            {
                site = new PersonnelExitSite(map, cell, IntVec3.Zero,
                    PersonnelAccessKind.OpenDeck);
                return true;
            }

            if (SupportedOpenCell(map, cell))
                foreach (IntVec3 outward in GenAdj.CardinalDirections)
                    if (OpenVacuum(map, cell + outward))
                    {
                        site = new PersonnelExitSite(map, cell, outward,
                            PersonnelAccessKind.VacuumEdge);
                        return true;
                    }
            return false;
        }

        internal static bool StillValid(Pawn pawn, PersonnelExitSite site)
        {
            if (pawn?.Spawned != true || pawn.Map != site.map || pawn.Position != site.cell) return false;
            if (!TryResolveExit(pawn, out PersonnelExitSite current)) return false;
            return current.kind == site.kind && current.outward == site.outward
                && current.portThingId == site.portThingId;
        }

        internal static bool TryResolveLanding(Map map, IntVec3 cell,
            out PersonnelLandingSite site)
        {
            site = default;
            if (map == null || map.Disposed || !cell.IsValid || !cell.InBounds(map)) return false;
            foreach (PersonnelAccessKind kind in new[]
            {
                PersonnelAccessKind.MoonPool,
                PersonnelAccessKind.DockingPort,
                PersonnelAccessKind.OpenDeck,
                PersonnelAccessKind.VacuumEdge
            })
                if (TryLandingAt(map, cell, kind, out site)) return true;
            return false;
        }

        internal static bool StillValid(PersonnelLandingSite site)
            => TryLandingAt(site.map, site.cell, site.kind, out PersonnelLandingSite current)
                && current.portThingId == site.portThingId;

        private static bool TryLandingAt(Map map, IntVec3 cell, PersonnelAccessKind kind,
            out PersonnelLandingSite site)
        {
            site = default;
            if (map == null || map.Disposed || !cell.InBounds(map) || !cell.Walkable(map)
                || map.mapPawns.AllPawnsSpawned.Any(p => p.Position == cell)) return false;
            if (kind == PersonnelAccessKind.MoonPool && MoonPoolUtility.IsMoonPoolAt(map, cell))
            {
                site = new PersonnelLandingSite(map, cell, kind);
                return true;
            }
            Building_DockingPort port = map.thingGrid.ThingsListAtFast(cell)
                .OfType<Building_DockingPort>().FirstOrDefault(p => p.Open
                    && OpenVacuum(map, cell + p.Rotation.FacingCell));
            if (kind == PersonnelAccessKind.DockingPort && port != null)
            {
                site = new PersonnelLandingSite(map, cell, kind, port.thingIDNumber);
                return true;
            }
            if (!SupportedOpenCell(map, cell)) return false;
            if (kind == PersonnelAccessKind.OpenDeck && map.roofGrid.RoofAt(cell) == null)
            {
                site = new PersonnelLandingSite(map, cell, kind);
                return true;
            }
            if (kind == PersonnelAccessKind.VacuumEdge
                && GenAdj.CardinalDirections.Any(d => OpenVacuum(map, cell + d)))
            {
                site = new PersonnelLandingSite(map, cell, kind);
                return true;
            }
            return false;
        }

        private static bool SupportedOpenCell(Map map, IntVec3 cell)
            => cell.InBounds(map) && SubstructureOwnership.HasFoundation(map, cell)
                && cell.Walkable(map)
                && !map.thingGrid.ThingsListAtFast(cell).Any(thing =>
                    SpaceStructureRules.IsSolidStructure(thing) && thing is not Building_DockingPort);

        private static bool OpenVacuum(Map map, IntVec3 cell)
            => !cell.InBounds(map) || !SubstructureOwnership.HasFoundation(map, cell)
                && cell.GetTerrain(map)?.exposesToVacuum == true
                && !map.thingGrid.ThingsListAtFast(cell).Any(SpaceStructureRules.IsSolidStructure);
    }
}

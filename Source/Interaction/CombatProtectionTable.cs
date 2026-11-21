using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class CombatProtectionTable
    {
        private const int Directions = 32;
        private const float SectorAngle = 2f * Mathf.PI / Directions;
        private readonly Dictionary<Map, Dictionary<DriveSubject, Receiver>> maps = new();
        internal long Scans, Cells, Layers;

        internal Receiver For(ShipBase ship, SpaceObject frame, FoundationTopologyService topology)
        {
            if (!maps.TryGetValue(ship.Map, out var ships))
                maps.Add(ship.Map, ships = new Dictionary<DriveSubject, Receiver>());
            var structure = ship.StructuralState;
            var domain = structure?.Foundation;
            int geometry = structure?.GeometryRevision ?? 0;
            if (ship.GetType() != typeof(FoundationShip) && ship.GetType() != typeof(ShuttleShip))
                geometry = ShipStructureIndex.ReadRevision(ship.Map, ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                    | ShipStructureAspect.Membership | ShipStructureAspect.Footprint | ShipStructureAspect.Roof
                    | ShipStructureAspect.ShieldGeometry | ShipStructureAspect.Representation);
            var revision = (domain, topology.DomainRevision(ship.Map, domain), geometry);
            if (!ships.TryGetValue(ship.Identity, out var receiver) || receiver.Revision != revision
                || receiver.Frame != frame || receiver.Representation != frame?.RepresentationVersion)
            {
                receiver?.Dispose();
                ships[ship.Identity] = receiver = new Receiver(this, ship, frame, revision);
            }
            return receiver;
        }

        internal void Forget(Map map)
        {
            if (map == null)
            {
                foreach (var group in maps.Values)
                    foreach (var receiver in group.Values) receiver.Dispose();
                maps.Clear();
                return;
            }
            if (maps.Remove(map, out var ships))
                foreach (var receiver in ships.Values) receiver.Dispose();
        }

        internal readonly struct AimTarget
        {
            internal readonly Thing Part;
            internal readonly Vector2 Offset;
            internal AimTarget(Thing part, Vector2 offset) { Part = part; Offset = offset; }
        }

        internal sealed class Path
        {
            internal double Hp;
            internal int Layers;
            internal Guard[] Guards;

            internal void SetLayer(int before, int after)
            {
                Hp += (double)after - before;
                Layers += (after > 0 ? 1 : 0) - (before > 0 ? 1 : 0);
            }
        }

        internal readonly struct Guard
        {
            internal readonly CompProjectileInterceptor Interceptor;
            internal readonly CompSpaceShield Shield;
            internal readonly object Bank;

            internal Guard(SpaceCollision.ShieldArc arc)
            {
                Interceptor = arc.interceptor;
                Shield = arc.building.TryGetComp<CompSpaceShield>();
                Bank = BankOf(Shield);
            }

            internal static object BankOf(CompSpaceShield shield)
            {
                var members = shield?.OwnedShields;
                return members?.Count > 0 && members.Contains(shield) ? members : (object)shield;
            }
        }

        internal sealed class Receiver : IDisposable
        {
            private readonly CombatProtectionTable owner;
            private readonly Map map;
            private readonly SpaceCollision.RayQuery ray;
            private readonly Vector2 hullCenter;
            private readonly ShipFireControl.ExternalTargets targets;
            private readonly Dictionary<(Thing, int), Path> paths = new();
            private readonly Dictionary<Thing, List<Path>> byBuilding = new();
            private readonly IDisposable structureChanges;
            internal (HashSet<IntVec3> Domain, int DomainRevision, int Geometry) Revision { get; }
            internal SpaceObject Frame { get; }
            internal int? Representation { get; }
            internal int CriticalTotal { get; }
            internal ThingDef[] GuardDefinitions { get; }

            internal Receiver(CombatProtectionTable owner, ShipBase ship, SpaceObject frame,
                (HashSet<IntVec3> Domain, int DomainRevision, int Geometry) revision)
            {
                this.owner = owner;
                Revision = revision;
                Frame = frame;
                Representation = frame?.RepresentationVersion;
                map = ship.Map;
                hullCenter = ship.HullCenter;
                ray = new SpaceCollision.RayQuery(frame, ship, collectCover: false);
                GuardDefinitions = ray.GuardDefinitions().Distinct().ToArray();
                targets = new ShipFireControl.ExternalTargets(ship);
                CriticalTotal = 5 * targets.weapons.Count + 4 * targets.propulsion.Count;
                structureChanges = ship.StructuralState?.Watch(ShipStructureAspect.Health, batch =>
                {
                    using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementProtectionUpdate);
                    foreach (var change in batch.Changes)
                        if (change.Before is int before && change.After is int after)
                            HealthChanged(change.Thing, before, after);
                });
            }

            internal AimTarget[] Select(Vector2 source, CombatPart priority)
            {
                return targets.weapons.Concat(targets.propulsion)
                    .Where(part => !part.Destroyed)
                    .OrderBy(part => (MapPosition(part) - hullCenter - source).sqrMagnitude)
                    .ThenBy(part => part.thingIDNumber).Take(6)
                    .OrderBy(part => targets.propulsion.Contains(part) != (priority == CombatPart.Propulsion))
                    .ThenBy(part => part.thingIDNumber)
                    .Select(part => new AimTarget(part, MapPosition(part) - hullCenter))
                    .Concat(new[] { new AimTarget(null, Vector2.zero) }).ToArray();
            }

            private static Vector2 MapPosition(Thing part)
            {
                var draw = part.DrawPos;
                return new Vector2(draw.x, draw.z);
            }

            internal int CriticalWeight(Thing target) => targets.propulsion.Contains(target) ? 4 : 5;

            internal static int DirectionOf(Vector2 direction) =>
                (Mathf.RoundToInt(Mathf.Atan2(direction.y, direction.x) / SectorAngle) + Directions) % Directions;

            internal Path Get(Thing target, int sector)
            {
                var key = (target, sector);
                if (paths.TryGetValue(key, out var found)) return found;
                using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementProtectionScan);
                owner.Scans++;
                var path = new Path();
                paths.Add(key, path);
                Vector2 incoming = new Vector2(Mathf.Cos(sector * SectorAngle), Mathf.Sin(sector * SectorAngle));
                if (sector % 8 == 0) incoming = new Vector2(Mathf.Round(incoming.x), Mathf.Round(incoming.y));
                Vector2 endpoint = target == null ? hullCenter : MapPosition(target);
                var seen = new HashSet<Thing>();
                ray.ScanProtection(endpoint, incoming, (_, building, foundation) =>
                {
                    owner.Cells++;
                    if (building == target && target != null) return;
                    if (building != null && !building.Destroyed && building.def.useHitPoints)
                    {
                        if (!seen.Add(building)) return;
                        owner.Layers++;
                        path.SetLayer(0, Math.Max(0, building.HitPoints));
                        if (!byBuilding.TryGetValue(building, out var registered))
                            byBuilding.Add(building, registered = new List<Path>());
                        registered.Add(path);
                    }
                    else if (foundation)
                    {
                        // Foundations have no persistent HP. The standard break threshold
                        // supplies their average-layer proxy for the estimate.
                        owner.Layers++;
                        path.SetLayer(0, 60);
                    }
                });
                var arcs = new List<SpaceCollision.ShieldArc>();
                ray.ScanGuardSurfaces(endpoint, incoming, arcs);
                path.Guards = arcs.Select(arc => new Guard(arc)).ToArray();
                return path;
            }

            private void HealthChanged(Thing building, int before, int after)
            {
                if (!byBuilding.TryGetValue(building, out var pathsForBuilding)) return;
                foreach (var path in pathsForBuilding) path.SetLayer(Math.Max(0, before), Math.Max(0, after));
            }

            internal float RegularCapacity(Guard guard) => (float)guard.Shield.SharedRegularCapacity;

            public void Dispose() => structureChanges?.Dispose();
        }
    }
}

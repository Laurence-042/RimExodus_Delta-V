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

        internal Receiver For(ShipBase ship, SpaceObject frame, int version)
        {
            if (!maps.TryGetValue(ship.Map, out var ships))
                maps.Add(ship.Map, ships = new Dictionary<DriveSubject, Receiver>());
            if (!ships.TryGetValue(ship.Identity, out var receiver) || receiver.Version != version)
                ships[ship.Identity] = receiver = new Receiver(this, ship, frame, version);
            return receiver;
        }

        internal void Forget(Map map)
        {
            if (map == null) maps.Clear();
            else maps.Remove(map);
        }

        internal void HealthChanged(Map map, Thing building, int before)
        {
            if (!maps.TryGetValue(map, out var ships)) return;
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementProtectionUpdate);
            foreach (var receiver in ships.Values) receiver.HealthChanged(building, before);
        }

        internal void GuardChanged(Map map)
        {
            if (map == null || !maps.TryGetValue(map, out var ships)) return;
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementProtectionUpdate);
            foreach (var receiver in ships.Values) receiver.GuardChanged();
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
                var members = Shield?.OwnedShields;
                Bank = members?.Count > 0 && members.Contains(Shield) ? members : (object)Shield;
            }
        }

        internal sealed class Receiver
        {
            private readonly CombatProtectionTable owner;
            private readonly Map map;
            private readonly SpaceCollision.RayQuery ray;
            private readonly Vector2 hullCenter;
            private readonly ShipFireControl.ExternalTargets targets;
            private readonly Dictionary<(Thing, int), Path> paths = new();
            private readonly Dictionary<Thing, List<Path>> byBuilding = new();
            private readonly Dictionary<object, float> regularCapacity = new();
            internal int Version { get; }
            internal int CriticalTotal { get; }
            internal ThingDef[] GuardDefinitions { get; }

            internal Receiver(CombatProtectionTable owner, ShipBase ship, SpaceObject frame, int version)
            {
                this.owner = owner;
                Version = version;
                map = ship.Map;
                hullCenter = ship.HullCenter;
                ray = new SpaceCollision.RayQuery(frame, ship, collectCover: false);
                GuardDefinitions = ray.GuardDefinitions().Distinct().ToArray();
                targets = new ShipFireControl.ExternalTargets(ship);
                CriticalTotal = 5 * targets.weapons.Count + 4 * targets.propulsion.Count;
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

            internal void HealthChanged(Thing building, int before)
            {
                if (!byBuilding.TryGetValue(building, out var pathsForBuilding)) return;
                int after = Math.Max(0, building.HitPoints);
                foreach (var path in pathsForBuilding) path.SetLayer(Math.Max(0, before), after);
            }

            internal void GuardChanged() => regularCapacity.Clear();
            internal float RegularCapacity(Guard guard)
            {
                if (!regularCapacity.TryGetValue(guard.Bank, out float amount))
                    regularCapacity.Add(guard.Bank, amount = (float)guard.Shield.SharedRegularCapacity);
                return amount;
            }
        }
    }
}

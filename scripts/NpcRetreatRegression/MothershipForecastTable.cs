#if MOTHERSHIP_OFFLINE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;
using Benefit = Program.MothershipSharedDamageReference.Benefit;

internal static partial class Program
{
    sealed class MothershipForecastTable
    {
        const int TargetCount = 6;
        const int DirectionCount = 32;
        const float Sector = 2f * Mathf.PI / DirectionCount;

        internal sealed class Counters
        {
            public int targets, defenseRays, gridVisits, layerVisits, shieldQueries;
            public int callbacks, affectedPaths, lookups, repeatRays, repeatLayers, repeatGridVisits;
        }

        internal sealed class Profile
        {
            public long grouping, response, distribute, fill, score, sort, copy;
            public long scan, classify, register;
            internal static long Stamp(Profile p) => p == null ? 0 : Stopwatch.GetTimestamp();
        }

        internal readonly record struct PathSample(bool Outgoing, Thing Target, int Sector,
            double Hp, int Layers);

        internal sealed class Path
        {
            internal double TotalHp { get; private set; }
            internal int Layers { get; private set; }

            internal void Update(float before, float after)
            {
                TotalHp += (double)after - before;
                Layers += (after > 0 ? 1 : 0) - (before > 0 ? 1 : 0);
            }
        }

        // The path carries two numbers. The reverse index belongs to the
        // notification source and holds no ordered contact data.
        internal sealed class DurabilityIndex
        {
            internal sealed class Source
            {
                internal float Hp { get; private set; }
                readonly HashSet<Path> subscribers = new();

                internal Source(float hp) => Hp = Math.Max(0, hp);

                internal void Register(Path path)
                {
                    if (subscribers.Add(path)) path.Update(0, Hp);
                }

                internal int Notify(float hp)
                {
                    hp = Math.Max(0, hp);
                    if (hp == Hp) return 0;
                    foreach (var path in subscribers) path.Update(Hp, hp);
                    Hp = hp;
                    return subscribers.Count;
                }
            }

            readonly Dictionary<object, Source> sources = new();
            internal IEnumerable<KeyValuePair<object, Source>> Sources => sources;

            internal void Register(object key, float hp, Path path)
            {
                if (!sources.TryGetValue(key, out var source))
                    sources.Add(key, source = new Source(hp));
                source.Register(path);
            }

            internal int Notify(object key, float hp) => sources.TryGetValue(key, out var source) ? source.Notify(hp) : 0;
        }

        internal readonly record struct PathKey(Thing Target, int Sector);
        readonly record struct ShotWeight(PathKey Path, float Budget, bool Outgoing);

        struct Volley
        {
            internal int Count;
            internal float Budget, Penetrating;
        }

        sealed class Defense
        {
            readonly Map map;
            readonly HashSet<IntVec3> hull;
            readonly Thing[] targets;
            readonly HashSet<Thing> propulsion;
            readonly MothershipWeaponOutputTable.Target[] weaponsFirst, propulsionFirst;
            readonly Vector2 center;
            readonly Dictionary<PathKey, Path> paths = new();
            readonly DurabilityIndex durability = new();
            readonly SpaceCollision.RayQuery guardQuery;
            readonly Counters counter;
            readonly bool timed;
            internal long ScanTicks { get; private set; }
            internal int CriticalTotal { get; }
            internal Profile Probe;
            internal IEnumerable<PathSample> Observed(bool outgoing) => paths.Select(x =>
                new PathSample(outgoing, x.Key.Target, x.Key.Sector, x.Value.TotalHp, x.Value.Layers));


            internal Defense(ManeuverMotionObject receiver, ManeuverMotionObject attacker, Counters counter, bool timed)
            {
                this.counter = counter;
                this.timed = timed;
                map = receiver.map;
                hull = receiver.member.HullCells.ToHashSet();
                center = receiver.member.HullCenter;
                var visible = new ShipFireControl.ExternalTargets(receiver.member);
                propulsion = visible.propulsion.ToHashSet();
                CriticalTotal = visible.weapons.Count * 5 + visible.propulsion.Count * 4;
                Vector2 source = center + SpaceTransform.RotateInverse(
                    OrbitSphere.Offset(receiver.pose.position, attacker.pose.position), receiver.pose.angle);
                targets = visible.weapons.Concat(visible.propulsion)
                    .OrderBy(t => (Position(t) - source).sqrMagnitude)
                    .ThenBy(t => t.thingIDNumber).Take(TargetCount).ToArray();
                weaponsFirst = OrderedTargets(false);
                propulsionFirst = OrderedTargets(true);
                counter.targets = targets.Length;
                if (map.listerThings.AllThings.OfType<ThingWithComps>()
                    .Any(t => receiver.member.Contains(t) && t.GetComp<CompProjectileInterceptor>() != null))
                    guardQuery = new SpaceCollision.RayQuery(receiver, receiver.member);
            }

            internal int CriticalWeight(Thing target) => propulsion.Contains(target) ? 4 : 5;
            internal MothershipWeaponOutputTable.Target[] AimTargets(CombatPart priority) =>
                priority == CombatPart.Propulsion ? propulsionFirst : weaponsFirst;

            MothershipWeaponOutputTable.Target[] OrderedTargets(bool preferPropulsion) => targets
                .OrderBy(t => propulsion.Contains(t) != preferPropulsion).ThenBy(t => t.thingIDNumber)
                .Select(t => new MothershipWeaponOutputTable.Target(t, Position(t) - center))
                .Append(new MothershipWeaponOutputTable.Target(null, Vector2.zero)).ToArray();

            static Vector2 Position(Thing target) => new Vector2(target.DrawPos.x, target.DrawPos.z);

            static Vector2 Direction(int sector)
            {
                var direction = new Vector2(Mathf.Cos(sector * Sector), Mathf.Sin(sector * Sector));
                return sector % 8 == 0 ? new Vector2(Mathf.Round(direction.x), Mathf.Round(direction.y)) : direction;
            }

            internal PathKey Resolve(Thing target, Vector2 direction)
            {
                int sector = (Mathf.RoundToInt(Mathf.Atan2(direction.y, direction.x) / Sector) + DirectionCount) % DirectionCount;
                if (target != null) return new PathKey(target, sector);
                // Use the closest parallel path when the aim gate falls back to center.
                Vector2 incoming = Direction(sector);
                Vector2 side = new Vector2(-incoming.y, incoming.x);
                float nearest = float.PositiveInfinity;
                Thing fallback = null;
                foreach (var part in targets)
                {
                    float offset = Mathf.Abs(Vector2.Dot(Position(part) - center, side));
                    if (offset >= nearest) continue;
                    fallback = part;
                    nearest = offset;
                }
                return new PathKey(fallback, sector);
            }

            internal Path Get(PathKey key)
            {
                if (paths.TryGetValue(key, out var path)) return path;
                long start = timed ? Stopwatch.GetTimestamp() : 0;
                path = new Path();
                paths.Add(key, path);
                Vector2 endpoint = key.Target == null ? center : Position(key.Target);
                Vector2 direction = Direction(key.Sector);
                long scanStart = Profile.Stamp(Probe);
                Scan(path, key.Target, endpoint, -direction);
                if (Probe != null) Probe.scan += Stopwatch.GetTimestamp() - scanStart;
                if (guardQuery != null) RegisterGuards(path, endpoint, direction);
                if (timed) ScanTicks += Stopwatch.GetTimestamp() - start;
                return path;
            }

            void Scan(Path path, Thing target, Vector2 endpoint, Vector2 outward)
            {
                counter.defenseRays++;
                int x = Mathf.FloorToInt(endpoint.x), z = Mathf.FloorToInt(endpoint.y);
                int stepX = outward.x > 0 ? 1 : -1, stepZ = outward.y > 0 ? 1 : -1;
                if (outward.x < 0 && endpoint.x == x) x--;
                if (outward.y < 0 && endpoint.y == z) z--;
                for (;;)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!cell.InBounds(map)) break;
                    counter.gridVisits++;
                    if (hull.Contains(cell))
                    {
                        long classifyStart = Profile.Stamp(Probe);
                        SpaceStructureRules.ClassifyCell(map, cell, out var building);
                        if (Probe != null) Probe.classify += Stopwatch.GetTimestamp() - classifyStart;
                        if (building != null && building != target && building.def.useHitPoints)
                        {
                            counter.layerVisits++;
                            long registerStart = Profile.Stamp(Probe);
                            durability.Register(building, building.HitPoints, path);
                            if (Probe != null) Probe.register += Stopwatch.GetTimestamp() - registerStart;
                        }
                    }
                    float nextX = outward.x == 0 ? float.PositiveInfinity :
                        (x + (stepX > 0 ? 1 : 0) - endpoint.x) / outward.x;
                    float nextZ = outward.y == 0 ? float.PositiveInfinity :
                        (z + (stepZ > 0 ? 1 : 0) - endpoint.y) / outward.y;
                    if (nextX <= nextZ) x += stepX;
                    if (nextZ <= nextX) z += stepZ;
                }
            }

            void RegisterGuards(Path path, Vector2 endpoint, Vector2 incoming)
            {
                var seen = new HashSet<SpaceCollision.ShieldArc>();
                var seenBanks = new HashSet<CompSpaceShield>();
                float length = map.Size.x + map.Size.z;
                Vector2 from = endpoint - incoming * length;
                for (;;)
                {
                    counter.shieldQueries++;
                    if (!guardQuery.Next(from, incoming, length, null, 0, null, null, _ => false,
                        g => !seen.Contains(g), out var contact, skipHull:true)) break;
                    seen.Add(contact.guard);
                    counter.layerVisits++;
                    var shield = contact.building.TryGetComp<CompSpaceShield>();
                    if (shield == null)
                    {
                        durability.Register(contact.guard.interceptor, contact.guard.interceptor.currentHitPoints, path);
                        continue;
                    }
                    var members = shield.OwnedShields;
                    var key = members == null ? shield : members.OrderBy(m => m.parent.thingIDNumber).FirstOrDefault() ?? shield;
                    if (seenBanks.Add(key)) durability.Register(key, (float)shield.SharedRegularCapacity, path);
                    // Emergency capacity belongs to the individual surface.
                    durability.Register(contact.guard.interceptor, (float)shield.EmergencyCapacity, path);
                }
            }

            internal void ExerciseCallbacks()
            {
                foreach (var (key, source) in durability.Sources)
                {
                    float original = source.Hp;
                    if (original <= 0) continue;
                    counter.affectedPaths += durability.Notify(key, original * .5f);
                    counter.affectedPaths += durability.Notify(key, 0);
                    counter.affectedPaths += durability.Notify(key, original);
                    counter.callbacks += 3;
                }
            }
        }

        readonly Defense attack, defend;
        internal readonly Counters outgoing = new();
        internal readonly Counters incoming = new();
        internal long ScanTicks => attack.ScanTicks + defend.ScanTicks;
        internal Profile Probe { get => attack.Probe; set { attack.Probe = value; defend.Probe = value; } }
        internal PathSample[] Observed() => attack.Observed(true).Concat(defend.Observed(false)).ToArray();
        internal PathSample ReplayPath(PathSample key)
        {
            var path = (key.Outgoing ? attack : defend).Get(new PathKey(key.Target, key.Sector));
            return key with { Hp = path.TotalHp, Layers = path.Layers };
        }

        internal MothershipForecastTable(ManeuverMotionObject own, ManeuverMotionObject enemy, bool timed)
        {
            attack = new Defense(enemy, own, outgoing, timed);
            defend = new Defense(own, enemy, incoming, timed);
        }

        internal MothershipWeaponOutputTable.Target[] AimTargets(bool outgoing, CombatPart priority) =>
            (outgoing ? attack : defend).AimTargets(priority);

        internal Benefit[] Query(IReadOnlyList<MothershipWeaponOutputTable.Delivery> shots)
        {
            var probe = Probe;
            long start = Profile.Stamp(probe);
            var outgoingVolley = new Dictionary<PathKey, Volley>();
            var incomingVolley = new Dictionary<PathKey, Volley>();
            var weights = new ShotWeight[shots.Count];
            for (int slot = 0; slot < shots.Count; slot++)
            {
                var shot = shots[slot];
                var defense = shot.Outgoing ? attack : defend;
                PathKey key = defense.Resolve(shot.Part, shot.Direction);
                var output = shot.Output;
                float budget = output.Budget;
                weights[slot] = new ShotWeight(key, budget, shot.Outgoing);
                var volleys = shot.Outgoing ? outgoingVolley : incomingVolley;
                volleys.TryGetValue(key, out var volley);
                volley.Count += output.Arrivals;
                volley.Budget += budget;
                volley.Penetrating += output.Penetrating;
                volleys[key] = volley;
            }

            if (probe != null) { probe.grouping += Stopwatch.GetTimestamp() - start; start = Stopwatch.GetTimestamp(); }
            var outgoingResults = Evaluate(attack, outgoing, outgoingVolley);
            var incomingResults = Evaluate(defend, incoming, incomingVolley);
            if (probe != null) { probe.response += Stopwatch.GetTimestamp() - start; start = Stopwatch.GetTimestamp(); }
            var benefits = new Benefit[shots.Count];
            for (int slot = 0; slot < shots.Count; slot++)
            {
                var weight = weights[slot];
                if (weight.Budget <= 0) continue;
                var volleys = weight.Outgoing ? outgoingVolley : incomingVolley;
                var result = (weight.Outgoing ? outgoingResults : incomingResults)[weight.Path];
                float share = weight.Budget / volleys[weight.Path].Budget;
                benefits[slot] = new Benefit(result.Damage * share, 0, result.Critical * share,
                    result.Armor * share, shots[slot].Laser ? 0 : result.Damage * share, 0);
            }
            if (probe != null) probe.distribute += Stopwatch.GetTimestamp() - start;
            return benefits;
        }

        static Dictionary<PathKey, Benefit> Evaluate(Defense defense, Counters counter, Dictionary<PathKey, Volley> volleys)
        {
            var results = new Dictionary<PathKey, Benefit>(volleys.Count);
            foreach (var (key, volley) in volleys)
            {
                var path = defense.Get(key);
                var damage = MothershipDamageSurface.AtAverage(volley.Count, volley.Budget, volley.Penetrating,
                    path.TotalHp, path.Layers);
                counter.lookups++;
                float targetHp = key.Target?.HitPoints ?? 0;
                float targetDamage = Math.Min(targetHp, damage.Through);
                float weight = defense.CriticalWeight(key.Target);
                float critical = targetHp > 0 && defense.CriticalTotal > 0
                    ? weight * targetDamage / (targetHp * defense.CriticalTotal) : 0;
                results.Add(key, new Benefit(damage.Damage + targetDamage, 0, critical,
                    path.TotalHp > 0 ? damage.Damage / (float)path.TotalHp : 0, 0, 0));
            }
            return results;
        }

        internal void ExerciseCallbacks()
        {
            attack.ExerciseCallbacks();
            defend.ExerciseCallbacks();
        }
    }
}
#endif

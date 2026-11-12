using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal struct CombatDamageEstimate
    {
        internal float structure, guard, armorProgress, returnedPotential, uncertainDamageUpper;
        internal bool uncertain, exhausted, hitArmor;
        internal int contacts;
    }

    // Reusable workspace for one shot. Contacts, fragments and pellets share local debits.
    internal sealed class CombatDamageForecast
    {
        internal sealed class CoverMask
        {
            internal readonly HashSet<Thing> things = new HashSet<Thing>();
            internal readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
            internal bool skipFloor, uncertain;
        }
        // Independent deterministic dimensions for each physical contact and floor
        // break test. Reusing one quantile for every layer correlates all misses and
        // incorrectly makes the second 50% cover layer receive zero expected hits.
        internal static float ContactSample(float sample, IntVec3 cell, uint dimension = 0)
        {
            unchecked
            {
                uint bits = (uint)Mathf.Clamp((int)(sample * 16777216f), 0, 16777215);
                uint value = bits ^ (uint)cell.x * 0x9e3779b9u ^ (uint)cell.z * 0x85ebca6bu ^ dimension * 0xc2b2ae35u;
                value ^= value >> 16; value *= 0x7feb352du;
                value ^= value >> 15; value *= 0x846ca68bu; value ^= value >> 16;
                return (value >> 8) / 16777216f;
            }
        }
        // Lifetime is one synchronous decision. Candidate damage never writes these
        // observed facts; only deterministic cover lookup entries are memoized here.
        internal sealed class Observation
        {
            internal readonly Map map;
            internal readonly SpaceCollision.RayQuery query;
            internal readonly Dictionary<Thing, float> health = new Dictionary<Thing, float>();
            internal readonly Dictionary<Thing, float> critical = new Dictionary<Thing, float>();
            internal readonly Dictionary<(ThingDef projectile, float sample), CoverMask> cover =
                new Dictionary<(ThingDef, float), CoverMask>();
            internal readonly float criticalTotal;
            internal Observation(SpaceObject target, ShipBase scope = null)
            {
                map = target.map; query = new SpaceCollision.RayQuery(target, scope);
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (thing is Building && !thing.Destroyed && (scope == null || scope.Contains(thing)))
                    {
                        health[thing] = Mathf.Max(0, thing.HitPoints);
                        float weight = ShipFireControl.PartValue(thing, CombatPart.Automatic);
                        if (weight > 0) { critical[thing] = weight; criticalTotal += weight; }
                    }
                }
            }
        }
        private readonly SpaceCollision.RayQuery query;
        private readonly Map map;
        private readonly Dictionary<Thing, float> health = new Dictionary<Thing, float>();
        private readonly HashSet<IntVec3> brokenFoundation = new HashSet<IntVec3>();
        private readonly Dictionary<Thing, float> initialHealth;
        private readonly Dictionary<Thing, float> criticalWeights;
        private readonly float criticalTotal;
        private float criticalDamaged;
        private readonly Dictionary<(ThingDef projectile, float sample), CoverMask> cover;
        private readonly HashSet<Thing> destroyed;
        private readonly CombatResourceLedger resources;
        private readonly Dictionary<CompSpaceShield, CompSpaceShield> guards = new Dictionary<CompSpaceShield, CompSpaceShield>();
        private readonly Dictionary<CompSpaceShield, double> emergency = new Dictionary<CompSpaceShield, double>();
        private readonly Dictionary<CompProjectileInterceptor, float> nativeGuard = new Dictionary<CompProjectileInterceptor, float>();
        private readonly HashSet<CompSpaceShield> disabledLines = new HashSet<CompSpaceShield>();
        private sealed class TraceBuffers
        {
            internal readonly HashSet<Thing> passed = new HashSet<Thing>();
            internal readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
        }
        // One workspace per recursion depth: child fragments must not overwrite
        // their parent's contact set. Reused only inside this candidate's ledger.
        private readonly List<TraceBuffers> traceBuffers = new List<TraceBuffers>();
        private readonly Predicate<CompSpaceShield> includeLine;
        private readonly Predicate<SpaceCollision.ShieldArc> includeGuard;
        internal SpaceCollision.RayQuery Query => query;
        internal CombatDamageForecast(SpaceObject target, ShipBase scope = null)
            : this(new Observation(target, scope)) { }
        internal CombatDamageForecast(Observation observation)
        {
            resources = new CombatResourceLedger(); destroyed = resources.destroyed;
            map = observation.map; query = observation.query; initialHealth = observation.health;
            criticalWeights = observation.critical; criticalTotal = observation.criticalTotal; cover = observation.cover;
            includeLine = LineAvailable; includeGuard = arc => GuardAvailable(arc) > 0;
        }

        internal void ResetShot()
        {
            resources.Reset();
            health.Clear();
            brokenFoundation.Clear();
            guards.Clear();
            emergency.Clear();
            nativeGuard.Clear();
            disabledLines.Clear();
            criticalDamaged = 0;
        }

        private CompSpaceShield Guard(CompSpaceShield shield)
        {
            if (!guards.TryGetValue(shield, out var state)) guards[shield] = state = shield.ForecastGuardCopy();
            return state;
        }
        internal float RemainingHP(Thing thing) => health.TryGetValue(thing, out float hp) ? hp
            : initialHealth.TryGetValue(thing, out float initial) ? initial : Mathf.Max(0, thing.HitPoints);
        internal float CriticalDamage => criticalTotal > 0 ? criticalDamaged / criticalTotal : 0;
        private float Apply(Thing thing, DamageDef damageDef, float amount, ref CombatDamageEstimate result)
        {
            float damage = SpaceImpactEffects.ExpectedBuildingDamage(thing, damageDef, amount, out bool uncertain);
            result.uncertain |= uncertain;
            float hp = RemainingHP(thing), dealt = Mathf.Min(hp, damage);
            health[thing] = hp - dealt; result.structure += dealt;
            if (!result.hitArmor && dealt > 0 && SpaceStructureRules.BuildingHitChance(thing) >= 1
                && !criticalWeights.ContainsKey(thing))
            {
                result.hitArmor = true;
                result.armorProgress = dealt / Mathf.Max(1, initialHealth[thing]);
            }
            if (criticalWeights.TryGetValue(thing, out float weight))
                criticalDamaged += weight * dealt / Mathf.Max(1, initialHealth[thing]);
            if (thing.def.useHitPoints && hp > 0 && health[thing] <= .00001f) destroyed.Add(thing);
            return dealt;
        }

        private static double Balance(Dictionary<CompSpaceShield, double> ledger, CompSpaceShield shield, bool local) =>
            ledger.TryGetValue(shield, out double value) ? value : local ? shield.EmergencyCapacity : shield.RegularCapacity;
        private bool UsesRegular(CompSpaceShield shield) => Guard(shield).UsesRegular;
        private List<CompSpaceShield> GuardMembers(CompSpaceShield shield)
        {
            var members = new List<CompSpaceShield>();
            if (!UsesRegular(shield)) return members;
            if (shield.OwnedShields != null)
                foreach (var member in shield.OwnedShields)
                    if (member.parent.Spawned && !member.parent.Destroyed && !destroyed.Contains(member.parent)
                        && member.FlickedOn && UsesRegular(member) && !members.Contains(member)) members.Add(member);
            if (members.Count == 0) members.Add(shield);
            return members;
        }
        private float GuardAvailable(SpaceCollision.ShieldArc arc)
        {
            if (destroyed.Contains(arc.building) || !CombatGuardAvailability.Operational(arc.interceptor)) return 0;
            var shield = arc.building.TryGetComp<CompSpaceShield>();
            if (shield == null) return nativeGuard.TryGetValue(arc.interceptor, out float amount) ? amount : arc.interceptor.currentHitPoints;
            double total = Balance(emergency, shield, true);
            foreach (var member in GuardMembers(shield)) total += Guard(member).RegularCapacity;
            return (float)total;
        }
        private float DebitGuard(SpaceCollision.ShieldArc arc, float amount)
        {
            var shield = arc.building.TryGetComp<CompSpaceShield>();
            if (shield == null)
            {
                float paid = Mathf.Min(GuardAvailable(arc), Mathf.Max(0, Mathf.RoundToInt(amount)));
                nativeGuard[arc.interceptor] = GuardAvailable(arc) - paid;
                return paid;
            }
            double remaining = amount;
            var members = GuardMembers(shield);
            while (members.Count > 0 && remaining > 1e-9)
            {
                double share = remaining / members.Count, paid = 0;
                for (int i = members.Count - 1; i >= 0; i--)
                {
                    var member = members[i];
                    paid += Guard(member).DebitRegular(share);
                    if (!Guard(member).UsesRegular) members.RemoveAt(i);
                }
                remaining = Math.Max(0, remaining - paid);
                if (paid <= 0) break;
            }
            double local = Math.Min(remaining, Balance(emergency, shield, true));
            emergency[shield] = Balance(emergency, shield, true) - local;
            return (float)(amount - remaining + local);
        }
        private bool LineAvailable(CompSpaceShield shield) => !destroyed.Contains(shield.parent)
            && shield.LineAvailableAt(Find.TickManager.TicksGame)
            && !disabledLines.Contains(shield);
        private float PulseEnergy(CompSpaceShield shield)
            => resources.Grid(shield.parent.TryGetComp<CompPowerTrader>()?.PowerNet);
        private float DebitPulse(CompSpaceShield shield, float requested)
        {
            if (requested <= 0) return 1;
            var net = shield.parent.TryGetComp<CompPowerTrader>()?.PowerNet;
            float left = requested - resources.Draw(net, requested);
            if (left > .0001f) disabledLines.Add(shield);
            return Mathf.Clamp01((requested - left) / requested);
        }

        internal CombatDamageEstimate Fire(Vector2 from, Vector2 direction, float length,
            ThingDef projectile, float mass, float speed, float scale = 1, float beamEnergy = 1, float coverSample = .5f)
        {
            using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.EngagementDamage);
            var result = new CombatDamageEstimate();
            Trace(from, direction.normalized, length, projectile, mass, speed, scale, beamEnergy,
                coverSample, 0, ref result);
            return result;
        }

        private void Trace(Vector2 from, Vector2 dir, float length, ThingDef projectile,
            float mass, float speed, float scale, float beamEnergy, float coverSample, int depth,
            ref CombatDamageEstimate result)
        {
            if (length <= .001f || scale <= .00001f) return;
            if (result.contacts >= 128 || depth >= 4) { result.exhausted = result.uncertain = true; return; }
            if (!cover.TryGetValue((projectile, coverSample), out var coverage))
            {
                coverage = new CoverMask();
                foreach (var pair in initialHealth)
                {
                    float chance = SpaceImpactEffects.HitChance(projectile, SpaceCellKind.Building, pair.Key,
                        SpaceImpactEffects.Resolve(projectile, pair.Key.def));
                    coverage.uncertain |= chance > 0 && chance < 1;
                    if (ContactSample(coverSample, pair.Key.Position) >= chance) coverage.things.Add(pair.Key);
                }
                float floorChance = SpaceImpactEffects.HitChance(projectile, SpaceCellKind.Substructure, null,
                    SpaceImpactEffects.Resolve(projectile, null));
                foreach (var cell in query.foundationCells)
                    if (ContactSample(coverSample, cell, 2) >= floorChance) coverage.cells.Add(cell);
                coverage.skipFloor = coverage.cells.Count == query.foundationCells.Count;
                coverage.uncertain |= floorChance < 1 && query.foundationCells.Count > 0;
                cover[(projectile, coverSample)] = coverage;
            }
            while (traceBuffers.Count <= depth) traceBuffers.Add(new TraceBuffers());
            var passed = traceBuffers[depth].passed;
            passed.Clear(); passed.UnionWith(coverage.things); passed.UnionWith(destroyed);
            result.uncertain |= coverage.uncertain;
            var passedCells = traceBuffers[depth].cells;
            passedCells.Clear(); passedCells.UnionWith(brokenFoundation); passedCells.UnionWith(coverage.cells);
            var props = projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
            if (props == null) { result.uncertain = true; return; }
            Vector2 initialDirection = dir;
            while (length > .001f && scale > .00001f)
            {
                if (result.contacts >= 128 || depth >= 4) { result.exhausted = result.uncertain = true; return; }
                if (!query.Next(from, dir, length, projectile, SpaceProjectileService.FootprintRadius(projectile), passed, passedCells,
                    includeLine, includeGuard, out var hit, coverage.skipFloor))
                {
                    if (Vector2.Dot(dir, initialDirection) < 0)
                        result.returnedPotential += SpaceImpactEffects.ComputeDamage(
                            SpaceImpactEffects.Resolve(projectile, null)?.defenderEffect, speed, 0, 1, mass) * scale;
                    return;
                }
                result.contacts++;
                float traveled = (hit.point - from).magnitude;
                length = Mathf.Max(0, length - traveled);
                from = hit.point;
                if (hit.line != null)
                {
                    var shield = hit.line;
                    if (shield.mode == SpaceShieldMode.Kinetic)
                    {
                        Vector2 velocity = dir * speed;
                        float normalSpeed = Vector2.Dot(velocity, hit.normal);
                        float requested = SpaceKinetics.ShieldImpulse(shield.kineticStrengthPercent);
                        float available = PulseEnergy(shield);
                        float impulse = SpaceKinetics.FundedShieldImpulse(mass, normalSpeed, requested, props.shieldFragmentCost, available);
                        DebitPulse(shield, SpaceKinetics.ShieldCost(mass, normalSpeed, impulse, props.shieldFragmentCost));
                        if (available < SpaceKinetics.ShieldCost(mass, normalSpeed, requested, props.shieldFragmentCost))
                            disabledLines.Add(shield);
                        Vector2 outgoing = SpaceKinetics.ApplyShieldImpulse(mass, velocity, hit.normal, impulse);
                        int fragments = SpaceKinetics.ShieldFragmentCount(mass, velocity, hit.normal, impulse, props);
                        float nextLength = speed > .0001f ? length * outgoing.magnitude / speed : 0;
                        if (fragments > 0)
                        {
                            ThingDef next = (fragments == 1 ? props.shieldTumbleDef : props.shieldFragmentDef)
                                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_ShieldFragment");
                            if (next == null) { result.uncertain = true; return; }
                            var fragment = next.GetCompProperties<CompProperties_SpaceProjectile>();
                            if (fragment?.timeoutTicks > 0)
                                nextLength = Mathf.Min(nextLength, outgoing.magnitude * fragment.timeoutTicks / 60f);
                            for (int i = 0; i < fragments; i++)
                            {
                                float spread = fragments == 1 ? 0 : (2f * i / (fragments - 1) - 1) * props.shieldFragmentScatterDeg;
                                Vector2 fragmentDirection = SpaceTransform.Rotate(outgoing.normalized, spread * Mathf.Deg2Rad);
                                Trace(from + fragmentDirection * .01f, fragmentDirection, nextLength, next,
                                    mass / fragments, outgoing.magnitude, scale, beamEnergy, coverSample, depth + 1, ref result);
                            }
                            return;
                        }
                        dir = outgoing.normalized; speed = outgoing.magnitude; length = nextLength;
                    }
                    else
                    {
                        var plan = SpaceShieldRefraction.Calculate(dir, hit.normal, shield.electromagneticStrengthPercent);
                        float paid = DebitPulse(shield, props.shieldLaserTurnEnergy * beamEnergy * plan.addedDeg / 180f);
                        dir = plan.Direction(paid);
                    }
                    from += dir * .01f; length = Mathf.Max(0, length - .01f);
                    continue;
                }
                bool isGuard = hit.guard != null;
                var effect = SpaceImpactEffects.Resolve(projectile, hit.building?.def, isGuard);
                var part = effect?.defenderEffect;
                if (part == null) { result.uncertain = true; return; }
                if (part.kind != ImpactEffectKind.DamageMap && part.kind != ImpactEffectKind.ShieldDrain)
                {
                    result.uncertain |= part.kind != ImpactEffectKind.None && part.kind != ImpactEffectKind.DestroySelf;
                    return;
                }
                float incidence = SpaceTransform.IncidenceAngle(dir, hit.normal);
                float amount = SpaceImpactEffects.ComputeDamage(part, speed, incidence,
                    isGuard ? 1 : SpaceImpactEffects.DurabilityScale(effect, hit.building), mass) * scale;
                if (isGuard)
                {
                    float capacity = GuardAvailable(hit.guard);
                    float paid = DebitGuard(hit.guard, amount); result.guard += paid;
                    if (!part.penetrates || capacity >= amount) return;
                    scale *= Mathf.Clamp01((amount - capacity) / Mathf.Max(.00001f, amount));
                    // Stay on the entry point so coincident independent guards remain visible.
                    // The exhausted surface is excluded by the private capacity ledger.
                    continue;
                }
                float chance = SpaceImpactEffects.HitChance(projectile, hit.kind, hit.building, effect);
                if (chance < 1) result.uncertain = true;
                if (hit.building != null) passed.Add(hit.building); else passedCells.Add(hit.cell);
                if (SpaceImpactEffects.Ricochets(part, hit.building, incidence)) return;
                if (hit.kind == SpaceCellKind.Substructure)
                {
                    result.uncertain = true;
                    if (ContactSample(coverSample, hit.cell, 1) >= Mathf.Clamp01(amount / part.substructureBreakThreshold)) return;
                    brokenFoundation.Add(hit.cell);
                    foreach (Thing thing in hit.cell.GetThingList(map))
                    {
                        if (thing is not Building || !destroyed.Add(thing)
                            || !initialHealth.ContainsKey(thing)) continue;
                        float hp = RemainingHP(thing);
                        health[thing] = 0f;
                        result.structure += hp;
                        if (criticalWeights.TryGetValue(thing, out float weight))
                        {
                            criticalDamaged += weight * hp / Mathf.Max(1f, initialHealth[thing]);
                        }
                    }
                    if (!part.penetrates) return;
                    scale *= Mathf.Clamp01((amount - part.substructureBreakThreshold) / Mathf.Max(.00001f, amount));
                    continue;
                }
                if (!part.penetrates && part.penetrationLayers?.Count > 0)
                {
                    var layerResult = result;
                    SpaceImpactEffects.WalkPenetration(part, map, hit.cell, dir, cell =>
                    {
                        SpaceStructureRules.ClassifyCell(map, cell, out Thing solid);
                        return solid != null && !destroyed.Contains(solid) ? solid : null;
                    }, (thing, factor) => Apply(thing, part.damageDef ?? DamageDefOf.Bullet,
                        amount * factor, ref layerResult), thing => !destroyed.Contains(thing));
                    result = layerResult; return;
                }
                if (part.explosionRadius > 0)
                {
                    // The native explosion wave and secondary effects are not run on
                    // hypothetical maps. Keep direct damage and an explicit no-occlusion
                    // structural upper bound; never advertise an unknown blast as safe.
                    result.uncertain = true;
                    foreach (var entry in initialHealth)
                    {
                        Vector2 center = new Vector2(entry.Key.Position.x + entry.Key.def.Size.x * .5f,
                            entry.Key.Position.z + entry.Key.def.Size.z * .5f);
                        float reach = part.explosionRadius + .5f * Mathf.Sqrt(entry.Key.def.Size.x * entry.Key.def.Size.x
                            + entry.Key.def.Size.z * entry.Key.def.Size.z);
                        if ((center - hit.point).sqrMagnitude > reach * reach) continue;
                        float upper = SpaceImpactEffects.ExpectedBuildingDamage(entry.Key, part.damageDef ?? DamageDefOf.Bullet,
                            amount, out _);
                        result.uncertainDamageUpper += Mathf.Min(RemainingHP(entry.Key), upper);
                    }
                }
                float dealt = Apply(hit.building, part.damageDef ?? DamageDefOf.Bullet, amount, ref result);
                if (!destroyed.Contains(hit.building) || !part.penetrates) return;
                scale *= Mathf.Clamp01((amount - dealt) / Mathf.Max(.00001f, amount));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class CombatWeaponOutputTable
    {
        internal const int WindowTicks = 600;
        private const float PerSecond = 60f / WindowTicks;
        private const int DistanceStep = 4;
        private readonly struct Sample
        {
            internal readonly int Count;
            internal readonly float Damage, Piercing, Guard;
            internal Sample(int count, float damage, float piercing, float guard)
            { Count = count; Damage = damage; Piercing = piercing; Guard = guard; }
        }

        private sealed class Row
        {
            internal CombatEngagementEvaluation.Battery Battery;
            internal CombatProtectionTable.AimTarget[] Targets;
            internal CombatProtectionTable.Receiver Receiver;
            internal Sample[] Output;
            internal Dictionary<ThingDef, float> GuardOutput;
            internal float CosHalfArc;
            internal ImpactOutcomePart Impact;
            internal bool Primary, FixedDirect;

            internal Sample At(float distance) =>
                Output[Math.Min(Output.Length - 1, Mathf.FloorToInt(distance / DistanceStep))];
        }

        private readonly struct Geometry
        {
            internal readonly Vector2 Center;
            internal readonly float Angle;
            internal Geometry(SpacePose shooter, SpacePose receiver)
            {
                Center = SpaceTransform.RotateInverse(OrbitSphere.Offset(receiver.position, shooter.position),
                    receiver.angle);
                var forward = SpaceTransform.TransportTangent(
                    new Vector2(Mathf.Cos(shooter.angle), Mathf.Sin(shooter.angle)), shooter.position, receiver.position);
                Angle = Mathf.Atan2(forward.y, forward.x) - receiver.angle;
            }
        }

        private struct Volley
        {
            internal int Count;
            internal float Damage, Piercing, Guard, PaidGuard;
        }

        private readonly List<Row> rows = new();
        private readonly SpacePose target;
        private readonly DriveSubject targetShip;
        private readonly bool offense;
        private readonly Dictionary<(CombatProtectionTable.Receiver, Thing, int), int> groups = new();
        private readonly List<(CombatProtectionTable.Receiver Receiver, Thing Target,
            CombatProtectionTable.Path Path, ThingDef Shield)> paths = new();
        private readonly List<Volley> volleys = new();
        private readonly List<(float Damage, float Guard, float Critical, float Armor)> results = new();
        private readonly List<(Row Row, int Group, Sample Output)> contributions = new();
        private readonly Dictionary<object, float> bankRemaining = new();
        private readonly Dictionary<CompProjectileInterceptor, float> shieldRemaining = new();

        internal CombatWeaponOutputTable(UniverseManager universe, ShipBase own, ShipBase enemy, SpacePose target,
            List<CombatEngagementEvaluation.Battery> batteries, List<CombatAttackWeapon> primary, bool offense)
        {
            this.target = target;
            targetShip = enemy.Identity;
            this.offense = offense;
            using var timing = universe.Performance.Measure(PerformanceStage.EngagementWeaponTable);
            var friendly = universe.ProtectionFor(own);
            var hostile = universe.ProtectionFor(enemy);
            friendly.GuardChanged();
            hostile.GuardChanged();
            var ownPose = ShipRegistry.ShipPose(own);
            ownPose.angle -= own.FrameAngle;
            var outgoingOrigin = new Geometry(ownPose, target).Center;
            var main = new HashSet<CompSpaceWeapon>();
            foreach (var gun in primary) main.Add(gun.source);
            var targetSets = new Dictionary<(int, CombatPart), CombatProtectionTable.AimTarget[]>();
            foreach (var battery in batteries)
            {
                var receiver = battery.outgoing ? hostile : friendly;
                var key = (battery.rangeGroup, battery.priority);
                if (!targetSets.TryGetValue(key, out var targets))
                {
                    var source = battery.outgoing ? outgoingOrigin : new Geometry(battery.pose, ownPose).Center;
                    targetSets.Add(key, targets = receiver.Select(source, battery.priority));
                }
                var impact = SpaceImpactEffects.Resolve(battery.shot.aim.Projectile, ThingDefOf.Wall)?.defenderEffect;
                rows.Add(new Row
                {
                    Battery = battery, Receiver = receiver, Targets = targets,
                    Primary = battery.outgoing && main.Contains(battery.gun.source),
                    FixedDirect = battery.outgoing && !battery.shot.aim.Guided
                        && !battery.gun.source.WeaponProps.topTurret
                        && battery.gun.source.Component<CompSpaceWeaponTraverse>() == null,
                    CosHalfArc = Mathf.Cos(Mathf.Min(180f, battery.gun.arc * .5f + .05f) * Mathf.Deg2Rad),
                    Impact = impact,
                    Output = Build(battery, impact),
                    GuardOutput = BuildGuards(battery, receiver.GuardDefinitions)
                });
            }
        }

        private static Sample[] Build(CombatEngagementEvaluation.Battery battery, ImpactOutcomePart impact)
        {
            var weapon = battery.gun.source;
            var shot = battery.shot;
            var props = weapon.WeaponProps;
            int pellets = Math.Max(1, props.projectilesPerShot);
            float amount = DamagePerProjectile(impact, shot.aim.Speed, shot.mass / pellets,
                props.impactScale * shot.power);
            int cooldown = weapon.CooldownTicksLeft;
            int interval = Math.Max(1, shot.cycleTicks > 0 ? shot.cycleTicks : weapon.ShotInterval);
            float speed = Mathf.Max(.001f, shot.aim.Speed);
            float reachable = Mathf.Min(battery.gun.range, speed * WindowTicks / 60f);
            int count = shot.aim.Laser ? 1 : Mathf.Max(1, Mathf.CeilToInt(reachable / DistanceStep) + 1);
            var samples = new Sample[count];
            for (int i = 0; i < count; i++)
            {
                float distance = (i + .5f) * DistanceStep;
                int first = cooldown + (shot.aim.Laser ? 0 : Mathf.CeilToInt(distance / speed * 60f));
                if (first >= WindowTicks) continue;
                int arrivals = (1 + (WindowTicks - 1 - first) / interval) * pellets;
                float damage = arrivals * amount;
                samples[i] = new Sample(arrivals, damage, impact?.penetrates == true ? damage : 0f, 0f);
            }
            return samples;
        }

        internal static float DamagePerProjectile(ImpactOutcomePart impact, float speed, float mass, float scale) =>
            impact?.kind == ImpactEffectKind.DamageMap
                ? SpaceImpactEffects.ComputeDamage(impact, speed, 0, 1, mass) * scale : 0f;

        private static Dictionary<ThingDef, float> BuildGuards(CombatEngagementEvaluation.Battery battery,
            ThingDef[] shields)
        {
            var shot = battery.shot;
            var projectile = shot.aim.Projectile;
            if (shields.Length == 0 || projectile?.GetCompProperties<CompProperties_SpaceProjectile>()?.ignoresShields == true)
                return null;
            var props = battery.gun.source.WeaponProps;
            var values = new Dictionary<ThingDef, float>();
            foreach (var def in shields)
            {
                var shield = SpaceImpactEffects.Resolve(projectile, def, shieldSurface: true)?.defenderEffect;
                if (shield?.kind != ImpactEffectKind.ShieldDrain) continue;
                float amount = SpaceImpactEffects.ComputeDamage(shield, shot.aim.Speed, 0, 1,
                    shot.mass / Math.Max(1, props.projectilesPerShot)) * props.impactScale * shot.power;
                if (amount > 0) values.Add(def, amount);
            }
            return values.Count == 0 ? null : values;
        }

        internal CombatEngagementEvaluation.Result Evaluate(SpacePose candidate, ref CombatAttackPose pose,
            CombatManeuverCurves curves)
        {
            groups.Clear();
            paths.Clear();
            volleys.Clear();
            results.Clear();
            contributions.Clear();
            bankRemaining.Clear();
            shieldRemaining.Clear();
            var outgoing = new Geometry(candidate, target);
            int lastIncoming = -1;
            Geometry incoming = default;
            float weightedError = 0, constrained = 0;
            foreach (var row in rows)
            {
                var battery = row.Battery;
                if (!battery.outgoing && lastIncoming != battery.rangeGroup)
                {
                    lastIncoming = battery.rangeGroup;
                    incoming = new Geometry(battery.pose, candidate);
                }
                var geometry = battery.outgoing ? outgoing : incoming;
                var gun = battery.gun;
                Vector2 muzzle = geometry.Center + SpaceTransform.Rotate(gun.muzzle, geometry.Angle);
                float facing = geometry.Angle + gun.mount;
                var axis = new Vector2(Mathf.Cos(facing), Mathf.Sin(facing));
                Vector2 delta = default;
                bool eligible = false;
                foreach (var aim in row.Targets)
                {
                    delta = aim.Offset - muzzle;
                    float squared = delta.sqrMagnitude;
                    if (squared <= 0f || squared < gun.minRange * gun.minRange || squared > gun.range * gun.range)
                        continue;
                    float distance = Mathf.Sqrt(squared);
                    if (Vector2.Dot(axis, delta) < distance * row.CosHalfArc - .00001f) continue;
                    eligible = true;
                    if (!battery.outgoing || offense)
                    {
                        var sample = row.At(distance);
                        if (sample.Count != 0) Add(row, aim.Part, delta, sample);
                    }
                    break;
                }
                if (!row.Primary) continue;
                if (eligible)
                {
                    pose.firepower += gun.damageRate;
                    if (gun.ready) pose.readyOutput += gun.damageRate;
                }
                if (gun.arc < 120f)
                {
                    weightedError += Mathf.Abs(SpaceTransform.WrapAngle(Mathf.Atan2(delta.y, delta.x) - facing))
                        * Mathf.Rad2Deg * gun.damageRate;
                    constrained += gun.damageRate;
                }
            }
            pose.alignment = constrained > 0 ? weightedError / constrained : 0;
            if (offense)
            {
                pose.outgoingScore = curves?.outgoing?.Evaluate(pose.readyOutput) ?? 0;
                pose.alignmentScore = constrained > 0 ? curves?.alignmentError?.Evaluate(pose.alignment) ?? 0f : 0;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                var (receiver, _, path, _) = paths[i];
                var volley = volleys[i];
                volley.PaidGuard = DebitShields(receiver, path, volley.Guard);
                volleys[i] = volley;
            }
            for (int i = 0; i < contributions.Count; i++)
            {
                var (row, group, sample) = contributions[i];
                var volley = volleys[group];
                if (sample.Guard <= 0 || volley.PaidGuard <= 0) continue;
                float paidFraction = Mathf.Clamp01(volley.PaidGuard / volley.Guard);
                float stopped = sample.Damage * paidFraction;
                if (row.Impact?.penetrationEnergyScale > 0f && paidFraction < 1f)
                {
                    // Shield demand is shared across batteries. Within a battery, only the
                    // round which exhausts its allocated capacity can be partially absorbed.
                    float survivingShots = sample.Count * (1f - paidFraction);
                    float partial = survivingShots - Mathf.Floor(survivingShots);
                    float perShot = sample.Damage / sample.Count;
                    var shot = row.Battery.shot;
                    float mass = shot.mass / Math.Max(1, row.Battery.gun.source.WeaponProps.projectilesPerShot);
                    float partialDamage = SpaceImpactEffects.DamageAtRemainingEnergy(row.Impact,
                        shot.aim.Speed, 0f, 1f, mass, partial) * row.Battery.gun.source.WeaponProps.impactScale * shot.power;
                    stopped = sample.Damage - (Mathf.Floor(survivingShots) * perShot + partialDamage);
                }
                volley.Damage -= stopped;
                if (sample.Piercing > 0f) volley.Piercing -= stopped;
                contributions[i] = (row, group, new Sample(sample.Count, Mathf.Max(0f, sample.Damage - stopped),
                    0f, sample.Guard));
                volleys[group] = volley;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                var (receiver, part, path, _) = paths[i];
                var volley = volleys[i];
                var response = CombatDamageSurface.AtAverage(volley.Count, Mathf.Max(0f, volley.Damage),
                    Mathf.Max(0f, volley.Piercing), path.Hp, path.Layers);
                float hp = part?.HitPoints ?? 0;
                float partDamage = Mathf.Min(Mathf.Max(0, hp), response.Through);
                float critical = hp > 0 && receiver.CriticalTotal > 0
                    ? receiver.CriticalWeight(part) * partDamage / (hp * receiver.CriticalTotal) : 0f;
                results.Add((response.Damage + partDamage, volley.PaidGuard, critical,
                    path.Hp > 0 ? response.Damage / (float)path.Hp : 0f));
            }

            var value = new CombatEngagementEvaluation.Result();
            float bestFixedBenefit = 0f;
            foreach (var (row, group, sample) in contributions)
            {
                var volley = volleys[group];
                var benefit = results[group];
                float share = volley.Damage > 0 ? sample.Damage / volley.Damage : 0f;
                var battery = row.Battery;
                var item = new CombatEngagementEvaluation.Contribution
                {
                    weapon = battery.gun.source, family = battery.family,
                    side = battery.outgoing ? CombatEngagementSide.Outgoing : CombatEngagementSide.Incoming,
                    damage = benefit.Damage * share * PerSecond,
                    guard = volley.Guard > 0 ? benefit.Guard * sample.Guard / volley.Guard * PerSecond : 0f,
                    critical = benefit.Critical * share * PerSecond, armorProgress = benefit.Armor * share * PerSecond,
                    uncertainty = battery.gun.instant ? 0 : battery.gun.damageRate
                };
                value.contributions.Add(item);
                value.uncertain |= item.uncertainty > 0;
                if (battery.outgoing)
                {
                    value.outgoing += item.damage;
                    pose.fireHeading |= pose.freeThrustFacing && (item.damage > 0f || item.guard > 0f);
                    float benefitValue = item.damage + item.guard;
                    if (pose.freeThrustFacing && benefitValue > bestFixedBenefit && row.FixedDirect)
                    {
                        bestFixedBenefit = benefitValue;
                        value.fixedAimWeapon = battery.gun.source;
                        value.fixedAimPart = paths[group].Target;
                        value.fixedAimTarget = CombatTarget.ForShip(targetShip, value.fixedAimPart?.thingIDNumber ?? 0);
                    }
                }
                else value.incoming += item.damage;
            }
            pose.freeThrustFacing &= !pose.fireHeading;
            return value;
        }

        private void Add(Row row, Thing part, Vector2 direction, Sample sample)
        {
            if (sample.Damage <= 0 && row.GuardOutput == null) return;
            int sector = CombatProtectionTable.Receiver.DirectionOf(direction);
            var key = (row.Receiver, part, sector);
            if (!groups.TryGetValue(key, out int group))
            {
                group = paths.Count;
                var path = row.Receiver.Get(part, sector);
                ThingDef firstShield = null;
                foreach (var guard in path.Guards)
                {
                    if (!guard.Interceptor.Active) continue;
                    firstShield = guard.Interceptor.parent.def;
                    break;
                }
                paths.Add((row.Receiver, part, path, firstShield));
                groups.Add(key, group);
                volleys.Add(default);
            }
            var shield = paths[group].Shield;
            if (shield != null && row.GuardOutput != null && row.GuardOutput.TryGetValue(shield, out float amount))
                sample = new Sample(sample.Count, sample.Damage, sample.Piercing, sample.Count * amount);
            var volley = volleys[group];
            volley.Count += sample.Count;
            volley.Damage += sample.Damage;
            volley.Piercing += sample.Piercing;
            volley.Guard += sample.Guard;
            volleys[group] = volley;
            contributions.Add((row, group, sample));
        }

        private float DebitShields(CombatProtectionTable.Receiver receiver, CombatProtectionTable.Path path, float demand)
        {
            if (demand <= 0) return 0f;
            float remaining = demand;
            foreach (var guard in path.Guards)
            {
                var interceptor = guard.Interceptor;
                if (!interceptor.Active) continue;
                var shield = guard.Shield;
                if (shield?.UsesRegular == true)
                {
                    if (!bankRemaining.TryGetValue(guard.Bank, out float bank)) bank = receiver.RegularCapacity(guard);
                    float debit = Mathf.Min(bank, remaining);
                    remaining -= debit;
                    bankRemaining[guard.Bank] = bank - debit;
                }
                if (!shieldRemaining.TryGetValue(interceptor, out float local))
                    local = Mathf.Max(0, shield == null ? interceptor.currentHitPoints : (float)shield.EmergencyCapacity);
                float paid = Mathf.Min(local, remaining);
                remaining -= paid;
                shieldRemaining[interceptor] = local - paid;
                if (remaining <= 0) break;
            }
            return demand - remaining;
        }
    }
}

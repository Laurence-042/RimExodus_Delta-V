#if MOTHERSHIP_OFFLINE
using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;
using Battery = DeltaV.CombatEngagementEvaluation.OfflineBattery;

internal static partial class Program
{
    sealed class MothershipWeaponOutputTable
    {
        internal const int DistanceStep = 4;

        internal readonly record struct Output(int Arrivals, float Budget, float Penetrating);
        internal readonly record struct Target(Thing Part, Vector2 Offset);
        internal readonly record struct Delivery(CompSpaceWeapon Weapon, CombatWeaponFamily Family,
            bool Outgoing, bool Laser, Thing Part, Vector2 Direction, Output Output);

        readonly record struct Row(Battery Battery, Output[] ByDistance, bool Primary, float CosHalfArc);

        readonly Row[] rows;
        readonly SpacePose targetPose;
        readonly bool offense;

        internal MothershipWeaponOutputTable(CombatEngagementEvaluation evaluator, bool offense, int window)
        {
            targetPose = evaluator.OfflineTargetPose;
            this.offense = offense;
            var primary = evaluator.primaryWeapons.Select(gun => gun.source).ToHashSet();
            rows = evaluator.OfflineBatteries.Select(battery =>
            {
                var weapon = battery.Gun.source;
                var shot = battery.Shot;
                var props = weapon.WeaponProps;
                var impact = SpaceImpactEffects.Resolve(shot.aim.Projectile, ThingDefOf.Wall)?.defenderEffect;
                float amount = impact?.kind == ImpactEffectKind.DamageMap ?
                    SpaceImpactEffects.ComputeDamage(impact, shot.aim.Speed, 0, 1,
                        shot.mass / props.projectilesPerShot) * props.impactScale * shot.power : 0;
                int cooldown = weapon.CooldownTicksLeft;
                int interval = Math.Max(1, shot.cycleTicks > 0 ? shot.cycleTicks : weapon.ShotInterval);
                var byDistance = new Output[DistanceSlot(battery.Gun.range) + 1];
                for (int slot = 0; slot < byDistance.Length; slot++)
                    byDistance[slot] = Sample(slot, window, cooldown, interval, props.projectilesPerShot,
                        shot.aim.Laser, shot.aim.Speed, amount, impact?.penetrates == true);
                return new Row(battery, byDistance, primary.Contains(weapon),
                    Mathf.Cos(Mathf.Min(180, battery.Gun.arc * .5f + .05f) * Mathf.Deg2Rad));
            }).ToArray();
        }

        internal static int DistanceSlot(float distance) => Mathf.FloorToInt(distance / DistanceStep);

        internal static Delivery[] FreezeLegacy(
            IReadOnlyList<CombatEngagementEvaluation.OfflineShot> shots, int window)
        {
            var deliveries = new List<Delivery>(shots.Count);
            foreach (var shot in shots)
            {
                int count = MothershipAverageDefenseBaseline.CountArrivals(shot, window);
                if (count == 0) continue;
                var projectile = shot.Shot.aim.Projectile;
                var rule = SpaceImpactEffects.Resolve(projectile, ThingDefOf.Wall)?.defenderEffect;
                if (rule?.kind != ImpactEffectKind.DamageMap) continue;
                var props = shot.Weapon.WeaponProps;
                float amount = SpaceImpactEffects.ComputeDamage(rule, shot.Speed, 0, 1,
                    shot.Shot.mass / props.projectilesPerShot) * props.impactScale * shot.Shot.power;
                if (amount <= 0) continue;
                float budget = count * amount;
                deliveries.Add(new Delivery(shot.Weapon, CombatWeaponFilter.FamilyOf(shot.Weapon, projectile),
                    shot.Outgoing, shot.Shot.aim.Laser, shot.Part, shot.Direction,
                    new Output(count, budget, rule.penetrates ? budget : 0)));
            }
            return deliveries.ToArray();
        }

        internal static Output Sample(int slot, int window, int cooldown, int interval,
            int projectiles, bool instant, float speed, float amount, bool penetrates)
        {
            float sampleDistance = (slot + .5f) * DistanceStep;
            int first = cooldown + (instant ? 0 : Mathf.CeilToInt(sampleDistance / Mathf.Max(.001f, speed) * 60));
            if (first >= window || amount <= 0) return default;
            int count = (1 + (window - 1 - first) / interval) * projectiles;
            float budget = count * amount;
            return new Output(count, budget, penetrates ? budget : 0);
        }

        internal static bool Covers(float min, float max, float cosHalfArc, Vector2 axis,
            Vector2 delta, out float distance)
        {
            float squared = delta.sqrMagnitude;
            distance = 0;
            if (squared < min * min || squared > max * max || squared <= 0) return false;
            distance = Mathf.Sqrt(squared);
            return Vector2.Dot(axis, delta) >= distance * cosHalfArc - .00001f;
        }

        readonly record struct Geometry(Vector2 SourceCenter, float Facing)
        {
            internal static Geometry Between(SpacePose shooter, SpacePose receiver)
            {
                Vector2 center = SpaceTransform.RotateInverse(
                    OrbitSphere.Offset(receiver.position, shooter.position), receiver.angle);
                Vector2 forward = SpaceTransform.TransportTangent(
                    new Vector2(Mathf.Cos(shooter.angle), Mathf.Sin(shooter.angle)), shooter.position, receiver.position);
                return new Geometry(center, Mathf.Atan2(forward.y, forward.x) - receiver.angle);
            }
        }

        internal Delivery[] Gate(SpacePose candidate, MothershipForecastTable defense, ref CombatAttackPose pose,
            CombatManeuverCurves curves)
        {
            var selected = new List<Delivery>(rows.Length);
            var outgoing = Geometry.Between(candidate, targetPose);
            SpacePose incomingSource = default;
            Geometry incoming = default;
            bool cachedIncoming = false;
            float weightedError = 0, constrained = 0;
            foreach (var row in rows)
            {
                var battery = row.Battery;
                if (!battery.Outgoing && (!cachedIncoming || battery.Pose.position != incomingSource.position ||
                    battery.Pose.angle != incomingSource.angle))
                {
                    incomingSource = battery.Pose;
                    incoming = Geometry.Between(incomingSource, candidate);
                    cachedIncoming = true;
                }
                var geometry = battery.Outgoing ? outgoing : incoming;
                var gun = battery.Gun;
                Vector2 muzzle = geometry.SourceCenter + SpaceTransform.Rotate(gun.muzzle, geometry.Facing);
                Vector2 axis = new Vector2(Mathf.Cos(geometry.Facing + gun.mount), Mathf.Sin(geometry.Facing + gun.mount));
                bool eligible = false;
                Vector2 delta = -muzzle;
                float distance = 0;
                foreach (var target in defense.AimTargets(battery.Outgoing, battery.Priority))
                {
                    delta = target.Offset - muzzle;
                    if (!Covers(gun.minRange, gun.range, row.CosHalfArc, axis, delta, out distance)) continue;
                    eligible = true;
                    var output = row.ByDistance[DistanceSlot(distance)];
                    if (output.Arrivals != 0)
                        selected.Add(new Delivery(gun.source, battery.Family, battery.Outgoing, battery.Shot.aim.Laser,
                            target.Part, delta, output));
                    break;
                }
                if (!row.Primary) continue;
                if (eligible)
                {
                    pose.firepower += gun.damageRate;
                    if (gun.ready) pose.readyOutput += gun.damageRate;
                }
                if (gun.arc < 120 && curves?.alignmentError != null)
                {
                    float error = Mathf.Abs(SpaceTransform.WrapAngle(Mathf.Atan2(delta.y, delta.x)
                        - geometry.Facing - gun.mount)) * Mathf.Rad2Deg;
                    weightedError += error * gun.damageRate;
                    constrained += gun.damageRate;
                }
            }
            pose.alignment = constrained > 0 ? weightedError / constrained : 0;
            if (offense)
            {
                pose.outgoingScore = curves?.outgoing?.Evaluate(pose.readyOutput) ?? 0;
                pose.alignmentScore = constrained > 0 ? curves.alignmentError.Evaluate(pose.alignment) : 0;
            }
            return selected.ToArray();
        }
    }
}
#endif

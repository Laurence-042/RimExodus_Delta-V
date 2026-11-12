using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class CombatEngagementEvaluation
    {
        internal sealed class Result
        {
            internal readonly List<Contribution> contributions = new List<Contribution>();
            internal float outgoing, incoming, travel, velocity, reverse, flank;
            internal bool uncertain;
            internal float alternativeScore = float.NegativeInfinity;
            internal bool holding;

            internal float Raw(CombatEngagementTerm term)
            {
                switch (term.metric)
                {
                    case CombatEngagementMetric.TravelSeconds: return travel;
                    case CombatEngagementMetric.VelocitySeconds: return velocity;
                    case CombatEngagementMetric.ReverseSeconds: return reverse;
                    case CombatEngagementMetric.Flanking: return flank;
                }
                float total = 0;
                foreach (var contribution in contributions)
                    if (contribution.side == term.side && term.weapons.Includes(contribution.weapon, contribution.family))
                        total += contribution.Value(term.metric);
                return total;
            }

            internal float Score(CombatEngagementSettings settings)
            {
                float score = 0;
                foreach (var term in settings.terms)
                    if (term.weight != 0) score += term.weight * term.curve.Evaluate(Raw(term));
                return score;
            }
        }

        internal struct Contribution
        {
            internal CompSpaceWeapon weapon;
            internal CombatWeaponFamily family;
            internal CombatEngagementSide side;
            internal float damage, guard, critical, uncertainty, armorProgress;

            internal float Value(CombatEngagementMetric metric) => metric switch
            {
                CombatEngagementMetric.StructureDamage => damage,
                CombatEngagementMetric.GuardDrain => guard,
                CombatEngagementMetric.CriticalDamage => critical,
                CombatEngagementMetric.ArmorProgress => armorProgress,
                CombatEngagementMetric.Uncertainty => uncertainty,
                _ => 0
            };
        }

        private sealed class Battery
        {
            internal SpacePose pose;
            internal CombatAttackWeapon gun;
            internal SpaceWeaponParameters shot;
            internal CombatWeaponFamily family;
            internal bool outgoing;
            internal float rate;
            internal List<(Thing thing, float value, Vector2 point)> parts;
        }

        private readonly UniverseManager universe;
        private readonly ShipBase own, enemy;
        private readonly SpacePose targetPose;
        private readonly List<Battery> batteries = new List<Battery>();
        internal readonly List<CombatAttackWeapon> primaryWeapons = new List<CombatAttackWeapon>();
        private readonly Vector2 ownCenter, enemyCenter;
        private readonly float ownRadius, enemyRadius;
        private readonly CombatDamageForecast ownShot, enemyShot;

        internal CombatEngagementEvaluation(UniverseManager universe, ShipBase own, ShipBase enemy,
            SpacePose targetPose, IEnumerable<ShipBase> enemies, bool offense, CombatWeaponFilter primary = null)
        {
            using var timing = universe.Performance.Measure(PerformanceStage.EngagementObservation);
            this.universe = universe; this.own = own; this.enemy = enemy; this.targetPose = targetPose;
            ownCenter = own.HullCenter; enemyCenter = enemy.HullCenter;
            ownRadius = ShipFireControl.HullRadius(own); enemyRadius = ShipFireControl.HullRadius(enemy);
            var ownObservation = new CombatDamageForecast.Observation(universe.FrameOf(own.Map), own);
            var enemyObservation = new CombatDamageForecast.Observation(universe.FrameOf(enemy.Map), enemy);
            ownShot = new CombatDamageForecast(ownObservation);
            enemyShot = new CombatDamageForecast(enemyObservation);
            var ownParts = new Dictionary<CombatPart, List<(Thing thing, float value, Vector2 point)>>();
            var enemyParts = new Dictionary<CombatPart, List<(Thing thing, float value, Vector2 point)>>();

            void Capture(ShipBase ship, ShipBase receiver, CombatDamageForecast.Observation observation,
                Dictionary<CombatPart, List<(Thing thing, float value, Vector2 point)>> parts, bool outgoing)
            {
                var pose = ShipRegistry.ShipPose(ship);
                pose.angle -= ship.FrameAngle;
                var control = universe.Controls.ExistingFor(ship)?.FireControl;
                var frame = universe.FrameOf(ship.Map);
                foreach (var (gun, shot) in ShipAutoManeuverControl.CaptureAttackWeapons(ship, frame))
                {
                    var weapon = gun.source;
                    var rule = control?.Strategy(ship).Fire(control.Facts(ship, frame).Context(receiver, weapon));
                    if (rule?.fireAction == CombatFireAction.Hold) continue;
                    var family = CombatWeaponFilter.FamilyOf(weapon, shot.aim.Projectile);
                    if (outgoing && (primary == null || primary.Includes(weapon, family))) primaryWeapons.Add(gun);
                    if (outgoing && !offense) continue;
                    var part = rule?.part ?? CombatPart.Automatic;
                    if (!parts.TryGetValue(part, out var targets))
                        parts[part] = targets = ShipFireControl.PartCandidates(observation.health.Keys, part);
                    batteries.Add(new Battery
                    {
                        pose = pose, shot = shot, parts = targets, outgoing = outgoing,
                        family = family,
                        gun = gun, rate = 60f / Mathf.Max(1, weapon.ShotInterval)
                    });
                }
            }

            Capture(own, enemy, enemyObservation, enemyParts, true);
            foreach (var other in enemies) Capture(other, own, ownObservation, ownParts, false);
        }

        internal static SpacePose At(SpacePose pose, float time)
        {
            if (time == 0 || pose.velocity.sqrMagnitude < 1e-12f && Mathf.Abs(pose.angularVelocity) < 1e-8f) return pose;
            var result = pose;
            result.position = OrbitSphere.Move(pose.position, pose.velocity * time);
            result.velocity = SpaceTransform.TransportVelocity(pose, result.position);
            Vector2 facing = SpaceTransform.TransportTangent(new Vector2(Mathf.Cos(pose.angle), Mathf.Sin(pose.angle)),
                pose.position, result.position);
            result.angle = SpaceTransform.WrapAngle(Mathf.Atan2(facing.y, facing.x) + pose.angularVelocity * time);
            return result;
        }

        private static bool Reachable(CombatAttackWeapon weapon, SpacePose source, SpacePose target, Vector2 localPoint)
        {
            Vector3 point = SpaceTransform.ShipLocalToUniverse(target, localPoint);
            Vector2 line = OrbitSphere.Offset(source.position, point);
            float bearing = Mathf.Atan2(line.y, line.x);
            Vector2 relativeVelocity = weapon.instant ? Vector2.zero : SpaceTransform.RotateInverse(
                SpaceTransform.TransportVelocity(SpaceTransform.RigidPointPose(target, localPoint, 0), source.position)
                - source.velocity, bearing);
            return weapon.Aim(line.magnitude, SpaceTransform.WrapAngle(source.angle - bearing), relativeVelocity, out _);
        }

        internal Result Evaluate(SpacePose candidate)
        {
            var performance = universe.Performance;
            performance.engagementCandidates++;
            using var timing = performance.Measure(PerformanceStage.EngagementScore);
            var result = new Result();
            foreach (var battery in batteries)
            {
                var source = battery.outgoing ? candidate : battery.pose;
                var target = battery.outgoing ? targetPose : candidate;
                var receiver = battery.outgoing ? enemy : own;
                var center = battery.outgoing ? enemyCenter : ownCenter;
                var hullMargin = (battery.outgoing ? enemyRadius : ownRadius) + 1;
                var forecast = battery.outgoing ? enemyShot : ownShot;
                if (!TryEstimate(battery, source, target, receiver, center, hullMargin, forecast, out var damage)) continue;

                float rate = battery.rate;
                var contribution = new Contribution
                {
                    weapon = battery.gun.source, family = battery.family,
                    side = battery.outgoing ? CombatEngagementSide.Outgoing : CombatEngagementSide.Incoming,
                    damage = damage.structure * rate, guard = damage.guard * rate,
                    critical = forecast.CriticalDamage * rate, armorProgress = damage.armorProgress * rate
                };
                if (damage.uncertain || damage.exhausted)
                {
                    contribution.uncertainty = Mathf.Max(damage.uncertainDamageUpper * rate,
                        Mathf.Max(contribution.damage, battery.gun.damageRate));
                    result.uncertain = true;
                }
                result.contributions.Add(contribution);
                if (!battery.outgoing) result.incoming += contribution.damage;
                else
                {
                    result.outgoing += contribution.damage;
                    if (damage.returnedPotential <= 0) continue;
                    result.contributions.Add(new Contribution { weapon = contribution.weapon, family = contribution.family,
                        side = CombatEngagementSide.Incoming, uncertainty = damage.returnedPotential * rate });
                    result.uncertain = true;
                }
            }
            return result;
        }

        private static bool TryEstimate(Battery battery, SpacePose source, SpacePose target, ShipBase receiver,
            Vector2 center, float hullMargin, CombatDamageForecast forecast, out CombatDamageEstimate damage)
        {
            damage = default;
            var gun = battery.gun;
            var weapon = gun.source;
            var shot = battery.shot;
            if (gun.OutsideInstantRange(OrbitSphere.ArcCells(source.position, target.position), hullMargin)) return false;
            Vector3 muzzle = SpaceTransform.ShipLocalToUniverse(source, gun.muzzle);
            Vector2 offset = SpaceTransform.RotateInverse(OrbitSphere.Offset(target.position, muzzle), target.angle);
            Vector2 from = center + offset;
            bool allPartsReachable = gun.instant && gun.arc >= 360
                && offset.magnitude - hullMargin >= gun.minRange && offset.magnitude + hullMargin <= gun.range;
            var part = ShipFireControl.ChoosePart(battery.parts, receiver, from,
                allPartsReachable ? null : (_, point) => Reachable(gun, source, target, point - center),
                query: forecast.Query);
            Vector2 endpoint = part == null ? center : new Vector2(part.DrawPos.x, part.DrawPos.z);
            Vector2 localTarget = endpoint - center;
            if (!allPartsReachable && !Reachable(gun, source, target, localTarget)) return false;

            Vector3 aimTarget = SpaceTransform.ShipLocalToUniverse(target, localTarget);
            Vector2 aimLine = OrbitSphere.Offset(muzzle, aimTarget);
            Vector2 direction = (endpoint - from).normalized;
            float flight = 0, speed = shot.aim.Speed, length = shot.aim.Range;
            if (!shot.aim.Laser)
            {
                var muzzlePose = SpaceTransform.RigidPointPose(source, gun.muzzle, 0);
                var targetPoint = SpaceTransform.RigidPointPose(target, localTarget, 0);
                Vector2 relative = SpaceTransform.TransportVelocity(targetPoint, muzzle) - muzzlePose.velocity;
                if (shot.aim.Guided) flight = aimLine.magnitude / Mathf.Max(.001f, speed);
                else if (SpaceTransform.TrySolveInterceptPlanar(aimLine, relative, speed, out flight))
                    aimLine += relative * flight;
                else return false;

                muzzlePose.velocity += aimLine.normalized * speed;
                muzzlePose.angularVelocity = 0;
                var impact = At(muzzlePose, flight);
                var receiverAt = At(target, flight);
                Vector2 contact = SpaceTransform.RelativeVelocityAtContact(impact, receiverAt, impact.position);
                Vector2 localVelocity = SpaceTransform.RotateInverse(SpaceTransform.TransportTangent(contact,
                    impact.position, receiverAt.position), receiverAt.angle);
                direction = localVelocity.normalized;
                float relativeSpeed = localVelocity.magnitude;
                length = shot.aim.Range * relativeSpeed / Mathf.Max(.001f, speed);
                speed = relativeSpeed;
                from = center + SpaceTransform.RotateInverse(
                    OrbitSphere.Offset(receiverAt.position, impact.position), receiverAt.angle) - localVelocity * flight;
            }

            float aim = SpaceTransform.WrapAngle(Mathf.Atan2(aimLine.y, aimLine.x) - source.angle);
            if (weapon.WeaponProps.casemate && !SpaceWeaponShot.HasClearCasemateAim(weapon, aim)) return false;

            forecast.ResetShot();
            int count = weapon.WeaponProps.projectilesPerShot;
            // Stable coverage samples prevent a bank of identical guns from sharing one miss.
            int sampleOffset = weapon.parent.thingIDNumber & 7;
            for (int i = 0; i < count; i++)
            {
                var pellet = forecast.Fire(from, SpaceTransform.Rotate(direction, SpaceWeaponShot.FanOffset(weapon.WeaponProps, i)),
                    length, shot.aim.Projectile, shot.mass / count, speed, weapon.WeaponProps.impactScale * shot.power,
                    shot.power, (((sampleOffset + i) & 7) + .5f) / 8);
                damage.structure += pellet.structure;
                damage.guard += pellet.guard;
                damage.armorProgress += pellet.armorProgress;
                damage.uncertainDamageUpper += pellet.uncertainDamageUpper;
                damage.returnedPotential += pellet.returnedPotential;
                damage.uncertain |= pellet.uncertain;
                damage.exhausted |= pellet.exhausted;
            }
            damage.uncertain |= !shot.aim.Laser;
            return true;
        }
    }
}

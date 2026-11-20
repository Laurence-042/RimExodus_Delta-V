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
            internal CompSpaceWeapon fixedAimWeapon;
            internal CombatTarget fixedAimTarget;
            internal Thing fixedAimPart;

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

        internal sealed class Battery
        {
            internal SpacePose pose;
            internal int rangeGroup;
            internal CombatAttackWeapon gun;
            internal SpaceWeaponParameters shot;
            internal CombatWeaponFamily family;
            internal bool outgoing;
            internal float rate;
            internal CombatPart priority;
        }

        private readonly UniverseManager universe;
        private readonly SpacePose targetPose;
        private readonly List<Battery> batteries = new List<Battery>();
        internal readonly List<CombatAttackWeapon> primaryWeapons = new List<CombatAttackWeapon>();
        private readonly Vector2 ownCenter, enemyCenter;
        private readonly ShipBase ownShip, enemyShip;
        private readonly bool offense;
        private float ownRadius, enemyRadius;
        private CombatDamageForecast ownShot, enemyShot;
        private readonly CombatWeaponOutputTable outputTable;

        internal CombatEngagementEvaluation(UniverseManager universe, ShipBase own, ShipBase enemy,
            SpacePose targetPose, IEnumerable<ShipBase> enemies, bool offense, CombatWeaponFilter primary = null,
            bool dualTable = false)
        {
            using var timing = universe.Performance.Measure(PerformanceStage.EngagementObservation);
            this.universe = universe; this.targetPose = targetPose;
            this.offense = offense;
            ownShip = own;
            enemyShip = enemy;
            ownCenter = own.HullCenter; enemyCenter = enemy.HullCenter;
            void Capture(ShipBase ship, ShipBase receiver, bool outgoing)
            {
                int rangeGroup = batteries.Count;
                var pose = ShipRegistry.ShipPose(ship);
                pose.angle -= ship.FrameAngle;
                var controller = universe.Controls.ExistingFor(ship);
                var control = controller?.FireControl;
                bool boarding = controller?.SuppressAutomaticSystems == true;
                var frame = universe.FrameOf(ship.Map);
                foreach (var (gun, shot) in ShipAutoManeuverControl.CaptureAttackWeapons(ship, frame))
                {
                    var weapon = gun.source;
                    var rule = control?.Strategy(ship).Fire(control.Facts(ship, frame).Context(receiver, weapon));
                    if (rule?.fireAction == CombatFireAction.Hold) continue;
                    var family = CombatWeaponFilter.FamilyOf(weapon, shot.aim.Projectile);
                    if (outgoing && (primary == null || primary.Includes(weapon, family))) primaryWeapons.Add(gun);
                    if (outgoing && !offense && !dualTable) continue;
                    batteries.Add(new Battery
                    {
                        pose = pose, rangeGroup = rangeGroup, shot = shot,
                        priority = boarding ? CombatPart.Weapons : rule?.part ?? CombatPart.Weapons,
                        outgoing = outgoing,
                        family = family,
                        gun = gun, rate = 60f / Mathf.Max(1, weapon.ShotInterval)
                    });
                }
            }

            Capture(own, enemy, true);
            foreach (var other in enemies) Capture(other, own, false);
            if (dualTable)
                outputTable = new CombatWeaponOutputTable(universe, own, enemy, targetPose, batteries,
                    primaryWeapons, offense);
        }

        internal Result Evaluate(SpacePose candidate, ref CombatAttackPose pose, CombatManeuverCurves curves)
        {
            var performance = universe.Performance;
            performance.engagementCandidates++;
            using var score = performance.Measure(PerformanceStage.EngagementScore);
            using var query = performance.Measure(PerformanceStage.EngagementTableQuery);
            return outputTable.Evaluate(candidate, ref pose, curves);
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
            if (ownShot == null)
            {
                ownRadius = ShipFireControl.HullRadius(ownShip);
                enemyRadius = ShipFireControl.HullRadius(enemyShip);
                ownShot = new CombatDamageForecast(universe.FrameOf(ownShip.Map), ownShip);
                enemyShot = new CombatDamageForecast(universe.FrameOf(enemyShip.Map), enemyShip);
            }
            var performance = universe.Performance;
            performance.engagementCandidates++;
            using var timing = performance.Measure(PerformanceStage.EngagementScore);
            var result = new Result();
            int lastRangeGroup = -1;
            float centerDistance = 0f;
            foreach (var battery in batteries)
            {
                if (battery.outgoing && !offense) continue;
                var source = battery.outgoing ? candidate : battery.pose;
                var target = battery.outgoing ? targetPose : candidate;
                // Capture appends each shooter's weapons consecutively with the same receiver.
                if (battery.gun.instant && battery.rangeGroup != lastRangeGroup)
                {
                    centerDistance = OrbitSphere.ArcCells(source.position, target.position);
                    lastRangeGroup = battery.rangeGroup;
                }
                var center = battery.outgoing ? enemyCenter : ownCenter;
                var hullMargin = (battery.outgoing ? enemyRadius : ownRadius) + 1;
                var forecast = battery.outgoing ? enemyShot : ownShot;
                if (!TryEstimate(battery, source, target, center, hullMargin, centerDistance, forecast, out var damage)) continue;

                float rate = battery.rate;
                var contribution = new Contribution
                {
                    weapon = battery.gun.source, family = battery.family,
                    side = battery.outgoing ? CombatEngagementSide.Outgoing : CombatEngagementSide.Incoming,
                    damage = (damage.structure + damage.foundation) * rate, guard = damage.guard * rate,
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

        internal Result Refine(SpacePose candidate, Result estimate)
        {
            Result result = Evaluate(candidate);
            result.travel = estimate.travel;
            result.velocity = estimate.velocity;
            result.reverse = estimate.reverse;
            result.flank = estimate.flank;
            result.fixedAimWeapon = estimate.fixedAimWeapon;
            result.fixedAimTarget = estimate.fixedAimTarget;
            result.fixedAimPart = estimate.fixedAimPart;
            return result;
        }

        private static bool TryEstimate(Battery battery, SpacePose source, SpacePose target,
            Vector2 center, float hullMargin, float centerDistance, CombatDamageForecast forecast, out CombatDamageEstimate damage)
        {
            damage = default;
            var gun = battery.gun;
            var weapon = gun.source;
            var shot = battery.shot;
            if (gun.OutsideInstantRange(centerDistance, hullMargin)) return false;
            Vector3 muzzle = SpaceTransform.ShipLocalToUniverse(source, gun.muzzle);
            Vector2 offset = SpaceTransform.RotateInverse(OrbitSphere.Offset(target.position, muzzle), target.angle);
            Vector2 muzzleInMap = center + offset;
            bool allPartsReachable = gun.instant && gun.arc >= 360
                && offset.magnitude - hullMargin >= gun.minRange && offset.magnitude + hullMargin <= gun.range;
            Vector2 from = default, direction = default;
            float length = 0, speed = 0;
            if (!forecast.Targets.TrySelect(battery.priority, TryAim)) return false;

            bool TryAim(Thing part)
            {
                Vector3 draw = part?.DrawPos ?? default;
                Vector2 endpoint = part == null ? center : new Vector2(draw.x, draw.z);
                Vector2 localTarget = endpoint - center;
                if (!allPartsReachable && !Reachable(gun, source, target, localTarget)) return false;
                from = muzzleInMap;
                Vector3 aimTarget = SpaceTransform.ShipLocalToUniverse(target, localTarget);
                Vector2 aimLine = OrbitSphere.Offset(muzzle, aimTarget);
                direction = (endpoint - from).normalized;
                float flight = 0;
                speed = shot.aim.Speed; length = shot.aim.Range;
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

                if (!weapon.WeaponProps.casemate) return true;
                float aim = SpaceTransform.WrapAngle(Mathf.Atan2(aimLine.y, aimLine.x) - source.angle);
                return SpaceWeaponShot.HasClearCasemateAim(weapon, aim);
            }

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
                damage.foundation += pellet.foundation;
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

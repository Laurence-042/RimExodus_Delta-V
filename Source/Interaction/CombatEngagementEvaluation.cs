using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class CombatEngagementEvaluation
    {
        internal sealed class Result
        {
            internal readonly List<Contribution> contributions = new List<Contribution>();
            internal readonly List<(float time, Contribution source, float fraction)> damageEvents = new List<(float, Contribution, float)>();
            internal float outgoing, incoming, travel, velocity, reverse, flank;
            internal bool uncertain;
            internal bool arrivalLowerBound;
            internal float alternativeScore = float.NegativeInfinity;
            internal bool holding;
            internal float Raw(CombatEngagementTerm term, float burstWindow) => term.metric switch
            {
                CombatEngagementMetric.TravelSeconds => travel,
                CombatEngagementMetric.VelocitySeconds => velocity,
                CombatEngagementMetric.ReverseSeconds => reverse,
                CombatEngagementMetric.Flanking => flank,
                CombatEngagementMetric.BurstFraction => Burst(term, burstWindow),
                _ => contributions.Where(c => c.side == term.side && term.weapons.Includes(c.weapon, c.family))
                    .Sum(c => c.Value(term.metric))
            };
            internal float Score(CombatEngagementSettings settings)
            {
                float score = 0;
                foreach (var term in settings.terms)
                {
                    if (term.weight == 0) continue;
                    float value = Raw(term, settings.burstSeconds);
                    score += term.weight * term.curve.Evaluate(value);
                }
                return score;
            }
            private float Burst(CombatEngagementTerm term, float window)
            {
                var events = damageEvents.Where(e => e.source.side == term.side
                    && term.weapons.Includes(e.source.weapon, e.source.family)).OrderBy(e => e.time).ToList();
                float total = 0, peak = 0; int first = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    total += events[i].fraction;
                    while (first < i && events[first].time < events[i].time - window) total -= events[first++].fraction;
                    peak = Mathf.Max(peak, total);
                }
                return peak;
            }
        }
        internal sealed class Contribution
        {
            internal CompSpaceWeapon weapon;
            internal CombatWeaponFamily family;
            internal CombatEngagementSide side;
            internal float damage, guard, capability, critical, burstFraction, uncertainty;
            internal float Value(CombatEngagementMetric metric) => metric switch
            {
                CombatEngagementMetric.StructureDamage => damage,
                CombatEngagementMetric.GuardDrain => guard,
                CombatEngagementMetric.CapabilityLoss => capability,
                CombatEngagementMetric.CriticalDamage => critical,
                CombatEngagementMetric.BurstFraction => burstFraction,
                CombatEngagementMetric.Uncertainty => uncertainty,
                _ => 0
            };
        }
        private sealed class Battery
        {
            internal ShipBase ship;
            internal SpacePose pose;
            internal CombatAttackWeapon gun;
            internal bool outgoing;
            internal Vector2 center;
            internal List<(Thing thing, float value, Vector2 point)> parts;
        }
        private struct ShotRay
        {
            internal float time, aim, flight, speed, length;
            internal int destruction;
            internal SpaceWeaponParameters shot;
            internal bool allowed, uncertain;
            internal Vector2 from, direction;
            internal Vector3 point;
        }
        private readonly UniverseManager universe;
        private readonly ShipBase own, enemy;
        private readonly CombatEngagementSettings settings;
        private readonly List<Battery> batteries = new List<Battery>();
        private readonly Dictionary<CompSpaceWeapon, List<SpaceWeaponParameters>> volleys = new Dictionary<CompSpaceWeapon, List<SpaceWeaponParameters>>();
        private readonly SpacePose targetPose;
        private readonly Vector2 ownCenter, enemyCenter;
        private readonly float ownRadius, enemyRadius;
        private readonly CombatDamageForecast.Observation ownObservation, enemyObservation;
        internal CombatEngagementEvaluation(UniverseManager universe, ShipBase own, ShipBase enemy,
            SpacePose targetPose, IEnumerable<ShipBase> enemies, CombatEngagementSettings settings, bool offense)
        {
            using var observationTiming = universe.Performance.Measure(PerformanceStage.EngagementObservation);
            this.universe = universe; this.own = own; this.enemy = enemy;
            this.targetPose = targetPose; this.settings = settings;
            ownCenter = own.HullCenter; enemyCenter = enemy.HullCenter;
            ownRadius = ShipFireControl.HullRadius(own); enemyRadius = ShipFireControl.HullRadius(enemy);
            ownObservation = new CombatDamageForecast.Observation(universe.FrameOf(own.Map), own);
            enemyObservation = new CombatDamageForecast.Observation(universe.FrameOf(enemy.Map), enemy);
            if (offense)
                foreach (var gun in ShipAutoManeuverControl.CaptureAttackWeapons(own, sustained: true))
                {
                    var control = universe.Controls.ExistingFor(own)?.FireControl;
                    var fireRule = control?.Strategy(own).Fire(control.Facts(own, universe.FrameOf(own.Map)).Context(enemy, gun.source));
                    if (fireRule?.fireAction == CombatFireAction.Hold) continue;
                    batteries.Add(new Battery { ship = own, center = ownCenter, gun = gun, outgoing = true,
                        parts = ShipFireControl.PartCandidates(enemyObservation.health.Keys, fireRule?.part ?? CombatPart.Automatic) });
                }
            foreach (var other in enemies)
            {
                var pose = ShipRegistry.ShipPose(other); pose.angle -= other.FrameAngle;
                var center = other.HullCenter;
                foreach (var gun in ShipAutoManeuverControl.CaptureAttackWeapons(other, sustained: true))
                {
                    var control = universe.Controls.ExistingFor(other)?.FireControl;
                    var fireRule = control?.Strategy(other).Fire(control.Facts(other, universe.FrameOf(other.Map)).Context(own, gun.source));
                    if (fireRule?.fireAction == CombatFireAction.Hold) continue;
                    batteries.Add(new Battery { ship = other, center = center, pose = pose, gun = gun,
                        parts = ShipFireControl.PartCandidates(ownObservation.health.Keys, fireRule?.part ?? CombatPart.Automatic) });
                }
            }
            // Readiness, heat, ammunition and shared firing energy are invariant during
            // the coarse pose pass. Capture once; refined trajectories use a fresh joint
            // ledger including damage, guards and line-shield power competition.
            new CombatWeaponForecast(batteries.Select(b => b.gun.source), new CombatResourceLedger())
                .Run(settings.windowSeconds, (w, p, t) => true, (w, p, t) =>
                {
                    if (!volleys.TryGetValue(w, out var shots)) volleys[w] = shots = new List<SpaceWeaponParameters>();
                    shots.Add(p);
                });
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
        private static bool SamePose(SpacePose a, SpacePose b) => a.direction.x == b.direction.x
            && a.direction.y == b.direction.y && a.direction.z == b.direction.z
            && a.angle == b.angle && a.angularVelocity == b.angularVelocity
            && a.velocity.x == b.velocity.x && a.velocity.y == b.velocity.y;
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
        // Allocate the reachability closure only when reselecting a constrained part,
        // not for every cache hit or every shot in the candidate's firing window.
        private static Thing ReachablePart(Battery battery, ShipBase receiver, Vector2 center, Vector2 from,
            CombatAttackWeapon weapon, SpacePose source, SpacePose target, ISet<Thing> removed, SpaceCollision.RayQuery query) =>
            ShipFireControl.ChoosePart(battery.parts, receiver, from,
                (part, point) => Reachable(weapon, source, target, point - center), removed, query);
        internal Result Evaluate(SpacePose candidate, bool coarse = false, Func<float, SpacePose> trajectory = null)
        {
            if (coarse) universe.Performance.engagementCoarse++;
            else universe.Performance.engagementFine++;
            using var evaluationTiming = universe.Performance.Measure(coarse ? PerformanceStage.EngagementCoarse : PerformanceStage.EngagementRefine);
            var result = new Result();
            if (batteries.Count == 0) return result;
            var resources = new CombatResourceLedger();
            var outgoing = new CombatDamageForecast(enemyObservation, resources);
            var incoming = new CombatDamageForecast(ownObservation, resources);
            var byWeapon = batteries.ToDictionary(b => b.gun.source);
            var contributions = new Dictionary<(CompSpaceWeapon, CombatWeaponFamily), Contribution>();
            var rays = new Dictionary<CompSpaceWeapon, ShotRay>();
            var pending = new Dictionary<int, List<(CompSpaceWeapon weapon, SpaceWeaponParameters shot, ShotRay ray)>>();
            var impactBuffers = new Stack<List<(CompSpaceWeapon weapon, SpaceWeaponParameters shot, ShotRay ray)>>();
            var rayFrames = new Dictionary<CompSpaceWeapon, (SpacePose source, SpacePose target)>();
            var selectedParts = new Dictionary<CompSpaceWeapon, (Thing part, float refresh)>();
            var traverseStates = new Dictionary<CompSpaceWeapon, CompSpaceWeaponTraverse.Forecast>();
            var trackingPoints = new Dictionary<CompSpaceWeapon, Vector3>();
            var otherPoses = new Dictionary<ShipBase, SpacePose>();
            float otherTime = -1;
            SpacePose OtherAt(ShipBase ship, SpacePose pose, float time)
            {
                if (time != otherTime) { otherTime = time; otherPoses.Clear(); }
                if (!otherPoses.TryGetValue(ship, out var state)) otherPoses[ship] = state = At(pose, time);
                return state;
            }
            float sampledTime = -1; SpacePose sampledOwn = candidate;
            SpacePose OwnAt(float time)
            {
                if (time != sampledTime) { sampledTime = time; sampledOwn = trajectory == null ? At(candidate, time) : trajectory(time); }
                return sampledOwn;
            }
            int sampleIndex = 0;
            bool GeometricAim(CompSpaceWeapon weapon, SpaceWeaponParameters shot, float time)
            {
                using var aimTiming = universe.Performance.Measure(PerformanceStage.EngagementAim);
                var battery = byWeapon[weapon];
                bool staticRay = trajectory == null && targetPose.velocity.sqrMagnitude < 1e-12f
                    && Mathf.Abs(targetPose.angularVelocity) < 1e-8f && battery.pose.velocity.sqrMagnitude < 1e-12f
                    && Mathf.Abs(battery.pose.angularVelocity) < 1e-8f && candidate.velocity.sqrMagnitude < 1e-12f
                    && Mathf.Abs(candidate.angularVelocity) < 1e-8f;
                float keyTime = staticRay ? 0 : time;
                bool sameShot = rays.TryGetValue(weapon, out var cached) && cached.destruction == resources.destroyed.Count
                    && cached.shot.aim.Projectile == shot.aim.Projectile && cached.shot.aim.Speed == shot.aim.Speed
                    && cached.shot.aim.MuzzleMap == shot.aim.MuzzleMap;
                if (sameShot && cached.time == keyTime) return cached.allowed;
                SpacePose ownAt = OwnAt(time);
                SpacePose source = battery.outgoing ? ownAt : OtherAt(battery.ship, battery.pose, time);
                SpacePose target = battery.outgoing ? OtherAt(enemy, targetPose, time) : ownAt;
                if (sameShot && rayFrames.TryGetValue(weapon, out var oldFrame)
                    && (shot.aim.Laser || trajectory == null)
                    && SamePose(source, oldFrame.source) && SamePose(target, oldFrame.target)) return cached.allowed;
                rayFrames[weapon] = (source, target);
                var gun = battery.gun;
                var current = new CombatAttackWeapon(gun.muzzle, gun.mount, gun.arc, shot.aim.Range,
                    shot.aim.MinRange, shot.aim.Speed, gun.damageRate, shot.aim.Laser || shot.aim.Guided, true, weapon);
                if (current.OutsideInstantRange(OrbitSphere.ArcCells(source.position, target.position),
                    (battery.outgoing ? enemyRadius : ownRadius) + 1))
                {
                    var line = OrbitSphere.Offset(source.position, target.position) - SpaceTransform.Rotate(current.muzzle, source.angle);
                    rays[weapon] = new ShotRay { time = keyTime, destruction = resources.destroyed.Count, shot = shot,
                        point = target.position, aim = SpaceTransform.WrapAngle(Mathf.Atan2(line.y, line.x) - source.angle) };
                    return false;
                }
                var receiver = battery.outgoing ? enemy : own;
                var receiverCenter = battery.outgoing ? enemyCenter : ownCenter;
                // Instantaneous rays require point positions, not the expensive
                // rigid-point velocity derivative. Both paths use SpaceTransform.
                var muzzlePosition = SpaceTransform.ShipLocalToUniverse(source, shot.aim.MuzzleMap - battery.center);
                Vector2 offset = SpaceTransform.RotateInverse(OrbitSphere.Offset(target.position, muzzlePosition), target.angle);
                Vector2 from = receiverCenter + offset;
                // A full-circle instantaneous weapon well inside both range edges
                // covers every part of the receiver's bounded hull. This exact
                // conservative gate avoids resolving each part's rigid pose just
                // to obtain the same reachability answer.
                float hullMargin = (battery.outgoing ? enemyRadius : ownRadius) + 1;
                bool allPartsReachable = current.instant && current.arc >= 360
                    && offset.magnitude - hullMargin >= current.minRange
                    && offset.magnitude + hullMargin <= current.range;
                if (!selectedParts.TryGetValue(weapon, out var selection) || time >= selection.refresh
                    || selection.part != null && resources.destroyed.Contains(selection.part))
                {
                    var query = battery.outgoing ? enemyObservation.query : ownObservation.query;
                    selection = (allPartsReachable
                        ? ShipFireControl.ChoosePart(battery.parts, receiver, from, null, resources.destroyed, query)
                        : ReachablePart(battery, receiver, receiverCenter, from, current, source, target, resources.destroyed, query),
                            coarse ? float.PositiveInfinity : time + 2);
                    selectedParts[weapon] = selection;
                }
                Vector2 endpoint = selection.part == null ? receiverCenter
                    : new Vector2(selection.part.DrawPos.x, selection.part.DrawPos.z);
                Vector2 localTarget = endpoint - receiverCenter;
                Vector3 aimTarget = SpaceTransform.ShipLocalToUniverse(target, localTarget);
                bool allowed = allPartsReachable || Reachable(current, source, target, localTarget);
                Vector2 direction = default;
                float aim = 0, flight = 0, impactSpeed = shot.aim.Speed, length = shot.aim.Range;
                Vector3 aimPoint = aimTarget;
                {
                    direction = (endpoint - from).normalized;
                    Vector2 aimLine = OrbitSphere.Offset(muzzlePosition, aimTarget);
                    SpacePose muzzlePose = default;
                    if (!shot.aim.Laser)
                    {
                        muzzlePose = SpaceTransform.RigidPointPose(source, shot.aim.MuzzleMap - battery.center, 0);
                        Vector2 relativeVelocity = SpaceTransform.TransportVelocity(
                            SpaceTransform.RigidPointPose(target, localTarget, 0), muzzlePosition) - muzzlePose.velocity;
                        if (shot.aim.Guided) flight = aimLine.magnitude / Mathf.Max(.001f, shot.aim.Speed);
                        else if (SpaceTransform.TrySolveInterceptPlanar(aimLine, relativeVelocity, shot.aim.Speed, out flight))
                            aimLine += relativeVelocity * flight;
                        else allowed = false;
                    }
                    aim = SpaceTransform.WrapAngle(Mathf.Atan2(aimLine.y, aimLine.x) - source.angle);
                    aimPoint = OrbitSphere.Move(muzzlePosition, aimLine);
                    if (!shot.aim.Laser && allowed)
                    {
                        // Ballistic motion inherits the muzzle velocity, just as Launch.
                        // Moving/turning contacts remain an estimate over this finite ray;
                        // both speed and direction use the common rigid contact contract.
                        var projectile = muzzlePose;
                        projectile.velocity += aimLine.normalized * shot.aim.Speed;
                        projectile.angularVelocity = 0;
                        projectile = At(projectile, flight);
                        var receiverAt = battery.outgoing ? At(targetPose, time + flight) : OwnAt(time + flight);
                        Vector2 contactVelocity = SpaceTransform.RelativeVelocityAtContact(projectile, receiverAt, projectile.position);
                        Vector2 localVelocity = SpaceTransform.RotateInverse(SpaceTransform.TransportTangent(contactVelocity,
                            projectile.position, receiverAt.position), receiverAt.angle);
                        impactSpeed = localVelocity.magnitude;
                        direction = localVelocity.normalized;
                        Vector2 impactPoint = receiverCenter + SpaceTransform.RotateInverse(
                            OrbitSphere.Offset(receiverAt.position, projectile.position), receiverAt.angle);
                        from = impactPoint - localVelocity * flight;
                        length = shot.aim.Range * impactSpeed / Mathf.Max(.001f, shot.aim.Speed);
                    }
                }
                rays[weapon] = new ShotRay { time = keyTime, destruction = resources.destroyed.Count, shot = shot,
                    allowed = allowed, from = from, direction = direction, aim = aim, flight = flight, speed = impactSpeed,
                    length = length, point = aimPoint, uncertain = !shot.aim.Laser };
                return allowed;
            }
            CompSpaceWeaponTraverse.Forecast Traverse(CompSpaceWeapon weapon)
            {
                if (!traverseStates.TryGetValue(weapon, out var state))
                    traverseStates[weapon] = state = new CompSpaceWeaponTraverse.Forecast(weapon.Component<CompSpaceWeaponTraverse>());
                return state;
            }
            void Track(CompSpaceWeapon weapon, SpaceWeaponParameters shot, float time)
            {
                int tick = Mathf.RoundToInt(time * 60);
                if (tick % ShipFireControl.AutomaticAimRefreshTicks == 0 || !trackingPoints.ContainsKey(weapon))
                {
                    GeometricAim(weapon, shot, time);
                    trackingPoints[weapon] = rays[weapon].point;
                }
                var battery = byWeapon[weapon];
                var source = battery.outgoing ? OwnAt(time) : OtherAt(battery.ship, battery.pose, time);
                var muzzle = SpaceTransform.ShipLocalToUniverse(source, shot.aim.MuzzleMap - battery.center);
                var line = OrbitSphere.Offset(muzzle, trackingPoints[weapon]);
                float desired = weapon.ClampAim(SpaceTransform.WrapAngle(Mathf.Atan2(line.y, line.x) - source.angle));
                Traverse(weapon).AimAt(desired, tick, resources.Powered(weapon.parent.TryGetComp<RimWorld.CompPowerTrader>()));
            }
            bool Aim(CompSpaceWeapon weapon, SpaceWeaponParameters shot, float time)
            {
                bool allowed = GeometricAim(weapon, shot, time);
                var traverse = weapon.Component<CompSpaceWeaponTraverse>();
                if (coarse || traverse == null) return allowed;
                var state = Traverse(weapon);
                float desired = rays[weapon].aim;
                // A ready-shot command replaces the cached tracking command within
                // this tick; it does not earn a second mechanical rotation step.
                state.AimAt(weapon.ClampAim(desired), Mathf.RoundToInt(time * 60),
                    resources.Powered(weapon.parent.TryGetComp<RimWorld.CompPowerTrader>()));
                return allowed && SpaceWeaponGeometry.WithinFireDeviation(desired, state.Angle, weapon.fireDeviationDeg);
            }
            float pulseMultiplier = 1;
            void ApplyImpact(CompSpaceWeapon weapon, SpaceWeaponParameters shot, float time, ShotRay ray)
            {
                var battery = byWeapon[weapon];
                var forecast = battery.outgoing ? outgoing : incoming;
                // Deterministic coverage quantiles never consume the simulation's random stream.
                float quantile = (sampleIndex++ % 8 + .5f) / 8;
                float before = forecast.CapabilityLoss;
                float criticalBefore = forecast.CriticalDamage;
                var damage = forecast.Fire(ray.from, ray.direction, ray.length, shot.aim.Projectile,
                    shot.mass, ray.speed, weapon.WeaponProps.impactScale * shot.power * pulseMultiplier,
                    shot.power * pulseMultiplier, quantile);
                damage.uncertain |= ray.uncertain;
                var family = CombatWeaponFilter.FamilyOf(weapon, shot.aim.Projectile);
                if (!contributions.TryGetValue((weapon, family), out var contribution))
                {
                    contributions[(weapon, family)] = contribution = new Contribution { weapon = weapon, family = family,
                        side = battery.outgoing ? CombatEngagementSide.Outgoing : CombatEngagementSide.Incoming };
                    result.contributions.Add(contribution);
                }
                contribution.damage += damage.structure;
                contribution.guard += damage.guard;
                contribution.capability += Mathf.Max(0, forecast.CapabilityLoss - before);
                contribution.critical += Mathf.Max(0, forecast.CriticalDamage - criticalBefore);
                // Unknown effects retain an explicit risk potential, never a zero-threat weak side.
                if (damage.uncertain || damage.exhausted)
                    contribution.uncertainty += Mathf.Max(damage.uncertainDamageUpper,
                        Mathf.Max(damage.structure, battery.gun.damageRate * weapon.ShotInterval / 60));
                result.uncertain |= damage.uncertain || damage.exhausted;
                if (battery.outgoing)
                {
                    result.outgoing += damage.structure;
                    if (damage.returnedPotential > 0)
                    {
                        result.contributions.Add(new Contribution { weapon = weapon, family = family, side = CombatEngagementSide.Incoming,
                            uncertainty = damage.returnedPotential });
                        result.uncertain = true;
                    }
                }
                else
                {
                    result.incoming += damage.structure;
                }
                if (damage.structure > 0)
                    result.damageEvents.Add((time, contribution, damage.structure / Mathf.Max(1, forecast.TotalHealth)));
            }
            void Launch(CompSpaceWeapon weapon, SpaceWeaponParameters shot, float time)
            {
                var ray = rays[weapon];
                int arrival = Mathf.RoundToInt(time * 60) + Mathf.CeilToInt(ray.flight * 60 - .0001f);
                if (arrival >= Mathf.CeilToInt(settings.windowSeconds * 60)) return;
                if (!pending.TryGetValue(arrival, out var impacts)) pending[arrival] = impacts = impactBuffers.Count > 0
                    ? impactBuffers.Pop() : new List<(CompSpaceWeapon, SpaceWeaponParameters, ShotRay)>();
                impacts.Add((weapon, shot, ray));
            }
            void ResolveImpacts(float time)
            {
                int tick = Mathf.RoundToInt(time * 60);
                if (!pending.TryGetValue(tick, out var impacts)) return;
                foreach (var impact in impacts) ApplyImpact(impact.weapon, impact.shot, time, impact.ray);
                pending.Remove(tick);
                impacts.Clear(); impactBuffers.Push(impacts);
            }
            if (coarse)
            {
                // Bounded pulse groups retain multiple contacts across a projectile's
                // footprint. Grouping is a ranking approximation, never a final score.
                const int groups = 8;
                for (int group = 0; group < groups; group++)
                foreach (var battery in batteries)
                {
                    if (!volleys.TryGetValue(battery.gun.source, out var shots) || shots.Count == 0) continue;
                    int first = group * shots.Count / groups, end = (group + 1) * shots.Count / groups;
                    if (end <= first) continue;
                    pulseMultiplier = end - first;
                    var shot = shots[first];
                    float time = settings.windowSeconds * group / groups;
                    if (Aim(battery.gun.source, shot, time) && time + rays[battery.gun.source].flight < settings.windowSeconds)
                        ApplyImpact(battery.gun.source, shot, time + rays[battery.gun.source].flight, rays[battery.gun.source]);
                }
            }
            else
            {
                var schedule = new CombatWeaponForecast(batteries.Select(b => b.gun.source), resources);
                schedule.Run(settings.windowSeconds, Aim, Launch, time =>
                { outgoing.AdvanceTo(time); incoming.AdvanceTo(time); }, afterTick: ResolveImpacts, track: Track);
            }
            return result;
        }
    }
}

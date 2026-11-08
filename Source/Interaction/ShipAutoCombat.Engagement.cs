using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DeltaV
{
    internal sealed partial class ShipAutoManeuverControl
    {
        private float engagementLocalBearing;
        private CombatEngagementEvaluation.Result engagementResult;
        private sealed class EngagementCandidate
        {
            internal float phase, score;
            internal CombatAttackPose pose;
            internal SpacePose state;
            internal CombatEngagementEvaluation.Result value;
        }

        private List<EngagementCandidate> EngagementCandidates(ShipBase ship, ShipBase enemy, SpacePose self,
            SpacePose target, DriveInfo drive, CombatStrategyRule rule, CombatTacticSettings settings,
            float? alliedPhase)
        {
            universe.Performance.engagementStarted++;
            long workStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var enemies = universe.objects.SelectMany(frame => ShipFireControl.Candidates(ship,
                universe.FrameOf(ship.Map), frame)).OrderBy(s => OrbitSphere.ArcCells(self.position,
                    ShipRegistry.ShipPose(s).position)).Take(64).ToList();
            var defender = target; defender.angle -= enemy.FrameAngle;
            var evaluator = new CombatEngagementEvaluation(universe, ship, enemy, defender, enemies, rule.engagement, offenseEnabled);
            var weapons = CaptureAttackWeapons(ship, settings.primaryWeapons, true);
            Vector2 offset = OrbitSphere.Offset(target.position, self.position);
            float current = Mathf.Atan2(offset.y, offset.x);
            float radius = Mathf.Max(1, offset.magnitude);
            Vector2 relative = SpaceTransform.TransportVelocity(self, target.position) - target.velocity;
            float tangent = Vector2.Dot(relative, new Vector2(-Mathf.Sin(current), Mathf.Cos(current)));
            var phases = new List<float> { current };
            void Phase(float value)
            {
                value = SpaceTransform.WrapAngle(value);
                if (phases.All(p => Mathf.Abs(SpaceTransform.WrapAngle(p - value)) > .001f)) phases.Add(value);
            }
            bool freeReposition = settings.behavior == CombatTactic.Automatic && settings.bearing == CombatBearing.Free;
            if (freeReposition)
            {
                if (reposition.initialized) Phase(target.angle + engagementLocalBearing);
                Phase(current + tangent * 4 / radius);
                if (alliedPhase.HasValue) Phase(alliedPhase.Value);
                for (int i = 0; i < 12; i++) Phase(target.angle + i * Mathf.PI / 6);
            }
            else if (settings.bearing != CombatBearing.Free)
            {
                phases.Clear();
                Phase((settings.bearing == CombatBearing.Enemy ? target.angle : 0) + settings.bearingDegrees * Mathf.Deg2Rad);
            }
            else if (settings.behavior == CombatTactic.HoldRange && reposition.initialized)
            { phases.Clear(); Phase(reposition.PhaseAt(target.position)); }
            float strongest = 0, strongestAngle = 0;
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI / 2;
                float a = CombatAttackPose.Acceleration(drive, Vector2.right, angle);
                if (a > strongest) { strongest = a; strongestAngle = angle; }
            }
            float separation = CombatWeaponThreat.HullHalfExtents(ship).magnitude
                + CombatWeaponThreat.HullHalfExtents(enemy).magnitude + 2;
            var distances = new List<float>();
            void Distance(float value)
            {
                value = Mathf.Max(separation, value);
                if (distances.All(d => Mathf.Abs(d - value) > 1)) distances.Add(value);
            }
            if (settings.distance > 0) distances.Add(settings.distance);
            else
            {
                Distance(radius);
                var ranges = weapons.Select(w => Mathf.Max(w.minRange + 2, w.range * .85f - w.muzzle.magnitude)).OrderBy(x => x).ToList();
                if (ranges.Count > 0) { Distance(ranges[ranges.Count / 2]); Distance(ranges[0]); }
                if (rule.maneuverCurves?.distance?.points?.Count > 0)
                {
                    var curve = rule.maneuverCurves.distance;
                    float preferred = curve.points.OrderByDescending(p => p.y).First().x;
                    // An increasing extrapolated curve must be able to request opening
                    // beyond its last knot. Bound the extra search by the physical
                    // accelerate/brake reach over this rule's observation window.
                    if (curve.extendRight && curve.points.Count >= 2
                        && curve.points[curve.points.Count - 1].y > curve.points[curve.points.Count - 2].y)
                    {
                        float opening = radius + .25f * strongest * settings.movement
                            * rule.engagement.windowSeconds * rule.engagement.windowSeconds;
                        if (curve.Evaluate(opening) > curve.Evaluate(preferred)) preferred = opening;
                    }
                    if (distances.Count >= 3) distances.RemoveAt(distances.Count - 1);
                    Distance(preferred);
                }
            }
            float alpha = Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            var ownShape = new SpaceCollision.HullShape(ship);
            var obstacles = universe.objects.SelectMany(frame => frame.Ships).Where(s => s.IsValid && s.Identity != ship.Identity)
                .Select(s => (shape: new SpaceCollision.HullShape(s), pose: ShipRegistry.ShipPose(s), frameAngle: s.FrameAngle)).ToList();
            var candidates = new List<EngagementCandidate>();
            var legacyThreats = new List<CombatWeaponThreat>();
            if (rule.maneuverCurves?.incoming != null || rule.maneuverCurves?.projectileSeconds != null)
                foreach (var other in enemies) CombatWeaponThreat.Capture(other, universe.FrameOf(other.Map), 3, legacyThreats);
            float LegacyScore(ref CombatAttackPose pose, SpacePose state, float? reachedDistance = null)
            {
                var curves = rule.maneuverCurves;
                pose.distanceScore = curves?.distance?.Evaluate(reachedDistance ?? pose.distance) ?? 0;
                float rate = 0, impact = 8;
                foreach (var threat in legacyThreats)
                {
                    threat.Score(state.position, state.velocity, ownShape.half, state.angle, Vector2.zero, out float dps, out float seconds);
                    rate += dps; if (dps > 0) impact = Mathf.Min(impact, seconds);
                }
                pose.incomingScore = curves?.incoming?.Evaluate(rate) ?? 0;
                pose.timeScore = curves?.projectileSeconds?.Evaluate(impact) ?? 0;
                pose.overlapScore = 0;
                if (curves?.hullOverlap != null)
                    foreach (var other in enemies)
                    {
                        var otherPose = CombatEngagementEvaluation.At(ShipRegistry.ShipPose(other), 3);
                        pose.overlapScore += curves.hullOverlap.Evaluate(Mathf.Clamp01(1
                            - OrbitSphere.ArcCells(state.position, otherPose.position)
                            / (ShipFireControl.HullRadius(ship) + ShipFireControl.HullRadius(other) + 8)));
                    }
                return pose.distanceScore + pose.outgoingScore + pose.alignmentScore + pose.incomingScore + pose.timeScore + pose.overlapScore;
            }
            foreach (float phase in phases)
            {
                float delta = SpaceTransform.WrapAngle(phase - current);
                if (freeReposition && (settings.direction == CombatTurn.Clockwise && delta > .001f
                    || settings.direction == CombatTurn.Counterclockwise && delta < -.001f)) continue;
                var position = OrbitSphere.Move(target.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * radius);
                var line = OrbitSphere.Offset(position, target.position);
                float lineAngle = Mathf.Atan2(line.y, line.x);
                var headings = new List<float>();
                if (settings.facing != CombatFacing.Automatic)
                    headings.Add((settings.facing == CombatFacing.Target ? 0 : settings.facingDegrees * Mathf.Deg2Rad) - ship.FrameAngle);
                else
                {
                    headings.Add(SpaceTransform.WrapAngle(self.angle - lineAngle));
                    foreach (var gun in weapons.OrderByDescending(w => w.damageRate))
                    {
                        float heading = gun.LeadHeading(radius, Vector2.zero);
                        if (headings.All(h => Mathf.Abs(SpaceTransform.WrapAngle(h - heading)) > .1f)) headings.Add(heading);
                        if (headings.Count >= 3) break;
                    }
                    if (headings.Count < 4 && headings.All(h => Mathf.Abs(SpaceTransform.WrapAngle(h - strongestAngle)) > .1f))
                        headings.Add(strongestAngle);
                }
                foreach (float distance in distances)
                foreach (float heading in headings.Take(4))
                {
                    position = OrbitSphere.Move(target.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * distance);
                    line = OrbitSphere.Offset(position, target.position); lineAngle = Mathf.Atan2(line.y, line.x);
                    float angle = SpaceTransform.WrapAngle(lineAngle + heading);
                    bool overlaps = obstacles.Any(o => SpaceCollision.HullsOverlap(ownShape, position, angle, o.shape,
                        new SpacePose(o.pose.position, o.pose.angle - o.frameAngle)));
                    if (overlaps) continue;
                    var state = new SpacePose(position, angle)
                    {
                        velocity = SpaceTransform.TransportVelocity(target, position)
                            + SpaceTransform.TransportTangent(new Vector2(-Mathf.Sin(phase), Mathf.Cos(phase))
                                * (target.angularVelocity * distance), target.position, position),
                        angularVelocity = target.angularVelocity
                    };
                    var value = evaluator.Evaluate(state, true);
                    float travel = Mathf.Abs(delta) * distance + Mathf.Abs(distance - radius);
                    value.travel = travel < 1 ? 0 : Mathf.Sqrt(4 * travel / Mathf.Max(.001f, strongest * settings.movement))
                        + Mathf.Sqrt(4 * Mathf.Abs(SpaceTransform.WrapAngle(angle - self.angle)) / Mathf.Max(.001f, alpha));
                    float required = target.angularVelocity * distance;
                    if (Mathf.Abs(delta) * distance > 1)
                        required += Mathf.Sign(delta) * CombatFlightPlanner.CruiseSpeed(drive, heading, distance,
                            delta >= 0 ? 1 : -1, settings.movement, settings.facing == CombatFacing.Automatic);
                    Vector2 requestedVelocity = new Vector2(-Mathf.Sin(current), Mathf.Cos(current)) * required;
                    value.velocity = (requestedVelocity - relative).magnitude / Mathf.Max(.001f, strongest);
                    value.reverse = required * tangent < 0 ? Mathf.Abs(tangent) / Mathf.Max(.001f,
                        drive.PreviewBraking(SpaceTransform.RotateInverse(SpaceTransform.TransportTangent(
                            new Vector2(-Mathf.Sin(current), Mathf.Cos(current)) * Mathf.Sign(tangent), target.position, self.position), self.angle))) : 0;
                    value.flank = alliedPhase.HasValue ? 1 - Mathf.Abs(SpaceTransform.WrapAngle(phase - alliedPhase.Value)) / Mathf.PI : 0;
                    var pose = new CombatAttackPose { behavior = settings.behavior, distance = distance, heading = heading,
                        freeThrustFacing = settings.facing == CombatFacing.Automatic };
                    pose.EvaluateFire(weapons, Vector2.zero, offenseEnabled, rule.maneuverCurves);
                    float extra = LegacyScore(ref pose, state);
                    candidates.Add(new EngagementCandidate { phase = phase, pose = pose, state = state,
                        value = value, score = value.Score(rule.engagement) + extra });
                }
            }
            // Refine attitude variants as well as position: the shortest initial turn
            // need not provide the best firing approach over the complete trajectory.
            // Two leading phases × two attitudes, plus current/continuation, is at most six.
            var finalists = freeReposition
                ? candidates.GroupBy(c => c.phase).OrderByDescending(g => g.Max(c => c.score)).Take(2)
                    .SelectMany(g => g.OrderByDescending(c => c.score).Take(2)).ToList()
                : candidates.OrderByDescending(c => c.score).Take(4).ToList();
            foreach (float phase in phases.Take(freeReposition ? 2 : 0))
            {
                var keep = candidates.Where(c => Mathf.Abs(SpaceTransform.WrapAngle(c.phase - phase)) < .001f)
                    .OrderByDescending(c => c.score).FirstOrDefault();
                if (keep != null && !finalists.Contains(keep)) finalists.Add(keep);
            }
            foreach (var candidate in finalists)
            {
                var action = EngagementAction(self, target, candidate.pose, candidate.phase, drive, settings);
                var reference = CombatFlightPlanner.Plan(self, target, action, drive, Verse.Find.TickManager.TicksGame);
                reference.localAnchor = SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter);
                if (!RouteClear(ship, reference, out _, out _)) { candidate.score = float.NegativeInfinity; continue; }
                var trajectory = EngagementReference(self, target, candidate.pose, candidate.phase, drive, settings,
                    rule.engagement.windowSeconds, out float arrival, out bool arrived);
                var value = evaluator.Evaluate(candidate.state, trajectory: trajectory);
                value.travel = arrival; value.velocity = candidate.value.velocity;
                value.arrivalLowerBound = !arrived;
                value.reverse = candidate.value.reverse; value.flank = candidate.value.flank;
                candidate.value = value;
                float reachedDistance = OrbitSphere.ArcCells(trajectory(rule.engagement.windowSeconds).position,
                    CombatEngagementEvaluation.At(target, rule.engagement.windowSeconds).position);
                candidate.score = value.Score(rule.engagement) + LegacyScore(ref candidate.pose, candidate.state, reachedDistance);
                candidate.pose.preference = candidate.score;
            }
            var result = finalists.OrderBy(c => Mathf.Abs(SpaceTransform.WrapAngle(c.phase - current)) < .001f ? 0 : 1).ToList();
            universe.Performance.engagementCompleted++;
            universe.Performance.engagementElapsed += System.Diagnostics.Stopwatch.GetTimestamp() - workStart;
            return result;
        }

        private Func<float, SpacePose> EngagementReference(SpacePose self, SpacePose target,
            CombatAttackPose pose, float phase, DriveInfo drive, CombatTacticSettings settings, float window, out float arrival, out bool arrived)
        {
            var segments = new List<CombatTrajectoryIntent>();
            var state = self; float localPhase = SpaceTransform.WrapAngle(phase - target.angle);
            arrival = window;
            arrived = false;
            for (int i = 0; i <= Mathf.CeilToInt(window); i++)
            {
                var other = CombatEngagementEvaluation.At(target, i);
                bool attached = settings.bearing == CombatBearing.Enemy
                    || settings.bearing == CombatBearing.Free && settings.behavior == CombatTactic.Automatic;
                float goal = attached ? SpaceTransform.WrapAngle(other.angle + localPhase) : phase;
                Vector2 offset = OrbitSphere.Offset(other.position, state.position);
                if (!arrived && Mathf.Abs(offset.magnitude - pose.distance) < 5
                    && Mathf.Abs(SpaceTransform.WrapAngle(Mathf.Atan2(offset.y, offset.x) - goal)) < .1f)
                { arrival = i; arrived = true; }
                var action = EngagementAction(state, other, pose, goal, drive, settings);
                var reference = CombatFlightPlanner.Plan(state, other, action, drive, 0);
                segments.Add(reference);
                reference.Sample(1, out var position, out var velocity, out float angle, out float omega);
                state = new SpacePose(position, angle) { velocity = velocity, angularVelocity = omega };
            }
            return time =>
            {
                int index = Mathf.Clamp(Mathf.FloorToInt(time), 0, segments.Count - 1);
                segments[index].Sample(time - index, out var position, out var velocity, out float angle, out float omega);
                return new SpacePose(position, angle) { velocity = velocity, angularVelocity = omega };
            };
        }

        private CombatManeuverIntent EngagementAction(SpacePose self, SpacePose target,
            CombatAttackPose pose, float phase, DriveInfo drive, CombatTacticSettings settings, bool preserveCircleHeading = false)
        {
            var action = new CombatManeuverIntent { behavior = pose.behavior, distance = pose.distance,
                heading = pose.heading, bearing = phase, bearingRate = settings.bearing == CombatBearing.Enemy
                    || settings.bearing == CombatBearing.Free && settings.behavior == CombatTactic.Automatic ? target.angularVelocity : 0 };
            bool circle = settings.behavior == CombatTactic.Circle && settings.bearing == CombatBearing.Free;
            if (circle)
            {
                action.bearing = null;
                action.tangentSpeed = turnDirection * CombatFlightPlanner.CruiseSpeed(drive, pose.heading, pose.distance,
                    turnDirection, settings.movement, pose.freeThrustFacing);
            }
            if (settings.facing != CombatFacing.Automatic || circle && preserveCircleHeading) return action;
            Vector2 offset = OrbitSphere.Offset(target.position, self.position);
            float current = Mathf.Atan2(offset.y, offset.x);
            float delta = SpaceTransform.WrapAngle(phase - current);
            if (!circle && Mathf.Abs(delta) * offset.magnitude < 8 && Mathf.Abs(offset.magnitude - pose.distance) < 8) return action;
            Vector2 thrust = new Vector2(offset.magnitude - pose.distance,
                -Mathf.Clamp(delta * offset.magnitude, -pose.distance, pose.distance));
            if (circle)
            {
                Vector2 relative = SpaceTransform.TransportVelocity(self, target.position) - target.velocity;
                float radial = Vector2.Dot(relative, offset.normalized);
                float speed = Vector2.Dot(relative, new Vector2(-offset.y, offset.x).normalized);
                thrust = new Vector2(speed * speed / Mathf.Max(1, offset.magnitude) + radial * .7f
                    + (offset.magnitude - pose.distance) * .2f, -(action.tangentSpeed - speed) * .5f);
            }
            Vector2 toward = OrbitSphere.Offset(self.position, target.position);
            float actualHeading = SpaceTransform.WrapAngle(self.angle - Mathf.Atan2(toward.y, toward.x));
            float thrustAngle = Mathf.Atan2(thrust.y, thrust.x), best = -1, bestTurn = float.PositiveInfinity;
            float alpha = Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            for (int i = 0; i < (circle ? 5 : 4); i++)
            {
                float heading = i == 4 ? actualHeading : SpaceTransform.WrapAngle(thrustAngle + i * Mathf.PI / 2);
                float a = CombatAttackPose.Acceleration(drive, thrust.normalized, heading);
                float turn = Mathf.Abs(SpaceTransform.WrapAngle(heading - actualHeading));
                // Compare available impulse over the next flight-reference horizon.
                // A slow-turning hull must not abandon usable present thrust for an
                // axis it cannot bring to bear during that horizon.
                float turnTime = Mathf.Sqrt(4 * turn / Mathf.Max(.001f, alpha));
                float usable = circle ? a * Mathf.Max(0, 1 - turnTime / 4) : a;
                // Transfers compare complete attitude candidates above. Retain that
                // candidate's orientation preference when equally strong axes exist.
                float tieTurn = circle ? turn : Mathf.Abs(SpaceTransform.WrapAngle(heading - pose.heading));
                if (usable > best + .0001f || Mathf.Abs(usable - best) <= .0001f && tieTurn < bestTurn)
                { best = usable; bestTurn = tieTurn; action.heading = heading; }
            }
            return action;
        }
    }
}

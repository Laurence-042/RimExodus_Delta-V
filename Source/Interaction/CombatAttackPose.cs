using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeltaV
{
    // Positions/velocities here are in the existing tangent basis, rotated so the
    // target bearing is +X. This is a transient query, never a second world state.
    internal readonly struct CombatAttackWeapon
    {
        internal readonly Vector2 muzzle;
        internal readonly float mount, arc, range, minRange, speed, damageRate;
        internal readonly bool instant, ready;
        internal readonly CompSpaceWeapon source;
        internal CombatAttackWeapon(Vector2 muzzle, float mount, float arc,
            float range, float minRange, float speed, float damageRate, bool instant, bool ready,
            CompSpaceWeapon source = null)
        {
            this.muzzle = muzzle; this.mount = mount; this.arc = arc;
            this.range = range; this.minRange = minRange; this.speed = speed;
            this.damageRate = damageRate; this.instant = instant; this.ready = ready;
            this.source = source;
        }

        internal bool Aim(float distance, float heading, Vector2 relativeVelocity,
            out float error)
        {
            Vector2 delta = Vector2.right * distance - SpaceTransform.Rotate(muzzle, heading);
            error = 180f;
            if (!instant)
            {
                if (!SpaceTransform.TrySolveInterceptPlanar(delta, relativeVelocity, speed, out float time))
                    return false;
                delta += relativeVelocity * time;
            }
            error = Mathf.Abs(SpaceTransform.WrapAngle(Mathf.Atan2(delta.y, delta.x)
                - heading - mount)) * Mathf.Rad2Deg;
            return delta.magnitude >= minRange && delta.magnitude <= range
                && error <= arc * 0.5f + 0.05f;
        }

        internal float LeadHeading(float distance, Vector2 relativeVelocity)
        {
            float angle = -mount;
            for (int i = 0; i < 4; i++)
            {
                Vector2 delta = Vector2.right * distance - SpaceTransform.Rotate(muzzle, angle);
                if (!instant && SpaceTransform.TrySolveInterceptPlanar(delta, relativeVelocity, speed, out float t))
                    delta += relativeVelocity * t;
                angle = SpaceTransform.WrapAngle(Mathf.Atan2(delta.y, delta.x) - mount);
            }
            return angle;
        }
    }

    internal struct CombatAttackPose
    {
        internal float distance, heading, firepower, readyOutput, alignment, mobility, preference;
        internal float distanceScore, outgoingScore, incomingScore, timeScore, overlapScore, alignmentScore;
        internal CombatTactic behavior;

        internal static int Turn(CombatTurn setting, int previous, float tangentSpeed, int seed)
        {
            if (setting == CombatTurn.Clockwise) return -1;
            if (setting == CombatTurn.Counterclockwise) return 1;
            if (previous != 0) return previous;
            return Mathf.Abs(tangentSpeed) > 0.5f ? (tangentSpeed > 0f ? 1 : -1)
                : (seed & 1) == 0 ? 1 : -1;
        }

        internal static CombatTactic Behavior(CombatTactic requested,
            IReadOnlyList<CombatAttackWeapon> weapons, bool offense)
        {
            if (requested != CombatTactic.Automatic) return requested;
            return CombatTactic.Automatic;
        }

        internal static CombatAttackPose Choose(IReadOnlyList<CombatAttackWeapon> weapons,
            CombatTacticSettings settings, DriveInfo drive, Vector2 relativeVelocity,
            float clearance, float fallback, float currentHeading, int turn, bool offense,
            CombatManeuverCurves curves, CombatAttackPose? previous,
            Func<CombatAttackPose, float> riskPreference = null,
            Func<CombatAttackPose, float> readyOutput = null)
        {
            CombatTactic behavior = Behavior(settings.behavior, weapons, offense);
            var anchors = new List<CombatAttackWeapon>(weapons);
            anchors.Sort((a, b) => b.damageRate.CompareTo(a.damageRate));
            if (anchors.Count > 12) anchors.RemoveRange(12, anchors.Count - 12);
            var headings = new List<float>();
            if (settings.facing != CombatFacing.Automatic)
                headings.Add(settings.facing == CombatFacing.Target ? 0f : settings.facingDegrees * Mathf.Deg2Rad);
            else
            {
                for (int i = 0; i < 8; i++) headings.Add(i * Mathf.PI / 4f);
                headings.Add(currentHeading);
                if (previous.HasValue) headings.Add(previous.Value.heading);
            }
            var distances = new List<float> { Mathf.Max(clearance, fallback) };
            if (settings.distance > 0f) distances = new List<float> { settings.distance };
            else
            {
                float maxRange = clearance;
                foreach (var w in weapons) maxRange = Mathf.Max(maxRange, w.range + w.muzzle.magnitude);
                for (int i = 0; i <= 16; i++) distances.Add(Mathf.Lerp(clearance, maxRange, i / 16f));
                if (previous.HasValue) distances.Add(Mathf.Max(clearance, previous.Value.distance));
                if (curves?.distance?.points != null)
                {
                    foreach (var point in curves.distance.points) distances.Add(Mathf.Max(clearance, point.x));
                    var points = curves.distance.points;
                    if (curves.distance.extendRight && points.Count >= 2
                        && points[points.Count - 1].y > points[points.Count - 2].y)
                        distances.Add(Mathf.Max(fallback + 40f, points[points.Count - 1].x + 40f));
                }
            }
            float totalFirepower = 0f;
            foreach (var w in weapons) totalFirepower += w.damageRate;
            if (!offense && settings.distance == 0f && curves?.distance == null && weapons.Count > 0)
            {
                // Maneuver-only keeps the ship's useful range, but gives heading selection
                // entirely to mobility and the player's defensive preferences.
                var rangeSettings = settings.Copy();
                rangeSettings.facing = CombatFacing.Automatic;
                float usefulRange = Choose(weapons, rangeSettings, drive, relativeVelocity,
                    clearance, fallback, currentHeading, turn, true, null, null).distance;
                distances = new List<float> { usefulRange };
            }
            var mobility = new Dictionary<int, float>();
            CombatAttackPose best = default;
            float bestScore = float.NegativeInfinity;
            CombatAttackPose Evaluate(float distance, float heading)
            {
                var p = new CombatAttackPose { distance = distance,
                    heading = SpaceTransform.WrapAngle(heading), behavior = behavior };
                float weightedError = 0f, constrained = 0f;
                foreach (var w in weapons)
                {
                    bool covers = w.Aim(distance, heading, relativeVelocity, out float error);
                    if (covers) { p.firepower += w.damageRate; if (w.ready) p.readyOutput += w.damageRate; }
                    if (w.arc < 120f) { weightedError += error * w.damageRate; constrained += w.damageRate; }
                }
                p.alignment = constrained > 0f ? weightedError / constrained : 0f;
                // QP capability queries are bounded to eight headings per pose search.
                int bin = (Mathf.RoundToInt(heading * 4f / Mathf.PI) % 8 + 8) % 8;
                if (!mobility.TryGetValue(bin, out p.mobility))
                {
                    float a = bin * Mathf.PI / 4f;
                    float tangent = Mathf.Min(Acceleration(drive, Vector2.up, a), Acceleration(drive, Vector2.down, a));
                    float inward = Acceleration(drive, Vector2.right, a);
                    p.mobility = behavior == CombatTactic.Circle ? Mathf.Sqrt(Mathf.Max(0f, tangent * inward)) : tangent;
                    mobility.Add(bin, p.mobility);
                }
                p.distanceScore = curves?.distance?.Evaluate(distance) ?? 0f;
                p.preference = p.distanceScore;
                if (offense)
                {
                    if (readyOutput != null) p.readyOutput = readyOutput(p);
                    p.outgoingScore = curves?.outgoing?.Evaluate(p.readyOutput) ?? 0f;
                    p.alignmentScore = constrained > 0f ? curves?.alignmentError?.Evaluate(p.alignment) ?? 0f : 0f;
                    p.preference += p.outgoingScore + p.alignmentScore;
                }
                p.preference += riskPreference?.Invoke(p) ?? 0f;
                return p;
            }
            float Score(CombatAttackPose p)
            {
                // Stable potential coverage establishes a useful firing pose even during cooldown.
                float coverage = offense && totalFirepower > 0f ? 100f * p.firepower / totalFirepower : 0f;
                float motion = behavior != CombatTactic.HoldRange ? 8f * p.mobility / (1f + p.mobility) : 0f;
                float range = totalFirepower > 0f ? Mathf.Min(p.distance, 100000f) * 0.0001f
                    : -0.001f * Mathf.Abs(p.distance - fallback);
                return coverage + motion + p.preference + range
                    - 0.01f * Mathf.Abs(SpaceTransform.WrapAngle(p.heading - currentHeading));
            }
            void Consider(float distance, float heading)
            {
                var candidate = Evaluate(distance, heading);
                float score = Score(candidate);
                if (score > bestScore) { best = candidate; bestScore = score; }
            }
            foreach (float heading in headings)
            {
                foreach (float distance in distances) Consider(distance, heading);
                if (settings.distance > 0f || !offense && curves?.distance == null) continue;
                // Per-muzzle range boundaries: a rear mount does not shorten every other gun.
                foreach (var w in anchors)
                {
                    Vector2 muzzle = SpaceTransform.Rotate(w.muzzle, heading);
                    float squared = w.range * w.range - muzzle.y * muzzle.y;
                    if (squared <= 0f) continue;
                    float boundary = muzzle.x + Mathf.Sqrt(squared);
                    if (boundary >= clearance) Consider(Mathf.Max(clearance, boundary * 0.98f), heading);
                }
            }
            if (settings.facing == CombatFacing.Automatic && offense)
                foreach (var w in anchors)
                {
                    if (w.arc >= 120f) continue;
                    foreach (float distance in distances)
                        Consider(distance, w.LeadHeading(distance, relativeVelocity));
                }
            if (previous.HasValue)
            {
                var old = Evaluate(Mathf.Max(clearance, previous.Value.distance), previous.Value.heading);
                // Keep equal-quality poses. Explicit settings/rule changes pass no previous pose.
                bool similarCoverage = old.firepower > 0f && best.firepower < old.firepower * 1.25f
                    && old.mobility >= best.mobility * 0.9f && best.preference - old.preference < 1f;
                bool opening = curves?.distance?.extendRight == true
                    && best.distance > old.distance && best.preference > old.preference;
                if (!opening && (Score(old) >= bestScore - 1f || similarCoverage))
                    best = old;
            }
            return best;
        }

        internal static float Acceleration(DriveInfo drive, Vector2 direction, float heading) =>
            drive == null ? 0f : drive.PreviewBraking(-SpaceTransform.RotateInverse(direction, heading));

        internal static CombatTrajectoryIntent Trajectory(SpacePose own, SpacePose target,
            CombatAttackPose pose, DriveInfo drive, int turn, float movement, int tick, float? goalPhase = null,
            float referenceOmega = 0f)
        {
            Vector2 outward = OrbitSphere.Offset(target.position, own.position);
            float radius = Mathf.Max(1f, outward.magnitude);
            float inward = Acceleration(drive, Vector2.right, pose.heading);
            float outwardAcceleration = Acceleration(drive, Vector2.left, pose.heading);
            float tangent = Acceleration(drive, new Vector2(0f, -turn), pose.heading);
            float radialError = pose.distance - radius;
            float braking = radialError > 0f ? inward : outwardAcceleration;
            float radial = Mathf.Sign(radialError) * Mathf.Min(Mathf.Abs(radialError) * 0.35f,
                Mathf.Sqrt(2f * braking * Mathf.Abs(radialError))) * movement;
            float speed = pose.behavior == CombatTactic.Circle
                ? Mathf.Min(Mathf.Sqrt(Mathf.Max(0f, inward * radius * 0.45f)), tangent * 6f) * movement : 0f;
            // Translation and turn rate both come from the same real drive capabilities.
            float angular = drive == null ? 0f : Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            speed = Mathf.Min(speed, radius * Mathf.Sqrt(Mathf.Max(0f, angular * 0.5f)));
            float phaseAdvance = turn * speed / radius * 4f;
            if (pose.behavior != CombatTactic.Circle && goalPhase.HasValue)
            {
                float phaseError = SpaceTransform.WrapAngle(goalPhase.Value - Mathf.Atan2(outward.y, outward.x));
                float acceleration = Mathf.Min(Acceleration(drive, Vector2.up, pose.heading),
                    Acceleration(drive, Vector2.down, pose.heading));
                float maximum = Mathf.Min(Mathf.Sqrt(2f * acceleration * Mathf.Abs(phaseError) * radius), acceleration * 6f) * movement;
                maximum = Mathf.Min(maximum, radius * Mathf.Sqrt(Mathf.Max(0f, angular * 0.5f)));
                phaseAdvance = Mathf.Clamp(phaseError, -maximum / radius * 4f, maximum / radius * 4f) + referenceOmega * 4f;
            }
            return new CombatTrajectoryIntent
            {
                startTick = tick, seconds = 4f,
                center = target.position, centerVelocity = target.velocity,
                startRadius = radius, radialStep = Mathf.Clamp(radial * 4f, -radius + 1f, 100000f),
                startPhase = Mathf.Atan2(outward.y, outward.x),
                frameAngle = -pose.heading, phaseAdvance = phaseAdvance
            };
        }
    }
}

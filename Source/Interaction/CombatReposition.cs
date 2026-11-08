using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeltaV
{
    internal enum CombatRepositionStatus { FixedBearing, Holding, Circling, WaitingTimer, Selected }
    // Logical-tick state owned by each controller. No projectile or fire-event input.
    internal sealed class CombatReposition
    {
        internal bool initialized;
        internal int nextTick;
        internal float phase;
        internal float tangentSpeed;
        internal Vector3 basis;
        internal float selectedScore, continuationScore, selectedVelocityCost, selectedReversalCost;
        internal int candidateCount;
        private uint random;
        internal CombatReposition(int seed)
        { random = unchecked((uint)seed * 747796405u + 2891336453u); }
        // A new target/rule invalidates intent, but the controller's random stream continues.
        internal void Reset()
        { initialized = false; nextTick = 0; }
        private float Unit()
        { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return (random & 0xffffff) / 16777216f; }
        internal float PhaseAt(Vector3 center)
        {
            if (!initialized) return phase;
            var old = new SpacePose(basis, 0f) { velocity = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) };
            Vector2 transported = SpaceTransform.TransportVelocity(old, center);
            return Mathf.Atan2(transported.y, transported.x);
        }
        internal CombatRepositionStatus Update(SpacePose own, SpacePose target, CombatAttackPose pose,
            CombatTacticSettings settings, DriveInfo drive, int tick, float? alliedOpposite = null,
            Func<float, float, float> preference = null, IReadOnlyList<float> engagementPhases = null,
            float switchThreshold = 0)
        {
            Vector2 offset = OrbitSphere.Offset(target.position, own.position);
            float current = Mathf.Atan2(offset.y, offset.x);
            bool fresh = !initialized;
            phase = fresh ? current : PhaseAt(target.position);
            basis = target.position; initialized = true;
            if (settings.bearing != CombatBearing.Free)
            {
                phase = SpaceTransform.WrapAngle((settings.bearing == CombatBearing.Enemy ? target.angle : 0f)
                    + settings.bearingDegrees * Mathf.Deg2Rad);
                return CombatRepositionStatus.FixedBearing;
            }
            if (pose.behavior != CombatTactic.Automatic) return pose.behavior == CombatTactic.Circle
                ? CombatRepositionStatus.Circling : CombatRepositionStatus.Holding;
            // The deadline starts a fresh decision from the actual position. Guidance handles
            // any braking or reversal using the current velocity; arrival never gates a decision.
            if (!fresh && tick < nextTick) return CombatRepositionStatus.WaitingTimer;
            float interval = Mathf.Lerp(settings.intervalMin, settings.intervalMax, Unit());
            float a = Mathf.Min(CombatAttackPose.Acceleration(drive, Vector2.up, pose.heading),
                CombatAttackPose.Acceleration(drive, Vector2.down, pose.heading));
            float angular = drive == null ? 0f : Mathf.Min(drive.PreviewAngular(1), drive.PreviewAngular(-1));
            float feasible = Mathf.Min(a * interval * interval * 0.25f * settings.movement / Mathf.Max(1f, pose.distance),
                Mathf.Sqrt(Mathf.Max(0f, angular)) * interval * 0.25f);
            float limit = Mathf.Min(feasible, settings.repositionDegrees * Mathf.Deg2Rad);
            float radius = Mathf.Max(1f, offset.magnitude);
            float speed = Vector2.Dot(SpaceTransform.TransportVelocity(own, target.position) - target.velocity,
                new Vector2(-Mathf.Sin(current), Mathf.Cos(current)));
            var tangentPose = new SpacePose(target.position, 0f)
                { velocity = new Vector2(-Mathf.Sin(current), Mathf.Cos(current)) * Mathf.Sign(speed) };
            // Braking starts in the real current attitude, before a candidate's desired heading is reached.
            float braking = drive?.PreviewBraking(SpaceTransform.RotateInverse(
                SpaceTransform.TransportVelocity(tangentPose, own.position), own.angle)) ?? 0f;
            var offsets = new List<float> { 0f };
            void Add(float value)
            {
                value = Mathf.Clamp(value, -limit, limit);
                foreach (float existing in offsets) if (Mathf.Abs(existing - value) < .00001f) return;
                offsets.Add(value);
            }
            Add(-limit); Add(-limit * .5f); Add(limit * .5f); Add(limit);
            Add(speed * 4f / radius);
            if (alliedOpposite.HasValue) Add(SpaceTransform.WrapAngle(alliedOpposite.Value - current));
            if (engagementPhases != null)
            {
                offsets.Clear();
                foreach (float value in engagementPhases) offsets.Add(SpaceTransform.WrapAngle(value - current));
            }
            float forwardCruise = CombatFlightPlanner.CruiseSpeed(drive, pose.heading, radius, 1, settings.movement, pose.freeThrustFacing);
            float reverseCruise = pose.freeThrustFacing ? forwardCruise
                : CombatFlightPlanner.CruiseSpeed(drive, pose.heading, radius, -1, settings.movement);
            var candidates = new List<(float phase, float speed, float score, float velocityCost, float reversalCost)>();
            float best = float.NegativeInfinity, bestForward = float.NegativeInfinity;
            var fallbackScores = engagementPhases == null ? CombatEngagementSettings.Default() : null;
            foreach (float delta in offsets)
            {
                if (settings.direction == CombatTurn.Clockwise && delta > 0f
                    || settings.direction == CombatTurn.Counterclockwise && delta < 0f) continue;
                float candidate = SpaceTransform.WrapAngle(current + delta);
                float cruise = delta >= 0f ? forwardCruise : reverseCruise;
                // A sustained action may accelerate over several decisions. A weak drive must
                // be allowed to choose a useful incremental speed instead of rejecting all motion.
                cruise = Mathf.Min(cruise, Mathf.Abs(speed) + a * interval * .25f);
                float requested = limit > .00001f ? Mathf.Clamp(delta / limit, -1, 1) * cruise : 0f;
                float benefit = preference?.Invoke(candidate, requested) ?? 0f;
                if (float.IsNaN(benefit) || float.IsNegativeInfinity(benefit)) continue;
                float velocityCost = -(fallbackScores?.Score(CombatEngagementMetric.VelocitySeconds,
                    Mathf.Abs(requested - speed) / Mathf.Max(.001f, a)) ?? 0);
                bool reverse = requested * speed < 0f && Mathf.Abs(speed) > .01f;
                float reversalCost = reverse ? -(fallbackScores?.Score(CombatEngagementMetric.ReverseSeconds,
                    Mathf.Abs(speed) / Mathf.Max(.001f, braking)) ?? 0) : 0;
                float flank = alliedOpposite.HasValue ? fallbackScores?.Score(CombatEngagementMetric.Flanking,
                    1 - Mathf.Abs(SpaceTransform.WrapAngle(alliedOpposite.Value - candidate)) / Mathf.PI) ?? 0 : 0;
                float score = benefit + flank - velocityCost - reversalCost;
                candidates.Add((candidate, requested, score, velocityCost, reversalCost));
                best = Mathf.Max(best, score);
                if (!reverse) bestForward = Mathf.Max(bestForward, score);
            }
            // Randomness only breaks near ties against the global best, never a chain of local ties.
            const float equivalent = .05f;
            if (candidates.Count > 0 && Mathf.Abs(SpaceTransform.WrapAngle(candidates[0].phase - current)) < .001f)
            {
                var currentChoice = candidates[0];
                if (best <= currentChoice.score + Mathf.Max(equivalent, switchThreshold))
                { candidates.Clear(); candidates.Add(currentChoice); best = currentChoice.score; }
            }
            candidateCount = candidates.Count;
            continuationScore = bestForward;
            var choices = new List<int>();
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].score >= best - equivalent
                    && (candidates[i].reversalCost == 0f || candidates[i].score > bestForward + equivalent)) choices.Add(i);
            if (choices.Count == 0)
                for (int i = 0; i < candidates.Count; i++)
                    if (candidates[i].reversalCost == 0f && candidates[i].score >= bestForward - equivalent) choices.Add(i);
            phase = current; tangentSpeed = 0f;
            selectedScore = float.NegativeInfinity; selectedVelocityCost = selectedReversalCost = 0f;
            if (choices.Count > 0)
            {
                var chosen = candidates[choices[choices.Count == 1 ? 0 : Mathf.Min(choices.Count - 1, (int)(Unit() * choices.Count))]];
                phase = chosen.phase; tangentSpeed = chosen.speed; selectedScore = chosen.score;
                selectedVelocityCost = chosen.velocityCost; selectedReversalCost = chosen.reversalCost;
            }
            nextTick = tick + Mathf.RoundToInt(interval * 60f);
            return CombatRepositionStatus.Selected;
        }
    }
}

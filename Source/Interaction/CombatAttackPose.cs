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

        // Triangle bounds only reject instantaneous rays whose entire target hull
        // lies outside the range band, regardless of either ship's attitude.
        internal bool OutsideInstantRange(float centerDistance, float targetRadius)
        {
            float margin = muzzle.magnitude + targetRadius + .001f;
            return instant && (centerDistance - margin > range || centerDistance + margin < minRange);
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
        internal bool freeThrustFacing, fireHeading;

        internal void EvaluateFire(IReadOnlyList<CombatAttackWeapon> weapons, Vector2 relativeVelocity,
            bool offense, CombatManeuverCurves curves, float? output = null)
        {
            firepower = readyOutput = alignment = outgoingScore = alignmentScore = 0f;
            float weightedError = 0f, constrained = 0f;
            foreach (var w in weapons)
            {
                bool covers = w.Aim(distance, heading, relativeVelocity, out float error);
                if (covers) { firepower += w.damageRate; if (w.ready) readyOutput += w.damageRate; }
                if (w.arc < 120f) { weightedError += error * w.damageRate; constrained += w.damageRate; }
            }
            alignment = constrained > 0f ? weightedError / constrained : 0f;
            if (!offense) return;
            if (output.HasValue) readyOutput = output.Value;
            outgoingScore = curves?.outgoing?.Evaluate(readyOutput) ?? 0f;
            alignmentScore = constrained > 0f ? curves?.alignmentError?.Evaluate(alignment) ?? 0f : 0f;
        }

        internal static int Turn(CombatTurn setting, int previous, float tangentSpeed, int seed)
        {
            if (setting == CombatTurn.Clockwise) return -1;
            if (setting == CombatTurn.Counterclockwise) return 1;
            if (previous != 0) return previous;
            return Mathf.Abs(tangentSpeed) > 0.5f ? (tangentSpeed > 0f ? 1 : -1)
                : (seed & 1) == 0 ? 1 : -1;
        }

        internal static float Acceleration(DriveInfo drive, Vector2 direction, float heading) =>
            drive == null ? 0f : drive.PreviewBraking(-SpaceTransform.RotateInverse(direction, heading));

    }
}

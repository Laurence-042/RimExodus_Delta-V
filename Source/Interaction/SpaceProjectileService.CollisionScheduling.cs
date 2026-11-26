using System.Collections.Generic;
using RimWorld;
using UnityEngine;

namespace DeltaV
{
    public sealed partial class SpaceProjectileService
    {
        private const float CollisionSafetyEpsilon = 0.05f;
        private const float MaxCollisionSchedulingClearance = 4096f;

        private sealed class CollisionTargetState
        {
            internal SpaceObject owner;
            internal int representation;
            internal Vector3 position;
            internal float radius;
            internal Faction faction;
            internal bool activeCollider;
            internal int seen;
        }

        private readonly Dictionary<int, CollisionTargetState> collisionTargets =
            new Dictionary<int, CollisionTargetState>();
        private readonly List<int> staleCollisionTargets = new List<int>();
        private int collisionTargetEpoch = 1;
        private int collisionTargetScan;
        private float collisionTargetMotionThisTick;

        /// <summary>
        /// One shared O(targets) snapshot per collision round. Existing certificates only
        /// spend the largest motion/radius growth of any target; a newly eligible target or
        /// representation invalidates them because it was absent from their original proof.
        /// </summary>
        private int RefreshCollisionSchedulingTargets(out bool targetSetChanged)
        {
            collisionTargetScan++;
            if (collisionTargetScan == int.MaxValue)
            {
                collisionTargetScan = 1;
                foreach (CollisionTargetState state in collisionTargets.Values) state.seen = 0;
            }

            targetSetChanged = false;
            collisionTargetMotionThisTick = 0f;
            int count = 0;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == null || obj.IsScattered
                    || !SpaceCollision.TryGetSchedulingBounds(obj, out float radius)) continue;
                count++;
                bool active = obj.IsActiveCollider;
                if (!collisionTargets.TryGetValue(obj.objectId, out CollisionTargetState state)
                    || state.owner != obj || state.representation != obj.RepresentationVersion
                    || state.faction != obj.faction || state.activeCollider != active)
                {
                    targetSetChanged = true;
                    if (state == null)
                    {
                        state = new CollisionTargetState();
                        collisionTargets[obj.objectId] = state;
                    }
                }
                else
                {
                    float spend = OrbitSphere.ArcCells(state.position, obj.pose.position)
                        + Mathf.Max(0f, radius - state.radius);
                    if (spend > collisionTargetMotionThisTick) collisionTargetMotionThisTick = spend;
                }

                state.owner = obj;
                state.representation = obj.RepresentationVersion;
                state.position = obj.pose.position;
                state.radius = radius;
                state.faction = obj.faction;
                state.activeCollider = active;
                state.seen = collisionTargetScan;
            }

            staleCollisionTargets.Clear();
            foreach (var pair in collisionTargets)
                if (pair.Value.seen != collisionTargetScan) staleCollisionTargets.Add(pair.Key);
            // Removal can only make an existing certificate more conservative.
            foreach (int id in staleCollisionTargets) collisionTargets.Remove(id);
            staleCollisionTargets.Clear();
            if (targetSetChanged)
            {
                collisionTargetEpoch++;
                if (collisionTargetEpoch == 0) collisionTargetEpoch = 1;
            }
            return count;
        }

        private bool RequiresExactCollision(Projectile projectile, bool forceExact, ref int invalidations)
        {
            if (forceExact) return true;
            SpaceObject attacker = projectile.container;
            bool ignoreNonHostile = projectile.body.ignoreNonHostileProjectiles;
            if (attacker == null || projectile.collisionTargetEpoch != collisionTargetEpoch
                || projectile.collisionFaction != attacker.faction
                || projectile.collisionIgnoreNonHostile != ignoreNonHostile)
            {
                invalidations++;
                return true;
            }

            float ownMotion = OrbitSphere.ArcCells(projectile.body.from, projectile.body.to);
            projectile.collisionClearance -= ownMotion + collisionTargetMotionThisTick;
            return !(projectile.collisionClearance > CollisionSafetyEpsilon);
        }

        private void StoreCollisionCertificate(Projectile projectile)
        {
            projectile.collisionSkipStreak = 0;
            SpaceObject attacker = projectile.container;
            float clearance = projectile.body.schedulingClearance;
            if (projectile.dead || attacker == null || projectile.laser || projectile.hardDockingActive
                || projectile.body.resolvedHit || projectile.body.didContinue || projectile.body.didRedirect
                || float.IsNaN(clearance))
            {
                projectile.collisionClearance = 0f;
                return;
            }
            if (float.IsPositiveInfinity(clearance)) clearance = MaxCollisionSchedulingClearance;
            projectile.collisionClearance = Mathf.Clamp(clearance - CollisionSafetyEpsilon,
                0f, MaxCollisionSchedulingClearance);
            projectile.collisionTargetEpoch = collisionTargetEpoch;
            projectile.collisionFaction = attacker.faction;
            projectile.collisionIgnoreNonHostile = projectile.body.ignoreNonHostileProjectiles;
        }

        private void ResetCollisionScheduling()
        {
            collisionTargets.Clear();
            staleCollisionTargets.Clear();
            collisionTargetEpoch = 1;
            collisionTargetScan = 0;
            collisionTargetMotionThisTick = 0f;
        }
    }
}

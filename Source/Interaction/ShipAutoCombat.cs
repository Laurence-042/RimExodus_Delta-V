using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>玩家和 NPC 共用的战斗站位选择；只提交 ShipAutopilot 任务。</summary>
    internal sealed class ShipAutoManeuverControl
    {
        private readonly UniverseManager universe;
        private readonly DriveSubject ownerId;
        private int lastDoctrineTick = -1;
        private bool orbitTaskObserved;
        private float lastOrbitRadius;
        private NpcDoctrine lastOrbitDoctrine;
        private CombatTarget lastOrbitTarget;
        private int lastOrbitLogTick = int.MinValue;
        private int orbitDirection;

        internal NpcDoctrine Doctrine { get; private set; }

        internal ShipAutoManeuverControl(UniverseManager universe, DriveSubject ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        internal void Decide(ShipBase ship, SpaceObject own, CombatTarget target,
            List<NpcDoctrine> priorities, bool allowBoarding, bool playerInitiated)
        {
            if (!target.TryResolve(universe, out var resolved)
                || !ShipFireControl.CandidateValid(ship, own, resolved.Container, resolved.Ship))
            {
                Clear(ship);
                return;
            }
            int now = Find.TickManager.TicksGame;
            int interval = 2 * Mathf.Clamp(DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
            if (lastDoctrineTick >= 0 && now - lastDoctrineTick < interval
                && universe.Autopilot.IsCombatOrbit(DriveSubject.Of(ship)) && lastOrbitTarget == target) return;
            lastDoctrineTick = now;
            PickDoctrine(ship, own, resolved, priorities, allowBoarding, out NpcDoctrine doctrine,
                out float radius);
            bool taskActive = universe.Autopilot.IsCombatOrbit(DriveSubject.Of(ship));
            bool changed = !orbitTaskObserved || lastOrbitTarget != target
                || lastOrbitDoctrine != doctrine || Mathf.Abs(lastOrbitRadius - radius) > 0.5f
                || !taskActive;
            if (lastOrbitTarget != target || !taskActive) orbitDirection = 0;
            Doctrine = doctrine;
            lastOrbitRadius = radius;
            if (lastOrbitTarget != target && taskActive) universe.Autopilot.Detach(DriveSubject.Of(ship));
            lastOrbitTarget = target;
            radius = Mathf.Max(radius, SpaceCollision.BoundsRadiusOf(resolved.Container) + 8f);
            universe.Autopilot.TrackOrbit(DriveSubject.Of(ship), SampleTarget, radius, PlanTrajectory, playerInitiated);
            if (changed && DeltaVLog.Enabled(DeltaVLogModule.Combat)
                && (lastOrbitLogTick == int.MinValue || now - lastOrbitLogTick >= 60))
            {
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"ship #{ownerId} orbit task: target=#{target.Ship} doctrine={doctrine} "
                    + $"radius={radius:F1}; {UniverseManager.MapTag(ship.Map)}");
                lastOrbitLogTick = now;
            }
            orbitTaskObserved = true;
            lastOrbitTarget = target;
            lastOrbitDoctrine = doctrine;
        }

        internal void Clear(ShipBase ship)
        {
            if (ship.IsValid && universe.Autopilot.IsCombatOrbit(DriveSubject.Of(ship)))
                universe.Autopilot.Detach(DriveSubject.Of(ship), "combat target lost");
            orbitTaskObserved = false;
            lastOrbitTarget = default;
            orbitDirection = 0;
        }

        private bool SampleTarget(out SpacePose pose)
        {
            pose = default;
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (!lastOrbitTarget.TryResolve(universe, out var resolved)
                || !ShipFireControl.CandidateValid(ship, universe.FrameOf(ship.Map), resolved.Container, resolved.Ship)) return false;
            pose = resolved.Pose;
            return true;
        }

        /// <summary>每个制导微步给共同 MPC 一段实际推进器可达的轨迹；不直接写推力。</summary>
        private bool PlanTrajectory(SpaceObject own, SpacePose target,
            float radius, DriveInfo report, out CombatTrajectoryIntent intent)
        {
            intent = default;
            if (own == null || report == null
                || report.mass <= 0f || report.inertia <= 0f) return false;
            Vector2 toCenter = OrbitSphere.Offset(own.pose.position, target.position);
            float distance = toCenter.magnitude;
            if (distance < 0.01f) return false;
            Vector2 outward = -toCenter / distance;
            Vector2 tangent = new Vector2(-outward.y, outward.x);
            Vector2 relativeVelocity = own.pose.velocity
                - SpaceTransform.TransportVelocity(target, own.pose.position);
            float frameAngle = report.frameAngle;
            float pathAngle = SpaceTransform.WrapAngle(Mathf.Atan2(toCenter.y, toCenter.x)
                - frameAngle);
            float desiredMapAngle = pathAngle;
            float mapFacing = own.pose.angle;

            // 侧推选择以船首对准圆心时的 Map 局部推进器能力为依据，只在目标切换时决定。
            if (orbitDirection == 0)
            {
                Vector2 mapPositive = SpaceTransform.RotateInverse(tangent, desiredMapAngle);
                float positive = report.PreviewBraking(-mapPositive);
                float negative = report.PreviewBraking(mapPositive);
                orbitDirection = positive >= negative ? 1 : -1;
            }
            Vector2 mapSide = SpaceTransform.RotateInverse(tangent * orbitDirection,
                desiredMapAngle);
            Vector2 mapOutward = SpaceTransform.RotateInverse(outward, desiredMapAngle);
            float sideAcceleration = report.PreviewBraking(-mapSide);
            float outwardAcceleration = report.PreviewBraking(-mapOutward);
            float inwardAcceleration = report.PreviewBraking(mapOutward);
            float angularAcceleration = report.PreviewAngular(
                orbitDirection);
            float radiusError = radius - distance;
            float radialAcceleration = radiusError >= 0f ? outwardAcceleration : inwardAcceleration;
            float seconds = Mathf.Clamp(2f + Mathf.Sqrt(2f * Mathf.Abs(radiusError)
                / Mathf.Max(0.1f, radialAcceleration)), 2f, 6f);
            float radialSpeed = Vector2.Dot(relativeVelocity, outward);
            float radialLow = radialSpeed * seconds
                - 0.5f * inwardAcceleration * seconds * seconds;
            float radialHigh = radialSpeed * seconds
                + 0.5f * outwardAcceleration * seconds * seconds;
            float radialStep = Mathf.Clamp(radiusError, radialLow, radialHigh);
            float futureRadius = Mathf.Max(0.1f, distance + radialStep);
            float sideSpeed = Vector2.Dot(relativeVelocity, tangent) * orbitDirection;
            float sideTravel = Mathf.Max(0f, sideSpeed * seconds
                + 0.5f * sideAcceleration * seconds * seconds);
            float headingError = Mathf.Abs(SpaceTransform.WrapAngle(pathAngle - mapFacing));
            float yawTravel = Mathf.Max(0f, own.pose.angularVelocity * orbitDirection * seconds
                + 0.5f * angularAcceleration * seconds * seconds
                - Mathf.Max(0f, headingError - 0.1f));
            float phase = Mathf.Min(Mathf.PI / 3f,
                sideTravel / Mathf.Max(Mathf.Max(distance, futureRadius), 8f), yawTravel);

            Vector2 currentFromCenter = OrbitSphere.Offset(target.position,
                own.pose.position);
            float currentPhase = Mathf.Atan2(currentFromCenter.y, currentFromCenter.x);
            intent = new CombatTrajectoryIntent
            {
                seconds = seconds,
                center = target.position,
                centerVelocity = target.velocity,
                startRadius = distance,
                radialStep = radialStep,
                startPhase = currentPhase,
                frameAngle = frameAngle,
                pathAngle = pathAngle,
                pathAngularVelocity = (outward.x * relativeVelocity.y
                    - outward.y * relativeVelocity.x) / distance,
                phaseAdvance = orbitDirection * phase
            };
            intent.Sample(seconds, out intent.futurePosition, out intent.futureVelocity,
                out intent.futureAngle, out intent.futureAngularVelocity);
            return true;
        }

        private void PickDoctrine(ShipBase ship, SpaceObject own, ResolvedCombatTarget enemy,
            List<NpcDoctrine> priorities, bool allowBoarding,
            out NpcDoctrine selected, out float distance)
        {
            if (priorities == null || priorities.Count == 0) priorities = NpcDoctrineDefaults.Create();
            float clearance = SpaceCollision.BoundsRadiusOf(own)
                + SpaceCollision.BoundsRadiusOf(enemy.Container)
                + OrbitSphere.Offset(enemy.Container.pose.position, enemy.Pose.position).magnitude + 10f;
            float maxRange = SpaceWeaponService.WeaponsOn(ship)
                .Where(w => w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.Volley)
                .Select(w => w.WeaponProps.range).DefaultIfEmpty(clearance + 30f).Max();
            float[] samples = { clearance, Mathf.Max(clearance, maxRange * 0.45f),
                Mathf.Max(clearance, maxRange * 0.7f), Mathf.Max(clearance, maxRange * 0.9f) };
            selected = NpcDoctrine.Standoff;
            distance = samples[0];
            float best = float.NegativeInfinity;
            for (int rank = 0; rank < priorities.Count; rank++)
            {
                NpcDoctrine option = priorities[rank];
                if (option == NpcDoctrine.Retreat || option == NpcDoctrine.Boarder && !allowBoarding) continue;
                for (int i = 0; i < samples.Length; i++)
                {
                    float at = samples[i];
                    float ownPower = universe.Weapons.EstimateFirepower(ship, at,
                        ShipFireControl.HullRadius(enemy.Ship), option == NpcDoctrine.MissileBoat);
                    if (option == NpcDoctrine.MissileBoat && ownPower <= 0f) continue;
                    if (option == NpcDoctrine.MissileBoat)
                    {
                        float enemyPd = universe.Weapons.EstimatePointDefense(enemy.Ship);
                        ownPower *= ownPower / Mathf.Max(ownPower, ownPower + enemyPd);
                    }
                    if (option == NpcDoctrine.Boarder && universe.Shuttles.BoardingCraftOf(ship) == null) continue;
                    float enemyPower = universe.Weapons.EstimateFirepower(enemy.Ship, at,
                        ShipFireControl.HullRadius(ship));
                    float preference = (priorities.Count - rank) * 0.1f * Mathf.Max(1f, ownPower);
                    float posture = option switch
                    {
                        NpcDoctrine.Brawler => -at * 0.02f,
                        NpcDoctrine.Standoff => at * 0.02f,
                        NpcDoctrine.MissileBoat => at * 0.035f,
                        NpcDoctrine.Boarder => -at * 0.015f,
                        _ => 0f
                    };
                    float score = ownPower - enemyPower + preference + posture;
                    if (score <= best) continue;
                    best = score;
                    selected = option;
                    distance = at;
                }
            }
        }

    }
}

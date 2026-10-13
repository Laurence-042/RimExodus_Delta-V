using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>玩家和 NPC 共用的自动索敌与齐射意图；驾驶方式由 controller 独立选择。</summary>
    internal sealed class ShipAutoFireControl
    {
        private readonly UniverseManager universe;
        private readonly int ownerId;
        private readonly bool shipsOnly;
        private SpaceObject target;
        private Thing preferredSystem;
        private int targetLockedUntil;
        private bool targetObserved;

        internal SpaceObject Target => target;

        internal ShipAutoFireControl(UniverseManager universe, int ownerId, bool shipsOnly)
        {
            this.universe = universe;
            this.ownerId = ownerId;
            this.shipsOnly = shipsOnly;
        }

        internal void Decide(Ship ship, SpaceObject own)
        {
            SpaceObject selected = SelectTarget(ship, own);
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat) && (!targetObserved || selected != target))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"ship #{ownerId} auto-fire target "
                    + (selected == null ? "none" : $"#{selected.objectId} '{selected.label}'")
                    + $"; {UniverseManager.MapTag(ship.map)}");
            targetObserved = true;
            target = selected;
        }

        internal bool TryGetIntent(Ship ship, SpaceObject own, bool suppressSystems,
            out ShipVolleyIntent intent)
        {
            intent = default;
            if (target == null || universe.Lifecycle.IsFrozen(ship.map)
                || !CandidateValid(ship, own, target, shipsOnly)) return false;
            if (suppressSystems && (preferredSystem == null || !preferredSystem.Spawned
                || preferredSystem.Map != target.map))
                preferredSystem = SelectSystemTarget(target);
            intent = new ShipVolleyIntent(target, suppressSystems ? preferredSystem : null,
                !suppressSystems);
            return true;
        }

        internal void Clear()
        {
            target = null;
            preferredSystem = null;
        }

        internal static bool CandidateValid(Ship ship, SpaceObject own, SpaceObject candidate,
            bool shipsOnly = false)
        {
            return ship.IsValid && own != null && candidate != null && candidate != own
                && candidate.map != null && !candidate.map.Disposed
                && (!shipsOnly || candidate.Tag == SpaceObjectTag.Ship)
                && candidate.baseTag != SpaceObjectTag.Projectile
                && candidate.baseTag != SpaceObjectTag.Shuttle
                && SpaceWeaponService.HostileTo(candidate, ShipRegistry.FactionOf(ship))
                && OrbitSphere.ArcCells(own.pose.position, candidate.pose.position)
                    <= DeltaVMod.CurrentSettings.poiActiveRadius;
        }

        private SpaceObject SelectTarget(Ship ship, SpaceObject own)
        {
            float best = float.NegativeInfinity;
            SpaceObject selected = null;
            foreach (SpaceObject candidate in universe.objects)
            {
                if (!CandidateValid(ship, own, candidate, shipsOnly)) continue;
                float distance = OrbitSphere.ArcCells(own.pose.position, candidate.pose.position);
                Ship enemy = ShipRegistry.ShipsOnMap(candidate.map).FirstOrDefault();
                float enemyFire = universe.Weapons.EstimateFirepower(enemy, distance,
                    SpaceCollision.BoundsRadiusOf(own));
                float survivability = candidate.map.listerThings.AllThings
                    .OfType<Building>().Where(b => b.Spawned).Sum(b => (float)b.HitPoints);
                float ownFire = universe.Weapons.EstimateFirepower(ship, distance,
                    SpaceCollision.BoundsRadiusOf(candidate));
                float score = enemyFire * 2f + ownFire / (1f + survivability / 1000f)
                    - distance * 0.1f + (candidate == target ? 10f : 0f);
                if (candidate == target && Find.TickManager.TicksGame < targetLockedUntil)
                    score += 100000f;
                if (score <= best) continue;
                best = score;
                selected = candidate;
            }
            if (selected != target) targetLockedUntil = Find.TickManager.TicksGame + 180;
            return selected;
        }

        private static Thing SelectSystemTarget(SpaceObject victim)
        {
            if (victim?.map == null) return null;
            Thing pointDefense = SpaceWeaponService.AllWeaponsOn(victim.map)
                .Where(w => w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.PointDefense
                    && w.parent.Spawned && !w.parent.Destroyed)
                .Select(w => w.parent).FirstOrDefault();
            return pointDefense ?? ShipPhysics.GetReport(victim.map)?.thrusters
                .FirstOrDefault(t => t.operational && t.thing != null)?.thing;
        }
    }

    /// <summary>玩家和 NPC 共用的战斗站位选择；只提交 ShipAutopilot 任务。</summary>
    internal sealed class ShipAutoManeuverControl
    {
        private readonly UniverseManager universe;
        private readonly int ownerId;
        private int lastDoctrineTick = -1;
        private bool orbitTaskObserved;
        private float lastOrbitRadius;
        private NpcDoctrine lastOrbitDoctrine;
        private SpaceObject lastOrbitTarget;
        private int lastOrbitLogTick = int.MinValue;
        private int orbitDirection;

        internal NpcDoctrine Doctrine { get; private set; }

        internal ShipAutoManeuverControl(UniverseManager universe, int ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        internal void Decide(Ship ship, SpaceObject own, SpaceObject target,
            List<NpcDoctrine> priorities, bool allowBoarding, bool playerInitiated)
        {
            if (target == null)
            {
                Clear(ship);
                return;
            }
            int now = Find.TickManager.TicksGame;
            int interval = 2 * Mathf.Clamp(DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
            if (lastDoctrineTick >= 0 && now - lastDoctrineTick < interval
                && universe.Autopilot.IsCombatOrbit(ship.ownerId) && lastOrbitTarget == target) return;
            lastDoctrineTick = now;
            PickDoctrine(ship, own, target, priorities, allowBoarding, out NpcDoctrine doctrine,
                out float radius);
            bool taskActive = universe.Autopilot.IsCombatOrbit(ship.ownerId);
            bool changed = !orbitTaskObserved || lastOrbitTarget != target
                || lastOrbitDoctrine != doctrine || Mathf.Abs(lastOrbitRadius - radius) > 0.5f
                || !taskActive;
            if (lastOrbitTarget != target || !taskActive) orbitDirection = 0;
            Doctrine = doctrine;
            lastOrbitRadius = radius;
            universe.Autopilot.TrackOrbit(ship, target, radius, PlanTrajectory, playerInitiated);
            if (changed && DeltaVLog.Enabled(DeltaVLogModule.Combat)
                && (lastOrbitLogTick == int.MinValue || now - lastOrbitLogTick >= 60))
            {
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"ship #{ownerId} orbit task: target=#{target.objectId} doctrine={doctrine} "
                    + $"radius={radius:F1}; {UniverseManager.MapTag(ship.map)}");
                lastOrbitLogTick = now;
            }
            orbitTaskObserved = true;
            lastOrbitTarget = target;
            lastOrbitDoctrine = doctrine;
        }

        internal void Clear(Ship ship)
        {
            if (ship.IsValid && universe.Autopilot.IsCombatOrbit(ship.ownerId))
                universe.Autopilot.Detach(ship, "combat target lost");
            orbitTaskObserved = false;
            lastOrbitTarget = null;
            orbitDirection = 0;
        }

        /// <summary>每个制导微步给共同 MPC 一段实际推进器可达的轨迹；不直接写推力。</summary>
        private bool PlanTrajectory(Ship ship, SpaceObject own, SpaceObject target,
            float radius, ShipPhysics.MassReport report, out CombatTrajectoryIntent intent)
        {
            intent = default;
            if (!ship.IsValid || own == null || target == null || report == null
                || report.mass <= 0f || report.inertia <= 0f) return false;
            Vector2 toCenter = OrbitSphere.Offset(own.pose.position, target.pose.position);
            float distance = toCenter.magnitude;
            if (distance < 0.01f) return false;
            Vector2 outward = -toCenter / distance;
            Vector2 tangent = new Vector2(-outward.y, outward.x);
            Vector2 relativeVelocity = own.pose.velocity
                - SpaceTransform.TransportVelocity(target.pose, own.pose.position);
            float frameAngle = PilotTarget.Of(ship).FrameAngle;
            float pathAngle = SpaceTransform.WrapAngle(Mathf.Atan2(toCenter.y, toCenter.x)
                - frameAngle);
            float desiredMapAngle = pathAngle;
            float mapFacing = own.pose.angle;

            // 侧推选择以船首对准圆心时的 Map 局部推进器能力为依据，只在目标切换时决定。
            if (orbitDirection == 0)
            {
                Vector2 mapPositive = SpaceTransform.RotateInverse(tangent, desiredMapAngle);
                float positive = ShipPhysics.PreviewBrakingAcceleration(report, -mapPositive);
                float negative = ShipPhysics.PreviewBrakingAcceleration(report, mapPositive);
                orbitDirection = positive >= negative ? 1 : -1;
            }
            Vector2 mapSide = SpaceTransform.RotateInverse(tangent * orbitDirection,
                desiredMapAngle);
            Vector2 mapOutward = SpaceTransform.RotateInverse(outward, desiredMapAngle);
            float sideAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, -mapSide);
            float outwardAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, -mapOutward);
            float inwardAcceleration = ShipPhysics.PreviewBrakingAcceleration(report, mapOutward);
            float angularAcceleration = ShipPhysics.PreviewAngularAcceleration(report,
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

            Vector2 currentFromCenter = OrbitSphere.Offset(target.pose.position,
                own.pose.position);
            float currentPhase = Mathf.Atan2(currentFromCenter.y, currentFromCenter.x);
            intent = new CombatTrajectoryIntent
            {
                seconds = seconds,
                center = target.pose.position,
                centerVelocity = target.pose.velocity,
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

        private void PickDoctrine(Ship ship, SpaceObject own, SpaceObject enemy,
            List<NpcDoctrine> priorities, bool allowBoarding,
            out NpcDoctrine selected, out float distance)
        {
            if (priorities == null || priorities.Count == 0) priorities = NpcDoctrineDefaults.Create();
            Ship hostile = ShipRegistry.ShipsOnMap(enemy.map).FirstOrDefault();
            float clearance = SpaceCollision.BoundsRadiusOf(own)
                + SpaceCollision.BoundsRadiusOf(enemy) + 10f;
            float maxRange = SpaceWeaponService.WeaponsOn(ship.map, ship.ownerId)
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
                        SpaceCollision.BoundsRadiusOf(enemy), option == NpcDoctrine.MissileBoat);
                    if (option == NpcDoctrine.MissileBoat && ownPower <= 0f) continue;
                    if (option == NpcDoctrine.MissileBoat)
                    {
                        float enemyPd = universe.Weapons.EstimatePointDefense(hostile);
                        ownPower *= ownPower / Mathf.Max(ownPower, ownPower + enemyPd);
                    }
                    if (option == NpcDoctrine.Boarder && !HasBoardingShuttle(ship)) continue;
                    float enemyPower = universe.Weapons.EstimateFirepower(hostile, at,
                        SpaceCollision.BoundsRadiusOf(own));
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

        private static bool HasBoardingShuttle(Ship ship) => ship.map.listerThings.AllThings
            .OfType<Building>().Any(b => b.Faction == ShipRegistry.FactionOf(ship)
                && b.TryGetComp<CompSpaceShuttle>() is { CanBoard: true } comp
                && comp.Launchable != null && comp.Launchable.CanLaunch()
                && b.TryGetComp<CompTransporter>()?.innerContainer?.Any(t => t is Pawn) == true);
    }
}

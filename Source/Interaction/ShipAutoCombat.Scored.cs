using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class ShipAutoManeuverControl
    {
        private readonly struct ScoredWeapon
        {
            internal readonly CompSpaceWeapon weapon;
            internal readonly float range, minRange, spread, damageRate;
            internal readonly bool readyNow;
            internal ScoredWeapon(CompSpaceWeapon weapon, float damageRate)
            {
                this.weapon = weapon;
                range = weapon.WeaponProps.range;
                minRange = weapon.WeaponProps.minRange;
                spread = weapon.WeaponProps.baseSpreadDeg * Mathf.Deg2Rad;
                this.damageRate = damageRate;
                readyNow = CombatStrategyFacts.WeaponReady(weapon);
            }
        }

        private readonly struct ScoredEnemy
        {
            internal readonly Vector3 future;
            internal readonly float radius, distance, power;
            internal ScoredEnemy(Vector3 future, float radius,
                float distance, float power)
            { this.future = future; this.radius = radius;
                this.distance = distance; this.power = power; }
        }

        private readonly struct ManeuverScore
        {
            internal readonly float distance, outgoing, incoming, projectile;
            internal readonly float overlap, alignment, control;
            internal float Total => distance + outgoing + incoming + projectile
                + overlap + alignment + control;

            internal ManeuverScore(float distance, float outgoing, float incoming,
                float projectile, float overlap, float alignment, float control)
            {
                this.distance = distance; this.outgoing = outgoing;
                this.incoming = incoming; this.projectile = projectile;
                this.overlap = overlap; this.alignment = alignment; this.control = control;
            }
        }

        private int scoredNextGoalTick;
        private Vector3 scoredGoalPosition;
        private float scoredGoalAngle;
        private int scoredReadySignature;
        private bool scoredLastOffenseEnabled;
        private bool scoredHasGoal;
        private bool scoredOffenseEnabled;
        private float scoredAnchorDistance;
        private int scoredStructureVersion = -1;
        internal bool Boarding { get; private set; }

        private bool PlanScoredTrajectory(SpaceObject own, SpacePose anchorPose,
            float clearance, DriveInfo drive, out CombatTrajectoryIntent intent)
        {
            intent = default;
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (ship?.IsValid != true || own == null || drive == null
                || drive.mass <= 0f || drive.inertia <= 0f)
                return false;
            int now = Find.TickManager.TicksGame;
            int readySignature = 17;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
                if (weapon.SpaceModeEnabled && weapon.GroupActive
                    && weapon.aimMode == SpaceWeaponAimMode.Volley)
                    readySignature = unchecked(readySignature * 31
                        + weapon.parent.thingIDNumber * (weapon.CooldownTicksLeft == 0 ? 1 : -1));
            if (!scoredHasGoal || now >= scoredNextGoalTick
                || scoredReadySignature != readySignature
                || scoredLastOffenseEnabled != scoredOffenseEnabled
                || scoredStructureVersion != SubstructureOwnership.GlobalStructureVersion)
            {
                var enemies = new List<(ShipBase ship, SpaceObject container)>();
                foreach (SpaceObject container in universe.objects)
                    foreach (ShipBase member in ShipFireControl.Candidates(ship, own, container))
                        enemies.Add((member, container));
                if (enemies.Count == 0)
                    return false;
                var facts = new CombatStrategyFacts(universe, ship, own);
                ShipBase ruleEnemy = enemies[0].ship;
                if (anchorTarget.TryResolve(universe, out var anchor))
                    ruleEnemy = anchor.Ship;
                CombatStrategyContext current = facts.Context(ruleEnemy);
                CombatStrategyRule rule = ship.CombatAI?.strategy?.Maneuver(current)
                    ?? new CombatStrategy().Maneuver(current);
                ChooseScoredGoal(ship, own, ruleEnemy, anchorPose, drive, enemies,
                    facts, rule, out scoredGoalPosition, out scoredGoalAngle,
                    out ManeuverScore score);
                scoredStructureVersion = SubstructureOwnership.GlobalStructureVersion;
                scoredNextGoalTick = now + (facts.incomingSeconds < 2f ? 6 : 30);
                scoredReadySignature = readySignature;
                scoredLastOffenseEnabled = scoredOffenseEnabled;
                scoredHasGoal = true;
                if (ship.CombatAI != null)
                    ship.CombatAI.lastManeuverReason = rule.name + " · "
                        + (scoredOffenseEnabled ? "DeltaV_CombatAIReasonBoth".Translate()
                            : "DeltaV_CombatAIReasonDefense".Translate()) + "\n"
                        + "DeltaV_CombatAIScoreReason".Translate(
                            score.distance.ToString("+0;-0;0"),
                            score.outgoing.ToString("+0;-0;0"),
                            score.incoming.ToString("+0;-0;0"),
                            score.projectile.ToString("+0;-0;0"),
                            score.overlap.ToString("+0;-0;0"),
                            score.alignment.ToString("+0;-0;0"),
                            score.control.ToString("+0;-0;0"));
            }
            const float horizon = 3f;
            Vector2 offset = OrbitSphere.Offset(own.pose.position, scoredGoalPosition);
            intent = new CombatTrajectoryIntent
            {
                authored = true, seconds = horizon,
                startPosition = own.pose.position,
                startVelocity = own.pose.velocity,
                startAngle = own.pose.angle,
                startAngularVelocity = own.pose.angularVelocity,
                futurePosition = scoredGoalPosition,
                futureVelocity = offset / horizon,
                futureAngle = scoredGoalAngle,
                futureAngularVelocity = 0f
            };
            return true;
        }

        private void ChooseScoredGoal(ShipBase ship, SpaceObject own,
            ShipBase ruleEnemy, SpacePose anchorPose, DriveInfo drive,
            List<(ShipBase ship, SpaceObject container)> enemies,
            CombatStrategyFacts facts, CombatStrategyRule rule,
            out Vector3 goalPosition, out float goalAngle, out ManeuverScore bestBreakdown)
        {
            const float horizon = 3f;
            float ownRadius = ShipFireControl.HullRadius(ship);
            var readyWeapons = new List<ScoredWeapon>();
            foreach (CompSpaceWeapon weapon in ship.Weapons)
                {
                    if (weapon.aimMode != SpaceWeaponAimMode.Volley
                        || weapon.WeaponProps.manualOnly || !weapon.SpaceModeEnabled
                        || !weapon.GroupActive
                        || weapon.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                        || !weapon.TryGetSkillLevel(out _)) continue;
                    float damage = SpaceImpactEffects.Resolve(
                        weapon.SelectMagazine()?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef,
                        null)?.defenderEffect?.damage ?? 20f;
                    readyWeapons.Add(new ScoredWeapon(weapon,
                        damage * weapon.WeaponProps.impactScale * 60f
                        / Mathf.Max(1, weapon.WeaponProps.cooldownTicks)));
                }
            float scoreClearance = ownRadius + ShipFireControl.HullRadius(ruleEnemy) + 8f;
            CombatManeuverCurves defaults = CombatManeuverCurveDefaults.For(
                ship, scoreClearance, scoredAnchorDistance);
            CombatManeuverCurves configured = rule.maneuverCurves;
            CombatScoreCurve distanceCurve = configured?.distance ?? defaults.distance;
            CombatScoreCurve outgoingCurve = configured?.outgoing ?? defaults.outgoing;
            CombatScoreCurve incomingCurve = configured?.incoming ?? defaults.incoming;
            CombatScoreCurve projectileCurve = configured?.projectileSeconds ?? defaults.projectileSeconds;
            CombatScoreCurve overlapCurve = configured?.hullOverlap ?? defaults.hullOverlap;
            CombatScoreCurve alignmentCurve = configured?.alignmentError ?? defaults.alignmentError;
            Vector3 anchorFuture = OrbitSphere.Move(anchorPose.position,
                anchorPose.velocity * horizon);
            var enemyPositions = new List<(ShipBase ship, Vector3 future, float radius)>();
            foreach (var enemy in enemies)
            {
                SpacePose pose = ShipRegistry.ShipPose(enemy.ship);
                enemyPositions.Add((enemy.ship,
                    OrbitSphere.Move(pose.position, pose.velocity * horizon),
                    ShipFireControl.HullRadius(enemy.ship)));
            }
            float passiveGuard = 0f;
            var lineNormals = new List<Vector2>();
            foreach (Thing thing in ship.Map.listerThings.AllThings)
            {
                if (!ship.Contains(thing) || thing is not Building building) continue;
                CompSpaceShield shield = building.TryGetComp<CompSpaceShield>();
                if (shield == null) continue;
                if (shield.mode == SpaceShieldMode.Guard)
                    passiveGuard = Mathf.Max(passiveGuard, shield.GuardHitPointsMax > 0
                        ? (float)shield.GuardHitPoints / shield.GuardHitPointsMax : 0f);
                else if (shield.State == SpaceShieldState.LineActive)
                {
                    shield.GetSegment(out _, out _, out Vector2 normal);
                    lineNormals.Add(normal);
                }
            }
            Vector2[] directions = { Vector2.zero, Vector2.right, Vector2.left,
                Vector2.up, Vector2.down, new Vector2(1f, 1f).normalized,
                new Vector2(1f, -1f).normalized, new Vector2(-1f, 1f).normalized,
                new Vector2(-1f, -1f).normalized };
            var angles = new List<float> { own.pose.angle };
            void AddAngle(float value)
            {
                if (angles.Count >= 8 || angles.Any(existing =>
                    Mathf.Abs(SpaceTransform.WrapAngle(value - existing)) < 0.05f)) return;
                angles.Add(value);
            }
            if (scoredOffenseEnabled)
                foreach (var enemy in enemyPositions.OrderBy(e =>
                    OrbitSphere.ArcCells(own.pose.position, e.future)).Take(3))
                {
                    Vector2 toward = OrbitSphere.Offset(own.pose.position,
                        enemy.future);
                    if (toward.sqrMagnitude < 0.01f) continue;
                    float bearing = Mathf.Atan2(toward.y, toward.x);
                    foreach (ScoredWeapon selected in readyWeapons
                        .Where(w => w.weapon.WeaponProps.fireArcDeg < 359f)
                        .OrderByDescending(w => w.damageRate)
                        .Take(3))
                    {
                        if (lineNormals.Count > 0 && angles.Count >= 6) break;
                        AddAngle(SpaceTransform.WrapAngle(bearing
                            - ShipOrientation.AngleFromRot4(selected.weapon.parent.Rotation)));
                    }
                    if (lineNormals.Count > 0 && angles.Count >= 6) break;
                }
            if (enemyPositions.Count > 0)
            {
                var strongest = enemyPositions.OrderByDescending(e => universe.Weapons.EstimateFirepower(
                    e.ship, OrbitSphere.ArcCells(own.pose.position, e.future), ownRadius))
                    .First();
                Vector2 toward = OrbitSphere.Offset(own.pose.position,
                    strongest.future);
                if (toward.sqrMagnitude > 0.01f)
                    foreach (Vector2 normal in lineNormals)
                        AddAngle(SpaceTransform.WrapAngle(
                            Mathf.Atan2(toward.y, toward.x) - Mathf.Atan2(normal.y, normal.x)));
            }
            goalPosition = own.pose.position;
            goalAngle = own.pose.angle;
            float bestScore = float.NegativeInfinity;
            bestBreakdown = default;
            foreach (Vector2 direction in directions)
            {
                float accel = direction == Vector2.zero ? 0f
                    : drive.PreviewBraking(-SpaceTransform.RotateInverse(direction, own.pose.angle));
                Vector2 displacement = own.pose.velocity * horizon
                    + direction * Mathf.Min(12f, 0.3f * accel * horizon * horizon);
                Vector3 position = OrbitSphere.Move(own.pose.position, displacement);
                Vector2 velocity = own.pose.velocity + direction * accel * horizon * 0.35f;
                var sampledEnemies = new List<ScoredEnemy>(enemyPositions.Count);
                float overlapScore = 0f;
                foreach (var enemy in enemyPositions)
                {
                    float distance = OrbitSphere.ArcCells(position, enemy.future);
                    float separation = ownRadius + enemy.radius + 8f;
                    float overlap = Mathf.Clamp01((separation - distance) / separation);
                    overlapScore += overlapCurve.Evaluate(overlap);
                    sampledEnemies.Add(new ScoredEnemy(enemy.future, enemy.radius,
                        distance, universe.Weapons.EstimateFirepower(enemy.ship, distance, ownRadius)));
                }
                // Projectile proximity belongs to the trajectory risk estimate; treating
                // every nearby projectile as a hard obstacle erased all facing decisions.
                float projectileRisk = facts.IncomingAt(position, velocity);
                float primaryDistance = OrbitSphere.ArcCells(position, anchorFuture);
                float distanceScore = distanceCurve.Evaluate(primaryDistance);
                float projectileScore = projectileCurve.Evaluate(Mathf.Min(8f, projectileRisk));
                foreach (float desired in angles)
                {
                    float turn = SpaceTransform.WrapAngle(desired - own.pose.angle);
                    float angular = drive.PreviewAngular(turn >= 0f ? 1 : -1);
                    float reachable = Mathf.Abs(own.pose.angularVelocity) * horizon
                        + 0.35f * angular * horizon * horizon;
                    float angle = SpaceTransform.WrapAngle(own.pose.angle
                        + Mathf.Clamp(turn, -reachable, reachable));
                    ManeuverScore breakdown = ScoreScoredGoal(own, position, velocity, angle,
                        accel, sampledEnemies, readyWeapons, passiveGuard, lineNormals,
                        distanceScore, projectileScore, overlapScore,
                        outgoingCurve, incomingCurve, alignmentCurve);
                    float score = breakdown.Total;
                    if (score <= bestScore) continue;
                    bestScore = score; goalPosition = position; goalAngle = angle;
                    bestBreakdown = breakdown;
                }
            }
        }

        private ManeuverScore ScoreScoredGoal(SpaceObject own, Vector3 position,
            Vector2 velocity, float angle, float accel, List<ScoredEnemy> enemies,
            List<ScoredWeapon> weapons, float passiveGuard, List<Vector2> lineNormals,
            float distanceScore, float projectileScore, float overlapScore,
            CombatScoreCurve outgoingCurve, CombatScoreCurve incomingCurve,
            CombatScoreCurve alignmentCurve)
        {
            float outgoing = 0f, incoming = 0f;
            float alignmentWeighted = 0f, alignmentWeight = 0f;
            var candidatePose = new SpacePose(position, angle) { velocity = velocity };
            foreach (ScoredEnemy enemy in enemies)
            {
                incoming += enemy.power * (1f - 0.6f * DirectionalProtection(
                    position, angle, enemy.future, passiveGuard, lineNormals));
            }
            if (scoredOffenseEnabled)
                foreach (ScoredWeapon weapon in weapons)
                {
                    float bestShot = 0f;
                    float bestArcError = 180f;
                    foreach (ScoredEnemy enemy in enemies)
                    {
                        float arcError = CombatStrategyFacts.ArcError(weapon.weapon, own,
                            candidatePose, enemy.future);
                        bestArcError = Mathf.Min(bestArcError, arcError);
                        if (enemy.distance < weapon.minRange || enemy.distance > weapon.range)
                            continue;
                        float hit = Mathf.Clamp01(enemy.radius /
                            Mathf.Max(enemy.radius, enemy.distance * Mathf.Tan(weapon.spread)));
                        if (weapon.readyNow && arcError <= Mathf.Max(0.1f,
                            weapon.weapon.fireDeviationDeg))
                            bestShot = Mathf.Max(bestShot, weapon.damageRate * hit);
                    }
                    outgoing += bestShot;
                    if (weapon.weapon.WeaponProps.fireArcDeg < 359f)
                    {
                        alignmentWeighted += weapon.damageRate * bestArcError;
                        alignmentWeight += weapon.damageRate;
                    }
                }
            float control = -0.05f * accel
                - 0.02f * Mathf.Abs(SpaceTransform.WrapAngle(angle - own.pose.angle));
            return new ManeuverScore(
                distanceScore,
                scoredOffenseEnabled ? outgoingCurve.Evaluate(outgoing) : 0f,
                incomingCurve.Evaluate(incoming),
                projectileScore,
                overlapScore,
                scoredOffenseEnabled && alignmentWeight > 0f
                    ? alignmentCurve.Evaluate(alignmentWeighted / alignmentWeight) : 0f,
                control);
        }

        private static float DirectionalProtection(Vector3 position, float angle,
            Vector3 enemyPosition, float passiveGuard, List<Vector2> lineNormals)
        {
            float guard = passiveGuard;
            Vector2 toward = OrbitSphere.Offset(position, enemyPosition);
            if (toward.sqrMagnitude < 0.01f) return 0f;
            Vector2 local = SpaceTransform.RotateInverse(toward.normalized, angle);
            foreach (Vector2 normal in lineNormals)
                if (Vector2.Dot(local, normal) > 0.4f) guard = Mathf.Max(guard, 0.7f);
            return Mathf.Clamp01(guard);
        }
    }
}

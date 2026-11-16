using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class ShipAutoManeuverControl
    {
        private int nextTacticTick, nextPoseTick, tacticDriveVersion = -1, tacticStructureVersion = -1;
        private DriveInfo tacticDrive;
        private CombatStrategyRule activeRule;
        private CombatAttackPose attackPose;
        private CombatTrajectoryIntent tacticIntent;
        private CombatManeuverIntent maneuverIntent;
        private bool hasTactic, offenseEnabled, lastOffenseEnabled;
        private float anchorDistance;
        private int turnDirection;
        private bool blockedPose;
        private int tacticConfiguration, tacticGroupMask;
        private readonly CombatReposition reposition;
        private readonly Dictionary<DriveSubject, (int version, SpaceCollision.HullShape shape)> routeShapes =
            new Dictionary<DriveSubject, (int, SpaceCollision.HullShape)>();
        internal bool Boarding { get; private set; }
        internal bool TryPosePreview(ShipBase ship, SpacePose target, out float distance, out float heading, out float phase)
        {
            distance = attackPose.distance;
            heading = SpaceTransform.WrapAngle(attackPose.heading + ship.FrameAngle);
            phase = reposition.PhaseAt(target.position);
            return hasTactic;
        }

        private bool PlanTacticTrajectory(SpaceObject own, SpacePose target,
            float clearance, DriveInfo drive, out CombatTrajectoryIntent intent)
        {
            var performance = universe.Performance;
            bool traceAi = DeltaVLog.Enabled(DeltaVLogModule.AI);
            if (!traceAi) lastAiDecisionState = null;
            using var planTiming = performance.Measure(PerformanceStage.CombatPlan);
            intent = default;
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (ship?.IsValid != true || drive == null || drive.mass <= 0f || drive.inertia <= 0f
                || !anchorTarget.TryResolve(universe, out var enemy)) return false;
            using var previewMemo = drive.ReusePreviews();
            Vector2 anchorLocal = SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter);
            SpacePose self = SpaceTransform.RigidPointPose(own.pose, anchorLocal, 0f);
            int now = Find.TickManager.TicksGame;
            SharedFixedAim = null;
            bool decisionDue = hasTactic && attackPose.behavior == CombatTactic.Automatic
                && activeRule?.tactic?.bearing == CombatBearing.Free && now >= reposition.nextTick;
            var control = universe.Controls.ExistingFor(ship)?.FireControl;
            int groupMask = (ship.Combat?.weaponGroupsMain ?? 0) | ((ship.Combat?.weaponGroupsSecondary ?? 0) << 6);
            bool changed = !hasTactic || tacticConfiguration != (control?.configurationVersion ?? 0)
                || tacticGroupMask != groupMask || lastOffenseEnabled != offenseEnabled;
            bool driveChanged = tacticDrive != drive || tacticDriveVersion != drive.version;
            CombatReplanCause cause = CombatReplanCause.None;
            if (performance.Enabled || traceAi)
            {
                if (!hasTactic) cause |= CombatReplanCause.InitialOrTarget;
                if (tacticConfiguration != (control?.configurationVersion ?? 0)) cause |= CombatReplanCause.Configuration;
                if (tacticGroupMask != groupMask) cause |= CombatReplanCause.Groups;
                if (lastOffenseEnabled != offenseEnabled) cause |= CombatReplanCause.Permission;
                if (tacticDrive != drive || tacticDriveVersion != drive.version) cause |= CombatReplanCause.Drive;
                if (SubstructureOwnership.CacheNeedsRefresh(tacticStructureVersion, ship.Map)) cause |= CombatReplanCause.Structure;
                if (now >= nextPoseTick || decisionDue) cause |= CombatReplanCause.Periodic;
            }
            if (changed || driveChanged || decisionDue || now >= nextTacticTick)
            {
                long ruleStart = performance.Start();
                var facts = control?.Facts(ship, own, changed || driveChanged || decisionDue) ?? new CombatStrategyFacts(universe, ship, own);
                var selection = new CombatSelectionTrace();
                var strategy = control?.Strategy(ship) ?? ship.CombatAI.strategy;
                var previousRule = activeRule;
                var rule = strategy.Maneuver(facts.Context(enemy.Ship), activeRule, selection);
                if (ship.CombatAI != null) ship.CombatAI.lastManeuverSelection = selection;
                performance.Record(PerformanceStage.CombatRule, ruleStart);
                if ((performance.Enabled || traceAi) && activeRule != rule) cause |= CombatReplanCause.Rule;
                bool restartReposition = !hasTactic || activeRule != rule;
                changed |= activeRule != rule;
                CombatTacticSettings settings = rule.tactic ?? new CombatTacticSettings();
                Vector2 toward = OrbitSphere.Offset(self.position, target.position);
                Vector2 outward = OrbitSphere.Offset(target.position, self.position);
                float currentPhase = Mathf.Atan2(outward.y, outward.x);
                float bearing = Mathf.Atan2(toward.y, toward.x);
                Vector2 relative = self.velocity - SpaceTransform.TransportVelocity(target, self.position);
                Vector2 tangent = SpaceTransform.Rotate(new Vector2(0f, -1f), bearing);
                turnDirection = CombatAttackPose.Turn(settings.direction, turnDirection,
                    Vector2.Dot(relative, tangent), ownerId.key);
                // A new physical model refreshes flight feasibility immediately; it
                // does not create an extra tactical search before the decision clock.
                bool poseReevaluated = changed || decisionDue || now >= nextPoseTick;
                var candidatePoses = new Dictionary<float, CombatAttackPose>();
                System.Func<float, float, float> repositionPreference = null;
                bool freeReposition = settings.behavior == CombatTactic.Automatic && settings.bearing == CombatBearing.Free;
                List<EngagementCandidate> engagementCandidates = null;
                List<float> engagementPhases = null;
                if (poseReevaluated)
                {
                    engagementCandidates = EngagementCandidates(ship, enemy.Ship, self, target, drive, rule, settings,
                        settings.coordinateFlanking ? FlankingPhase(ship, target) : null);
                    var selected = engagementCandidates.GroupBy(c => c.phase)
                        .Select(g => g.OrderByDescending(c => c.score).First()).ToList();
                    engagementPhases = selected.Select(c => c.phase).ToList();
                    repositionPreference = (phase, speed) =>
                    {
                        var candidate = selected.FirstOrDefault(c => Mathf.Abs(SpaceTransform.WrapAngle(c.phase - phase)) < .001f);
                        if (candidate == null) return float.NegativeInfinity;
                        candidatePoses[phase] = candidate.pose;
                        return candidate.score;
                    };
                    if (restartReposition && selected.Count > 0) attackPose = selected[0].pose;
                    if (!freeReposition)
                    {
                        var best = engagementCandidates.OrderByDescending(c => c.score).FirstOrDefault();
                        blockedPose = best == null || float.IsNegativeInfinity(best.score);
                        engagementResult = blockedPose ? null : best.value;
                        if (!blockedPose)
                        {
                            attackPose = best.pose; reposition.selectedScore = best.score;
                            if (!attackPose.fireHeading && settings.behavior == CombatTactic.Circle &&
                                settings.bearing == CombatBearing.Free)
                                attackPose.heading = EngagementAction(self, target, attackPose, best.phase, drive, settings).heading;
                        }
                        reposition.candidateCount = engagementCandidates.Count(c => !float.IsNegativeInfinity(c.score));
                        nextPoseTick = now + 120 - ((now + ownerId.key % 120) % 120);
                    }
                }
                float oldPhase = traceAi ? reposition.PhaseAt(target.position) : 0f;
                int oldNextTick = reposition.nextTick;
                if (restartReposition) reposition.Reset();
                CombatRepositionStatus repositionStatus;
                using (performance.Measure(PerformanceStage.CombatReposition))
                    repositionStatus = reposition.Update(self, target, attackPose, settings, drive, now,
                        settings.coordinateFlanking && settings.bearing == CombatBearing.Free ? FlankingPhase(ship, target) : null,
                        repositionPreference, engagementPhases, rule.engagement.switchThreshold);
                if (repositionStatus == CombatRepositionStatus.Selected)
                {
                    blockedPose = reposition.candidateCount == 0;
                    if (candidatePoses.TryGetValue(reposition.phase, out var chosenPose)) attackPose = chosenPose;
                    if (Mathf.Abs(reposition.tangentSpeed) > .01f) turnDirection = reposition.tangentSpeed > 0f ? 1 : -1;
                    if (freeReposition)
                    {
                        engagementLocalBearing = SpaceTransform.WrapAngle(reposition.phase - target.angle);
                        engagementResult = engagementCandidates?.Where(c => Mathf.Abs(SpaceTransform.WrapAngle(c.phase - reposition.phase)) < .001f)
                            .OrderByDescending(c => c.score).FirstOrDefault()?.value;
                        if (engagementResult != null)
                        {
                            engagementResult.alternativeScore = engagementCandidates.Where(c => c.value != engagementResult)
                                .Select(c => c.score).DefaultIfEmpty(float.NegativeInfinity).Max();
                            engagementResult.holding = Mathf.Abs(SpaceTransform.WrapAngle(currentPhase - reposition.phase)) < .001f
                                && Mathf.Abs(outward.magnitude - attackPose.distance) < 5;
                        }
                        nextPoseTick = reposition.nextTick;
                    }
                }
                if (freeReposition)
                {
                    reposition.phase = SpaceTransform.WrapAngle(target.angle + engagementLocalBearing);
                    reposition.basis = target.position;
                }
                if (!offenseEnabled || settings.facing != CombatFacing.Automatic)
                    ClearFixedAim();
                else if (poseReevaluated)
                {
                    if (previousRule != rule) ClearFixedAim();
                    if (engagementResult?.fixedAimWeapon != null)
                    {
                        FixedAimWeapon = engagementResult.fixedAimWeapon;
                        fixedAimTarget = engagementResult.fixedAimTarget;
                        FixedAimPart = engagementResult.fixedAimPart;
                    }
                }
                FixedWeaponAim? aim = SolveFixedAim(ship, own, self, control, now);
                maneuverIntent = EngagementAction(self, target, attackPose, reposition.PhaseAt(target.position), drive, settings, true);
                float coarseHeading = maneuverIntent.heading;
                if (aim.HasValue) maneuverIntent.heading = SpaceTransform.WrapAngle(aim.Value.HullAngle - bearing);
                CombatTrajectoryIntent BuildTrajectory()
                {
                    var translation = maneuverIntent;
                    translation.heading = coarseHeading;
                    var trajectory = CombatFlightPlanner.Plan(self, target, translation, drive, now);
                    trajectory.localAnchor = anchorLocal;
                    return aim.HasValue && !trajectory.holdCurrent
                        ? CombatFlightPlanner.RetargetHeading(trajectory, self, maneuverIntent.heading, drive, 0f)
                        : trajectory;
                }
                tacticIntent = BuildTrajectory();
                bool blockedBeforeRoute = blockedPose;
                int routeShrinks = 0, blockedSecond = 0;
                DriveSubject blocker = default;
                if (!blockedPose && !RouteClear(ship, tacticIntent, out blocker, out blockedSecond))
                {
                    DriveSubject firstBlocker = blocker;
                    int firstBlockedSecond = blockedSecond;
                    // Only automatic/free choices may shrink. Explicit player fields remain intact.
                    if (settings.bearing == CombatBearing.Free && attackPose.behavior == CombatTactic.Automatic)
                    {
                        for (int retry = 0; retry < 2; retry++)
                        {
                            routeShrinks++;
                            if (maneuverIntent.bearing.HasValue)
                                maneuverIntent.bearing = currentPhase + .5f * SpaceTransform.WrapAngle(maneuverIntent.bearing.Value - currentPhase);
                            else maneuverIntent.tangentSpeed *= .5f;
                            tacticIntent = BuildTrajectory();
                            if (RouteClear(ship, tacticIntent, out blocker, out blockedSecond)) break;
                        }
                    }
                    blockedPose = !RouteClear(ship, tacticIntent, out blocker, out blockedSecond);
                    if (!blockedPose) { blocker = firstBlocker; blockedSecond = firstBlockedSecond; }
                }
                if (blockedPose)
                {
                    tacticIntent = CombatFlightPlanner.Brake(own.pose, drive, now);
                    if (aim.HasValue || attackPose.fireHeading && settings.facing == CombatFacing.Automatic)
                    {
                        var aimingBrake = CombatFlightPlanner.BrakeWhileAiming(self, target,
                            aim.HasValue ? maneuverIntent.heading : attackPose.heading, drive, now);
                        aimingBrake.localAnchor = anchorLocal;
                        if (RouteClear(ship, aimingBrake, out _, out _)) tacticIntent = aimingBrake;
                    }
                }
                if (!tacticIntent.holdCurrent || tacticIntent.brakeTarget.HasValue) SharedFixedAim = aim;
                nextTacticTick = now + 30;
                hasTactic = true;
                activeRule = rule;
                lastOffenseEnabled = offenseEnabled;
                tacticConfiguration = control?.configurationVersion ?? 0; tacticGroupMask = groupMask;
                tacticDrive = drive;
                tacticDriveVersion = drive.version;
                tacticStructureVersion = SubstructureOwnership.StructureVersion(ship.Map);
                if (traceAi) TraceAiDecision(ship, own, self, target, rule, settings, cause, restartReposition,
                    repositionStatus, oldPhase, oldNextTick, blockedBeforeRoute, poseReevaluated, routeShrinks, blocker, blockedSecond);
                if (ship.CombatAI != null)
                    ship.CombatAI.lastManeuverReason = CombatDecisionText.Selection(strategy.maneuverRules, rule, previousRule) + " · "
                        + ("DeltaV_CombatTactic_" + attackPose.behavior).Translate() + "\n"
                        + "DeltaV_CombatTacticReason".Translate(attackPose.distance.ToString("0.0"),
                            (SpaceTransform.WrapAngle(attackPose.heading + ship.FrameAngle) * Mathf.Rad2Deg).ToString("0"),
                            ("DeltaV_CombatTurn_" + (turnDirection > 0 ? "Counterclockwise" : "Clockwise")).Translate(),
                            attackPose.firepower.ToString("0"), attackPose.preference.ToString("+0;-0;0"))
                        + "\n" + "DeltaV_CombatPoseStatus".Translate(
                            (reposition.PhaseAt(target.position) * Mathf.Rad2Deg).ToString("0"),
                            (Mathf.Max(0, reposition.nextTick - now) / 60f).ToString("0.0"),
                            attackPose.firepower.ToString("0.0"), attackPose.mobility.ToString("0.0"),
                            $"{attackPose.distanceScore:+0.0;-0.0;0} / {attackPose.outgoingScore:+0.0;-0.0;0} / "
                            + $"{attackPose.incomingScore:+0.0;-0.0;0} / {attackPose.timeScore:+0.0;-0.0;0} / "
                            + $"{attackPose.overlapScore:+0.0;-0.0;0} / {attackPose.alignmentScore:+0.0;-0.0;0}");
                if (blockedPose && ship.CombatAI != null)
                    ship.CombatAI.lastManeuverReason += "\n" + "DeltaV_CombatPoseBlocked".Translate();
                if (engagementResult != null && ship.CombatAI != null)
                    ship.CombatAI.lastManeuverReason += "\n" + "DeltaV_EngagementDecision".Translate(
                        engagementResult.outgoing.ToString("0.#"),
                        engagementResult.incoming.ToString("0.#"), engagementResult.travel.ToString("0.#"),
                        engagementResult.velocity.ToString("0.#"), engagementResult.reverse.ToString("0.#"))
                        + (engagementResult.uncertain ? "\n" + "DeltaV_EngagementUncertain".Translate().ToString() : "");
            }
            else if (SolveFixedAim(ship, own, self, control, now) is FixedWeaponAim aim)
            {
                Vector2 toward = OrbitSphere.Offset(self.position, target.position);
                float heading = SpaceTransform.WrapAngle(aim.HullAngle - Mathf.Atan2(toward.y, toward.x));
                float elapsed = (now - tacticIntent.startTick) / 60f;
                if (!tacticIntent.holdCurrent || tacticIntent.brakeTarget.HasValue)
                {
                    var updated = CombatFlightPlanner.RetargetHeading(tacticIntent, self, heading, drive, elapsed);
                    if (RouteClearAt(ship, updated, elapsed, out _, out _))
                    {
                        maneuverIntent.heading = heading;
                        tacticIntent = updated;
                        SharedFixedAim = aim;
                    }
                }
                if (!SharedFixedAim.HasValue)
                {
                    var brake = CombatFlightPlanner.BrakeWhileAiming(self, target, heading, drive, now);
                    brake.localAnchor = anchorLocal;
                    if (RouteClear(ship, brake, out _, out _))
                    {
                        maneuverIntent.heading = heading;
                        tacticIntent = brake;
                        SharedFixedAim = aim;
                    }
                    else tacticIntent = CombatFlightPlanner.Brake(own.pose, drive, now);
                }
            }
            intent = tacticIntent;
            return true;
        }

        private bool RouteClear(ShipBase ship, CombatTrajectoryIntent intent,
            out DriveSubject blocker, out int blockedSecond) => RouteClearAt(ship, intent, 0f, out blocker, out blockedSecond);

        private bool RouteClearAt(ShipBase ship, CombatTrajectoryIntent intent, float elapsed,
            out DriveSubject blocker, out int blockedSecond)
        {
            blocker = default; blockedSecond = 0;
            using var routeTiming = universe.Performance.Measure(PerformanceStage.CombatRoute);
            SpaceCollision.HullShape Shape(ShipBase member)
            {
                int version = SubstructureOwnership.StructureVersion(member.Map);
                if (!routeShapes.TryGetValue(member.Identity, out var cached) || cached.version != version)
                {
                    using var shapeTiming = universe.Performance.Measure(PerformanceStage.CombatRouteShape);
                    if (routeShapes.Count >= 128) routeShapes.Clear();
                    routeShapes[member.Identity] = cached = (version, new SpaceCollision.HullShape(member));
                }
                return cached.shape;
            }
            var ownShape = Shape(ship);
            Vector3 ownPosition = ShipRegistry.ShipPose(ship).position;
            float horizon = elapsed + 4f;
            float routeReach = intent.holdCurrent ? intent.holdVelocity.magnitude * horizon :
                intent.startRadius + Mathf.Abs(intent.radialStep) + intent.centerVelocity.magnitude * horizon;
            if (!intent.holdCurrent && elapsed > 0f)
                routeReach += Mathf.Abs(intent.initialRadialSpeed) * elapsed + Mathf.Abs(intent.targetRadius - intent.startRadius);
            // Four bounded trajectory checkpoints; the real collision chain remains authoritative.
            foreach (var other in universe.objects.SelectMany(frame => frame.Ships)
                .Where(other => other.IsValid && other.Identity != ownerId && other.Map != ship.Map)
                .OrderBy(other => OrbitSphere.ArcCells(ownPosition,
                    ShipRegistry.ShipPose(other).position)).Take(64))
            {
                var otherPose = ShipRegistry.ShipPose(other);
                var shape = Shape(other);
                if (OrbitSphere.ArcCells(intent.center, otherPose.position) > routeReach + ownShape.half.magnitude
                    + shape.half.magnitude + otherPose.velocity.magnitude * 4f + 10f) continue;
                for (int step = 1; step <= 4; step++)
                {
                    intent.Sample(elapsed + step, out var position, out _, out float angle, out _);
                    var center = SpaceTransform.RigidPointPose(new SpacePose(position, angle), intent.localAnchor, 0f);
                    var future = new SpacePose(OrbitSphere.Move(otherPose.position, otherPose.velocity * step),
                        otherPose.angle - other.FrameAngle + otherPose.angularVelocity * step);
                    if (SpaceCollision.HullsOverlap(ownShape, center.position, angle, shape, future))
                    { blocker = other.Identity; blockedSecond = step; return false; }
                }
            }
            return true;
        }

        private float? FlankingPhase(ShipBase ship, SpacePose target)
        {
            Vector2 sum = Vector2.zero;
            foreach (var frame in universe.objects)
            foreach (var ally in frame.Ships)
            {
                if (!ally.IsValid || ally.Identity == ownerId || ally.Faction != ship.Faction) continue;
                // Lower identity gets the first stable slot; later ships do not make it swap sides.
                if (ally.Identity.shuttle.CompareTo(ownerId.shuttle) > 0
                    || ally.Identity.shuttle == ownerId.shuttle && ally.Identity.key >= ownerId.key) continue;
                var controller = universe.Controls.ExistingFor(ally);
                var maneuver = controller?.AutoManeuver;
                if (maneuver == null || !universe.Autopilot.IsCombatOrbit(ally.Identity)
                    || maneuver.anchorTarget.Ship != anchorTarget.Ship || !maneuver.hasTactic
                    || maneuver.activeRule?.tactic?.coordinateFlanking != true
                    || maneuver.activeRule.tactic.bearing != CombatBearing.Free) continue;
                float phase = maneuver.reposition.PhaseAt(target.position);
                sum += new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
            }
            return sum.sqrMagnitude > 0.01f ? Mathf.Atan2(-sum.y, -sum.x) : (float?)null;
        }

        internal static List<(CombatAttackWeapon gun, SpaceWeaponParameters shot)> CaptureAttackWeapons(ShipBase ship, SpaceObject frame)
        {
            var result = new List<(CombatAttackWeapon, SpaceWeaponParameters)>();
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!weapon.Armed || weapon.aimMode != SpaceWeaponAimMode.Volley || weapon.WeaponProps.manualOnly
                    || !weapon.SpaceModeEnabled || !weapon.GroupActive
                    || !weapon.Operational
                    || !weapon.TryGetSkillLevel(out _)) continue;
                var energy = weapon.QueryEnergy;
                var shot = SpaceWeaponBallistics.Read(weapon, frame, energy, true);
                if (!energy.ready || !weapon.MagazineAvailable(shot.magazine)) continue;
                var props = shot.aim.Projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
                if (props == null || !props.guided && !SpaceWeaponShot.HasClearFixedPath(weapon, weapon.Layout.acceleration)) continue;
                float damage = SpaceWeaponBallistics.ExpectedDamage(weapon, shot);
                if (damage <= 0f) continue;
                var traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
                weapon.CoverageSector(out float mount, out float arc);
                if (traverse != null) arc = Mathf.Min(arc, traverse.TraverseProps.mechanicalArcDeg);
                else if (!weapon.WeaponProps.topTurret && !props.guided)
                    arc = Mathf.Min(arc, 2f * weapon.fireDeviationDeg);
                if (props.guided) arc = Mathf.Min(arc, props.lockArcDeg);
                var gun = new CombatAttackWeapon(shot.aim.MuzzleMap - ship.HullCenter, mount, arc,
                    shot.aim.Range, shot.aim.MinRange, shot.aim.Speed,
                    damage * 60f / Mathf.Max(1, weapon.ShotInterval),
                    shot.aim.Laser || shot.aim.Guided, weapon.CooldownTicksLeft == 0, weapon);
                result.Add((gun, shot));
            }
            return result;
        }

    }
}

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
        private bool hasTactic, offenseEnabled, lastOffenseEnabled;
        private float anchorDistance;
        private int turnDirection;
        private bool blockedPose;
        private int tacticConfiguration, tacticGroupMask;
        private readonly CombatReposition reposition = new CombatReposition();
        private readonly Dictionary<DriveSubject, SpaceCollision.HullShape> routeShapes = new Dictionary<DriveSubject, SpaceCollision.HullShape>();
        private int routeStructureVersion = -1;
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
            using var planTiming = performance.Measure(PerformanceStage.CombatPlan);
            intent = default;
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (ship?.IsValid != true || drive == null || drive.mass <= 0f || drive.inertia <= 0f
                || !anchorTarget.TryResolve(universe, out var enemy)) return false;
            Vector2 anchorLocal = SpaceTransform.MapToShipLocal(ship.Map, ship.HullCenter);
            SpacePose self = SpaceTransform.RigidPointPose(own.pose, anchorLocal, 0f);
            int now = Find.TickManager.TicksGame;
            var control = universe.Controls.ExistingFor(ship)?.FireControl;
            int groupMask = (ship.Combat?.weaponGroupsMain ?? 0) | ((ship.Combat?.weaponGroupsSecondary ?? 0) << 6);
            bool changed = !hasTactic || tacticConfiguration != (control?.configurationVersion ?? 0) || tacticGroupMask != groupMask || lastOffenseEnabled != offenseEnabled
                || tacticDrive != drive || tacticDriveVersion != drive.version
                || SubstructureOwnership.CacheNeedsRefresh(tacticStructureVersion);
            CombatReplanCause cause = CombatReplanCause.None;
            if (performance.Enabled)
            {
                if (!hasTactic) cause |= CombatReplanCause.InitialOrTarget;
                if (tacticConfiguration != (control?.configurationVersion ?? 0)) cause |= CombatReplanCause.Configuration;
                if (tacticGroupMask != groupMask) cause |= CombatReplanCause.Groups;
                if (lastOffenseEnabled != offenseEnabled) cause |= CombatReplanCause.Permission;
                if (tacticDrive != drive || tacticDriveVersion != drive.version) cause |= CombatReplanCause.Drive;
                if (SubstructureOwnership.CacheNeedsRefresh(tacticStructureVersion)) cause |= CombatReplanCause.Structure;
                if (now >= nextPoseTick) cause |= CombatReplanCause.Periodic;
            }
            if (changed || now >= nextTacticTick)
            {
                long ruleStart = performance.Start();
                var facts = control?.Facts(ship, own) ?? new CombatStrategyFacts(universe, ship, own);
                var rule = (control?.Strategy(ship) ?? ship.CombatAI.strategy).Maneuver(facts.Context(enemy.Ship), activeRule);
                performance.Record(PerformanceStage.CombatRule, ruleStart);
                if (performance.Enabled && activeRule != rule) cause |= CombatReplanCause.Rule;
                changed |= activeRule != rule;
                CombatTacticSettings settings = rule.tactic ?? new CombatTacticSettings();
                Vector2 toward = OrbitSphere.Offset(self.position, target.position);
                float bearing = Mathf.Atan2(toward.y, toward.x);
                Vector2 relative = self.velocity - SpaceTransform.TransportVelocity(target, self.position);
                Vector2 tangent = SpaceTransform.Rotate(new Vector2(0f, -1f), bearing);
                turnDirection = CombatAttackPose.Turn(settings.direction, turnDirection,
                    Vector2.Dot(relative, tangent), ownerId.key);
                if (changed || now >= nextPoseTick)
                {
                    long poseStart = performance.Start();
                    var poseSettings = settings.Copy();
                    if (settings.facing != CombatFacing.Automatic)
                    {
                        poseSettings.facing = CombatFacing.Relative;
                        poseSettings.facingDegrees = SpaceTransform.WrapAngle((settings.facing == CombatFacing.Target ? 0f : settings.facingDegrees * Mathf.Deg2Rad)
                            - ship.FrameAngle) * Mathf.Rad2Deg;
                    }
                    var weapons = CaptureAttackWeapons(ship, settings.primaryWeapons);
                    Vector2 ownHalf = CombatWeaponThreat.HullHalfExtents(ship), enemyHalf = CombatWeaponThreat.HullHalfExtents(enemy.Ship);
                    float separation = Mathf.Min(ownHalf.x, ownHalf.y) + Mathf.Min(enemyHalf.x, enemyHalf.y) + 2f;
                    var threats = new List<CombatWeaponThreat>();
                    var others = new List<(Vector3 position, float radius)>();
                    var neighbors = new List<(SpaceCollision.HullShape shape, SpacePose pose)>();
                    var ownShape = new SpaceCollision.HullShape(ship);
                    var enemyShape = new SpaceCollision.HullShape(enemy.Ship);
                    var tacticalEnemies = universe.objects.SelectMany(container => ShipFireControl.Candidates(ship, own, container))
                        .OrderBy(member => OrbitSphere.ArcCells(self.position, ShipRegistry.ShipPose(member).position)).Take(64).ToList();
                    CombatManeuverCurves curves = rule.maneuverCurves;
                    var outputTargets = new List<(CombatAttackWeapon weapon, List<SpacePose> targets)>();
                    if (offenseEnabled && curves?.outgoing != null)
                        foreach (var weapon in CaptureAttackWeapons(ship))
                        {
                            if (!weapon.ready) continue;
                            var targets = new List<SpacePose>();
                            foreach (var other in tacticalEnemies)
                            {
                                var fireRule = control.Strategy(ship).Fire(facts.Context(other, weapon.source));
                                if (fireRule.fireAction == CombatFireAction.Hold || fireRule.anchorOnly && other.Identity != enemy.Ship.Identity) continue;
                                targets.Add(ShipRegistry.ShipPose(other));
                            }
                            outputTargets.Add((weapon, targets));
                        }
                    bool risk = true;
                    if (risk)
                            foreach (ShipBase other in tacticalEnemies)
                            {
                                SpacePose otherPose = ShipRegistry.ShipPose(other);
                                others.Add((OrbitSphere.Move(otherPose.position, otherPose.velocity * 3f),
                                    ShipFireControl.HullRadius(other)));
                                CombatWeaponThreat.Capture(other, universe.FrameOf(other.Map), 3f, threats);
                            }
                    foreach (var member in universe.objects.SelectMany(frame => frame.Ships)
                        .Where(member => member.IsValid && member.Identity != ownerId && member.Faction == ship.Faction)
                        .OrderBy(member => OrbitSphere.ArcCells(self.position, ShipRegistry.ShipPose(member).position)).Take(64))
                                {
                                SpacePose neighborPose = ShipRegistry.ShipPose(member);
                                neighborPose.angle -= member.FrameAngle;
                                neighbors.Add((new SpaceCollision.HullShape(member), neighborPose));
                            }
                    Vector2 halfExtents = risk ? CombatWeaponThreat.HullHalfExtents(ship) : Vector2.zero;
                    float ownRadius = ShipFireControl.HullRadius(ship);
                    float incomingScore = 0f, timeScore = 0f, overlapScore = 0f;
                    void PoseAt(CombatAttackPose p, out Vector3 position, out float angle)
                    {
                        float phase = reposition.initialized ? reposition.PhaseAt(target.position) : Mathf.Atan2(-toward.y, -toward.x);
                        if (settings.bearing != CombatBearing.Free) phase = (settings.bearing == CombatBearing.Enemy ? target.angle : 0f)
                            + settings.bearingDegrees * Mathf.Deg2Rad;
                        Vector2 away = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
                        position = OrbitSphere.Move(OrbitSphere.Move(target.position, target.velocity * 3f), away * p.distance);
                        angle = SpaceTransform.WrapAngle(phase + Mathf.PI + p.heading);
                    }
                    float Output(CombatAttackPose p)
                    {
                        using var timing = performance.Measure(PerformanceStage.CombatOutput);
                        PoseAt(p, out Vector3 position, out float heading);
                        Vector2 velocity = SpaceTransform.TransportVelocity(target, position);
                        float output = 0f;
                        foreach (var entry in outputTargets)
                            foreach (var other in entry.targets)
                            {
                                Vector3 otherPosition = OrbitSphere.Move(other.position, other.velocity * 3f);
                                Vector2 delta = OrbitSphere.Offset(position, otherPosition);
                                float otherBearing = Mathf.Atan2(delta.y, delta.x);
                                if (!entry.weapon.Aim(delta.magnitude, SpaceTransform.WrapAngle(heading - otherBearing),
                                    SpaceTransform.RotateInverse(SpaceTransform.TransportVelocity(other, position) - velocity, otherBearing), out _)) continue;
                                output += entry.weapon.damageRate;
                                break; // Each ready gun contributes its best allowed target once.
                            }
                        return output;
                    }
                    float Risk(CombatAttackPose p)
                    {
                        using var timing = performance.Measure(PerformanceStage.CombatRisk);
                        if (!risk) return 0f;
                        float phase = reposition.initialized ? reposition.PhaseAt(target.position)
                            : Mathf.Atan2(-toward.y, -toward.x);
                        if (settings.bearing != CombatBearing.Free) phase = (settings.bearing == CombatBearing.Enemy ? target.angle : 0f)
                            + settings.bearingDegrees * Mathf.Deg2Rad;
                        Vector2 away = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase));
                        Vector3 center = OrbitSphere.Move(target.position, target.velocity * 3f);
                        Vector3 position = OrbitSphere.Move(center, away * p.distance);
                        float candidateAngle = SpaceTransform.WrapAngle(Mathf.Atan2(-away.y, -away.x) + p.heading);
                        if (SpaceCollision.HullsOverlap(ownShape, position, candidateAngle, enemyShape,
                            new SpacePose(center, target.angle - enemy.Ship.FrameAngle))) return -1000000f;
                        foreach (var neighbor in neighbors)
                            if (SpaceCollision.HullsOverlap(ownShape, position, candidateAngle, neighbor.shape, neighbor.pose)) return -1000000f;
                        float rate = 0f, impact = 8f, overlap = 0f;
                        Vector2 velocity = SpaceTransform.TransportVelocity(target, position);
                        foreach (var threat in threats)
                        {
                            threat.Score(position, velocity, halfExtents, candidateAngle,
                                Vector2.zero, out float dps, out float seconds);
                            rate += dps;
                            if (dps > 0f) impact = Mathf.Min(impact, seconds);
                        }
                        foreach (var other in others)
                            overlap += curves?.hullOverlap?.Evaluate(Mathf.Clamp01(1f
                                - OrbitSphere.ArcCells(position, other.position) / (ownRadius + other.radius + 8f))) ?? 0f;
                        incomingScore = curves?.incoming?.Evaluate(rate) ?? -5f * rate / (1f + rate);
                        timeScore = curves?.projectileSeconds?.Evaluate(impact) ?? 0f;
                        overlapScore = overlap;
                        return incomingScore + timeScore + overlapScore;
                    }
                    CombatAttackPose previous = attackPose;
                    performance.Record(PerformanceStage.CombatPoseSetup, poseStart);
                    long searchStart = performance.Start();
                    attackPose = CombatAttackPose.Choose(weapons, poseSettings, drive,
                        SpaceTransform.RotateInverse(-relative, bearing), separation,
                        curves?.distance != null ? toward.magnitude : anchorDistance,
                        SpaceTransform.WrapAngle(self.angle - bearing), turnDirection,
                        offenseEnabled, curves, changed ? (CombatAttackPose?)null : attackPose, Risk,
                        offenseEnabled && curves?.outgoing != null ? Output : (System.Func<CombatAttackPose, float>)null);
                    performance.Record(PerformanceStage.CombatPoseSearch, searchStart);
                    blockedPose = Risk(attackPose) <= -1000000f;
                    attackPose.incomingScore = incomingScore; attackPose.timeScore = timeScore; attackPose.overlapScore = overlapScore;
                    performance.RecordCombatPose(performance.Elapsed(poseStart), ownerId, ship.Map, cause,
                        weapons.Count, tacticalEnemies.Count, threats.Count, neighbors.Count);
                    if (DeltaVLog.Enabled(DeltaVLogModule.Development)
                        && (changed || attackPose.behavior != previous.behavior
                            || Mathf.Abs(attackPose.distance - previous.distance) > 10f
                            || Mathf.Abs(SpaceTransform.WrapAngle(attackPose.heading - previous.heading)) > 0.5f))
                        DeltaVLog.Message(DeltaVLogModule.Development,
                            $"combat-tactic ship={ownerId} {UniverseManager.MapTag(ship.Map)} rule='{rule.name}' "
                            + $"behavior={attackPose.behavior} distance={toward.magnitude:F1}->{attackPose.distance:F1} "
                            + $"relativeHeading={attackPose.heading * Mathf.Rad2Deg:F0} turn={turnDirection} "
                            + $"coverage={attackPose.firepower:F1} ready={attackPose.readyOutput:F1} "
                            + $"mobility={attackPose.mobility:F2} preference={attackPose.preference:F1} offense={offenseEnabled}");
                    nextPoseTick = now + 120 - ((now + ownerId.key % 120) % 120);
                }
                if (changed) reposition.Reset(ownerId.key);
                using (performance.Measure(PerformanceStage.CombatReposition))
                    reposition.Update(self, target, attackPose, settings, drive, now,
                        settings.coordinateFlanking && settings.bearing == CombatBearing.Free ? FlankingPhase(ship, target) : null);
                tacticIntent = CombatAttackPose.Trajectory(self, target, attackPose,
                    drive, turnDirection, settings.movement, now, reposition.PhaseAt(target.position),
                    settings.bearing == CombatBearing.Enemy ? target.angularVelocity : 0f);
                tacticIntent.localAnchor = anchorLocal;
                if (!blockedPose && !RouteClear(ship, tacticIntent))
                {
                    // Only automatic/free choices may shrink. Explicit player fields remain intact.
                    if (settings.bearing == CombatBearing.Free && attackPose.behavior == CombatTactic.Automatic)
                    {
                        float currentPhase = Mathf.Atan2(-toward.y, -toward.x);
                        for (int retry = 0; retry < 2; retry++)
                        {
                            reposition.phase = currentPhase + SpaceTransform.WrapAngle(reposition.phase - currentPhase) * 0.5f;
                            tacticIntent = CombatAttackPose.Trajectory(self, target, attackPose, drive,
                                turnDirection, settings.movement, now, reposition.phase);
                            tacticIntent.localAnchor = anchorLocal;
                            if (RouteClear(ship, tacticIntent)) break;
                        }
                    }
                    blockedPose = !RouteClear(ship, tacticIntent);
                }
                if (blockedPose)
                    tacticIntent = new CombatTrajectoryIntent { holdCurrent = true, center = own.pose.position,
                        frameAngle = own.pose.angle, seconds = 4f, startTick = now };
                nextTacticTick = now + 30;
                hasTactic = true;
                activeRule = rule;
                lastOffenseEnabled = offenseEnabled;
                tacticConfiguration = control?.configurationVersion ?? 0; tacticGroupMask = groupMask;
                tacticDrive = drive;
                tacticDriveVersion = drive.version;
                tacticStructureVersion = SubstructureOwnership.GlobalStructureVersion;
                if (ship.CombatAI != null)
                    ship.CombatAI.lastManeuverReason = rule.name + " · "
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
            }
            intent = tacticIntent;
            return true;
        }

        private bool RouteClear(ShipBase ship, CombatTrajectoryIntent intent)
        {
            using var routeTiming = universe.Performance.Measure(PerformanceStage.CombatRoute);
            if (SubstructureOwnership.CacheNeedsRefresh(routeStructureVersion))
            { routeShapes.Clear(); routeStructureVersion = SubstructureOwnership.GlobalStructureVersion; }
            SpaceCollision.HullShape Shape(ShipBase member)
            {
                if (!routeShapes.TryGetValue(member.Identity, out var shape))
                {
                    using var shapeTiming = universe.Performance.Measure(PerformanceStage.CombatRouteShape);
                    routeShapes[member.Identity] = shape = new SpaceCollision.HullShape(member);
                }
                return shape;
            }
            var ownShape = Shape(ship);
            // Four bounded trajectory checkpoints; the real collision chain remains authoritative.
            foreach (var other in universe.objects.SelectMany(frame => frame.Ships)
                .Where(other => other.IsValid && other.Identity != ownerId && other.Map != ship.Map)
                .OrderBy(other => OrbitSphere.ArcCells(ShipRegistry.ShipPose(ship).position,
                    ShipRegistry.ShipPose(other).position)).Take(64))
            {
                var otherPose = ShipRegistry.ShipPose(other);
                var shape = Shape(other);
                if (OrbitSphere.ArcCells(intent.center, otherPose.position) > intent.startRadius
                    + Mathf.Abs(intent.radialStep) + ownShape.half.magnitude
                    + shape.half.magnitude + otherPose.velocity.magnitude * 4f + 10f) continue;
                for (int step = 1; step <= 4; step++)
                {
                    intent.Sample(step, out var position, out _, out float angle, out _);
                    var center = SpaceTransform.RigidPointPose(new SpacePose(position, angle), intent.localAnchor, 0f);
                    var future = new SpacePose(OrbitSphere.Move(otherPose.position, otherPose.velocity * step),
                        otherPose.angle - other.FrameAngle + otherPose.angularVelocity * step);
                    if (SpaceCollision.HullsOverlap(ownShape, center.position, angle, shape, future)) return false;
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

        internal static List<CombatAttackWeapon> CaptureAttackWeapons(ShipBase ship, CombatWeaponFilter filter = null)
        {
            var result = new List<CombatAttackWeapon>();
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (filter != null && !filter.Includes(weapon)) continue;
                if (weapon.aimMode != SpaceWeaponAimMode.Volley || weapon.WeaponProps.manualOnly
                    || !weapon.SpaceModeEnabled || !weapon.GroupActive
                    || weapon.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                    || !weapon.TryGetSkillLevel(out _)) continue;
                var magazine = weapon.SelectMagazine();
                if (!weapon.MagazineAvailable(magazine) || !SpaceWeaponBallistics.HasBaseShotEnergy(weapon)) continue;
                ThingDef projectile = magazine?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                var props = projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
                if (props == null) continue;
                var layout = weapon.Layout;
                if (!props.guided && !SpaceWeaponShot.HasClearFixedPath(weapon, layout.acceleration)) continue;
                float damage = SpaceImpactEffects.Resolve(projectile, null)?.defenderEffect?.damage ?? 0f;
                if (damage <= 0f) continue;
                float speed = SpaceWeaponBallistics.ProjectedSpeed(weapon, SpaceWeaponService.ProjectileSpeedOf(projectile));
                var traverse = weapon.parent.TryGetComp<CompSpaceWeaponTraverse>();
                weapon.CoverageSector(out float mount, out float arc);
                if (traverse != null) arc = Mathf.Min(arc, traverse.TraverseProps.mechanicalArcDeg);
                else if (!weapon.WeaponProps.topTurret && !props.guided)
                    arc = Mathf.Min(arc, 2f * weapon.fireDeviationDeg);
                if (props.guided) arc = Mathf.Min(arc, props.lockArcDeg);
                Vector2 muzzle = layout.acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon);
                result.Add(new CombatAttackWeapon(muzzle - ship.HullCenter,
                    mount, arc,
                    weapon.EffectiveRange, weapon.WeaponProps.minRange, speed,
                    damage * weapon.WeaponProps.impactScale * 60f / Mathf.Max(1, weapon.WeaponProps.cooldownTicks),
                    props.laserRange > 0f || props.guided, CombatStrategyFacts.WeaponReady(weapon), weapon));
            }
            return result;
        }

    }
}

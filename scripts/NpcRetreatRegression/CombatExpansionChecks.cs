using System;
using System.IO;
using System.Xml.Serialization;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckCombatExpansion()
    {
        CheckCombatDecisionTrace();
        foreach (var cells in new[] { Array.Empty<Verse.IntVec3>(), new[] { new Verse.IntVec3(-7, 0, -3) },
            new[] { new Verse.IntVec3(-4, 0, -8), new Verse.IntVec3(6, 0, 3), new Verse.IntVec3(1, 0, -4) } })
        {
            var expected = Verse.CellRect.FromCellList(new System.Collections.Generic.List<Verse.IntVec3>(cells)).CenterVector3;
            Check(SpaceTransform.BoundsCenterOfCells(cells) == new Vector2(expected.x, expected.z),
                "bounds center preserves vanilla empty/negative/mixed coordinate semantics");
        }
        CombatStrategyContext Shield(float shield) => new CombatStrategyContext(1, 1, shield, 0,
            float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
        var rule = new CombatStrategyRule { name = "shield hold" };
        rule.conditions.Add(new CombatCriterion { metric = CombatMetric.OwnShieldFraction,
            value = .25f, hysteresis = true, exitValue = .5f });
        var strategy = new CombatStrategy(); strategy.maneuverRules.Add(rule);
        Check(strategy.Maneuver(Shield(.2f)) == rule && strategy.Maneuver(Shield(.4f), rule) == rule
            && strategy.Maneuver(Shield(.6f), rule) != rule, "shield rule enters at 25%, retains below 50%, then releases");
        var emergency = new CombatStrategyRule { name = "priority" }; strategy.maneuverRules.Insert(0, emergency);
        Check(strategy.Maneuver(Shield(.4f), rule) == emergency, "higher priority rule preempts a hysteresis latch");
        Check(!new CombatCriterion().Matches(Shield(1)) && !new CombatCriterion
            { metric = CombatMetric.WeaponCooldownSeconds }.Matches(Shield(1)), "missing target and weapon facts cannot masquerade as zero cooldown or distance");
        rule.conditions[0].weapons.set = CombatWeaponSet.Main; rule.conditions[0].weapons.group = 2;
        var fire = new CombatStrategyRule { fireAction = CombatFireAction.Hold, part = CombatPart.Power, anchorOnly = true };
        strategy.fireRules.Add(fire); strategy.targetRules.Add(new CombatStrategyRule { targetOrder = CombatTargetOrder.Threat, includeDisarmed = true });
        strategy.maneuverRules[1].tactic.bearing = CombatBearing.Enemy;
        strategy.maneuverRules[1].tactic.bearingDegrees = 90;
        strategy.maneuverRules[1].tactic.behavior = CombatTactic.HoldRange;
        strategy.Validate();
        var serializer = new XmlSerializer(typeof(CombatStrategy));
        using var text = new StringWriter(); serializer.Serialize(text, strategy);
        var loaded = (CombatStrategy)serializer.Deserialize(new StringReader(text.ToString())); loaded.Validate();
        Check(loaded.format == 3 && loaded.fireRules[0].fireAction == CombatFireAction.Hold
            && loaded.targetRules[0].includeDisarmed && loaded.maneuverRules[1].tactic.bearingDegrees == 90
            && loaded.maneuverRules[1].conditions[0].exitValue == .5f,
            "format 3 XML preserves target/fire actions, bearing, selectors and release thresholds");
        loaded.maneuverRules[1].conditions[0].weapons.group = 6;
        Check(rule.conditions[0].weapons.group == 2, "deserialized selectors are independent per ship");
        var comp = new CompShipCombatAI(); var original = comp.strategy;
        loaded.format = 2; bool rejected = false;
        try { comp.Replace(loaded); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && comp.strategy == original, "format 2 import is rejected without replacing configuration");
        loaded.format = 3; loaded.maneuverRules[1].tactic.behavior = CombatTactic.Circle; rejected = false;
        try { comp.Replace(loaded); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && comp.strategy == original, "fixed bearing plus continuous circle is rejected atomically");

        var target = new SpacePose(Vector3.forward, .3f);
        var own = new SpacePose(OrbitSphere.Move(target.position, new Vector2(100, 0)), 0);
        var settings = new CombatTacticSettings { behavior = CombatTactic.HoldRange,
            bearing = CombatBearing.Enemy, bearingDegrees = 90f };
        var pose = new CombatAttackPose { behavior = CombatTactic.HoldRange, distance = 100 };
        var state = new CombatReposition(); state.Reset(17);
        state.Update(own, target, pose, settings, null, 100);
        Check(Mathf.Abs(SpaceTransform.WrapAngle(state.phase - target.angle - Mathf.PI / 2f)) < .001f,
            "enemy-relative bearing follows enemy nose rather than own facing");
        settings.bearing = CombatBearing.World; state.Update(own, target, pose, settings, null, 110);
        target.angle = 1.5f; state.Update(own, target, pose, settings, null, 120);
        Check(Mathf.Abs(state.phase - Mathf.PI / 2f) < .001f, "world-relative bearing ignores enemy rotation");
        settings = new CombatTacticSettings(); pose.behavior = CombatTactic.Automatic; state.Reset(17);
        state.Update(own, target, pose, settings, null, 200); int next = state.nextTick; float phase = state.phase;
        state.Update(own, target, pose, settings, null, 230);
        Check(state.nextTick == next && state.phase == phase && next >= 500 && next <= 800,
            "trajectory refresh does not resample the 5–10 second timer; zero thrust invents no displacement");
        var hold = new CombatTrajectoryIntent { seconds = 4, holdCurrent = true, center = own.position, frameAngle = .7f };
        hold.Sample(3, out var pos, out var velocity, out var angle, out var omega);
        Check(OrbitSphere.ArcCells(pos, own.position) < .001f && velocity == Vector2.zero
            && angle == .7f && omega == 0f, "infeasible pose requests a stationary safe reference without changing configuration");
        var trajectory = new CombatTrajectoryIntent { seconds = 4, center = target.position,
            centerVelocity = new Vector2(2, 1), startRadius = 100, phaseAdvance = .2f, frameAngle = .4f };
        trajectory.Sample(2, out var hullPosition, out var hullVelocity, out var hullAngle, out var hullOmega);
        trajectory.localAnchor = new Vector2(7, -4);
        trajectory.Sample(2, out pos, out velocity, out angle, out omega);
        var recovered = SpaceTransform.RigidPointPose(new SpacePose(pos, angle)
            { velocity = velocity, angularVelocity = omega }, trajectory.localAnchor, 0f);
        Check(OrbitSphere.ArcCells(recovered.position, hullPosition) < .01f
            && (recovered.velocity - hullVelocity).magnitude < .01f && Mathf.Abs(recovered.angle - hullAngle) < .001f,
            "combat reference converts hull center to map origin through the shared rigid-point transform");
    }
}

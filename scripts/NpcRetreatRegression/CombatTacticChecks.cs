using System;
using System.IO;
using System.Xml.Serialization;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckCombatTactics()
    {
        CheckCombatExpansion();
        bool rangeBoundsSafe = true;
        foreach (float range in new[] { 5f, 100f })
        foreach (float minimum in new[] { 0f, 4f })
        foreach (float centerDistance in new[] { 0f, 1f, 5f, 90f, 100f, 105f, 130f })
        foreach (float radius in new[] { 0f, 2f, 12f })
        for (int angle = 0; angle < 24; angle++)
        {
            var ray = new CombatAttackWeapon(new Vector2(3, 4), 0, 360, range, minimum, 60, 1, true, true);
            for (int point = 0; point < 24; point++)
            {
                Vector2 part = new Vector2(centerDistance, 0) + SpaceTransform.Rotate(Vector2.right * radius, point * Mathf.PI / 12);
                float bearing = Mathf.Atan2(part.y, part.x);
                if (ray.Aim(part.magnitude, angle * Mathf.PI / 12 - bearing, Vector2.zero, out _))
                    rangeBoundsSafe &= !ray.OutsideInstantRange(centerDistance, radius);
            }
        }
        Check(rangeBoundsSafe, "instantaneous hull range bounds never reject a reachable part across offsets, attitudes and both range edges");
        var fixedGun = new CombatAttackWeapon(new Vector2(0f, 8f), 0f, 2f,
            160f, 0f, 45f, 100f, false, false);
        float lead = fixedGun.LeadHeading(100, new Vector2(0, 8));
        Check(fixedGun.Aim(100, lead, new Vector2(0, 8), out _),
            "shared finite-speed aim respects muzzle offset and transverse relative velocity");
        var broadside = new CombatAttackWeapon(Vector2.zero, Mathf.PI / 2, 5,
            120, 0, 45, 100, true, true);
        Check(broadside.Aim(100, -Mathf.PI / 2, Vector2.zero, out _)
            && !broadside.Aim(100, 0, Vector2.zero, out _),
            "shared arc test distinguishes side-mounted fire from a forward attitude");
        var imposed = new CombatTacticSettings { behavior = CombatTactic.Circle, facing = CombatFacing.Relative,
            facingDegrees = 90f, distance = 75f, direction = CombatTurn.Clockwise, movement = 0.6f };
        Check(CombatAttackPose.Turn(CombatTurn.Automatic, 1, -20f, 1) == 1
            && CombatAttackPose.Turn(CombatTurn.Clockwise, 1, 20f, 2) == -1,
            "automatic turn direction persists while an explicit direction overrides it");
        var strategy = new CombatStrategy();
        strategy.maneuverRules.Add(new CombatStrategyRule { tactic = imposed });
        var copy = strategy.Copy();
        copy.maneuverRules[0].tactic.distance = 200f;
        Check(strategy.maneuverRules[0].tactic.distance == 75f, "tactic copies remain per-ship independent");
        var serializer = new XmlSerializer(typeof(CombatStrategy));
        using var text = new StringWriter();
        serializer.Serialize(text, strategy);
        var restored = (CombatStrategy)serializer.Deserialize(new StringReader(text.ToString()));
        restored.Validate();
        Check(restored.maneuverRules[0].tactic.facingDegrees == 90f
            && restored.maneuverRules[0].tactic.direction == CombatTurn.Clockwise,
            "production XML preserves per-rule tactic parameters");
        var comp = new CompShipCombatAI();
        CombatStrategy original = comp.strategy;
        restored.maneuverRules[0].tactic.movement = float.NaN;
        bool rejected = false;
        try { comp.Replace(restored); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && ReferenceEquals(original, comp.strategy), "invalid tactic import preserves current strategy");

        var console = new CompShipCombatAI { strategy = strategy.Copy() };
        var shuttle = new CompShipCombatAI { strategy = strategy.Copy() };
        console.strategy.targetRules.Add(new CombatStrategyRule { targetOrder = CombatTargetOrder.Threat, includeDisarmed = true });
        console.strategy.fireRules.Add(new CombatStrategyRule { fireAction = CombatFireAction.Hold, part = CombatPart.Weapons });
        console.strategy.maneuverRules[0].tactic.primaryWeapons.group = 3;
        console.strategy.maneuverRules[0].engagement = CombatEngagementSettings.Default();
        console.strategy.maneuverRules[0].engagement.terms[0].weapons.family = CombatWeaponFamily.Laser;
        console.strategy.maneuverRules[0].engagement.terms[0].weight = 2.5f;
        console.strategy.maneuverRules[0].conditions.Add(new CombatCriterion { metric = CombatMetric.OwnShieldFraction,
            scope = CombatScope.EnemyShip, value = .25f, hysteresis = true, exitValue = .5f });
        var coverage = new CombatWeaponCoverage { range = 70f, arc = 45f, direction = 20f, fixedDirection = true };
        shuttle.strategy.maneuverRules[0].tactic.distance = 95f;
        string path = Path.Combine(Path.GetTempPath(), "delta-v-tactics-" + Guid.NewGuid() + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "ships");
            Scribe.EnterNode("console"); console.PostExposeData(); Scribe.ExitNode();
            Scribe.EnterNode("shuttle"); shuttle.PostExposeData(); Scribe.ExitNode();
            Scribe_Deep.Look(ref coverage, "coverage");
            Scribe.saver.FinalizeSaving();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var loadedConsole = new CompShipCombatAI();
                var loadedShuttle = new CompShipCombatAI();
                CombatWeaponCoverage loadedCoverage = null;
                Scribe.loader.InitLoading(path);
                Scribe.EnterNode("console"); loadedConsole.PostExposeData(); Scribe.ExitNode();
                Scribe.EnterNode("shuttle"); loadedShuttle.PostExposeData(); Scribe.ExitNode();
                Scribe_Deep.Look(ref loadedCoverage, "coverage");
                Scribe.loader.FinalizeLoading();
                Check(loadedConsole.strategy.maneuverRules[0].tactic.distance == 75f
                    && loadedShuttle.strategy.maneuverRules[0].tactic.distance == 95f
                    && loadedConsole.strategy.maneuverRules[0].tactic.direction == CombatTurn.Clockwise,
                    "real per-building Scribe reload preserves independent tactics, instance " + repeat);
                Check(loadedConsole.strategy.targetRules[0].includeDisarmed
                    && loadedConsole.strategy.fireRules[0].fireAction == CombatFireAction.Hold
                    && loadedConsole.strategy.maneuverRules[0].tactic.primaryWeapons.group == 3
                    && loadedConsole.strategy.maneuverRules[0].conditions[0].exitValue == .5f
                    && loadedCoverage.range == 70f && loadedCoverage.arc == 45f && loadedCoverage.fixedDirection,
                    "Scribe preserves target rules, hold fire, selectors, hysteresis and weapon coverage, instance " + repeat);
                loadedConsole.strategy.maneuverRules[0].tactic.distance = 10f;
                var loadedEngagement = loadedConsole.strategy.maneuverRules[0].engagement;
                Check(loadedEngagement.terms.Count == console.strategy.maneuverRules[0].engagement.terms.Count
                    && loadedEngagement.terms[0].weight == 2.5f && loadedEngagement.terms[0].weapons.family == CombatWeaponFamily.Laser
                    && loadedEngagement.terms[5].curve.Evaluate(.3f) == console.strategy.maneuverRules[0].engagement.terms[5].curve.Evaluate(.3f)
                    && loadedShuttle.strategy.maneuverRules[0].engagement.terms.Count == 0,
                    "new-save Scribe retains independent engagement curves, filters and weights, instance " + repeat);
            }
        }
        finally { Scribe.ForceStop(); File.Delete(path); }
    }
}

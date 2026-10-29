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
        var settings = new CombatTacticSettings();
        var fixedGun = new CombatAttackWeapon(new Vector2(0f, 8f), 0f, 2f,
            160f, 0f, 45f, 100f, false, false);
        CombatAttackPose Choose(CombatAttackWeapon[] weapons, CombatTacticSettings s = null,
            Vector2 velocity = default, bool offense = true, CombatManeuverCurves curves = null,
            CombatAttackPose? previous = null) => CombatAttackPose.Choose(weapons,
                s ?? settings, null, velocity, 30f, 100f, 0f, 1, offense, curves, previous);
        var railPose = Choose(new[] { fixedGun }, velocity: new Vector2(0f, 8f));
        Check(railPose.behavior == CombatTactic.Automatic && railPose.firepower > 99f
            && railPose.readyOutput == 0f && fixedGun.Aim(railPose.distance,
                railPose.heading, new Vector2(0f, 8f), out _),
            "cooling fixed gun retains useful range and exact muzzle-offset lead heading");
        var broadside = new CombatAttackWeapon(Vector2.zero, Mathf.PI / 2f, 5f,
            120f, 0f, 45f, 100f, true, true);
        var sidePose = Choose(new[] { broadside });
        Check(sidePose.firepower > 99f && Mathf.Abs(sidePose.heading + Mathf.PI / 2f) < 0.1f,
            "side-mounted battery selects a broadside attack pose");
        var turret = new CombatAttackWeapon(Vector2.zero, 0f, 360f, 120f, 0f, 45f, 100f, true, true);
        var turretPose = Choose(new[] { turret });
        Check(turretPose.behavior == CombatTactic.Automatic && turretPose.distance <= 120f,
            "turret-only automatic tactic selects repositioning in weapon range");
        var imposed = new CombatTacticSettings { behavior = CombatTactic.Circle, facing = CombatFacing.Relative,
            facingDegrees = 90f, distance = 75f, direction = CombatTurn.Clockwise, movement = 0.6f };
        var imposedPose = Choose(new[] { fixedGun }, imposed);
        Check(imposedPose.behavior == CombatTactic.Circle && imposedPose.distance == 75f
            && Mathf.Abs(imposedPose.heading - Mathf.PI / 2f) < 0.001f,
            "player tactic overrides automatic range and heading");
        var defense = Choose(new[] { turret }, offense: false);
        Check(defense.behavior == CombatTactic.Automatic && defense.distance <= 120f,
            "maneuver-only mode keeps a useful range without an offensive heading requirement");
        var fleeing = new CombatManeuverCurves { distance = new CombatScoreCurve(true,
            new CombatScorePoint(0f, 0f), new CombatScorePoint(100f, 1000f)) };
        var fleePose = Choose(new[] { turret }, curves: fleeing);
        var farther = CombatAttackPose.Choose(new[] { turret }, settings, null, Vector2.zero,
            30f, fleePose.distance + 20f, 0f, 1, true, fleeing, fleePose);
        Check(farther.distance > fleePose.distance && fleePose.distance > 120f,
            "explicit rising distance curve continues opening beyond weapon range");
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
            }
        }
        finally { Scribe.ForceStop(); File.Delete(path); }
    }
}

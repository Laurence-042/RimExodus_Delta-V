using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using DeltaV;

internal static partial class Program
{
    static void CheckCombatDecisionTrace()
    {
        var strategy = new CombatStrategy();
        var disabled = new CombatStrategyRule { enabled = false };
        disabled.conditions.Add(new CombatCriterion());
        var all = new CombatStrategyRule { name = "all" };
        all.conditions.Add(new CombatCriterion { value = 20 });
        all.conditions.Add(new CombatCriterion { value = 99 });
        var any = new CombatStrategyRule { name = "any", matchAny = true };
        any.conditions.Add(new CombatCriterion { value = 10 });
        any.conditions.Add(new CombatCriterion { value = 40, hysteresis = true, exitValue = 60 });
        any.conditions.Add(new CombatCriterion { value = 99 });
        strategy.maneuverRules.AddRange(new[] { disabled, all, any, new CombatStrategyRule() });
        int calls = 0;
        CombatStrategyContext Context(float distance) => new CombatStrategyContext(1, 1, 1, 1, 0, 1, 1, 0,
            distance, 0, 8, (c, retaining) => { calls++; return c.Compare(distance, retaining); });
        var trace = new CombatSelectionTrace();
        var selected = strategy.Maneuver(Context(30), null, trace);
        Check(selected == any && calls == 3 && trace.rules.Count == 3,
            "decision trace preserves priority and does not reevaluate predicates or evaluate later rules");
        Check(trace.rules[0].conditions.Count == 0 && !trace.rules[0].matched
            && trace.rules[1].conditions.SequenceEqual(new[] { false })
            && trace.rules[2].conditions.SequenceEqual(new[] { false, true }),
            "trace distinguishes disabled rules, ALL short-circuit and ANY short-circuit");
        calls = 0;
        selected = strategy.Maneuver(Context(50), any, trace);
        Check(selected == any && trace.rules.Count == 3 && trace.rules[2].retaining && calls == 3,
            "retained rule trace uses the actual exit threshold and clears the previous evaluation");
        var fallback = new CombatStrategy();
        var builtin = fallback.BuiltInManeuver;
        var runtime = fallback.Maneuver(Context(30), null, trace);
        Check(trace.rules.Count == 0 && builtin.tactic.behavior == runtime.tactic.behavior
            && builtin.tactic.intervalMin == runtime.tactic.intervalMin && builtin.tactic.intervalMax == runtime.tactic.intervalMax,
            "visible built-in maneuver is a copy of the actual fallback configuration");
        builtin.tactic.distance = 77;
        Check(runtime.tactic.distance == 0, "editing a built-in copy does not mutate the runtime fallback");
        fallback.maneuverRules.Add(builtin);
        Check(fallback.Maneuver(Context(30)) == builtin, "copied built-in rule becomes an editable last-priority override");
        var serializer = new XmlSerializer(typeof(CombatStrategy));
        using var writer = new StringWriter(); serializer.Serialize(writer, fallback);
        var loaded = (CombatStrategy)serializer.Deserialize(new StringReader(writer.ToString()));
        loaded.Validate();
        Check(loaded.Maneuver(Context(30)).tactic.distance == 77 && !writer.ToString().Contains("CombatSelectionTrace"),
            "editable built-in copy roundtrips through strategy XML without serializing diagnostic observations");
        Check(loaded.maneuverRules[0].engagement.terms.Count == runtime.engagement.terms.Count
            && loaded.maneuverRules[0].engagement.terms.Zip(runtime.engagement.terms, (a, b) =>
                a.metric == b.metric && a.side == b.side && a.weight == b.weight
                && a.curve.Evaluate(.3f) == b.curve.Evaluate(.3f)).All(equal => equal),
            "default engagement terms and nonlinear curves use the same editable XML representation");
        var rebuilt = new CombatStrategyRule { name = "independent player rule" };
        rebuilt.engagement.windowSeconds = runtime.engagement.windowSeconds;
        rebuilt.engagement.burstSeconds = runtime.engagement.burstSeconds;
        rebuilt.engagement.switchThreshold = runtime.engagement.switchThreshold;
        foreach (var term in runtime.engagement.terms)
            rebuilt.engagement.terms.Add(new CombatEngagementTerm { metric = term.metric, side = term.side,
                weapons = new CombatWeaponFilter { set = term.weapons.set, family = term.weapons.family,
                    group = term.weapons.group, weaponDef = term.weapons.weaponDef }, weight = term.weight,
                curve = new CombatScoreCurve(term.curve.extendRight,
                    term.curve.points.Select(p => new CombatScorePoint(p.x, p.y)).ToArray()) });
        var costs = new CombatEngagementEvaluation.Result { travel = 12, velocity = 3, reverse = 2, flank = .5f };
        Check(costs.Score(rebuilt.engagement) == costs.Score(runtime.engagement),
            "rebuilding engagement data from a blank rule needs no preset identity or hidden score");
        foreach (var term in rebuilt.engagement.terms) term.weight = 0;
        Check(costs.Score(rebuilt.engagement) == 0 && costs.Score(new CombatEngagementSettings()) == 0,
            "zeroed weights and empty term lists remove all engagement soft scores");
        var draftEngagement = loaded.maneuverRules[0].engagement.Copy();
        draftEngagement.terms[0].curve.points[1].y = 999;
        Check(runtime.engagement.terms[0].curve.points[1].y != 999 && loaded.maneuverRules[0].engagement.terms[0].curve.points[1].y != 999,
            "curve edits stay inside the strategy draft until apply");
        bool badEngagement = false;
        draftEngagement.windowSeconds = float.NaN;
        try { draftEngagement.Validate(); } catch (InvalidDataException) { badEngagement = true; }
        Check(badEngagement, "nonfinite engagement windows are rejected at the shared validation boundary");
        var comp = new CompShipCombatAI { lastManeuverSelection = trace, lastNpcDecisionTick = 10, lastNpcReason = "old" };
        comp.Replace(fallback);
        Check(comp.lastManeuverSelection == null && comp.lastNpcDecisionTick == -1 && comp.lastNpcReason == null,
            "applying a strategy invalidates observations from the previous configuration");
    }
}

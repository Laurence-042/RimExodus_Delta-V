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
        var comp = new CompShipCombatAI { lastManeuverSelection = trace, lastNpcDecisionTick = 10, lastNpcReason = "old" };
        comp.Replace(fallback);
        Check(comp.lastManeuverSelection == null && comp.lastNpcDecisionTick == -1 && comp.lastNpcReason == null,
            "applying a strategy invalidates observations from the previous configuration");
    }
}

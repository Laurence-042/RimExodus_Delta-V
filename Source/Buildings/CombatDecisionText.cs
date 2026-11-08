using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class CombatDecisionText
    {
        internal static string Engagement(CombatEngagementEvaluation.Result result, CombatEngagementSettings settings, float score)
        {
            var text = new StringBuilder();
            text.AppendLine((result.holding ? "DeltaV_EngagementHolding" : "DeltaV_EngagementTransfer").Translate());
            if (!float.IsInfinity(result.alternativeScore))
                text.AppendLine("DeltaV_EngagementMargin".Translate(score.ToString("0.##"), result.alternativeScore.ToString("0.##"),
                    (score - result.alternativeScore).ToString("+0.##;-0.##;0"), settings.switchThreshold.ToString("0.##")));
            foreach (var term in settings.terms)
            {
                float raw = result.Raw(term, settings.burstSeconds), curve = term.curve.Evaluate(raw);
                text.AppendLine("DeltaV_EngagementTermValue".Translate(
                    ("DeltaV_EngagementSide_" + term.side).Translate() + " / " + ("DeltaV_EngagementMetric_" + term.metric).Translate(),
                    ("DeltaV_CombatWeaponFamily_" + term.weapons.family).Translate(), raw.ToString("0.###"),
                    ("DeltaV_EngagementUnit_" + term.metric).Translate(), curve.ToString("0.###"),
                    term.weight.ToString("0.###"), (curve * term.weight).ToString("+0.###;-0.###;0")));
            }
            return text.ToString();
        }
        internal static string BuiltIn(CombatStrategyRule rule, int tab)
        {
            string Value(string key, object value) => key.Translate() + ": " + value;
            string Flag(bool value) => (value ? "DeltaV_On" : "DeltaV_Off").Translate();
            if (tab != 0)
                return (tab == 4 ? ("DeltaV_CombatTargetOrder_" + rule.targetOrder).Translate()
                    + "\n" + Value("DeltaV_CombatIncludeDisarmed", Flag(rule.includeDisarmed))
                    : ("DeltaV_CombatFireAction_" + rule.fireAction).Translate()
                    + " · " + ("DeltaV_CombatPart_" + rule.part).Translate()
                    + "\n" + Value("DeltaV_CombatAnchorOnly", Flag(rule.anchorOnly)))
                    + "\n" + Value("DeltaV_CombatAIOutgoing", rule.outgoingWeight.ToString("0.0"))
                    + "\n" + Value("DeltaV_CombatAIThreat", rule.targetThreatWeight.ToString("0.0"))
                    + "\n" + Value("DeltaV_CombatAIFinish", rule.finishingWeight.ToString("0.0"));
            var tactic = rule.tactic;
            return ("DeltaV_CombatTactic_" + tactic.behavior).Translate()
                + " · " + ("DeltaV_CombatFacing_" + tactic.facing).Translate()
                + " · " + ("DeltaV_CombatTurn_" + tactic.direction).Translate()
                + "\n" + Value("DeltaV_CombatTacticDistance", tactic.distance.ToString("0.#"))
                + "\n" + "DeltaV_CombatBuiltInDistance".Translate()
                + "\n" + Value("DeltaV_CombatTacticMovement", tactic.movement.ToString("P0"))
                + "\n" + ("DeltaV_CombatBearing_" + tactic.bearing).Translate()
                + "\n" + Value("DeltaV_CombatRepositionAngle", tactic.repositionDegrees.ToString("0.#") + "°")
                + "\n" + Value("DeltaV_CombatRepositionTime", $"{tactic.intervalMin:0.#}–{tactic.intervalMax:0.#}")
                + "\n" + Value("DeltaV_CombatFlanking", Flag(tactic.coordinateFlanking))
                + "\n" + Value("DeltaV_CombatPrimaryWeapons", ("DeltaV_CombatWeaponSet_" + tactic.primaryWeapons.set).Translate())
                + "\n" + "DeltaV_CombatBuiltInCurves".Translate()
                + "\n" + Value("DeltaV_EngagementWindow", rule.engagement.windowSeconds.ToString("0.#"))
                + "\n" + string.Join("\n", rule.engagement.terms.Select(t =>
                    ("DeltaV_EngagementMetric_" + t.metric).Translate() + " / "
                    + ("DeltaV_EngagementSide_" + t.side).Translate() + " × " + t.weight.ToString("0.##")
                    + ": " + string.Join("; ", t.curve.points.Select(p => $"({p.x:0.##}, {p.y:0.##})"))));
        }

        internal static string Selection(List<CombatStrategyRule> rules, CombatStrategyRule rule,
            CombatStrategyRule previous = null)
        {
            int index = rules?.IndexOf(rule) ?? -1;
            if (index < 0) return "DeltaV_CombatDecisionFallback".Translate();
            string match = (rule.conditions.Count == 0 ? "DeltaV_CombatDecisionUnconditional"
                : rule.matchAny ? "DeltaV_CombatDecisionAny" : "DeltaV_CombatDecisionAll").Translate();
            if (rule == previous && rule.conditions.Any(c => c.hysteresis))
                match += " · " + "DeltaV_CombatDecisionRetaining".Translate();
            return "DeltaV_CombatDecisionSelected".Translate(index + 1, rule.name, match);
        }

        internal static string Retreat(string reason) => (reason switch
        {
            "thrust below 30% of initial" => "DeltaV_CombatDecisionRetreatThrust",
            "firepower below 30% of initial" => "DeltaV_CombatDecisionRetreatFirepower",
            "backup console succeeded main console" => "DeltaV_CombatDecisionRetreatConsole",
            _ => "DeltaV_CombatDecisionRetreatRule"
        }).Translate();

        internal static string Status(CompShipCombatAI comp)
        {
            var text = new StringBuilder();
            text.AppendLine("DeltaV_CombatDecisionOwner".Translate(comp.parent?.LabelCap.ToString() ?? "-", comp.strategy.name));
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            ShipBase ship = comp.parent?.Spawned == true ? ShipRegistry.ShipOf(comp.parent) : ShipBase.Invalid;
            if (universe == null || ship?.IsValid != true)
            { text.AppendLine("DeltaV_CombatDecisionNoShip".Translate()); return text.ToString(); }
            if (ship.CombatAI != comp)
            { text.AppendLine("DeltaV_CombatDecisionBackup".Translate()); return text.ToString(); }
            SpaceObject frame = universe.FrameOf(ship.Map);
            var controller = universe.Controls.ExistingFor(ship);
            if (controller is PlayerShipController player)
                text.AppendLine("DeltaV_CombatDecisionPlayer".Translate(player.ManualControl
                    ? "DeltaV_ControlManual".Translate().ToString() : player.AutoTaskLabel));
            else
            {
                var gate = universe.NpcShips.GateFor(frame, ship);
                text.AppendLine(("DeltaV_CombatGate_" + gate).Translate());
                text.AppendLine(comp.lastNpcDecisionTick < 0 ? "DeltaV_CombatDecisionPending".Translate().ToString()
                    : "DeltaV_CombatDecisionNpc".Translate(comp.lastNpcDecisionTick, comp.lastNpcReason).ToString());
            }
            if (frame != null)
            {
                float thrust = NpcShipAiService.ShipThrust(ship);
                text.AppendLine("DeltaV_CombatDecisionThrust".Translate(thrust.ToString("0.##")));
                if (thrust <= 0f) text.AppendLine("DeltaV_CombatDecisionNoThrust".Translate());
                text.AppendLine(universe.Autopilot.DecisionStatus(ship.Identity));
            }
            text.AppendLine("DeltaV_CombatDecisionHistory".Translate());
            text.AppendLine("DeltaV_CombatAITarget".Translate() + ": " + (comp.lastTargetReason ?? "DeltaV_CombatDecisionPending".Translate().ToString()));
            text.AppendLine("DeltaV_CombatAIManeuver".Translate() + ": " + (comp.lastManeuverReason ?? "DeltaV_CombatDecisionPending".Translate().ToString()));
            text.AppendLine(controller?.AutoManeuver?.EngagementDecisionDetails() ?? "");
            AppendTrace(text, comp.lastManeuverSelection);
            text.AppendLine("\n" + "DeltaV_CombatAIFire".Translate() + ": " + (comp.lastFireReason ?? "DeltaV_CombatDecisionPending".Translate().ToString()));
            return text.ToString();
        }

        internal static void AppendTrace(StringBuilder text, CombatSelectionTrace trace)
        {
            if (trace == null) return;
            text.AppendLine("DeltaV_CombatDecisionTrace".Translate());
            for (int i = 0; i < trace.rules.Count; i++)
            {
                var entry = trace.rules[i];
                var rule = entry.rule;
                if (rule == null) continue;
                text.AppendLine($"{i + 1}. {rule.name} — " + (!rule.enabled ? "DeltaV_CombatDecisionDisabled"
                    : entry.matched ? "DeltaV_CombatDecisionMatched" : "DeltaV_CombatDecisionUnmatched").Translate());
                for (int j = 0; j < rule.conditions.Count; j++)
                {
                    var c = rule.conditions[j];
                    if (c == null) continue;
                    string comparison = c.comparison switch
                    { CombatComparison.Less => "<", CombatComparison.LessOrEqual => "≤", CombatComparison.Greater => ">", _ => "≥" };
                    bool exit = entry.retaining && c.hysteresis;
                    string scope = ("DeltaV_CombatScope_" + c.scope).Translate();
                    if (c.scope == CombatScope.WeaponGroup || c.scope == CombatScope.Metric
                        && c.metric >= CombatMetric.ReadyWeaponFraction && c.metric <= CombatMetric.WeaponArcErrorDegrees)
                        scope += " / " + ("DeltaV_CombatWeaponSet_" + c.weapons.set).Translate()
                            + " / " + c.weapons.group + " / " + c.weapons.weaponDef
                            + " / " + (c.allWeapons ? "ALL" : "ANY");
                    text.AppendLine("    " + scope + " · " + ("DeltaV_CombatMetric_" + c.metric).Translate()
                        + " " + comparison + " " + (exit ? c.exitValue : c.value).ToString("0.##")
                        + (exit ? " (" + "DeltaV_CombatDecisionExit".Translate().ToString() + ")" : "")
                        + " : " + (j >= entry.conditions.Count ? "DeltaV_CombatDecisionNotChecked"
                            : entry.conditions[j] ? "DeltaV_CombatDecisionMatched" : "DeltaV_CombatDecisionUnmatched").Translate());
                }
            }
        }
    }
}

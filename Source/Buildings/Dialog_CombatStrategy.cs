using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_CombatStrategy : Window
    {
        private readonly CompShipCombatAI owner;
        private CombatStrategy draft;
        private Vector2 scroll;
        private int tab;
        private readonly Dictionary<CombatCriterion, string> numberBuffers =
            new Dictionary<CombatCriterion, string>();

        public override Vector2 InitialSize => new Vector2(820f, 690f);
        protected override float Margin => 12f;

        internal Dialog_CombatStrategy(CompShipCombatAI owner)
        {
            this.owner = owner;
            draft = owner.strategy.Copy();
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, rect.width, 32f), "DeltaV_CombatAIEdit".Translate());
            Text.Font = GameFont.Small;
            draft.name = Widgets.TextField(new Rect(0f, 36f, rect.width - 170f, 28f), draft.name);
            if (draft.name.Length > 100) draft.name = draft.name.Substring(0, 100);
            if (Widgets.ButtonText(new Rect(rect.width - 160f, 36f, 75f, 28f), "DeltaV_CombatAIImport".Translate()))
                ShowImportMenu();
            if (Widgets.ButtonText(new Rect(rect.width - 80f, 36f, 75f, 28f), "DeltaV_CombatAIExport".Translate()))
                TryFile(() => CombatStrategyFile.Export(draft));
            Rect status = new Rect(0f, 72f, rect.width, 110f);
            string reason = "DeltaV_CombatAIStatus".Translate(
                owner.lastManeuverReason ?? "-", owner.lastFireReason ?? "-");
            Widgets.Label(status, reason);
            TooltipHandler.TipRegion(status, reason);
            string[] tabs = { "DeltaV_CombatAIManeuver", "DeltaV_CombatAIFire",
                "DeltaV_CombatAIRetreat", "DeltaV_CombatAIBoard" };
            float tabWidth = rect.width / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
                if (Widgets.ButtonText(new Rect(i * tabWidth, 185f, tabWidth - 4f, 28f),
                    tabs[i].Translate())) { tab = i; scroll = Vector2.zero; }
            Rect viewport = new Rect(0f, 220f, rect.width, rect.height - 270f);
            float contentHeight = ContentHeight();
            Widgets.BeginScrollView(viewport, ref scroll,
                new Rect(0f, 0f, viewport.width - 18f, contentHeight));
            float y = 0f;
            if (tab <= 1) DrawRules(tab == 0 ? draft.maneuverRules : draft.fireRules,
                viewport.width - 20f, ref y);
            else
            {
                bool any = tab == 2 ? draft.npcRetreatMatchAny : draft.npcBoardMatchAny;
                if (Widgets.ButtonText(new Rect(0f, y, 150f, 26f), any ? "ANY" : "ALL"))
                {
                    if (tab == 2) draft.npcRetreatMatchAny = !any;
                    else draft.npcBoardMatchAny = !any;
                }
                y += 34f;
                DrawConditions(tab == 2 ? draft.npcRetreatConditions : draft.npcBoardConditions,
                    viewport.width - 20f, ref y);
            }
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(0f, rect.height - 36f, 110f, 32f), "CancelButton".Translate()))
                Close();
            if (Widgets.ButtonText(new Rect(rect.width - 116f, rect.height - 36f, 110f, 32f), "OK".Translate()))
            {
                try { owner.Replace(draft); Close(); }
                catch (Exception ex) { Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, false); }
            }
        }

        private float ContentHeight()
        {
            if (tab >= 2)
                return Mathf.Max(600f, 85f + 42f * (tab == 2
                    ? draft.npcRetreatConditions.Count : draft.npcBoardConditions.Count));
            List<CombatStrategyRule> rules = tab == 0 ? draft.maneuverRules : draft.fireRules;
            return Mathf.Max(600f, 90f + rules.Sum(r => 275f + (r.conditions?.Count ?? 0) * 42f));
        }

        private void DrawRules(List<CombatStrategyRule> rules, float width, ref float y)
        {
            Widgets.Label(new Rect(0f, y, width, 32f), "DeltaV_CombatAIRuleHelp".Translate());
            y += 38f;
            if (Widgets.ButtonText(new Rect(0f, y, 150f, 28f), "DeltaV_CombatAIAddRule".Translate()))
                if (rules.Count < 64) rules.Add(new CombatStrategyRule());
            y += 40f;
            for (int i = 0; i < rules.Count; i++)
            {
                CombatStrategyRule rule = rules[i];
                if (rule == null) continue;
                Widgets.Checkbox(new Vector2(0f, y + 3f), ref rule.enabled, 24f);
                rule.name = Widgets.TextField(new Rect(34f, y, width - 288f, 28f), rule.name ?? "");
                if (rule.name.Length > 100) rule.name = rule.name.Substring(0, 100);
                if (Widgets.ButtonText(new Rect(width - 248f, y, 78f, 28f),
                    "DeltaV_CombatAICopyRule".Translate()))
                { if (rules.Count < 64) rules.Insert(i + 1, rule.Copy()); break; }
                if (i > 0 && Widgets.ButtonText(new Rect(width - 166f, y, 38f, 28f), "↑"))
                    (rules[i - 1], rules[i]) = (rules[i], rules[i - 1]);
                if (i < rules.Count - 1 && Widgets.ButtonText(new Rect(width - 124f, y, 38f, 28f), "↓"))
                    (rules[i + 1], rules[i]) = (rules[i], rules[i + 1]);
                if (Widgets.ButtonText(new Rect(width - 80f, y, 78f, 28f), "Delete".Translate()))
                { rules.RemoveAt(i); break; }
                y += 34f;
                if (Widgets.ButtonText(new Rect(0f, y, 130f, 26f), rule.matchAny ? "ANY" : "ALL"))
                    rule.matchAny = !rule.matchAny;
                Widgets.Label(new Rect(138f, y, width - 138f, 26f), "DeltaV_CombatAIConditionHelp".Translate());
                y += 30f;
                DrawConditions(rule.conditions, width, ref y);
                if (tab == 0)
                {
                    if (Widgets.ButtonText(new Rect(8f, y, 190f, 28f),
                        "DeltaV_CombatAICurves".Translate()))
                    {
                        ShipBase ship = ShipRegistry.ShipOf(owner.parent);
                        float radius = ship?.IsValid == true
                            ? ShipFireControl.HullRadius(ship) : 20f;
                        float clearance = radius * 2f + 8f;
                        float fallbackDistance = 100f;
                        if (ship?.IsValid == true)
                            Current.Game?.GetComponent<UniverseManager>()?.Controls.ExistingFor(ship)
                                ?.AutoManeuver.DefaultCurveReference(ship,
                                    out clearance, out fallbackDistance);
                        CombatManeuverCurves defaults = CombatManeuverCurveDefaults.For(
                            ship, clearance, fallbackDistance);
                        Find.WindowStack.Add(new Dialog_CombatScoreCurves(rule, defaults));
                    }
                    y += 32f;
                }
                else
                {
                    WeightRow("DeltaV_CombatAIOutgoing", ref rule.outgoingWeight, width, ref y);
                    WeightRow("DeltaV_CombatAIThreat", ref rule.targetThreatWeight, width, ref y);
                    WeightRow("DeltaV_CombatAIFinish", ref rule.finishingWeight, width, ref y);
                }
                y += 12f;
                Widgets.DrawLineHorizontal(0f, y, width);
                y += 12f;
            }
            Widgets.Label(new Rect(0f, y, width, 30f), "DeltaV_CombatAIDefaultRule".Translate());
        }

        private void DrawConditions(List<CombatCriterion> conditions, float width, ref float y)
        {
            if (Widgets.ButtonText(new Rect(0f, y, 150f, 26f), "DeltaV_CombatAIAddCondition".Translate()))
                if (conditions.Count < 16) conditions.Add(new CombatCriterion());
            y += 32f;
            for (int i = 0; i < conditions.Count; i++)
            {
                CombatCriterion condition = conditions[i];
                if (condition == null) continue;
                if (Widgets.ButtonText(new Rect(8f, y, width * 0.48f, 27f),
                    ("DeltaV_CombatMetric_" + condition.metric).Translate()))
                {
                    var options = Enum.GetValues(typeof(CombatMetric)).Cast<CombatMetric>()
                        .Select(metric => new FloatMenuOption(("DeltaV_CombatMetric_" + metric).Translate(),
                            () => condition.metric = metric)).ToList();
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                if (Widgets.ButtonText(new Rect(width * 0.49f, y, 58f, 27f),
                    ComparisonLabel(condition.comparison)))
                    condition.comparison = (CombatComparison)(((int)condition.comparison + 1) % 4);
                string buffer = numberBuffers.TryGetValue(condition, out string previous)
                    ? previous : condition.value.ToString("0.##");
                Widgets.TextFieldNumeric(new Rect(width * 0.49f + 64f, y, 90f, 27f),
                    ref condition.value, ref buffer, -100000f, 100000f);
                numberBuffers[condition] = buffer;
                if (Widgets.ButtonText(new Rect(width - 76f, y, 72f, 27f), "Delete".Translate()))
                { conditions.RemoveAt(i); numberBuffers.Remove(condition); break; }
                y += 40f;
            }
        }

        private static string ComparisonLabel(CombatComparison comparison) => comparison switch
        {
            CombatComparison.Less => "<", CombatComparison.LessOrEqual => "≤",
            CombatComparison.Greater => ">", _ => "≥"
        };

        private static void WeightRow(string key, ref float value, float width, ref float y)
        {
            Widgets.Label(new Rect(8f, y, 235f, 24f), key.Translate() + ": " + value.ToString("0.0"));
            value = Mathf.Round(Widgets.HorizontalSlider(new Rect(250f, y, width - 255f, 24f),
                value, 0f, 10f) * 10f) / 10f;
            y += 30f;
        }

        private void ShowImportMenu()
        {
            var options = new List<FloatMenuOption>();
            try
            {
                foreach (string path in CombatStrategyFile.Files())
                {
                    string selected = path;
                    options.Add(new FloatMenuOption(Path.GetFileName(path), () => TryFile(() =>
                    { draft = CombatStrategyFile.Import(selected); return selected; })));
                }
            }
            catch (Exception ex) { Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, false); }
            if (options.Count == 0) options.Add(new FloatMenuOption("DeltaV_CombatAINoFiles".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void TryFile(Func<string> action)
        {
            try { Messages.Message(action(), MessageTypeDefOf.NeutralEvent, false); }
            catch (Exception ex) { Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, false); }
        }
    }
}

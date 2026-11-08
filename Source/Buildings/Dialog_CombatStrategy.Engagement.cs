using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class Dialog_CombatStrategy
    {
        private void DrawEngagement(CombatEngagementSettings settings, float width, ref float y)
        {
            Widgets.Label(new Rect(8, y, width - 16, 28), "DeltaV_EngagementTitle".Translate()); y += 30;
            void Field(string key, ref float value, float min, float max, ref float row)
            {
                Widgets.Label(new Rect(8, row, width - 130, 28), key.Translate());
                Number(settings, key, new Rect(width - 118, row, 110, 28), ref value, min, max); row += 32;
            }
            Field("DeltaV_EngagementWindow", ref settings.windowSeconds, 1, 120, ref y);
            Field("DeltaV_EngagementBurst", ref settings.burstSeconds, .1f, settings.windowSeconds, ref y);
            Field("DeltaV_EngagementSwitch", ref settings.switchThreshold, 0, 10000, ref y);
            Widgets.Label(new Rect(8, y, width - 16, 46), "DeltaV_EngagementHelp".Translate()); y += 48;
            for (int i = 0; i < settings.terms.Count; i++)
            {
                var term = settings.terms[i];
                Choice(new Rect(8, y, width * .5f - 16, 28), term.metric, "DeltaV_EngagementMetric_", v => term.metric = v);
                Choice(new Rect(width * .5f, y, width * .5f - 96, 28), term.side, "DeltaV_EngagementSide_", v => term.side = v);
                if (Widgets.ButtonText(new Rect(width - 88, y, 80, 28), "Delete".Translate()))
                { settings.terms.RemoveAt(i); break; }
                y += 32;
                Widgets.Label(new Rect(8, y, 150, 28), "DeltaV_EngagementWeight".Translate());
                Number(term, "weight", new Rect(164, y, 100, 28), ref term.weight, 0, 100);
                if (Widgets.ButtonText(new Rect(280, y, width - 288, 28), "DeltaV_CombatAICurves".Translate()))
                    Find.WindowStack.Add(new Dialog_CombatScoreCurves(term.curve,
                        ("DeltaV_EngagementMetric_" + term.metric).Translate(),
                        ("DeltaV_EngagementUnit_" + term.metric).Translate()));
                y += 32;
                if (term.metric <= CombatEngagementMetric.BurstFraction || term.metric == CombatEngagementMetric.Uncertainty
                    || term.metric == CombatEngagementMetric.CriticalDamage)
                    DrawFilter(term.weapons, width, ref y);
            }
            if (settings.terms.Count < 32 && Widgets.ButtonText(new Rect(8, y, width - 16, 28),
                "DeltaV_EngagementAdd".Translate())) settings.terms.Add(new CombatEngagementTerm());
            y += 34;
        }
    }
}

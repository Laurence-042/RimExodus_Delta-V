using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_CombatScoreCurves : Window
    {
        private readonly CombatStrategyRule rule;
        private readonly CombatManeuverCurves defaults;
        private readonly Dictionary<CombatScorePoint, string> xBuffers =
            new Dictionary<CombatScorePoint, string>();
        private readonly Dictionary<CombatScorePoint, string> yBuffers =
            new Dictionary<CombatScorePoint, string>();
        private ManeuverCurveMetric metric;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(760f, 700f);
        protected override float Margin => 12f;

        internal Dialog_CombatScoreCurves(CombatStrategyRule rule,
            CombatManeuverCurves defaults)
        {
            this.rule = rule;
            this.defaults = defaults;
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, rect.width, 32f),
                "DeltaV_CombatAICurveTitle".Translate(rule.name));
            Text.Font = GameFont.Small;
            float tabWidth = rect.width / 6f;
            for (int i = 0; i < 6; i++)
            {
                ManeuverCurveMetric candidate = (ManeuverCurveMetric)i;
                if (!Widgets.ButtonText(new Rect(i * tabWidth, 38f, tabWidth - 4f, 30f),
                    ("DeltaV_CombatAICurve_" + candidate).Translate())) continue;
                metric = candidate;
                scroll = Vector2.zero;
            }
            Widgets.Label(new Rect(0f, 74f, rect.width, 26f),
                "DeltaV_CombatAICurveAxes".Translate(
                    ("DeltaV_CombatAICurveUnit_" + metric).Translate()));
            CombatScoreCurve custom = rule.maneuverCurves?.Get(metric);
            CombatScoreCurve curve = custom ?? defaults.Get(metric);
            float minX = 0f;
            float maxX = metric == ManeuverCurveMetric.HullOverlap ? 1f
                : metric == ManeuverCurveMetric.AlignmentError ? 180f : 100000f;
            bool valid = true;
            try { curve.Validate(minX, maxX); }
            catch (InvalidDataException) { valid = false; }
            Rect chart = new Rect(0f, 103f, rect.width, 238f);
            if (valid)
                SimpleCurveDrawer.DrawCurve(chart, curve.AsSimpleCurve(),
                    new SimpleCurveDrawerStyle
                    {
                        DrawBackground = true, DrawMeasures = true,
                        DrawPoints = true, UseAntiAliasedLines = true,
                        DrawCurveMousePoint = true,
                        LabelX = ("DeltaV_CombatAICurveUnit_" + metric).Translate()
                    });
            else
            {
                Widgets.DrawBoxSolid(chart, new Color(0.25f, 0.1f, 0.1f));
                Widgets.Label(chart.ContractedBy(12f), "DeltaV_CombatAICurveInvalid".Translate());
            }
            Widgets.Label(new Rect(0f, 348f, rect.width - 218f, 28f),
                (custom == null ? "DeltaV_CombatAICurveAutomatic"
                    : "DeltaV_CombatAICurveCustom").Translate());
            if (custom == null)
            {
                if (Widgets.ButtonText(new Rect(rect.width - 206f, 347f, 204f, 28f),
                    "DeltaV_CombatAICurveCustomize".Translate()))
                {
                    rule.maneuverCurves ??= new CombatManeuverCurves();
                    rule.maneuverCurves.Set(metric, curve.Copy());
                }
            }
            else if (Widgets.ButtonText(new Rect(rect.width - 206f, 347f, 204f, 28f),
                "DeltaV_CombatAICurveReset".Translate()))
            {
                rule.maneuverCurves.Set(metric, null);
                xBuffers.Clear(); yBuffers.Clear();
            }
            if (custom != null)
            {
                bool extend = custom.extendRight;
                Widgets.CheckboxLabeled(new Rect(0f, 381f, 335f, 26f),
                    "DeltaV_CombatAICurveExtend".Translate(), ref extend);
                custom.extendRight = extend;
                if (Widgets.ButtonText(new Rect(rect.width - 206f, 381f, 204f, 26f),
                    "DeltaV_CombatAICurveAdd".Translate()) && valid)
                    AddPoint(custom, maxX);
            }
            Rect viewport = new Rect(0f, 416f, rect.width, rect.height - 466f);
            Widgets.BeginScrollView(viewport, ref scroll,
                new Rect(0f, 0f, viewport.width - 18f,
                    Math.Max(viewport.height, 36f + curve.points.Count * 34f)));
            Widgets.Label(new Rect(6f, 0f, 330f, 27f),
                "DeltaV_CombatAICurvePointHeader".Translate());
            for (int i = 0; i < curve.points.Count; i++)
            {
                CombatScorePoint point = curve.points[i];
                float y = 30f + 34f * i;
                Widgets.Label(new Rect(7f, y, 36f, 27f), (i + 1).ToString());
                if (custom == null)
                {
                    Widgets.Label(new Rect(62f, y, 170f, 27f), point.x.ToString("0.##"));
                    Widgets.Label(new Rect(245f, y, 170f, 27f), point.y.ToString("+0.##;-0.##;0"));
                    continue;
                }
                float oldX = point.x, oldY = point.y;
                string xb = xBuffers.TryGetValue(point, out string previousX)
                    ? previousX : oldX.ToString("0.##");
                string yb = yBuffers.TryGetValue(point, out string previousY)
                    ? previousY : oldY.ToString("0.##");
                Widgets.TextFieldNumeric(new Rect(62f, y, 170f, 27f), ref point.x,
                    ref xb, minX, maxX);
                Widgets.TextFieldNumeric(new Rect(245f, y, 170f, 27f), ref point.y,
                    ref yb, -10000f, 10000f);
                xBuffers[point] = xb; yBuffers[point] = yb;
                if (oldX != point.x || oldY != point.y) custom.Invalidate();
                if (custom.points.Count > 2
                    && Widgets.ButtonText(new Rect(viewport.width - 110f, y, 80f, 27f),
                        "Delete".Translate()))
                {
                    custom.points.RemoveAt(i);
                    custom.Invalidate();
                    xBuffers.Remove(point); yBuffers.Remove(point);
                    break;
                }
            }
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(rect.width - 110f, rect.height - 36f,
                108f, 32f), "CloseButton".Translate())) Close();
        }

        private static void AddPoint(CombatScoreCurve curve, float maxX)
        {
            if (curve.points.Count >= 16) return;
            int after = -1;
            float widest = 0f;
            for (int i = 0; i < curve.points.Count - 1; i++)
            {
                float gap = curve.points[i + 1].x - curve.points[i].x;
                if (gap <= widest) continue;
                widest = gap;
                after = i;
            }
            if (after >= 0 && widest > 0.0002f)
            {
                CombatScorePoint left = curve.points[after], right = curve.points[after + 1];
                curve.points.Insert(after + 1, new CombatScorePoint(
                    (left.x + right.x) * 0.5f, (left.y + right.y) * 0.5f));
            }
            else
            {
                CombatScorePoint last = curve.points[curve.points.Count - 1];
                float x = Mathf.Min(maxX, last.x + (maxX <= 1f ? 0.1f : 10f));
                if (x <= last.x) return;
                curve.points.Add(new CombatScorePoint(x, last.y));
            }
            curve.Invalidate();
        }
    }
}

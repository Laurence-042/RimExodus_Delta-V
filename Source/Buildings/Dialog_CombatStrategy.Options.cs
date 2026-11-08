using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class Dialog_CombatStrategy
    {
        private readonly Dictionary<object, Dictionary<string, string>> optionBuffers = new Dictionary<object, Dictionary<string, string>>();
        private void Number(object identity, string key, Rect rect, ref float value, float min, float max)
        {
            if (!optionBuffers.TryGetValue(identity, out var entries)) optionBuffers[identity] = entries = new Dictionary<string, string>();
            string buffer = entries.TryGetValue(key, out var old) ? old : value.ToString("0.##");
            Widgets.TextFieldNumeric(rect, ref value, ref buffer, min, max);
            entries[key] = buffer;
        }
        private static void Choice<T>(Rect rect, T value, string prefix, Action<T> choose) where T : Enum
        {
            if (!Widgets.ButtonText(rect, (prefix + value).Translate())) return;
            Find.WindowStack.Add(new FloatMenu(Enum.GetValues(typeof(T)).Cast<T>().Select(v =>
                new FloatMenuOption((prefix + v).Translate(), () => choose(v))).ToList()));
        }
        private void DrawFilter(CombatWeaponFilter filter, float width, ref float y)
        {
            Choice(new Rect(8f, y, 145f, 26f), filter.set, "DeltaV_CombatWeaponSet_", v => filter.set = v);
            if (Widgets.ButtonText(new Rect(160f, y, 120f, 26f), "DeltaV_CombatGroup".Translate(filter.group)))
                filter.group = (filter.group + 1) % 7;
            if (Widgets.ButtonText(new Rect(288f, y, width - 296f, 26f), string.IsNullOrEmpty(filter.weaponDef)
                ? "DeltaV_CombatAllWeaponTypes".Translate().ToString() : filter.weaponDef))
            {
                var options = new List<FloatMenuOption> { new FloatMenuOption("DeltaV_CombatAllWeaponTypes".Translate(), () => filter.weaponDef = "") };
                var ship = ShipRegistry.ShipOf(owner.parent);
                if (ship?.IsValid == true)
                    foreach (var def in ship.Weapons.Select(w => w.parent.def).Distinct())
                    { var selected = def; options.Add(new FloatMenuOption(def.LabelCap, () => filter.weaponDef = selected.defName)); }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y += 32f;
            Choice(new Rect(8f, y, width - 16f, 26f), filter.family, "DeltaV_CombatWeaponFamily_", v => filter.family = v);
            y += 32f;
        }
        private void DrawCriterionOptions(CombatCriterion c, float width, ref float y)
        {
            bool equipment = c.metric >= CombatMetric.ReadyWeaponFraction && c.metric <= CombatMetric.WeaponArcErrorDegrees;
            if (Widgets.ButtonText(new Rect(8f, y, 220f, 26f), ("DeltaV_CombatScope_" + c.scope).Translate()))
            {
                var scopes = new List<CombatScope> { CombatScope.Metric };
                if (equipment) { scopes.Add(CombatScope.WeaponGroup); if (tab == 1) scopes.Add(CombatScope.CurrentWeapon); }
                else if (c.metric <= CombatMetric.OwnShieldFraction) { scopes.Add(CombatScope.OwnShip); scopes.Add(CombatScope.EnemyShip); }
                else scopes.Add(CombatScope.EnemyShip);
                Find.WindowStack.Add(new FloatMenu(scopes.Select(v => new FloatMenuOption(("DeltaV_CombatScope_" + v).Translate(), () => c.scope = v)).ToList()));
            }
            bool hadHysteresis = c.hysteresis;
            Widgets.CheckboxLabeled(new Rect(240f, y, 170f, 26f), "DeltaV_CombatHysteresis".Translate(), ref c.hysteresis);
            if (c.hysteresis && !hadHysteresis) c.exitValue = c.value;
            if (c.hysteresis)
                Number(c, "exit", new Rect(width - 95f, y, 90f, 26f), ref c.exitValue, -100000f, 100000f);
            y += 32f;
            if (equipment && c.scope != CombatScope.CurrentWeapon)
            {
                DrawFilter(c.weapons, width, ref y);
                Widgets.CheckboxLabeled(new Rect(8f, y, width - 16f, 26f), "DeltaV_CombatAllWeapons".Translate(), ref c.allWeapons);
                y += 32f;
            }
            Widgets.Label(new Rect(8f, y, width - 16f, 28f), "DeltaV_CombatConditionMissing".Translate());
            y += 32f;
        }
        private void DrawRuleAction(CombatStrategyRule rule, float width, ref float y)
        {
            if (tab == 4)
            {
                Choice(new Rect(8f, y, width * 0.48f, 28f), rule.targetOrder, "DeltaV_CombatTargetOrder_", v => rule.targetOrder = v);
                Widgets.CheckboxLabeled(new Rect(width * 0.5f, y, width * 0.49f, 28f), "DeltaV_CombatIncludeDisarmed".Translate(), ref rule.includeDisarmed);
                y += 34f;
                if (rule.targetOrder == CombatTargetOrder.MainWeapons) DrawFilter(rule.tactic.primaryWeapons, width, ref y);
            }
            else
            {
                Choice(new Rect(8f, y, width * 0.48f, 28f), rule.fireAction, "DeltaV_CombatFireAction_", v => rule.fireAction = v);
                Choice(new Rect(width * 0.5f, y, width * 0.49f, 28f), rule.part, "DeltaV_CombatPart_", v => rule.part = v);
                y += 34f;
                Widgets.CheckboxLabeled(new Rect(8f, y, width - 16f, 26f), "DeltaV_CombatAnchorOnly".Translate(), ref rule.anchorOnly);
                y += 32f;
            }
        }
        private void DrawTacticOptions(CombatTacticSettings settings, float width, ref float y)
        {
            Widgets.Label(new Rect(8f, y, width - 16f, 26f), "DeltaV_CombatPrimaryWeapons".Translate()); y += 28f;
            DrawFilter(settings.primaryWeapons, width, ref y);
            Choice(new Rect(8f, y, 250f, 28f), settings.bearing, "DeltaV_CombatBearing_", v =>
            { settings.bearing = v; if (v != CombatBearing.Free) settings.behavior = CombatTactic.HoldRange; });
            if (settings.bearing != CombatBearing.Free)
                Number(settings, "bearing", new Rect(280f, y, 100f, 28f), ref settings.bearingDegrees, -180f, 180f);
            y += 34f;
            if (settings.bearing == CombatBearing.Free)
            {
                Widgets.CheckboxLabeled(new Rect(8f, y, width - 16f, 26f), "DeltaV_CombatFlanking".Translate(), ref settings.coordinateFlanking);
                y += 32f;
            }
            if (settings.behavior == CombatTactic.Automatic)
            {
                Widgets.Label(new Rect(8f, y, 250f, 26f), "DeltaV_CombatRepositionAngle".Translate());
                Number(settings, "jitter", new Rect(280f, y, 100f, 26f), ref settings.repositionDegrees, 0f, 90f); y += 32f;
                Widgets.Label(new Rect(8f, y, 250f, 26f), "DeltaV_CombatRepositionTime".Translate());
                Number(settings, "min", new Rect(280f, y, 100f, 26f), ref settings.intervalMin, 1f, 600f);
                Number(settings, "max", new Rect(390f, y, 100f, 26f), ref settings.intervalMax, 1f, 600f); y += 32f;
            }
            if (Widgets.ButtonText(new Rect(8f, y, 240f, 28f), "DeltaV_CombatPosePreview".Translate()))
                Find.WindowStack.Add(new Dialog_CombatPose(settings, ShipRegistry.ShipOf(owner.parent), () =>
                { tacticBuffers.Remove(settings); optionBuffers.Remove(settings); }));
            y += 34f;
        }
        private void ShowTemplates(List<CombatStrategyRule> rules)
        {
            var options = new List<FloatMenuOption>();
            for (int i = 0; i < 4; i++)
            {
                int template = i;
                options.Add(new FloatMenuOption(("DeltaV_CombatTemplate" + i).Translate(), () =>
                {
                    if (rules.Count >= 64) return;
                    var rule = new CombatStrategyRule { name = ("DeltaV_CombatTemplate" + template).Translate() };
                    if (template == 0) { rule.tactic.primaryWeapons.set = CombatWeaponSet.Main; rule.tactic.primaryWeapons.group = 1; }
                    if (template == 1) { rule.tactic.facing = CombatFacing.Relative; rule.tactic.facingDegrees = 90f; }
                    if (template == 2) rule.tactic.coordinateFlanking = true;
                    if (template == 3) rule.tactic.behavior = CombatTactic.HoldRange;
                    rules.Add(rule);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}

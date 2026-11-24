using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static class AutoSpaceWeaponSettingsUI
    {
        private const float GroupHeight = 34f;
        private const float RowHeight = 44f;
        private static readonly HashSet<string> collapsed = new HashSet<string>();
        private static Vector2 scrollPosition;
        private static string search = "";

        public static void Draw(Rect rect, DeltaVSettings settings)
        {
            Rect noteRect = new Rect(rect.x, rect.y, rect.width, 42f);
            Widgets.Label(noteRect, "DeltaV_AutoWeaponRestartNotice".Translate());
            Rect exportRect = new Rect(rect.x, noteRect.yMax + 4f, 220f, 30f);
            if (Widgets.ButtonText(exportRect, "DeltaV_AutoAdaptationExport".Translate()))
            {
                try
                {
                    string path = AutoSpaceAdaptationReport.Export(settings);
                    Messages.Message("DeltaV_AutoAdaptationExported".Translate(path),
                        MessageTypeDefOf.TaskCompletion, false);
                }
                catch (Exception ex)
                {
                    Messages.Message("DeltaV_AutoAdaptationExportFailed".Translate(ex.Message),
                        MessageTypeDefOf.RejectInput, false);
                }
            }
            Rect exportNote = new Rect(exportRect.xMax + 10f, exportRect.y,
                rect.xMax - exportRect.xMax - 10f, 30f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(exportNote, "DeltaV_AutoAdaptationExportTip".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect searchLabel = new Rect(rect.x, exportRect.yMax + 4f, 70f, 30f);
            Widgets.Label(searchLabel, "DeltaV_AutoWeaponSearch".Translate());
            Rect searchRect = new Rect(searchLabel.xMax, searchLabel.y, rect.width - searchLabel.width, 30f);
            search = Widgets.TextField(searchRect, search ?? "");

            List<IGrouping<string, AutoSpaceWeaponCandidate>> groups = AutoSpaceWeaponDefGenerator.Candidates
                .Where(MatchesSearch)
                .OrderBy(x => x.modName)
                .ThenBy(x => x.turret?.label)
                .GroupBy(x => x.packageId)
                .ToList();
            float height = groups.Sum(group => GroupHeight
                + (collapsed.Contains(group.Key) ? 0f : group.Count() * RowHeight));
            Rect outer = new Rect(rect.x, searchRect.yMax + 8f, rect.width,
                rect.yMax - searchRect.yMax - 8f);
            Rect view = new Rect(0f, 0f, outer.width - 16f, Math.Max(outer.height, height));
            Widgets.BeginScrollView(outer, ref scrollPosition, view);
            float y = 0f;
            foreach (IGrouping<string, AutoSpaceWeaponCandidate> group in groups)
            {
                List<AutoSpaceWeaponCandidate> entries = group.ToList();
                DrawGroup(new Rect(0f, y, view.width, GroupHeight), entries, settings);
                y += GroupHeight;
                if (collapsed.Contains(group.Key)) continue;
                foreach (AutoSpaceWeaponCandidate candidate in entries)
                {
                    DrawCandidate(new Rect(0f, y, view.width, RowHeight), candidate, settings);
                    y += RowHeight;
                }
            }
            if (groups.Count == 0)
                Widgets.Label(new Rect(0f, 8f, view.width, 30f),
                    "DeltaV_AutoWeaponNoCandidates".Translate());
            Widgets.EndScrollView();
        }

        private static bool MatchesSearch(AutoSpaceWeaponCandidate candidate)
        {
            if (search.NullOrEmpty()) return true;
            return Contains(candidate.modName, search) || Contains(candidate.packageId, search)
                || Contains(candidate.turret?.defName, search) || Contains(candidate.turret?.label, search);
        }

        private static bool Contains(string value, string fragment) =>
            value?.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void DrawGroup(Rect rect, List<AutoSpaceWeaponCandidate> entries,
            DeltaVSettings settings)
        {
            AutoSpaceWeaponCandidate first = entries[0];
            Widgets.DrawHighlight(rect);
            Rect foldRect = new Rect(rect.x + 4f, rect.y + 2f, 28f, rect.height - 4f);
            if (Widgets.ButtonText(foldRect, collapsed.Contains(first.packageId) ? "+" : "−"))
            {
                if (!collapsed.Add(first.packageId)) collapsed.Remove(first.packageId);
            }
            Rect labelRect = new Rect(foldRect.xMax + 6f, rect.y, rect.width - 230f, rect.height);
            Widgets.Label(labelRect, first.modName + "  (" + entries.Count + ")");
            AutoSpaceWeaponModPolicy policy = settings.GetAutoWeaponModPolicy(first.packageId);
            Rect buttonRect = new Rect(rect.xMax - 190f, rect.y + 3f, 186f, rect.height - 6f);
            if (Widgets.ButtonText(buttonRect, ModPolicyLabel(policy)))
                OpenModPolicyMenu(first.packageId, settings);
        }

        private static void DrawCandidate(Rect rect, AutoSpaceWeaponCandidate candidate,
            DeltaVSettings settings)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            Rect nameRect = new Rect(rect.x + 14f, rect.y + 2f, rect.width - 390f, 22f);
            Widgets.Label(nameRect, candidate.turret?.LabelCap ?? candidate.turret?.defName ?? "?");
            Text.Font = GameFont.Tiny;
            GUI.color = candidate.StructurallySupported ? Color.gray : ColorLibrary.RedReadable;
            Rect detailRect = new Rect(nameRect.x, nameRect.yMax, nameRect.width, 18f);
            Widgets.Label(detailRect, CandidateDetail(candidate));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            AutoSpaceWeaponModPolicy modPolicy = settings.GetAutoWeaponModPolicy(candidate.packageId);
            AutoSpaceWeaponOverride weaponOverride = settings.GetAutoWeaponOverride(candidate.packageId,
                candidate.turret.defName);
            bool effective = AutoSpaceWeaponPolicy.Enabled(modPolicy, weaponOverride,
                candidate.StructurallySupported, candidate.warnings,
                candidate.source?.extension?.force == true);
            Rect stateRect = new Rect(rect.xMax - 370f, rect.y + 8f, 130f, 28f);
            GUI.color = effective ? Color.green : Color.gray;
            Widgets.Label(stateRect, effective
                ? "DeltaV_AutoWeaponEffectiveEnabled".Translate()
                : "DeltaV_AutoWeaponEffectiveDisabled".Translate());
            GUI.color = Color.white;
            Rect buttonRect = new Rect(rect.xMax - 230f, rect.y + 6f, 226f, 30f);
            if (Widgets.ButtonText(buttonRect, WeaponOverrideLabel(weaponOverride)))
                OpenWeaponOverrideMenu(candidate, settings);
            TooltipHandler.TipRegion(rect, candidate.turret?.defName ?? "");
        }

        private static string CandidateDetail(AutoSpaceWeaponCandidate candidate)
        {
            if (!candidate.StructurallySupported)
                return "DeltaV_AutoWeaponRejected".Translate(RejectionLabel(candidate.rejection));
            string family = ("DeltaV_AutoWeaponFamily" + candidate.family).Translate();
            if (candidate.warnings == AutoSpaceWeaponWarning.None) return family;
            return family + " · " + "DeltaV_AutoWeaponWarnings".Translate(WarningLabels(candidate.warnings));
        }

        private static string WarningLabels(AutoSpaceWeaponWarning warnings)
        {
            var labels = new List<string>();
            foreach (AutoSpaceWeaponWarning value in Enum.GetValues(typeof(AutoSpaceWeaponWarning)))
            {
                if (value == AutoSpaceWeaponWarning.None || !warnings.HasFlag(value)) continue;
                labels.Add(("DeltaV_AutoWeaponWarning" + value).Translate());
            }
            return string.Join(", ", labels);
        }

        private static string RejectionLabel(AutoSpaceWeaponRejection rejection) =>
            ("DeltaV_AutoWeaponRejection" + rejection).Translate();

        private static string ModPolicyLabel(AutoSpaceWeaponModPolicy policy) =>
            ("DeltaV_AutoWeaponModPolicy" + policy).Translate();

        private static string WeaponOverrideLabel(AutoSpaceWeaponOverride value) =>
            ("DeltaV_AutoWeaponOverride" + value).Translate();

        private static void OpenModPolicyMenu(string packageId, DeltaVSettings settings)
        {
            var options = new List<FloatMenuOption>();
            foreach (AutoSpaceWeaponModPolicy value in Enum.GetValues(typeof(AutoSpaceWeaponModPolicy)))
            {
                AutoSpaceWeaponModPolicy captured = value;
                options.Add(new FloatMenuOption(ModPolicyLabel(captured),
                    () => settings.SetAutoWeaponModPolicy(packageId, captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void OpenWeaponOverrideMenu(AutoSpaceWeaponCandidate candidate,
            DeltaVSettings settings)
        {
            var options = new List<FloatMenuOption>();
            foreach (AutoSpaceWeaponOverride value in Enum.GetValues(typeof(AutoSpaceWeaponOverride)))
            {
                AutoSpaceWeaponOverride captured = value;
                if (captured == AutoSpaceWeaponOverride.Enabled
                    && !candidate.StructurallySupported) continue;
                options.Add(new FloatMenuOption(WeaponOverrideLabel(captured),
                    () => settings.SetAutoWeaponOverride(candidate.packageId,
                        candidate.turret.defName, captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}

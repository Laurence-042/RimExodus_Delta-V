using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class SectorSettingsUI
    {
        private const float RowHeight = 30f, SectionGap = 18f, IndentPerDepth = 18f;
        private static Vector2 scrollPosition;
        private static readonly Dictionary<string, string> Buffers = new Dictionary<string, string>();

        public static void Draw(Rect rect, DeltaVSettings settings)
        {
            settings.EnsureSectorGenerationTable();
            Text.Font = GameFont.Tiny; GUI.color = new Color(1, 1, 1, .6f);
            Rect intro = new Rect(rect.x, rect.y, rect.width, Text.LineHeight * 2 + 4);
            Widgets.Label(intro, "DeltaV_SettingsSectorIntro".Translate());
            GUI.color = Color.white; Text.Font = GameFont.Small;
            Rect controls = new Rect(rect.x, intro.yMax + 4, rect.width, RowHeight);
            float button = (controls.width - 24) / 4f;
            if (Widgets.ButtonText(new Rect(controls.x, controls.y, button, RowHeight), "DeltaV_SectorEditorButton".Translate()))
                Find.WindowStack.Add(new Dialog_SectorGenerationEditor(settings));
            if (Widgets.ButtonText(new Rect(controls.x + button + 8, controls.y, button, RowHeight), "DeltaV_SectorExport".Translate()))
            {
                try { string path = SectorGenerationJson.Export(settings); Messages.Message("DeltaV_SectorExported".Translate(path), MessageTypeDefOf.TaskCompletion, false); }
                catch (System.Exception ex) { Messages.Message("DeltaV_SectorJsonFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false); }
            }
            if (Widgets.ButtonText(new Rect(controls.x + (button + 8) * 2, controls.y, button, RowHeight), "DeltaV_SectorImport".Translate())) Import(settings);
            if (Widgets.ButtonText(new Rect(controls.x + (button + 8) * 3, controls.y, button, RowHeight), "DeltaV_SectorReset".Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DeltaV_SectorResetConfirm".Translate(), () =>
                { settings.sectorGenerationRoots = SectorGenerationTable.Defaults(); settings.sectorGenerationPreset = SectorGenerationPreset.Standard; Buffers.Clear(); }));

            Rect preset = new Rect(rect.x, controls.yMax + 4, rect.width, RowHeight);
            Widgets.Label(preset.LeftPart(.55f), "DeltaV_SettingsSectorPreset".Translate());
            if (Widgets.ButtonText(preset.RightPart(.45f), SectorGenerationPresets.LabelKey(settings.sectorGenerationPreset).Translate()))
                Find.WindowStack.Add(new FloatMenu(SectorGenerationPresets.All.Select(value =>
                { SectorGenerationPreset captured = value; return new FloatMenuOption(SectorGenerationPresets.LabelKey(captured).Translate(), () => settings.sectorGenerationPreset = captured); }).ToList()));
            TooltipHandler.TipRegion(preset, "DeltaV_SettingsSectorPresetTip".Translate());
            Rect expected = new Rect(rect.x, preset.yMax, rect.width, RowHeight);
            double area = SectorGenerationPresets.ReferenceSectorAreaCellsSquared();
            Text.Font = GameFont.Tiny; GUI.color = new Color(1, 1, 1, .7f);
            Widgets.Label(expected, "DeltaV_SettingsSectorExpectedCount".Translate(
                (area / SectorGenerationPresets.DensityAreaUnitCellsSquared).ToString("0.##"),
                SectorGenerationPresets.ExpectedObjectCount(settings, area).ToString("0.##")));
            GUI.color = Color.white; Text.Font = GameFont.Small;
            Rect viewport = new Rect(rect.x, expected.yMax, rect.width, rect.yMax - expected.yMax);
            Rect view = new Rect(0, 0, viewport.width - 16, ContentHeight(settings.sectorGenerationRoots));
            Widgets.BeginScrollView(viewport, ref scrollPosition, view);
            var list = new Listing_Standard(); list.Begin(view);
            foreach (SectorGenerationNode root in settings.sectorGenerationRoots) { DrawNode(list, root, 0); list.Gap(SectionGap); }
            list.End(); Widgets.EndScrollView();
        }

        private static float ContentHeight(IEnumerable<SectorGenerationNode> nodes)
            => nodes.Sum(node => node.group ? RowHeight + ContentHeight(node.children) : RowHeight * 3) + SectionGap;
        private static void DrawNode(Listing_Standard list, SectorGenerationNode node, int depth)
        {
            float indent = depth * IndentPerDepth;
            if (node.group)
            {
                Rect row = Indented(list.GetRect(RowHeight), indent);
                Text.Font = GameFont.Medium; GUI.color = new Color(.72f, .78f, .95f); Widgets.Label(row, node.label);
                GUI.color = Color.white; Text.Font = GameFont.Small;
                foreach (SectorGenerationNode child in node.children) DrawNode(list, child, depth + 1);
                return;
            }
            Rect header = Indented(list.GetRect(RowHeight), indent);
            Text.Font = GameFont.Medium; GUI.color = new Color(.9f, .85f, .6f); Widgets.Label(header, node.label);
            Text.Font = GameFont.Small; GUI.color = Color.white;
            NumericRow(list, indent, node.id + ":density", "DeltaV_SettingsSectorDensity".Translate(),
                "DeltaV_SettingsSectorDensityTip".Translate(), ref node.standardDensityPer100MCells2, 0, SectorGenerationTable.MaxStandardDensity);
            Rect prefabs = Indented(list.GetRect(RowHeight), indent);
            if (Widgets.ButtonText(prefabs, "DeltaV_SettingsSelectGenerationPrefabs".Translate()))
                Dialog_PrefabSelector.OpenMultiple("DeltaV_SettingsPrefabTreeTitle".Translate(node.label), node.prefabPaths);
            TooltipHandler.TipRegion(prefabs, "DeltaV_SettingsSectorPrefabPoolTip".Translate() + "\n\n"
                + "DeltaV_SettingsPrefabTreeCurrentCount".Translate(node.prefabPaths.Count));
        }
        private static void NumericRow(Listing_Standard list, float indent, string key, string label, string tip,
            ref float value, float min, float max)
        {
            Rect row = Indented(list.GetRect(RowHeight), indent); Widgets.Label(row.LeftPart(.55f), label);
            if (!Buffers.TryGetValue(key, out string buffer)) buffer = value.ToString("0.###");
            float edited = value; Widgets.TextFieldNumeric(row.RightPart(.45f), ref edited, ref buffer, min, max);
            Buffers[key] = buffer; value = edited; TooltipHandler.TipRegion(row, tip);
        }
        private static Rect Indented(Rect row, float indent) => new Rect(row.x + indent, row.y, row.width - indent, row.height);
        private static void Import(DeltaVSettings settings)
        {
            List<string> files = SectorGenerationJson.Files();
            if (files.Count == 0) { Messages.Message("DeltaV_SectorImportEmpty".Translate(SectorGenerationJson.DirectoryPath), MessageTypeDefOf.RejectInput, false); return; }
            Find.WindowStack.Add(new FloatMenu(files.Select(path => new FloatMenuOption(Path.GetFileName(path), () =>
            {
                if (!SectorGenerationJson.TryRead(path, out SectorGenerationDocument document, out string error))
                { Messages.Message("DeltaV_SectorJsonFailed".Translate(error), MessageTypeDefOf.RejectInput, false); return; }
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DeltaV_SectorImportConfirm".Translate(Path.GetFileName(path)), () =>
                { settings.sectorGenerationRoots = document.roots; settings.sectorGenerationPreset = document.densityPreset; Buffers.Clear(); }));
            })).ToList()));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_SectorGenerationEditor : Window
    {
        private readonly DeltaVSettings settings;
        private readonly List<SectorGenerationNode> roots;
        private Vector2 scroll;
        private Action pending;
        public override Vector2 InitialSize => new Vector2(900f, 720f);

        internal Dialog_SectorGenerationEditor(DeltaVSettings settings)
        {
            this.settings = settings;
            roots = settings.sectorGenerationRoots.Select(root => root.Copy()).ToList();
            forcePause = true; doCloseX = true; absorbInputAroundWindow = true; closeOnAccept = false;
        }

        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, rect.width, 32), "DeltaV_SectorEditorTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 34, rect.width, 42), "DeltaV_SectorEditorHelp".Translate());
            if (Widgets.ButtonText(new Rect(0, 80, 150, 32), "DeltaV_SectorEditorAddGroup".Translate())) roots.Add(NewNode(true));
            if (Widgets.ButtonText(new Rect(160, 80, 150, 32), "DeltaV_SectorEditorAddEntry".Translate())) roots.Add(NewNode(false));
            List<(SectorGenerationNode node, int depth)> rows = Flatten();
            Rect viewport = new Rect(0, 120, rect.width, rect.height - 174);
            Rect view = new Rect(0, 0, viewport.width - 18, Mathf.Max(viewport.height, rows.Count * 38f));
            Widgets.BeginScrollView(viewport, ref scroll, view);
            pending = null;
            for (int i = 0; i < rows.Count; i++) DrawRow(new Rect(0, i * 38f, view.width, 34f), rows[i].node, rows[i].depth);
            Widgets.EndScrollView(); pending?.Invoke();
            if (Widgets.ButtonText(new Rect(rect.width - 300, rect.height - 40, 140, 36), "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(rect.width - 150, rect.height - 40, 150, 36), "OK".Translate())) Apply();
        }

        private void DrawRow(Rect row, SectorGenerationNode node, int depth)
        {
            float x = depth * 20f;
            Widgets.Label(new Rect(x, row.y + 7, 72, 26), node.group ? "DeltaV_SectorEditorGroup".Translate() : "DeltaV_SectorEditorEntry".Translate());
            node.label = Widgets.TextField(new Rect(x + 74, row.y, Mathf.Max(120, row.width - x - 410), 34), node.label ?? "");
            float right = row.xMax;
            if (Widgets.ButtonText(new Rect(right - 324, row.y, 72, 34), "DeltaV_SectorEditorParent".Translate())) ChooseParent(node);
            if (Widgets.ButtonText(new Rect(right - 246, row.y, 48, 34), "↑")) pending = () => Move(node, -1);
            if (Widgets.ButtonText(new Rect(right - 192, row.y, 48, 34), "↓")) pending = () => Move(node, 1);
            if (node.group && Widgets.ButtonText(new Rect(right - 138, row.y, 66, 34), "+")) pending = () => node.children.Add(NewNode(false));
            if (Widgets.ButtonText(new Rect(right - 66, row.y, 66, 34), "Delete".Translate())) pending = () => Remove(node);
            TooltipHandler.TipRegion(row, node.id);
        }

        private void ChooseParent(SectorGenerationNode node)
        {
            var options = new List<FloatMenuOption> { new FloatMenuOption("DeltaV_SectorEditorRoot".Translate(), () => Reparent(node, null)) };
            foreach (SectorGenerationNode group in SectorGenerationTable.AllNodes(roots).Where(n => n.group && n != node && !Contains(node, n)))
            { SectorGenerationNode target = group; options.Add(new FloatMenuOption(target.label, () => Reparent(node, target))); }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        private void Reparent(SectorGenerationNode node, SectorGenerationNode target) { Remove(node); (target?.children ?? roots).Add(node); }
        private void Move(SectorGenerationNode node, int delta)
        {
            List<SectorGenerationNode> siblings = Siblings(node); int from = siblings.IndexOf(node), to = Mathf.Clamp(from + delta, 0, siblings.Count - 1);
            if (from < 0 || from == to) return; siblings.RemoveAt(from); siblings.Insert(to, node);
        }
        private void Remove(SectorGenerationNode node) => Siblings(node).Remove(node);
        private List<SectorGenerationNode> Siblings(SectorGenerationNode node)
        {
            SectorGenerationNode parent = SectorGenerationTable.AllNodes(roots).FirstOrDefault(candidate => candidate.group && candidate.children.Contains(node));
            return parent?.children ?? roots;
        }
        private static bool Contains(SectorGenerationNode root, SectorGenerationNode candidate)
            => root.children != null && (root.children.Contains(candidate) || root.children.Any(child => Contains(child, candidate)));
        private List<(SectorGenerationNode node, int depth)> Flatten()
        {
            var result = new List<(SectorGenerationNode, int)>();
            void Visit(SectorGenerationNode node, int depth) { result.Add((node, depth)); if (node.group) foreach (SectorGenerationNode child in node.children) Visit(child, depth + 1); }
            foreach (SectorGenerationNode root in roots) Visit(root, 0); return result;
        }
        private static SectorGenerationNode NewNode(bool group) => new SectorGenerationNode
        {
            id = (group ? "group-" : "entry-") + Guid.NewGuid().ToString("N").Substring(0, 8),
            label = group ? "DeltaV_SectorEditorNewGroup".Translate() : "DeltaV_SectorEditorNewEntry".Translate(), group = group
        };
        private void Apply()
        {
            if (!SectorGenerationTable.Validate(roots, out string error))
            { Messages.Message("DeltaV_SectorEditorInvalid".Translate(error), MessageTypeDefOf.RejectInput, false); return; }
            settings.sectorGenerationRoots = roots.Select(root => root.Copy()).ToList(); Close();
        }
    }
}

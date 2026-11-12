using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Prefab 目录树节点。展开状态由原版 TreeNode 的 openBits 保存，
    /// 不另建一套折叠状态。
    /// </summary>
    internal sealed class PrefabTreeNode : TreeNode
    {
        public readonly string label;
        public readonly string path;
        public readonly bool isFile;
        public readonly bool available;

        public PrefabTreeNode(string label, string path, bool isFile, bool available = true)
        {
            this.label = label;
            this.path = path;
            this.isFile = isFile;
            this.available = available;
            children = new List<TreeNode>();
        }

        public override bool Openable => !isFile && children.Count > 0;

        public IEnumerable<PrefabTreeNode> DescendantFiles()
        {
            if (isFile)
            {
                yield return this;
                yield break;
            }
            foreach (PrefabTreeNode child in children.Cast<PrefabTreeNode>())
            foreach (PrefabTreeNode file in child.DescendantFiles())
                yield return file;
        }

        public static PrefabTreeNode Build(IEnumerable<string> paths, int openMask)
            => Build(paths, paths, openMask);

        public static PrefabTreeNode Build(IEnumerable<string> paths, IEnumerable<string> availablePaths,
            int openMask)
        {
            var available = new HashSet<string>(availablePaths.Select(path => path.Replace('\\', '/').Trim('/')),
                StringComparer.OrdinalIgnoreCase);
            var root = new PrefabTreeNode(string.Empty, string.Empty, false);
            foreach (string normalized in paths.Where(path => !path.NullOrEmpty())
                         .Select(path => path.Replace('\\', '/').Trim('/'))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                PrefabTreeNode parent = root;
                string directoryPath = string.Empty;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    directoryPath = directoryPath.Length == 0 ? parts[i] : directoryPath + "/" + parts[i];
                    PrefabTreeNode directory = parent.children.Cast<PrefabTreeNode>()
                        .FirstOrDefault(node => !node.isFile
                            && string.Equals(node.label, parts[i], StringComparison.OrdinalIgnoreCase));
                    if (directory == null)
                    {
                        directory = new PrefabTreeNode(parts[i], directoryPath, false) { parentNode = parent };
                        parent.children.Add(directory);
                    }
                    parent = directory;
                }
                var file = new PrefabTreeNode(parts[parts.Length - 1], normalized, true,
                    available.Contains(normalized)) { parentNode = parent };
                parent.children.Add(file);
            }
            Sort(root);
            root.SetOpen(openMask, true);
            foreach (PrefabTreeNode firstLevel in root.children.Cast<PrefabTreeNode>().Where(node => !node.isFile))
                firstLevel.SetOpen(openMask, true);
            return root;
        }

        private static void Sort(PrefabTreeNode node)
        {
            node.children = node.children.Cast<PrefabTreeNode>()
                .OrderBy(child => child.isFile ? 1 : 0)
                .ThenBy(child => child.label, StringComparer.OrdinalIgnoreCase)
                .Cast<TreeNode>().ToList();
            foreach (PrefabTreeNode child in node.children.Cast<PrefabTreeNode>())
                Sort(child);
        }
    }

    /// <summary>
    /// 复用原版 Listing_Tree 的缩进、折叠箭头、声音与行布局；
    /// 勾选交互对齐 Listing_TreeThingFilter 的目录三态勾选方式。
    /// </summary>
    internal sealed class Listing_PrefabTree : Listing_Tree
    {
        private readonly HashSet<string> selected;
        private readonly bool single;
        private Rect visibleRect;

        public Listing_PrefabTree(HashSet<string> selected, bool single)
        {
            this.selected = selected;
            this.single = single;
        }

        public void DrawChildren(PrefabTreeNode root, int openMask, Rect visibleRect)
        {
            this.visibleRect = visibleRect;
            foreach (PrefabTreeNode child in root.children.Cast<PrefabTreeNode>())
                DrawNode(child, 0, openMask);
        }

        private void DrawNode(PrefabTreeNode node, int indentLevel, int openMask)
        {
            if (CurrentRowVisible())
            {
                if (!node.isFile) OpenCloseWidget(node, indentLevel, openMask);
                string label = node.isFile && !node.available
                    ? "DeltaV_SettingsPrefabTreeMissingPath".Translate(node.label) : node.label;
                LabelLeft(label, node.path, indentLevel, textColor: node.available ? (Color?)null : Color.gray);
                if (single)
                {
                    if (node.isFile && Widgets.RadioButton(LabelWidth, curY, selected.Contains(node.path),
                            disabled: !node.available))
                    {
                        selected.Clear();
                        selected.Add(node.path);
                    }
                }
                else if (node.isFile)
                {
                    bool value = selected.Contains(node.path);
                    bool before = value;
                    Widgets.Checkbox(new Vector2(LabelWidth, curY), ref value, lineHeight,
                        disabled: false, paintable: true);
                    if (value != before) SetSelected(node, value);
                }
                else
                {
                    MultiCheckboxState before = SelectionState(node);
                    MultiCheckboxState value = Widgets.CheckboxMulti(
                        new Rect(LabelWidth, curY, lineHeight, lineHeight), before, paintable: true);
                    if (value != before) SetSelected(node, value == MultiCheckboxState.On);
                }
            }
            EndLine();
            if (!node.isFile && IsOpen(node, openMask))
            {
                foreach (PrefabTreeNode child in node.children.Cast<PrefabTreeNode>())
                    DrawNode(child, indentLevel + 1, openMask);
            }
        }

        private MultiCheckboxState SelectionState(PrefabTreeNode node)
        {
            List<PrefabTreeNode> files = node.DescendantFiles().ToList();
            int count = files.Count(file => selected.Contains(file.path));
            if (count == 0) return MultiCheckboxState.Off;
            return count == files.Count ? MultiCheckboxState.On : MultiCheckboxState.Partial;
        }

        private void SetSelected(PrefabTreeNode node, bool value)
        {
            foreach (PrefabTreeNode file in node.DescendantFiles())
            {
                if (value) selected.Add(file.path);
                else selected.Remove(file.path);
            }
        }

        private bool CurrentRowVisible()
        {
            return visibleRect.Overlaps(new Rect(0f, curY, ColumnWidth, lineHeight));
        }
    }

    internal sealed class Dialog_PrefabSelector : Window
    {
        private const int OpenMask = TreeOpenMasks.Default;
        private readonly Action<List<string>> apply;
        private readonly HashSet<string> selected;
        private readonly List<string> available;
        private readonly PrefabTreeNode root;
        private readonly string title;
        private readonly bool single;
        private readonly string automaticLabel;
        private Vector2 scrollPosition;
        private float viewHeight = 100f;
        private string scanError;

        public override Vector2 InitialSize => new Vector2(720f, 720f);

        public static void OpenSingle(string title, string current, Action<string> apply, string automaticLabel = null)
        {
            Find.WindowStack.Add(new Dialog_PrefabSelector(title, new[] { current },
                paths => apply(paths.SingleOrDefault() ?? string.Empty), single: true, automaticLabel: automaticLabel));
        }

        public static void OpenMultiple(string title, List<string> target)
        {
            Find.WindowStack.Add(new Dialog_PrefabSelector(title, target, paths =>
            {
                target.Clear();
                target.AddRange(paths);
            }));
        }

        private Dialog_PrefabSelector(string title, IEnumerable<string> current, Action<List<string>> apply,
            bool single = false, string automaticLabel = null)
        {
            this.title = title;
            this.apply = apply;
            this.single = single;
            this.automaticLabel = automaticLabel;
            selected = new HashSet<string>(current.Where(path => !path.NullOrEmpty())
                .Select(path => path.Replace('\\', '/').Trim('/')), StringComparer.OrdinalIgnoreCase);
            try
            {
                available = MapPrefab.ListPrefabFiles();
            }
            catch (Exception ex)
            {
                available = new List<string>();
                scanError = ex.Message;
                Log.Warning($"[DeltaV] prefab directory scan failed: {ex.Message}");
            }
            // 已保存但当前失效的路径仍放入树中，避免打开并确认时静默丢失设置。
            root = PrefabTreeNode.Build(available.Concat(selected), available, OpenMask);
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 52f), title);
            Text.Font = GameFont.Small;

            Rect tools = new Rect(0f, 54f, inRect.width, 30f);
            if (single)
            {
                if (automaticLabel != null && Widgets.RadioButtonLabeled(tools, automaticLabel, selected.Count == 0))
                    selected.Clear();
            }
            else if (scanError == null)
            {
                Widgets.Label(tools.LeftPart(0.48f),
                    "DeltaV_SettingsPrefabTreeCurrentCount".Translate(selected.Count));
                if (Widgets.ButtonText(new Rect(inRect.width - 250f, tools.y, 120f, tools.height), "ClearAll".Translate()))
                    selected.Clear();
                if (Widgets.ButtonText(new Rect(inRect.width - 120f, tools.y, 120f, tools.height), "AllowAll".Translate()))
                {
                    selected.Clear();
                    selected.UnionWith(available);
                }
            }

            float treeTop = !single || automaticLabel != null ? 92f : 54f;
            Rect treeRect = new Rect(0f, treeTop, inRect.width, inRect.height - treeTop - 50f);
            Widgets.DrawMenuSection(treeRect);
            Rect inner = treeRect.ContractedBy(6f);
            if (scanError != null)
            {
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(inner, "DeltaV_SettingsPrefabTreeScanFailed".Translate(scanError));
                GUI.color = Color.white;
            }
            else if (available.Count == 0 && selected.Count == 0)
            {
                Widgets.Label(inner, "DeltaV_SettingsPrefabTreeNoFiles".Translate());
            }
            else
            {
                Rect view = new Rect(0f, 0f, inner.width - 16f, Math.Max(inner.height, viewHeight));
                Rect visible = new Rect(0f, scrollPosition.y, view.width, inner.height);
                Widgets.BeginScrollView(inner, ref scrollPosition, view);
                var listing = new Listing_PrefabTree(selected, single);
                listing.Begin(view);
                listing.DrawChildren(root, OpenMask, visible);
                listing.End();
                if (Event.current.type == EventType.Layout)
                    viewHeight = listing.CurHeight + 12f;
                Widgets.EndScrollView();
            }

            float buttonY = inRect.height - 38f;
            if (Widgets.ButtonText(new Rect(0f, buttonY, 120f, 32f), "CancelButton".Translate()))
                Close();
            bool canApply = single
                ? selected.Count == 0 ? automaticLabel != null : available.Any(selected.Contains)
                : scanError == null;
            if (Widgets.ButtonText(new Rect(inRect.width - 120f, buttonY, 120f, 32f), "OK".Translate(),
                    active: canApply))
            {
                Close();
                apply(selected.OrderBy(path => path, StringComparer.Ordinal).ToList());
            }
        }
    }
}

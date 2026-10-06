using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Mod 设置「生成」页：按 SectorContentTypes 类型树逐叶子配置保底数、分段概率
    /// 额外量与物化 prefab 池（写入 DeltaVSettings.sectorRules；分支节点只是分组标题）。
    ///
    /// - 递归绘制（任意深度），每层缩进 IndentPerDepth；行高全部用本类常量（自绘控件
    ///   而非 Listing 的字体相关行高），滚动内容总高可在绘制前精确递归算出；
    /// - prefab 池 = 目录扫描 + 勾选（MapPrefab.ListPrefabFiles 的正斜杠相对路径）。
    ///   扫描结果缓存到页头「重新扫描」按钮手动刷新（单个按钮服务整棵树）；勾选状态
    ///   即 rule.prefabPaths 成员关系；
    /// - TextFieldNumeric 缓冲按 (leafId, 字段) 键惰性建立；段的增删会使后续段错位，
    ///   增删时清该叶子的全部段缓冲重建（值本身在规则对象上，不丢失）。
    /// </summary>
    internal static class SectorSettingsUI
    {
        private const float RowHeight = 30f;
        private const float FileRowHeight = 24f;
        private const float SectionGap = 22f;
        private const float IndentPerDepth = 18f;

        private static Vector2 scrollPosition;
        private static List<string> scannedFiles;

        private static readonly Dictionary<string, string> Buffers = new Dictionary<string, string>();

        public static void Draw(Rect rect, DeltaVSettings settings)
        {
            settings.EnsureSectorRulesSeeded();
            settings.prefabDoctrines ??= new List<PrefabDoctrineRule>();
            if (scannedFiles == null) ScanPrefabFiles();

            // 说明 + 全局「重新扫描」画在滚动区外（固定头部，不参与高度计算）。
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Rect introRect = new Rect(rect.x, rect.y, rect.width - 130f, Text.LineHeight * 2f + 4f);
            Widgets.Label(introRect, "DeltaV_SettingsSectorIntro".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Rect rescanRect = new Rect(rect.xMax - 120f, rect.y, 120f, RowHeight);
            if (Widgets.ButtonText(rescanRect, "DeltaV_SettingsSectorPrefabRefresh".Translate()))
            {
                ScanPrefabFiles();
            }

            var scrollRect = new Rect(rect.x, rect.y + introRect.height, rect.width,
                rect.height - introRect.height);
            Rect view = new Rect(0f, 0f, scrollRect.width - 16f, ComputeContentHeight(settings));
            Widgets.BeginScrollView(scrollRect, ref scrollPosition, view);
            var list = new Listing_Standard();
            list.Begin(view);
            foreach (SectorContentType root in SectorContentTypes.All)
            {
                DrawNode(list, settings, root, 0);
                list.Gap(SectionGap);
            }
            list.End();
            Widgets.EndScrollView();
        }

        // ---- 布局 ----

        private static float ComputeContentHeight(DeltaVSettings settings)
        {
            float height = 0f;
            foreach (SectorContentType root in SectorContentTypes.All)
            {
                height += ComputeNodeHeight(settings, root) + SectionGap;
            }
            return height;
        }

        private static float ComputeNodeHeight(DeltaVSettings settings, SectorContentType node)
        {
            if (!node.IsLeaf)
            {
                float height = RowHeight; // 分组标题行
                foreach (SectorContentType child in node.children)
                {
                    height += ComputeNodeHeight(settings, child);
                }
                return height;
            }
            SectorTypeRule rule = settings.EffectiveSectorRule(node);
            float leafHeight = RowHeight;                                       // 类型标题
            leafHeight += RowHeight;                                            // 保底数
            leafHeight += RowHeight;                                            // 分段标题 + 添加按钮
            leafHeight += rule.segments.Count * RowHeight;                      // 各段
            leafHeight += RowHeight;                                            // 池标题（计数）
            leafHeight += (scannedFiles?.Count ?? 0) * FileRowHeight;           // 池文件勾选行
            if (scannedFiles != null)
            {
                foreach (string file in scannedFiles)
                {
                    if (!rule.prefabPaths.Contains(file)) continue;
                    PrefabDoctrineRule doctrine = settings.prefabDoctrines
                        ?.FirstOrDefault(r => r.prefabPath == file);
                    leafHeight += RowHeight * (2 + (doctrine?.priorities?.Count ?? 3));
                }
            }
            return leafHeight;
        }

        private static void DrawNode(Listing_Standard list, DeltaVSettings settings,
            SectorContentType node, int depth)
        {
            float indent = depth * IndentPerDepth;
            if (!node.IsLeaf)
            {
                // 分支：仅分组标题（子级递归绘制）。
                Rect headerRect = list.GetRect(RowHeight);
                Text.Font = GameFont.Medium;
                GUI.color = new Color(0.72f, 0.78f, 0.95f);
                Widgets.Label(Indented(headerRect, indent), node.Label);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                foreach (SectorContentType child in node.children)
                {
                    DrawNode(list, settings, child, depth + 1);
                }
                return;
            }
            DrawLeafSection(list, settings, node, indent);
        }

        private static void DrawLeafSection(Listing_Standard list, DeltaVSettings settings,
            SectorContentType type, float indent)
        {
            SectorTypeRule rule = settings.EffectiveSectorRule(type);

            // 类型标题行。
            Rect headerRect = Indented(list.GetRect(RowHeight), indent);
            GUI.color = new Color(0.9f, 0.85f, 0.6f);
            Text.Font = GameFont.Medium;
            Widgets.Label(headerRect, type.Label);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            // 保底数。
            NumericRow(list, indent, $"{type.id}:min", "DeltaV_SettingsSectorMinCount".Translate(),
                "DeltaV_SettingsSectorMinCountTip".Translate(), ref rule.minCount, 0, 100);

            // 分段标题 + 添加按钮。
            Rect segHeaderRect = list.GetRect(RowHeight);
            Widgets.Label(Indented(segHeaderRect, indent).LeftPart(0.72f),
                "DeltaV_SettingsSectorSegments".Translate());
            TooltipHandler.TipRegion(segHeaderRect, "DeltaV_SettingsSectorSegmentsTip".Translate());
            if (Widgets.ButtonText(RightPart(segHeaderRect, 120f), "DeltaV_SettingsSectorAddSegment".Translate()))
            {
                rule.segments.Add(new SegmentRule());
                ClearSegmentBuffers(type.id);
            }

            // 各段：概率% | 追加上限 | 删除。
            for (int i = 0; i < rule.segments.Count; i++)
            {
                SegmentRule segment = rule.segments[i];
                Rect row = list.GetRect(RowHeight);
                Rect indented = Indented(row, indent);
                Widgets.Label(indented.LeftPart(0.08f), (i + 1).ToString());
                int chancePct = Mathf.RoundToInt(segment.chance01 * 100f);
                string chanceBuffer = BufferFor($"{type.id}:seg{i}:chance", chancePct.ToString());
                Rect chanceRect = indented.LeftPart(0.6f).RightPart(0.46f);
                Widgets.TextFieldNumeric(chanceRect, ref chancePct, ref chanceBuffer, 0, 100);
                Buffers[$"{type.id}:seg{i}:chance"] = chanceBuffer;
                segment.chance01 = chancePct / 100f;
                int maxCount = segment.maxCount;
                string maxBuffer = BufferFor($"{type.id}:seg{i}:max", maxCount.ToString());
                Rect maxRect = indented.LeftPart(0.86f).RightPart(0.26f);
                Widgets.TextFieldNumeric(maxRect, ref maxCount, ref maxBuffer, 1, 1000);
                Buffers[$"{type.id}:seg{i}:max"] = maxBuffer;
                segment.maxCount = maxCount;
                TooltipHandler.TipRegion(row, "DeltaV_SettingsSectorSegmentTip".Translate());
                if (Widgets.ButtonText(RightPart(row, 80f), "DeltaV_SettingsSectorRemoveSegment".Translate()))
                {
                    rule.segments.RemoveAt(i);
                    ClearSegmentBuffers(type.id);
                    break; // 行集合已变，本帧不再继续画段
                }
            }

            // 池标题（计数；扫描/刷新按钮在页头，服务整棵树）。
            Rect poolHeaderRect = list.GetRect(RowHeight);
            Widgets.Label(Indented(poolHeaderRect, indent).LeftPart(0.85f),
                "DeltaV_SettingsSectorPrefabPool".Translate(rule.prefabPaths.Count, scannedFiles?.Count ?? 0));
            TooltipHandler.TipRegion(poolHeaderRect, "DeltaV_SettingsSectorPrefabPoolTip".Translate());

            // 池文件勾选行（目录为空时不画行——页头说明与计数已表达）。
            if (scannedFiles != null)
            {
                foreach (string file in scannedFiles)
                {
                    Rect row = list.GetRect(FileRowHeight);
                    bool selected = rule.prefabPaths.Contains(file);
                    bool toggled = selected;
                    Text.Font = GameFont.Tiny;
                    Widgets.CheckboxLabeled(Indented(row, indent), file, ref toggled);
                    Text.Font = GameFont.Small;
                    if (toggled != selected)
                    {
                        if (toggled) rule.prefabPaths.Add(file);
                        else rule.prefabPaths.Remove(file);
                    }
                    if (toggled) DrawDoctrineEditor(list, settings, file, indent + IndentPerDepth);
                }
            }
        }

        private static void DrawDoctrineEditor(Listing_Standard list, DeltaVSettings settings,
            string file, float indent)
        {
            PrefabDoctrineRule entry = settings.prefabDoctrines
                .FirstOrDefault(r => r.prefabPath == file);
            List<NpcDoctrine> priorities = entry?.priorities ?? NpcDoctrineDefaults.Create();
            Rect header = Indented(list.GetRect(RowHeight), indent);
            Widgets.Label(header, "DeltaV_SettingsNpcDoctrine".Translate());
            for (int i = 0; i < priorities.Count; i++)
            {
                int index = i;
                Rect row = Indented(list.GetRect(RowHeight), indent);
                Widgets.Label(new Rect(row.x, row.y, row.width - 126f, row.height),
                    (i + 1) + ". " + DoctrineLabel(priorities[i]));
                if (i > 0 && Widgets.ButtonText(new Rect(row.xMax - 126f, row.y, 38f, row.height), "↑"))
                {
                    entry = EnsureDoctrineEntry(settings, file);
                    (entry.priorities[index - 1], entry.priorities[index]) =
                        (entry.priorities[index], entry.priorities[index - 1]);
                }
                if (i < priorities.Count - 1 && Widgets.ButtonText(new Rect(row.xMax - 84f, row.y, 38f, row.height), "↓"))
                {
                    entry = EnsureDoctrineEntry(settings, file);
                    (entry.priorities[index + 1], entry.priorities[index]) =
                        (entry.priorities[index], entry.priorities[index + 1]);
                }
                if (Widgets.ButtonText(new Rect(row.xMax - 42f, row.y, 38f, row.height), "×"))
                {
                    entry = EnsureDoctrineEntry(settings, file);
                    entry.priorities.RemoveAt(index);
                    break;
                }
            }
            Rect add = Indented(list.GetRect(RowHeight), indent);
            if (Widgets.ButtonText(add, "DeltaV_SettingsNpcDoctrineAdd".Translate()))
            {
                var choices = new List<FloatMenuOption>();
                foreach (NpcDoctrine doctrine in System.Enum.GetValues(typeof(NpcDoctrine)))
                {
                    if (priorities.Contains(doctrine)) continue;
                    NpcDoctrine selected = doctrine;
                    choices.Add(new FloatMenuOption(DoctrineLabel(selected), () =>
                        EnsureDoctrineEntry(settings, file).priorities.Add(selected)));
                }
                if (choices.Count > 0) Find.WindowStack.Add(new FloatMenu(choices));
            }
        }

        private static PrefabDoctrineRule EnsureDoctrineEntry(DeltaVSettings settings, string file)
        {
            PrefabDoctrineRule entry = settings.prefabDoctrines.FirstOrDefault(r => r.prefabPath == file);
            if (entry != null) return entry;
            entry = new PrefabDoctrineRule { prefabPath = file, priorities = NpcDoctrineDefaults.Create() };
            settings.prefabDoctrines.Add(entry);
            return entry;
        }

        private static string DoctrineLabel(NpcDoctrine doctrine) =>
            ("DeltaV_NpcDoctrine_" + doctrine).Translate();

        /// <summary>带左标签 + 右数值输入 + 整行 tooltip 的行；缓冲按 key 存取并写回。</summary>
        private static void NumericRow(Listing_Standard list, float indent, string bufferKey, string label,
            string tip, ref int value, int min, int max)
        {
            Rect row = Indented(list.GetRect(RowHeight), indent);
            Widgets.Label(row.LeftPart(0.55f), label);
            int edited = value;
            string buffer = BufferFor(bufferKey, value.ToString());
            Widgets.TextFieldNumeric(row.RightPart(0.45f), ref edited, ref buffer, min, max);
            Buffers[bufferKey] = buffer;
            value = edited;
            TooltipHandler.TipRegion(row, tip);
        }

        private static Rect Indented(Rect row, float indent)
        {
            return new Rect(row.x + indent, row.y, row.width - indent, row.height);
        }

        private static Rect RightPart(Rect row, float width)
        {
            return new Rect(row.xMax - width, row.y, width, row.height);
        }

        // ---- 状态 ----

        private static void ScanPrefabFiles()
        {
            try
            {
                scannedFiles = MapPrefab.ListPrefabFiles();
            }
            catch (System.Exception ex)
            {
                // 目录定位失败（mod pack 未就绪等）：留空列表，用户可手动刷新重试。
                Log.Warning($"[DeltaV] prefab directory scan failed: {ex.Message}");
                scannedFiles = new List<string>();
            }
        }

        private static string BufferFor(string key, string seedValue)
        {
            if (!Buffers.TryGetValue(key, out string buffer) || buffer == null)
            {
                buffer = seedValue;
                Buffers[key] = buffer;
            }
            return buffer;
        }

        private static void ClearSegmentBuffers(string typeId)
        {
            string prefix = typeId + ":seg";
            foreach (string key in Buffers.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                Buffers.Remove(key);
            }
        }
    }
}

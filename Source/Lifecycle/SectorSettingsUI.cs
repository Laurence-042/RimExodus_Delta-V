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
    /// - prefab 池在叶子区只显示一个选择按钮。按下时重新扫描合并目录，
    ///   并交由 Dialog_PrefabPoolTree 用原版 Listing_Tree / TreeNode 展示可折叠勾选树；
    /// - TextFieldNumeric 缓冲按 (leafId, 字段) 键惰性建立；段的增删会使后续段错位，
    ///   增删时清该叶子的全部段缓冲重建（值本身在规则对象上，不丢失）。
    /// </summary>
    internal static class SectorSettingsUI
    {
        private const float RowHeight = 30f;
        private const float SectionGap = 22f;
        private const float IndentPerDepth = 18f;

        private static Vector2 scrollPosition;
        private static readonly Dictionary<string, string> Buffers = new Dictionary<string, string>();

        public static void Draw(Rect rect, DeltaVSettings settings)
        {
            settings.EnsureSectorRulesSeeded();

            // 说明画在滚动区外；prefab 扫描在选择窗口每次打开时执行。
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Rect introRect = new Rect(rect.x, rect.y, rect.width, Text.LineHeight * 2f + 4f);
            Widgets.Label(introRect, "DeltaV_SettingsSectorIntro".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

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
            leafHeight += RowHeight;                                            // prefab 选择按钮
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

            Rect prefabButtonRect = Indented(list.GetRect(RowHeight), indent);
            if (Widgets.ButtonText(prefabButtonRect, "DeltaV_SettingsSelectGenerationPrefabs".Translate()))
            {
                Find.WindowStack.Add(new Dialog_PrefabPoolTree(type.Label, rule.prefabPaths));
            }
            TooltipHandler.TipRegion(prefabButtonRect,
                "DeltaV_SettingsSectorPrefabPoolTip".Translate() + "\n\n"
                + "DeltaV_SettingsPrefabTreeCurrentCount".Translate(rule.prefabPaths.Count));
        }


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

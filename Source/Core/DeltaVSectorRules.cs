using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 分段概率额外量（星区内容机制：保底 + 分段概率，星区架构 §4/§12）：
    /// 每段独立掷骰，以 chance01 概率追加 1..maxCount 个。
    /// </summary>
    public sealed class SegmentRule : IExposable
    {
        public float chance01 = 0.5f;

        public int maxCount = 1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref chance01, "chance", 0.5f);
            Scribe_Values.Look(ref maxCount, "maxCount", 1);
        }
    }

    /// <summary>
    /// 单个星区内容类型的生成规则（Mod 设置「生成」页的数据载体）。
    /// 属于全局 Mod 设置：跨存档共享，只影响尚未首次生成的星区（星区一旦标记
    /// generatedSectorTiles 即定型，后改设置不追溯）。prefabPaths 是相对
    /// MapPrefab 合并目录的相对路径池；物化内容链 prefab-only——池为空时生成期
    /// 即拦截（报错跳过该类型），配置了但失效时物化期取消该 POI。
    /// </summary>
    public sealed class SectorTypeRule : IExposable
    {
        public string typeId;

        /// <summary>每星区保底数量（不足则补齐；现存更多时不删）。</summary>
        public int minCount;

        /// <summary>保底之外的额外生成分段（各段独立掷骰，可多段可空）。</summary>
        public List<SegmentRule> segments = new List<SegmentRule>();

        /// <summary>物化内容 prefab 池（相对合并目录的路径，正斜杠分隔）。</summary>
        public List<string> prefabPaths = new List<string>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref typeId, "typeId", null);
            Scribe_Values.Look(ref minCount, "minCount", 0);
            Scribe_Collections.Look(ref segments, "segments", LookMode.Deep);
            Scribe_Collections.Look(ref prefabPaths, "prefabPaths", LookMode.Value);
        }
    }
}

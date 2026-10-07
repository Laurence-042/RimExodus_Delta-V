using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 星区内容类型注册表节点（任意深度树，数据驱动开闭条目）。
    ///
    /// - **分支节点**只是分组标题（UI 树形展示用），不参与生成记账；
    ///   **叶子节点**承载生成规则：保底数 + 分段概率额外量 + 物化 prefab 池
    ///   （Mod 设置「生成」页，SectorTypeRule）。
    /// - id 是存档（PoiState.contentTypeId）与设置（SectorTypeRule.typeId）的稳定键，
    ///   一经使用不再改名；显示名走 Keyed（labelKey），进入完整名的类型名段。
    /// - 占位类型默认 0 个 → 不生成；物化内容链 prefab-only（池空在生成期拦截报错），
    ///   因此「先注册类型、后做内容」无害。
    /// - 新类型 = 在树上加一个节点（叶子或带子级的分支），生成/设置/物化链零结构
    ///   改动（递归遍历，不建家族特判）。
    /// </summary>
    public sealed class SectorContentType
    {
        public readonly string id;

        public readonly string labelKey;

        /// <summary>每星区保底数量的默认值（仅叶子使用；设置里无该类型条目时生效）。</summary>
        public readonly int defaultMin;

        /// <summary>子类型（空 = 叶子）。</summary>
        public readonly List<SectorContentType> children = new List<SectorContentType>();

        public SectorContentType(string id, string labelKey, int defaultMin = 0,
            params SectorContentType[] childNodes)
        {
            this.id = id;
            this.labelKey = labelKey;
            this.defaultMin = defaultMin;
            if (childNodes != null && childNodes.Length > 0)
            {
                children.AddRange(childNodes);
            }
        }

        public bool IsLeaf => children.Count == 0;

        /// <summary>显示名（翻译）；生成时烘焙进 envLabel。</summary>
        public string Label => labelKey.Translate();
    }

    public static class SectorContentTypes
    {
        /// <summary>
        /// 类型树根清单（星区架构 §11「星区内容类型」）：飞行器五级、空间站六类
        /// （卫星再分两级）；全部占位默认 0。残骸是地图内容的派生结果，不是生成类型。
        /// 伴生物不在此
        /// 树——它由空间站蓝图生成点伴生，不是独立星区生成项（§11 仍为设计状态）。
        /// </summary>
        public static readonly List<SectorContentType> All = new List<SectorContentType>
        {
            new SectorContentType("Craft", "DeltaV_SectorTypeCraft", 0,
                new SectorContentType("Fighter", "DeltaV_SectorTypeFighter"),
                new SectorContentType("Gunboat", "DeltaV_SectorTypeGunboat"),
                new SectorContentType("Frigate", "DeltaV_SectorTypeFrigate"),
                new SectorContentType("Destroyer", "DeltaV_SectorTypeDestroyer"),
                new SectorContentType("Cruiser", "DeltaV_SectorTypeCruiser")),
            new SectorContentType("Station", "DeltaV_SectorTypeStation", 0,
                new SectorContentType("Satellite", "DeltaV_SectorTypeSatellite", 0,
                    new SectorContentType("WeaponUnlockSatellite", "DeltaV_SectorTypeWeaponUnlockSatellite"),
                    new SectorContentType("AbandonedSatellite", "DeltaV_SectorTypeAbandonedSatellite")),
                new SectorContentType("WeaponPlatform", "DeltaV_SectorTypeWeaponPlatform"),
                new SectorContentType("TradeStation", "DeltaV_SectorTypeTradeStation"),
                new SectorContentType("Shipyard", "DeltaV_SectorTypeShipyard"),
                new SectorContentType("FactoryStation", "DeltaV_SectorTypeFactoryStation"),
                new SectorContentType("Asteroid", "DeltaV_SectorTypeAsteroid")),
        };

        /// <summary>深度优先遍历全部节点（含分支）。</summary>
        public static IEnumerable<SectorContentType> AllNodes()
        {
            foreach (SectorContentType root in All)
            {
                foreach (SectorContentType node in Subtree(root))
                {
                    yield return node;
                }
            }
        }

        /// <summary>全部叶子——生成循环与设置条目的唯一遍历口径。</summary>
        public static IEnumerable<SectorContentType> AllLeaves()
        {
            return AllNodes().Where(node => node.IsLeaf);
        }

        /// <summary>按 id 查节点；未知 id（注册表未来收缩）返回 null，调用方自行回退。</summary>
        public static SectorContentType ById(string id)
        {
            return AllNodes().FirstOrDefault(node => node.id == id);
        }

        private static IEnumerable<SectorContentType> Subtree(SectorContentType node)
        {
            yield return node;
            foreach (SectorContentType child in node.children)
            {
                foreach (SectorContentType descendant in Subtree(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

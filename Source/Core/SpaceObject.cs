using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 宇宙承载对象（解耦架构 §2）：**一张内部地图与星球轨道坐标之间的中间层**，
    /// 保存该地图整体的轨道运动状态。它不是某艘飞船的子类型或永久所有者——飞船 =
    /// 主驾驶台 + 同归属四连通基架（见 ShipRegistry），同图可承载多艘船与残骸归属。
    ///
    /// 权威事实：对象 ID、唯一逻辑内部地图、轨道位姿与动量、物化/生命周期所需数据
    /// （PoiState 特化）。名称是派生结果：纯容器名 = 内部全部结构名拼接
    /// （UTF-8 字节序，见 label 虚属性），POI 保留环境结构名；派生缓存不落盘。
    /// faction 字段保留为环境派系（仅 PoiState 赋值并持久化；多船图的驾驶授权
    /// 逐船从主台派系判定，不得把容器 faction 当作全部船的授权）。
    ///
    /// 「每个可进入地图都属于一个 SpaceObject」：口袋图 = Delta-V 自建对象的内部，
    /// worldtile 地图 = 原版站点/空画布锚对象的内部；内部地图坐标 ⇄ 轨道坐标的唯一
    /// 通道是所属对象的位姿（对象位姿 ⊕ 内部局部坐标），不存在图级坐标锚。
    ///
    /// "map == null" 只表示当前没有对象自己的已加载地图（POI 的 Packed 状态），
    /// 不表示对象不存在。map 之外的姿态数据是存档事实；外壳显示以 Map 的
    /// SectionLayer 为事实源。
    /// </summary>
    public class SpaceObject : IExposable
    {
        public int objectId;

        public SpacePose pose;

        /// <summary>对应的内部地图（已加载时非空）。Scribe_References 在 cross-ref 阶段解析。</summary>
        public Map map;

        /// <summary>
        /// 环境派系（POI 的归属事实；纯容器恒为 null）。多船混合时显示层应作组合标记，
        /// 驾驶授权逐船经 ShipRegistry.FactionOf 判定（解耦架构 §10）。
        /// </summary>
        public Faction faction;

        [Unsaved] private string cachedLabel;
        [Unsaved] private int cachedLabelVersion = -1;

        /// <summary>
        /// 显示名。纯容器 = 内部全部结构名拼接（可重建派生，不落盘）；PoiState 覆盖为
        /// 环境结构名（持久事实）。名称只用于显示，不参与身份与定位（解耦架构 §10）。
        /// </summary>
        public virtual string label
        {
            get
            {
                int version = UniverseManager.StructureNameVersion;
                if (cachedLabelVersion == version && cachedLabel != null) return cachedLabel;
                cachedLabel = ComputeContainerLabel();
                cachedLabelVersion = version;
                return cachedLabel;
            }
        }

        /// <summary>对象的当前内部地图（唯一逻辑内部图，无第二语义）。</summary>
        public virtual Map InteriorMap => map;

        public SpaceObject()
        {
        }

        public SpaceObject(int objectId, Map map, SpacePose pose)
        {
            this.objectId = objectId;
            this.map = map;
            this.pose = pose;
        }

        /// <summary>
        /// 容器拼接名：图上现存基架的归属名按 UTF-8 字节序排序、同名按结构 ID 稳定排序，
        /// 以本地化分隔符连接；同归属的多个未分离断片合为一个条目（一个 ID 只出现一次）。
        /// 无任何结构的容器显示「未注册对象」。
        /// </summary>
        private string ComputeContainerLabel()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Map interior = InteriorMap;
            if (universe == null || interior == null || interior.Disposed)
            {
                return "DeltaV_OwnerUnregistered".Translate(objectId);
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(interior);
            List<int> owners = ownership?.OwnersOnMap();
            if (owners == null || owners.Count == 0)
            {
                return "DeltaV_OwnerUnregistered".Translate(objectId);
            }
            owners.Sort(CompareOwners);
            var parts = new System.Text.StringBuilder();
            foreach (int owner in owners)
            {
                if (parts.Length > 0)
                {
                    parts.Append("DeltaV_NameSeparator".Translate());
                }
                parts.Append(universe.LabelForOwner(owner));
            }
            return parts.ToString();
        }

        /// <summary>排序：名称的 UTF-8 字节序；同名按结构 ID（确定性，与当前语言文化无关）。</summary>
        private int CompareOwners(int a, int b)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            int byName = string.CompareOrdinal(
                universe?.LabelForOwner(a) ?? "", universe?.LabelForOwner(b) ?? "");
            // string.CompareOrdinal 即 UTF-16 码元序；对补充平面外字符与 UTF-8 字节序一致，
            // 本项目名称均为常规字符，二者等价。不同名按名称，同名按 ID。
            return byName != 0 ? byName : a.CompareTo(b);
        }

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId", -1);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_References.Look(ref map, "map");
        }
    }
}

using Verse;

namespace DeltaV
{
    /// <summary>
    /// 一艘飞船的宇宙状态（Universe State，AGENTS §11）。
    /// map 之外的姿态数据是存档事实。外壳显示以飞船 Map 的 SectionLayer 为事实源；
    /// 地形层有一份可丢弃的局部 UV 派生网格，结构变化经 dirty 自驱动重建，无可序列化显示状态。
    /// </summary>
    public class ShipState : IExposable
    {
        public int shipId;
        public string label;
        public SpacePose pose;

        /// <summary>该飞船的口袋地图。Scribe_References 在 cross-ref 阶段解析，Map 实现 ILoadReferenceable。</summary>
        public Map map;

        public ShipState()
        {
        }

        public ShipState(int shipId, string label, Map map, SpacePose pose)
        {
            this.shipId = shipId;
            this.label = label;
            this.map = map;
            this.pose = pose;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref shipId, "shipId", -1);
            Scribe_Values.Look(ref label, "label");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_References.Look(ref map, "map");
        }
    }
}

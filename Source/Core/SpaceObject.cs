using Verse;

namespace DeltaV
{
    /// <summary>
    /// 宇宙对象基类（AGENTS §11 Universe State）：宇宙位置、朝向、速度、必要身份、
    /// Map 对应关系。飞船（ShipState）与太空 POI（PoiState）共用同一坐标系与
    /// SpaceTransform 变换链——坐标语义对所有宇宙对象唯一。
    ///
    /// "map == null" 只表示当前没有已加载的地图（POI 的 Packed 状态），不表示对象
    /// 不存在（AGENTS：当前没有 Map 不代表 SpaceObject 不存在）。
    /// map 之外的姿态数据是存档事实；外壳显示以 Map 的 SectionLayer 为事实源。
    /// </summary>
    public abstract class SpaceObject : IExposable
    {
        public int objectId;

        public string label;

        public SpacePose pose;

        /// <summary>对应的口袋地图（已加载时非空）。Scribe_References 在 cross-ref 阶段解析。</summary>
        public Map map;

        protected SpaceObject()
        {
        }

        protected SpaceObject(int objectId, string label, Map map, SpacePose pose)
        {
            this.objectId = objectId;
            this.label = label;
            this.map = map;
            this.pose = pose;
        }

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref objectId, "objectId", -1);
            Scribe_Values.Look(ref label, "label");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_References.Look(ref map, "map");
        }
    }
}

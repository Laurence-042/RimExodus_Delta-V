using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 宇宙对象基类（AGENTS §11 Universe State；星区架构 §4）：宇宙位置、朝向、速度、
    /// 必要身份、派系归属、Map 对应关系。飞船（ShipState）与太空 POI（PoiState）共用
    /// 同一坐标系与 SpaceTransform 变换链——坐标语义对所有宇宙对象唯一。
    ///
    /// 「每个可进入地图都属于一个 SpaceObject」：口袋图 = Delta-V 自建对象的内部，
    /// worldtile 地图 = 原版站点/空画布锚对象的内部；内部地图坐标 ⇄ 轨道坐标的唯一
    /// 通道是所属对象的位姿（对象位姿 ⊕ 内部局部坐标），不存在图级坐标锚。
    ///
    /// "map == null" 只表示当前没有对象自己的已加载地图（POI 的 Packed 状态，或飞船
    /// 固定轨道停靠在 dockedMap），不表示对象不存在（AGENTS：当前没有 Map 不代表
    /// SpaceObject 不存在）。
    /// map 之外的姿态数据是存档事实；外壳显示以 Map 的 SectionLayer 为事实源。
    /// </summary>
    public abstract class SpaceObject : IExposable
    {
        public int objectId;

        public string label;

        /// <summary>派系归属（星区架构 §9：§11 敌我差异复用原版派系的前置）。持久化。</summary>
        public Faction faction;

        public SpacePose pose;

        /// <summary>对应的口袋地图（已加载时非空）。Scribe_References 在 cross-ref 阶段解析。</summary>
        public Map map;

        /// <summary>
        /// 对象当前可进入的内部地图。普通对象就是 <see cref="map"/>；停靠飞船由
        /// <see cref="ShipState"/> 覆盖为共享宿主图。所有 UI、渲染、拾取与结构查询
        /// 必须消费本入口，禁止在调用点重复拼装 docked/map 分支。
        /// </summary>
        public virtual Map InteriorMap => map;

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
            Scribe_References.Look(ref faction, "faction");
            Scribe_Deep.Look(ref pose, "pose");
            Scribe_References.Look(ref map, "map");
        }
    }
}

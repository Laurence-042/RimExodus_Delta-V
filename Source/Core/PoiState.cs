using UnityEngine;
using Verse;
using RimWorld.Planet;

namespace DeltaV
{
    public enum PoiKind : byte
    {
        SnapshotPocket,
        NativeParent
    }

    /// <summary>
    /// 未物化 POI 触发器（SpaceObject 特化，星区架构 §4；2026-09-25 用户定案收窄）。
    ///
    /// **marker（本类）= 无图的触发器特例**：显示生成时冻结的名称与 prefab 投影，
    /// 玩家接近后经 SpaceLifecycle 物化——物化产物是**普通 SpaceObject 容器**
    /// （不是 PoiState；ambientLabel 继承 envLabel 作纯环境兜底名）。归档（收纳）
    /// 保留同 ID，替换为 ArchivedSpaceObject。即：**SnapshotPocket 型 PoiState 永远没有图**。
    ///
    /// - NativeParent = 例外：原版/第三方轨道 MapParent 的镜像（含空画布锚对象），
    ///   map 字段跟随 vanilla 生命周期（SynchronizeNative/UpdateFromParent），物化态
    ///   保留原版图标与名称；不参与 marker↔容器替换制。
    /// - SnapshotPocket = marker：携结构快照（玩家改动可恢复）或模板 seed；
    ///   物化时消费进图。
    ///
    /// POI 名（envLabel）是持久的环境结构名（无基架的小行星等环境不能显示为空对象，
    /// 解耦架构 §10）；派系是环境事实。地图生命周期三段（活跃/休眠/归档，
    /// SpaceLifecycle 统一管理，判定沿用普通距离与玩家保护规则）：
    /// - 有图未休眠：活跃（正常 tick）；
    /// - 有图休眠：地图保留但冻结 tick（Harmony 前缀早退）；
    /// - marker（无图）：接近后物化替换。
    /// 休眠集合是运行时状态、不存档：读档后先活跃，下一次生命周期扫描重新分段。
    /// </summary>
    public class PoiState : MapSpaceObject
    {
        public PoiKind kind;

        /// <summary>原版或第三方轨道 POI 的事实源；定制 POI 为 null。</summary>
        public MapParent nativeParent;

        /// <summary>模板种子：无快照重生成时使用（快照优先，保证玩家改动可恢复）。</summary>
        public int seed;

        /// <summary>删除段收起的结构级快照（地形/屋顶/建筑/物品；不保 pawn 与 comp 内部状态）。</summary>
        public PoiSnapshot snapshot;

        /// <summary>环境结构名（持久事实；marker/NativeParent 的显示名来源）。</summary>
        public string envLabel;

        /// <summary>marker 显示名 = envLabel（无图，无拼接可言）；NativeParent = 原版名。
        /// 物化容器（普通 SpaceObject）不经本类，名 = 结构拼接。</summary>
        public override string label => envLabel;

        /// <summary>PoiState 的缺省 tag 是 Marker。</summary>
        protected override SpaceObjectTag DefaultTag => SpaceObjectTag.Marker;

        public PoiState()
        {
        }

        public PoiState(int poiId, string label, Vector3 position, float angle, int seed)
        {
            objectId = poiId;
            envLabel = label;
            pose = new SpacePose(position, angle);
            kind = PoiKind.SnapshotPocket;
            baseTag = SpaceObjectTag.Marker;
            this.seed = seed;
        }

        public PoiState(int poiId, MapParent parent)
        {
            objectId = poiId;
            envLabel = parent?.LabelCap ?? "Orbital POI";
            map = parent?.Map;
            pose = new SpacePose(parent != null && parent.Tile.Valid
                ? OrbitSphere.TileCenterDirection(parent.Tile)
                : Vector3.right, 0f);
            kind = PoiKind.NativeParent;
            baseTag = SpaceObjectTag.Marker | SpaceObjectTag.GravityRegion;
            nativeParent = parent;
            faction = parent?.Faction;
            seed = parent?.ID ?? 0;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref kind, "kind", PoiKind.SnapshotPocket);
            Scribe_References.Look(ref nativeParent, "nativeParent");
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Deep.Look(ref snapshot, "snapshot");
            Scribe_Values.Look(ref envLabel, "label", null);
        }
    }
}

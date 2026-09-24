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
    /// 太空 POI 的宇宙状态（SpaceObject 特化，星区架构 §4）。
    ///
    /// - NativeParent = 「以 worldtile 地图为内部的 SpaceObject」：事实源是原版或
    ///   第三方轨道 MapParent（含空画布锚对象），位置由 tile 中心方向纯函数导出
    ///   （它没有更细的地址），经 GetOrGenerateMap 原版语义物化；降落进入它内部
    ///   与「最近 tile 选图」无关——战术判定一律在连续轨道坐标完成。
    /// - SnapshotPocket = Delta-V 自建对象（残骸等）：独立球面位置 + 结构快照，
    ///   内部是口袋图。
    ///
    /// POI 名（envLabel）是持久的环境结构名（无基架的小行星等环境不能显示为空对象，
    /// 解耦架构 §10）；派系是环境事实。地图生命周期三段（活跃/休眠/删除，
    /// SpaceLifecycle 统一管理）：
    /// - map != null 且未休眠：活跃（正常 tick）；
    /// - map != null 且休眠：地图保留但冻结 tick（Harmony 前缀早退）；
    /// - map == null：NativeParent 仍由 MapParent 表示，SnapshotPocket 则为 Packed；
    ///   接近后分别用 parent 原生生成定义或结构快照恢复。
    /// 休眠集合是运行时状态、不存档：读档后先活跃，下一次生命周期扫描重新分段。
    /// </summary>
    public class PoiState : SpaceObject
    {
        public PoiKind kind;

        /// <summary>原版或第三方轨道 POI 的事实源；定制 POI 为 null。</summary>
        public MapParent nativeParent;

        /// <summary>模板种子：无快照重生成时使用（快照优先，保证玩家改动可恢复）。</summary>
        public int seed;

        /// <summary>删除段收起的结构级快照（地形/屋顶/建筑/物品；不保 pawn 与 comp 内部状态）。</summary>
        public PoiSnapshot snapshot;

        /// <summary>环境结构名（持久事实；POI 显示名的唯一来源）。</summary>
        public string envLabel;

        public override string label => envLabel;

        public PoiState()
        {
        }

        public PoiState(int poiId, string label, Vector3 position, float angle, int seed)
        {
            objectId = poiId;
            envLabel = label;
            pose = new SpacePose(position, angle);
            kind = PoiKind.SnapshotPocket;
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

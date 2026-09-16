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
    /// 太空 POI 的宇宙状态（SpaceObject 特化，阶段3）。NativeParent 的位置由真实
    /// 轨道 tile 纯函数导出；SnapshotPocket 使用独立球面位置。
    ///
    /// 地图生命周期三段（活跃/休眠/删除，SpaceLifecycle 统一管理）：
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

        public PoiState()
        {
        }

        public PoiState(int poiId, string label, Vector3 position, float angle, int seed)
            : base(poiId, label, null, new SpacePose(position, angle))
        {
            kind = PoiKind.SnapshotPocket;
            this.seed = seed;
        }

        public PoiState(int poiId, MapParent parent)
            : base(poiId, parent?.LabelCap ?? "Orbital POI", parent?.Map,
                new SpacePose(parent != null && parent.Tile.Valid
                    ? OrbitSphere.TileCenterDirection(parent.Tile)
                    : Vector3.right, 0f))
        {
            kind = PoiKind.NativeParent;
            nativeParent = parent;
            seed = parent?.ID ?? 0;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref kind, "kind", PoiKind.SnapshotPocket);
            Scribe_References.Look(ref nativeParent, "nativeParent");
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Deep.Look(ref snapshot, "snapshot");
        }
    }
}

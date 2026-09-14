using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 太空 POI 的宇宙状态（SpaceObject 特化，阶段3）。阶段3 的 POI 静止
    /// （velocity 恒零，字段保留以维持统一物理语义）。
    ///
    /// 地图生命周期三段（活跃/休眠/删除，SpaceLifecycle 统一管理）：
    /// - map != null 且未休眠：活跃（正常 tick）；
    /// - map != null 且休眠：地图保留但冻结 tick（Harmony 前缀早退）；
    /// - map == null：Packed——只有本轻量状态（+可选快照），再次接近时按
    ///   快照恢复（无快照则按模板与 seed 重生成）。
    /// 休眠集合是运行时状态、不存档：读档后先活跃，下一次生命周期扫描重新分段。
    /// </summary>
    public class PoiState : SpaceObject
    {
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
            this.seed = seed;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Deep.Look(ref snapshot, "snapshot");
        }
    }
}

using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船/POI 口袋地图填充步：结构外全图铺**原版 Space 真空**（用户决策 2026-09-17，
    /// 取代旧 DeltaV_Void）：甲板可经原版逆重机制向外扩建（Substructure 可铺在 Space 上），
    /// 残骸附近空位可停靠降落，也为 EVA 维护扩展留出地形基础。地图仍是合法矩形；
    /// 结构外的通行性由 Space 自身语义决定（与原版轨道图一致）。
    /// 纪律不变：这不是可飞行宇宙——不得让远距离弹丸横穿、不得用 Map 边长定义宇宙距离。
    /// </summary>
    public class GenStep_DeltaVShipFill : GenStep
    {
        public override int SeedPart => 917331;

        public override void Generate(Map map, GenStepParams parms)
        {
            foreach (IntVec3 cell in map.AllCells)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Space);
            }
        }
    }
}

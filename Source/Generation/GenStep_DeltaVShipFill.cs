using Verse;

namespace DeltaV
{
    /// <summary>
    /// 飞船地图填充步：全图铺 DeltaV_Void，可活动区域由 ShipStructureStamp 后续铺设。
    /// 保证地图仍是合法矩形，同时船外区域不可通行、不可建造（AGENTS §7 Void 纪律：
    /// Void 不是宇宙空间，只是地图边界承载）。
    /// </summary>
    public class GenStep_DeltaVShipFill : GenStep
    {
        public override int SeedPart => 917331;

        public override void Generate(Map map, GenStepParams parms)
        {
            foreach (IntVec3 cell in map.AllCells)
            {
                map.terrainGrid.SetTerrain(cell, DeltaVDefOf.DeltaV_Void);
            }
        }
    }
}

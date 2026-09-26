using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 月池力场的**墙壁式连接图渲染**（2026-09-26 用户定案：参考墙壁按邻接关系自动
    /// 取图集变体——月池团中心显示力场、外缘显示包边）。机制与墙壁完全同源：
    /// 4 位邻居掩码（N=1/E=2/S=4/W=8，邻居 = 月池地形）→ MaterialAtlasPool 的
    /// 4×4 十六格图集细分（Terrain/MoonPoolAtlas）。
    ///
    /// 地形本体 dontRender（舱口透出下方宇宙——「可透视力场」的原始需求），力场由
    /// 本类绘制：聚焦图 = 每帧 GenDraw.DrawQuad（推进器聚焦路径同款）；外壳路径 =
    /// MapOverlayRenderer.CollectSection 的地形分支改取本类材质。两条路径共用
    /// <see cref="MaterialFor"/> 唯一材质入口（单实现纪律）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MoonPoolGraphics
    {
        private static Material atlasMaterial;
        private static readonly Material[] SubMaterials = new Material[16];

        static MoonPoolGraphics()
        {
            Texture2D atlas = ContentFinder<Texture2D>.Get("Terrain/MoonPoolAtlas", false);
            if (atlas != null)
            {
                atlasMaterial = MaterialPool.MatFrom(new MaterialRequest(atlas,
                    ShaderDatabase.Transparent, Color.white));
            }
        }

        /// <summary>
        /// 一格月池的连接图材质（唯一入口）：掩码语义与 Graphic_Linked 一致——
        /// 位清零 = 该侧无月池邻接 → 图集变体在该侧画包边；图外（地图边缘）视为未连接。
        /// </summary>
        public static Material MaterialFor(Map map, IntVec3 cell)
        {
            if (atlasMaterial == null || map == null) return BaseContent.BadMat;
            int mask = 0;
            int bit = 1;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 neighbor = cell + GenAdj.CardinalDirections[i];
                if (neighbor.InBounds(map)
                    && map.terrainGrid.TerrainAt(neighbor) == MoonPoolUtility.MoonPoolTerrain)
                {
                    mask += bit;
                }
                bit *= 2;
            }
            Material material = SubMaterials[mask];
            if (material == null)
            {
                material = MaterialAtlasPool.SubMaterialFromAtlas(atlasMaterial, (LinkDirections)mask);
                SubMaterials[mask] = material;
            }
            return material;
        }

        /// <summary>
        /// 聚焦图力场绘制（UniverseManager.GameComponentUpdate 每帧；只读真实地形，
        /// 不参与任何模拟——表现层纪律）。相机视野内的月池格逐格 DrawQuad。
        /// </summary>
        public static void DrawFocused(UniverseManager universe)
        {
            Map map = Find.CurrentMap;
            if (map == null || atlasMaterial == null || universe == null
                || universe.FrameOf(map) == null || !MoonPoolUtility.MapHasMoonPool(map))
            {
                return;
            }
            float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc;
            CellRect view = Find.CameraDriver.CurrentViewRect.ClipInsideMap(map).ExpandedBy(1);
            foreach (IntVec3 cell in MoonPoolUtility.MoonPoolCells(map))
            {
                if (!view.Contains(cell)) continue;
                Material material = MaterialFor(map, cell);
                if (material == null || material == BaseContent.BadMat) continue;
                GenDraw.DrawQuad(material,
                    new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f),
                    Quaternion.identity, 1f);
            }
        }
    }
}

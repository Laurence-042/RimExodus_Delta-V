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
    /// 4×4 十六格图集细分。边框直接采用 Odyssey GravshipHull linked atlas 的图形
    /// 语法，透明力场是独立底层；因此连通域共享边天然消失、内部不会铺成实体地板。
    ///
    /// 地形本体 dontRender（舱口透出下方宇宙——「可透视力场」的原始需求），力场由
    /// SectionLayer_MoonPool 烘入原版 section mesh（地形变更才重建，不走每帧逐格绘制）；
    /// 外壳路径由 MapOverlayRenderer 的缓存 section 取同一材质。两条路径共用
    /// <see cref="MaterialFor"/> 唯一材质入口（单实现纪律）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MoonPoolGraphics
    {
        private static Material rimAtlasMaterial;
        private static Material fieldMaterial;
        private static readonly Material[] SubMaterials = new Material[16];

        static MoonPoolGraphics()
        {
            Texture2D atlas = ContentFinder<Texture2D>.Get("Terrain/MoonPoolAtlas", false);
            if (atlas != null)
            {
                rimAtlasMaterial = MaterialPool.MatFrom(new MaterialRequest(atlas,
                    ShaderDatabase.Transparent, Color.white));
            }
            fieldMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Transparent,
                new Color(0.18f, 0.68f, 0.92f, 0.10f));
        }

        public static Material FieldMaterial => fieldMaterial ?? BaseContent.BadMat;

        /// <summary>
        /// 一格月池的连接图材质（唯一入口）：掩码语义与 Graphic_Linked 一致——
        /// 位清零 = 该侧无月池邻接 → 图集变体在该侧画包边；图外（地图边缘）视为未连接。
        /// </summary>
        public static Material RimMaterialFor(Map map, IntVec3 cell)
        {
            if (rimAtlasMaterial == null || map == null) return BaseContent.BadMat;
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
                material = MaterialAtlasPool.SubMaterialFromAtlas(rimAtlasMaterial, (LinkDirections)mask);
                SubMaterials[mask] = material;
            }
            return material;
        }

    }

    /// <summary>
    /// 月池的原版式静态 section layer。Section 会自动发现所有 SectionLayer 子类；地形
    /// dirty 时才重建，机制与墙/原版 substructure props 相同，不在 GameComponentUpdate
    /// 常驻遍历地图或相机视野。
    /// </summary>
    public sealed class SectionLayer_MoonPool : SectionLayer
    {
        public SectionLayer_MoonPool(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => ModsConfig.OdysseyActive;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            float altitude = AltitudeLayer.Terrain.AltitudeFor() + Altitudes.AltInc;
            foreach (IntVec3 cell in section.CellRect)
            {
                if (map.terrainGrid.TerrainAt(cell) != MoonPoolUtility.MoonPoolTerrain) continue;
                Material field = MoonPoolGraphics.FieldMaterial;
                if (field != null && field != BaseContent.BadMat)
                {
                    Printer_Plane.PrintPlane(this,
                        new Vector3(cell.x + 0.5f, altitude, cell.z + 0.5f),
                        Vector2.one, field, topVerticesAltitudeBias: 0f);
                }
                Material material = MoonPoolGraphics.RimMaterialFor(map, cell);
                if (material == null || material == BaseContent.BadMat) continue;
                Printer_Plane.PrintPlane(this,
                    new Vector3(cell.x + 0.5f, altitude + Altitudes.AltInc, cell.z + 0.5f),
                    Vector2.one, material, topVerticesAltitudeBias: 0f);
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}

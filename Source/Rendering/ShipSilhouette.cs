using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 全项目唯一的剪影逻辑（2026-09-17 用户决策：有且仅有一个）：格子集合 → 标准
    /// 朝向剪影纹理。标准朝向 = 地图 +x → 纹理 +x（屏幕右），地图 +z（北）→ 纹理
    /// +y（屏幕上）。依据：Unity SetPixels32 的第 0 行是纹理底部、GUI.DrawTexture /
    /// DrawTextureRotated 不再做翻转、世界 quad 标准 UV v 向上——因此构建时一律
    /// **不做任何翻转**；此前星球仪按「纹理 y 向下」的误解翻转过 y，剪影呈上下
    /// 颠倒，叠加朝向旋转后表现为左右反（2026-09-17 实测：右侧扩建显示在左侧）。
    /// 消费方（世界视图标记 / 星球仪剪影层）各自保留缓存与快照策略，但纹理生成
    /// 与朝向语义必须经此入口，不得再各写一份。
    /// </summary>
    public static class ShipSilhouette
    {
        private const int TexturePadding = 1;

        /// <summary>
        /// 按格子集合构建正方形剪影纹理（内容在方形内居中——旋转围绕方形中心即围绕
        /// 船体中心，细长船不会画圆弧甩摆）。无有效格子返回 null；调用方负责纹理生命周期。
        /// </summary>
        public static Texture2D BuildTexture(IEnumerable<IntVec3> cells, string name)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (IntVec3 cell in cells)
            {
                minX = Mathf.Min(minX, cell.x);
                maxX = Mathf.Max(maxX, cell.x);
                minZ = Mathf.Min(minZ, cell.z);
                maxZ = Mathf.Max(maxZ, cell.z);
            }
            if (minX > maxX)
            {
                return null;
            }
            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;
            int side = Mathf.Max(width, height) + TexturePadding * 2;
            int offsetX = (side - width) / 2;
            int offsetY = (side - height) / 2;
            var pixels = new Color32[side * side];
            Color32 opaque = new Color32(255, 255, 255, 255);
            foreach (IntVec3 cell in cells)
            {
                int x = cell.x - minX + offsetX;
                int y = cell.z - minZ + offsetY;
                pixels[x + y * side] = opaque;
            }
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>
        /// 全图基架格扫描（船甲板与残骸甲板同口径；原版站点地图没有 Substructure →
        /// 空列表 → 调用方退回图标）。星球仪剪影层的格子来源。
        /// </summary>
        public static List<IntVec3> ScanSubstructure(Map map)
        {
            var cells = new List<IntVec3>();
            if (map == null || map.Disposed)
            {
                return cells;
            }
            foreach (IntVec3 cell in CellRect.WholeMap(map))
            {
                if (map.terrainGrid.TerrainAt(cell) == DeltaVDefOf.Substructure)
                {
                    cells.Add(cell);
                }
            }
            return cells;
        }
    }
}

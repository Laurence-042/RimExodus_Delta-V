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
    ///
    /// 剪影管线有三个自由度，事故全部出自自由度间的失配（2026-09-18 第三次事故
    /// 后在此收口）：(1) 纹理格子的源坐标系——口袋图是规范化形状（+x = 船头，
    /// 进移动模式时 PlacementRotation 归一到 East），停靠图是嵌入宿主图坐标的形状
    /// （含落点朝向 landedFacing 的旋转，+x = 宿主图东向）；(2) 旋转角输入；(3) 角
    /// 度数学（MarkerForwardWorld，2026-09-17 已统一）。历史上只统一了 (3)，(1)↔(2)
    /// 的配对散落在消费者手里：星球仪把船姿态角配停靠图纹理 → 朝向转过 2×
    /// landedFacing（实测朝西停成朝东）。配对规则：**口袋图纹理配 ship.pose.angle，
    /// 停靠图纹理配宿主帧角 host.pose.angle**（船形已嵌入图内，再叠加姿态角即重复
    /// 计入落点朝向）。经 OrientationForCurrentMap 获取，消费者不得自行组合。
    /// </summary>
    public static class ShipSilhouette
    {
        /// <summary>「按当前图扫描」的剪影朝向：纹理 +x 轴在切空间基（东=0，逆时针）下的方向角。</summary>
        public struct SilhouetteOrientation
        {
            /// <summary>切空间基的取值位置（球面方向）。</summary>
            public Vector3 referenceDir;

            /// <summary>纹理 +x 轴在该基下的方向角（弧度）。</summary>
            public float angle;
        }

        /// <summary>
        /// 「按 SpaceObject.InteriorMap 扫描」纹理的朝向配对（见类注释）：
        /// 停靠态纹理嵌入宿主图坐标系 → 宿主帧角；移动态纹理为口袋图规范化形状 →
        /// 船姿态角。宿主缺对象时兜底船姿态角（不变量 #17 下不应发生）。
        /// </summary>
        public static SilhouetteOrientation OrientationForCurrentMap(ShipState ship, UniverseManager universe)
        {
            if (ship.docked && ship.dockedMap != null)
            {
                SpaceObject host = universe?.SpaceObjectOnMap(ship.dockedMap);
                if (host != null)
                {
                    return new SilhouetteOrientation
                    {
                        referenceDir = ship.pose.position,
                        angle = host.pose.angle
                    };
                }
            }
            return new SilhouetteOrientation
            {
                referenceDir = ship.pose.position,
                angle = ship.pose.angle
            };
        }

        private const int TexturePadding = 1;

        /// <summary>
        /// 按格子集合构建正方形剪影纹理。contentAnchor = null 时内容按包围盒居中
        /// （世界剪影语义：旋转围绕船体中心，细长船不甩摆）；显式传锚点（连续格角坐标，
        /// 与格子同空间）时按「锚点 → 方形中心」布置、边长按各格到锚点最大距离取齐——
        /// 停靠剪影传口袋图几何中心（位姿锚），剪影内容与未来真实脚印逐格重合
        /// （2026-09-21 用户定案：视觉重叠即判据本身）。无有效格子返回 null；
        /// 调用方负责纹理生命周期。
        /// </summary>
        public static Texture2D BuildTexture(IEnumerable<IntVec3> cells, string name,
            Vector2? contentAnchor = null)
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
            if (contentAnchor.HasValue)
            {
                float ax = contentAnchor.Value.x;
                float az = contentAnchor.Value.y;
                int reach = TexturePadding + 1;
                foreach (IntVec3 cell in cells)
                {
                    reach = Mathf.Max(reach, Mathf.CeilToInt(
                        Mathf.Max(Mathf.Abs(cell.x + 0.5f - ax), Mathf.Abs(cell.z + 0.5f - az))));
                }
                int anchoredSide = reach * 2;
                var anchoredPixels = new Color32[anchoredSide * anchoredSide];
                Color32 anchoredOpaque = new Color32(255, 255, 255, 255);
                foreach (IntVec3 cell in cells)
                {
                    int x = Mathf.RoundToInt(cell.x - ax) + reach;
                    int y = Mathf.RoundToInt(cell.z - az) + reach;
                    if (x >= 0 && x < anchoredSide && y >= 0 && y < anchoredSide)
                    {
                        anchoredPixels[x + y * anchoredSide] = anchoredOpaque;
                    }
                }
                var anchoredTexture = new Texture2D(anchoredSide, anchoredSide, TextureFormat.RGBA32, false)
                {
                    name = name,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                anchoredTexture.SetPixels32(anchoredPixels);
                anchoredTexture.Apply(false, true);
                return anchoredTexture;
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
        /// 全图基架格扫描（ownerId ≤ 0 的旧整图口径，仅调试/兜底用；原版站点地图没有
        /// Substructure → 空列表 → 调用方退回图标）。
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

        /// <summary>
        /// 按归属过滤的基架格扫描（2026-09-18 基架归属设计后的正口径）：停靠在 wreck 图
        /// 上的飞船只扫自己 ID 的格子，与 wreck 剪影互不污染。归属数据缺失（组件不存在
        /// 或该对象 ID 无格）时回退整图口径。
        /// </summary>
        public static List<IntVec3> ScanSubstructure(Map map, int ownerId)
        {
            if (map == null || map.Disposed)
            {
                return new List<IntVec3>();
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            if (ownership == null)
            {
                return ScanSubstructure(map);
            }
            return ownership.CellsOwnedBy(ownerId);
        }
    }
}

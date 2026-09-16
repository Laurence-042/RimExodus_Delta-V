using System;
using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 轨道球面几何的唯一入口（AGENTS §5：宇宙坐标是球面绝对坐标）。
    ///
    /// universe 位置 = 轨道层球面上的单位方向向量（SpacePose.position）；
    /// 切空间约定与旧平面系一致：x = 东向、y = 北向（东向基手性沿用阶段2 EastSign 结论）。
    ///
    /// 尺度（星区架构 §8）：CellsPerTile 是**设置项**（默认 20000）——1 个星区
    /// （worldtile）的弧长约等于多少宇宙格；它只参与"格 ↔ 弧度"换算，
    /// 不参与 tile 划分（球面方向 → tile 是与尺度常量无关的纯几何函数），
    /// 因此调整该值不会让星区记账错位。无轨道层的存档（非 Odyssey 开局）
    /// 视为退化：Move 原地返回、Offset 归零，上层入口已按 isSpace 门控。
    ///
    /// 层参数化（星区架构 §9）：方向 → tile 的地址函数与半径/尺度换算按
    /// PlanetLayer 参数化（缓存按层分键），不得烧死"只有一个轨道层"——
    /// 恒星系扩展时新增恒星系坐标系与 manager，把本类按层实例化挂为其成员。
    /// Move/Offset/Basis 仍以当前轨道层为隐式参数（唯一的飞行空间），
    /// 其依赖的尺度状态已按层分键，届时扩展是机械动作。
    ///
    /// sphere↔tile 互推是纯函数（无锚）；TileFromDirection 只作 coarse address
    /// （世界视图、原版世界交互、星区记账），**不参与任何战术几何判定**
    /// （碰撞、降落选点一律在连续轨道坐标中完成，星区架构 §3）。
    /// </summary>
    public static class OrbitSphere
    {
        /// <summary>CellsPerTile 设置项的默认值（星区架构 §8：跨一个 worldtile 约 2 分钟）。</summary>
        public const float DefaultCellsPerTile = 20000f;

        /// <summary>1 个星区（worldtile）弧长 ≈ 多少宇宙格。设置项，可调；只动尺度不动 tile 划分。</summary>
        public static float CellsPerTile => DeltaVMod.Settings?.cellsPerTile ?? DefaultCellsPerTile;

        // 东向基向量手性：若实测星球背景移动方向与飞行方向相反，翻转此常量
        // （沿用 UniverseLayerMapping 阶段2/3 结论）。
        private const float EastSign = 1f;

        private static World cachedWorld;
        private static PlanetLayer cachedLayer;
        private static readonly Dictionary<PlanetLayer, float> RadiusByLayer = new Dictionary<PlanetLayer, float>();
        private static readonly Dictionary<PlanetLayer, float> WorldPerCellByLayer = new Dictionary<PlanetLayer, float>();
        private static readonly List<PlanetTile> NeighborBuffer = new List<PlanetTile>(6);

        /// <summary>当前游戏的轨道层（Def.isSpace 的第一层）。无则 null；按 World 实例缓存。</summary>
        public static PlanetLayer Layer
        {
            get
            {
                if (Current.Game == null || Find.World == null)
                {
                    return null;
                }
                if (ReferenceEquals(cachedWorld, Find.World) && cachedLayer != null)
                {
                    return cachedLayer;
                }
                cachedWorld = Find.World;
                cachedLayer = null;
                foreach (PlanetLayer layer in Find.WorldGrid.PlanetLayers.Values)
                {
                    if (layer.Def.isSpace)
                    {
                        cachedLayer = layer;
                        break;
                    }
                }
                return cachedLayer;
            }
        }

        /// <summary>层是否可承载轨道坐标（有 tile 才能取中心/半径）。</summary>
        public static bool LayerUsable(PlanetLayer layer)
        {
            return layer != null && layer.TilesCount > 0;
        }

        public static bool Valid => LayerUsable(Layer);

        /// <summary>指定轨道层的球面半径（世界单位）。按层缓存。</summary>
        public static float RadiusOf(PlanetLayer layer)
        {
            if (!LayerUsable(layer)) return 1f;
            if (!RadiusByLayer.TryGetValue(layer, out float radius))
            {
                radius = layer.GetTileCenter(0).magnitude;
                RadiusByLayer[layer] = radius;
            }
            return radius;
        }

        /// <summary>轨道层球面半径（世界单位）。</summary>
        public static float Radius => RadiusOf(Layer);

        /// <summary>指定轨道层上 1 宇宙格对应的世界单位长度。按层缓存。</summary>
        public static float WorldPerCellOf(PlanetLayer layer)
        {
            if (!LayerUsable(layer)) return 1f;
            if (!WorldPerCellByLayer.TryGetValue(layer, out float value))
            {
                value = layer.AverageTileSize / CellsPerTile;
                WorldPerCellByLayer[layer] = value;
            }
            return value;
        }

        /// <summary>1 宇宙格对应的世界单位长度。</summary>
        public static float WorldPerCell => WorldPerCellOf(Layer);

        /// <summary>指定层上 1 弧度弧长对应的宇宙格数（球面上量距的换算系数）。</summary>
        public static float CellsPerRadianOf(PlanetLayer layer)
        {
            return RadiusOf(layer) / WorldPerCellOf(layer);
        }

        /// <summary>1 弧度弧长对应的宇宙格数（球面上量距的换算系数）。</summary>
        public static float CellsPerRadian => CellsPerRadianOf(Layer);

        /// <summary>读档 / 新世界 / 尺度设置变化后清空缓存（与 ShellRenderer 缓存自愈纪律一致）。</summary>
        public static void ClearCache()
        {
            cachedWorld = null;
            cachedLayer = null;
            RadiusByLayer.Clear();
            WorldPerCellByLayer.Clear();
        }

        // ---- 切空间基与球面移动 ----

        /// <summary>
        /// 方向处的切空间正交基（东/北，单位向量）。北向 = 世界 up 在切平面上的投影，
        /// 与 RimWorld 层北极 (0, Radius, 0) 一致；极点退化时取任意切向。
        /// </summary>
        public static void Basis(Vector3 dir, out Vector3 east, out Vector3 north)
        {
            Vector3 up = dir.normalized;
            Vector3 northRef = Vector3.up;
            north = northRef - up * Vector3.Dot(northRef, up);
            if (north.sqrMagnitude < 1e-6f)
            {
                north = Vector3.forward - up * Vector3.Dot(Vector3.forward, up);
            }
            north.Normalize();
            east = Vector3.Cross(up, north) * EastSign;
        }

        /// <summary>
        /// 从 fromDir 沿切空间偏移（格）移动后的新方向（Rodrigues 精确旋转，全程单位长度）。
        /// </summary>
        public static Vector3 Move(Vector3 fromDir, Vector2 tangentCells)
        {
            if (!Valid || tangentCells.sqrMagnitude < 1e-12f)
            {
                return fromDir.normalized;
            }
            Basis(fromDir, out Vector3 east, out Vector3 north);
            Vector3 tangent = (east * tangentCells.x + north * tangentCells.y) * WorldPerCell;
            float angle = tangent.magnitude / Radius;
            Vector3 axis = Vector3.Cross(fromDir, tangent);
            if (axis.sqrMagnitude < 1e-12f)
            {
                return fromDir.normalized;
            }
            axis.Normalize();
            // k·v = 0（axis ⊥ fromDir），Rodrigues 简化式。
            return (fromDir * Mathf.Cos(angle) + Vector3.Cross(axis, fromDir) * Mathf.Sin(angle)).normalized;
        }

        /// <summary>
        /// 两方向间的切空间偏移（格，观察者基下）。点积 × CellsPerRadian，
        /// 对游戏尺度（跨数十 tile）内的相对量算足够精确，且全程无状态。
        /// </summary>
        public static Vector2 Offset(Vector3 fromDir, Vector3 toDir)
        {
            if (!Valid)
            {
                return Vector2.zero;
            }
            Basis(fromDir, out Vector3 east, out Vector3 north);
            Vector3 to = toDir.normalized;
            return new Vector2(Vector3.Dot(to, east), Vector3.Dot(to, north)) * CellsPerRadian;
        }

        /// <summary>两方向间的球面弧距（格）。POI 生命周期等距离判定用。</summary>
        public static float ArcCells(Vector3 a, Vector3 b)
        {
            if (!Valid)
            {
                return 0f;
            }
            float dot = Mathf.Clamp(Vector3.Dot(a.normalized, b.normalized), -1f, 1f);
            return Mathf.Acos(dot) * CellsPerRadian;
        }

        /// <summary>世界相机球面点（MapParent.WorldCameraPosition 覆写的输出格式）。</summary>
        public static Vector3 SpherePoint(Vector3 dir)
        {
            return dir.normalized * Radius;
        }

        // ---- sphere ⇄ tile（纯地址函数，无锚；与尺度常量无关）----

        /// <summary>tile → 中心方向。层由 tile 自身导出，可安全用于任意层。</summary>
        public static Vector3 TileCenterDirection(PlanetTile tile)
        {
            PlanetLayer layer = tile.Valid ? tile.Layer : Layer;
            if (!LayerUsable(layer) || !tile.Valid)
            {
                return Vector3.right;
            }
            return layer.GetTileCenter(tile).normalized;
        }

        /// <summary>
        /// 球面方向 → 指定层上最近 tile（coarse address：世界视图、原版世界交互、
        /// 星区记账专用，不作战术几何判定）。线性扫描取最大点积；只在低频路径调用，
        /// 轨道层 tile 数量级（千）下单次调用耗时可忽略。
        /// </summary>
        public static PlanetTile TileFromDirection(PlanetLayer layer, Vector3 dir)
        {
            if (!LayerUsable(layer))
            {
                return PlanetTile.Invalid;
            }
            Vector3 d = dir.normalized;
            int best = -1;
            float bestDot = float.MinValue;
            for (int i = 0; i < layer.TilesCount; i++)
            {
                float dot = Vector3.Dot(layer.GetTileCenter(i), d);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }
            return best < 0 ? PlanetTile.Invalid : new PlanetTile(best, layer);
        }

        /// <summary>球面方向 → 当前轨道层最近 tile（转发 <see cref="Layer"/>）。</summary>
        public static PlanetTile TileFromDirection(Vector3 dir)
        {
            return TileFromDirection(Layer, dir);
        }

        /// <summary>
        /// 球面方向 → 指定层上最近 tile 的热路径版本。hint 来自同一对象上次的结果时，
        /// 沿球面邻接图向点积更大的格爬坡；连续运动通常只检查当前格的 5/6 个邻居。
        /// hint 缺失或属于其他层时才退回全量扫描。纯函数语义不变，hint 只是加速提示。
        /// </summary>
        public static PlanetTile TileFromDirection(PlanetLayer layer, Vector3 dir, PlanetTile hint)
        {
            if (!LayerUsable(layer))
            {
                return PlanetTile.Invalid;
            }
            if (!hint.Valid || hint.Layer != layer)
            {
                return TileFromDirection(layer, dir);
            }

            Vector3 d = dir.normalized;
            PlanetTile current = hint;
            float currentDot = Vector3.Dot(layer.GetTileCenter(current), d);
            // 正常连续运动只走 0/1 步；上限防御损坏的邻接数据，极端瞬移超限则
            // 回到已验证的全量扫描，不能把局部缓存变成坐标事实源。
            for (int step = 0; step < layer.TilesCount; step++)
            {
                NeighborBuffer.Clear();
                layer.GetTileNeighbors(current, NeighborBuffer);
                PlanetTile next = current;
                float nextDot = currentDot;
                for (int i = 0; i < NeighborBuffer.Count; i++)
                {
                    PlanetTile candidate = NeighborBuffer[i];
                    float dot = Vector3.Dot(layer.GetTileCenter(candidate), d);
                    if (dot > nextDot)
                    {
                        next = candidate;
                        nextDot = dot;
                    }
                }
                if (next == current)
                {
                    return current;
                }
                current = next;
                currentDot = nextDot;
            }
            return TileFromDirection(layer, dir);
        }

        /// <summary>球面方向 → 当前轨道层最近 tile（hint 爬坡版，转发 <see cref="Layer"/>）。</summary>
        public static PlanetTile TileFromDirection(Vector3 dir, PlanetTile hint)
        {
            return TileFromDirection(Layer, dir, hint);
        }
    }
}

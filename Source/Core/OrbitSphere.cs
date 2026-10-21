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
        // The sole stored direction of SpacePose. Vector3 is a computed Unity/API view,
        // never a second position or a residual-displacement accumulator.
        internal struct Direction
        {
            internal double x, y, z;
            internal Direction(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            internal Direction(Vector3 v) : this(v.x, v.y, v.z) { }
            internal Vector3 Vector => new Vector3((float)x, (float)y, (float)z);
            internal double LengthSquared => Dot(this, this);
            internal Direction Normalized => LengthSquared > 0d ? this * (1d / Math.Sqrt(LengthSquared)) : this;
            internal static double Dot(Direction a, Direction b) => a.x * b.x + a.y * b.y + a.z * b.z;
            internal static Direction Cross(Direction a, Direction b)
                => new Direction(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            public static Direction operator +(Direction a, Direction b) => new Direction(a.x + b.x, a.y + b.y, a.z + b.z);
            public static Direction operator -(Direction a, Direction b) => new Direction(a.x - b.x, a.y - b.y, a.z - b.z);
            public static Direction operator *(Direction a, double scale) => new Direction(a.x * scale, a.y * scale, a.z * scale);
        }
        /// <summary>CellsPerTile 设置项的默认值（星区架构 §8：跨一个 worldtile 约 2 分钟）。</summary>
        public const float DefaultCellsPerTile = 20000f;

        /// <summary>1 个星区（worldtile）弧长 ≈ 多少宇宙格。设置项，可调；只动尺度不动 tile 划分。</summary>
        public static float CellsPerTile => DeltaVMod.CurrentSettings?.cellsPerTile ?? DefaultCellsPerTile;

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
            Basis(new Direction(dir), out Direction e, out Direction n);
            east = e.Vector; north = n.Vector;
        }

        private static void Basis(Direction dir, out Direction east, out Direction north)
        {
            Direction up = dir.Normalized;
            Direction northRef = new Direction(0d, 1d, 0d);
            north = northRef - up * Direction.Dot(northRef, up);
            if (north.LengthSquared < 1e-6d)
            {
                northRef = new Direction(0d, 0d, 1d);
                north = northRef - up * Direction.Dot(northRef, up);
            }
            north = north.Normalized;
            east = Direction.Cross(up, north) * EastSign;
        }

        /// <summary>
        /// 从 fromDir 沿切空间偏移（格）移动后的新方向（Rodrigues 精确旋转，全程单位长度）。
        /// </summary>
        public static Vector3 Move(Vector3 fromDir, Vector2 tangentCells)
            => Move(new Direction(fromDir), tangentCells).Vector;

        internal static Direction Move(Direction fromDir, Vector2 tangentCells)
        {
            if (!Valid || tangentCells.x == 0f && tangentCells.y == 0f)
            {
                return fromDir.Normalized;
            }
            Basis(fromDir, out Direction east, out Direction north);
            double distance = Math.Sqrt((double)tangentCells.x * tangentCells.x + (double)tangentCells.y * tangentCells.y);
            // Normalize the cell-space direction before scaling. Unity's normalization
            // epsilon must never turn a short world-space displacement into a speed gate.
            Direction tangent = east * (tangentCells.x / distance) + north * (tangentCells.y / distance);
            double angle = distance / CellsPerRadian;
            return (fromDir.Normalized * Math.Cos(angle) + tangent * Math.Sin(angle)).Normalized;
        }

        /// <summary>
        /// Move 的局部逆变换：两方向间的球面弧偏移（格，观察者切基下）。
        /// 切向投影的长度是 sin(angle)，必须用 atan2 还原弧角；直接乘
        /// CellsPerRadian 会使 Map→universe→Map 在远处不互逆。
        /// 对跖点没有唯一的最短方向，返回零偏移（调用方不得用它表示跨半球地图点）。
        /// </summary>
        public static Vector2 Offset(Vector3 fromDir, Vector3 toDir)
            => Offset(new Direction(fromDir), new Direction(toDir));

        internal static Vector2 Offset(Direction fromDir, Direction toDir)
        {
            if (!Valid)
            {
                return Vector2.zero;
            }
            Direction from = fromDir.Normalized, to = toDir.Normalized;
            Basis(from, out Direction east, out Direction north);
            double x = Direction.Dot(to - from, east), y = Direction.Dot(to - from, north);
            double tangentLength = Math.Sqrt(x * x + y * y);
            double radial = Math.Max(-1d, Math.Min(1d, Direction.Dot(from, to)));
            if (tangentLength == 0d || (radial < 0d && tangentLength < 1e-12d))
                return Vector2.zero;
            double scale = Math.Atan2(tangentLength, radial) * CellsPerRadian / tangentLength;
            return new Vector2((float)(x * scale), (float)(y * scale));
        }

        /// <summary>两方向间的球面弧距（格）。POI 生命周期等距离判定用。</summary>
        public static float ArcCells(Vector3 a, Vector3 b)
            => ArcCells(new Direction(a), new Direction(b));

        internal static float ArcCells(Direction a, Direction b)
        {
            if (!Valid)
            {
                return 0f;
            }
            Direction from = a.Normalized, to = b.Normalized;
            double dot = Math.Max(-1d, Math.Min(1d, Direction.Dot(from, to)));
            // acos(dot) loses small angles when the float dot rounds to 1. Use the
            // sine component as Offset does, retaining sub-cell docking distances.
            return (float)(Math.Atan2(Math.Sqrt(Direction.Cross(from, to).LengthSquared), dot) * CellsPerRadian);
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

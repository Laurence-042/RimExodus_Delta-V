using System;
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
    /// sphere↔tile 是纯函数（方向→最近 tile / tile→中心方向），不存在任何需要对齐的
    /// 持久锚——旧实现的 hostMap 锚、floating origin、重锚机制已全部退役。
    ///
    /// 尺度：CellsPerTile 是设计常量（1 tile ≈ 100 宇宙格），配合轨道层的
    /// AverageTileSize 与球面半径换算"格 ↔ 弧度"。无轨道层的存档（非 Odyssey 开局）
    /// 视为退化：Move 原地返回、Offset 归零，上层入口已按 isSpace 门控。
    /// </summary>
    public static class OrbitSphere
    {
        /// <summary>1 个星球 tile ≈ 多少宇宙格（阶段2 设计常量，尺度语义见类注释）。</summary>
        public const float CellsPerTile = 100f;

        // 东向基向量手性：若实测星球背景移动方向与飞行方向相反，翻转此常量
        // （沿用 UniverseLayerMapping 阶段2/3 结论）。
        private const float EastSign = 1f;

        [NonSerialized] private static World cachedWorld;
        [NonSerialized] private static PlanetLayer cachedLayer;
        [NonSerialized] private static float cachedRadius = -1f;
        [NonSerialized] private static float cachedWorldPerCell = -1f;

        /// <summary>轨道层（Def.isSpace 的第一层）。无则 null；按 World 实例缓存。</summary>
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
                cachedRadius = -1f;
                cachedWorldPerCell = -1f;
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

        public static bool Valid => Layer != null && Layer.TilesCount > 0;

        /// <summary>轨道层球面半径（世界单位）。</summary>
        public static float Radius
        {
            get
            {
                if (!Valid) return 1f;
                if (cachedRadius < 0f)
                {
                    cachedRadius = Layer.GetTileCenter(0).magnitude;
                }
                return cachedRadius;
            }
        }

        /// <summary>1 宇宙格对应的世界单位长度。</summary>
        public static float WorldPerCell
        {
            get
            {
                if (!Valid) return 1f;
                if (cachedWorldPerCell < 0f)
                {
                    cachedWorldPerCell = Layer.AverageTileSize / CellsPerTile;
                }
                return cachedWorldPerCell;
            }
        }

        /// <summary>1 弧度弧长对应的宇宙格数（球面上量距的换算系数）。</summary>
        public static float CellsPerRadian => Radius / WorldPerCell;

        /// <summary>读档 / 新世界后清空缓存（与 ShellRenderer 缓存自愈纪律一致）。</summary>
        public static void ClearCache()
        {
            cachedWorld = null;
            cachedLayer = null;
            cachedRadius = -1f;
            cachedWorldPerCell = -1f;
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

        // ---- sphere ⇄ tile（纯函数，无锚）----

        public static Vector3 TileCenterDirection(PlanetTile tile)
        {
            if (!Valid || !tile.Valid)
            {
                return Vector3.right;
            }
            return Layer.GetTileCenter(tile).normalized;
        }

        /// <summary>
        /// 球面方向 → 最近 tile。线性扫描取最大点积；只在降落选图等低频路径调用，
        /// 轨道层 tile 数量级（千）下单次调用耗时可忽略。
        /// </summary>
        public static PlanetTile TileFromDirection(Vector3 dir)
        {
            PlanetLayer layer = Layer;
            if (!Valid)
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
    }
}

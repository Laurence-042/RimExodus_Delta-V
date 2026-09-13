using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Universe Layer 正式映射（阶段2，权威规范见 doc/阶段2-基础驾驶.md）。
    ///
    /// 语义：宇宙系锚定在宿主图 tile——宇宙原点 = 宿主图中心，+X 东 / +Z 北，
    /// 即宿主 tile 切平面上的局部直角系（阶段1 的 HostFrame 参数由此升格为正式记账锚点，
    /// 机制不变）。星球 tile 是飞船口袋图 parent.Tile 的背景锚点。
    ///
    /// 映射方向唯一：universe → tile（AGENTS 不变量 #9，tile 绝不反驱宇宙状态）。
    /// 船图 tile 由该船 universe 位置投影（贪心邻接走向）得出；移动跨过 tile 边界后
    /// 由 UniverseManager 每 60 tick 调 ReanchorShips 惰性重锚，目标 tile 被占用则暂留原 tile。
    /// 映射尺度是显示层常量（CellsPerTile），不是宇宙坐标协议的一部分。
    /// </summary>
    public static class UniverseLayerMapping
    {
        /// <summary>1 个星球 tile ≈ 多少宇宙格。控制背景换 tile 的空间尺度（阶段2 常量）。</summary>
        public const float CellsPerTile = 100f;

        // 东向基向量手性：若实测星球背景移动方向与飞行方向相反，翻转此常量（阶段2 验证项）。
        private const float EastSign = 1f;

        private static readonly List<PlanetTile> NeighborBuffer = new List<PlanetTile>(8);

        /// <summary>
        /// 宇宙位置 → 宿主 tile 切平面偏移后的球面方向（单位向量）。
        /// 偏移尺度：每 CellsPerTile 格 ≈ 1 个 tile ≈ AverageTileSize 个球面长度单位。
        /// </summary>
        public static Vector3 UniverseToSphereDirection(PlanetLayer layer, PlanetTile hostTile, Vector2 universePos)
        {
            Vector3 center = layer.GetTileCenter(hostTile);
            Vector3 up = center.normalized;
            Vector3 northRef = Vector3.up; // layer.NorthPolePos = (0, Radius, 0)
            Vector3 north = northRef - up * Vector3.Dot(northRef, up);
            if (north.sqrMagnitude < 1e-6f)
            {
                // 宿主 tile 恰在极点：东向退化，取任意切向（轨道层几乎不会出现）。
                north = Vector3.forward;
            }
            north.Normalize();
            Vector3 east = Vector3.Cross(up, north) * EastSign;
            Vector3 tangent = east * universePos.x + north * universePos.y;
            return (center + tangent * (layer.AverageTileSize / CellsPerTile)).normalized;
        }

        /// <summary>
        /// 宇宙位置 → 最近 tile。从 start 出发贪心走向目标方向（tile 数有限、步数上限保护）；
        /// start 通常用该船当前 tile，典型只走 0–2 步。
        /// </summary>
        public static PlanetTile ProjectToTile(PlanetLayer layer, PlanetTile hostTile, Vector2 universePos, PlanetTile start)
        {
            Vector3 targetDir = UniverseToSphereDirection(layer, hostTile, universePos);
            PlanetTile current = start;
            float bestDot = Vector3.Dot(layer.GetTileCenter(current).normalized, targetDir);
            for (int step = 0; step < 256; step++)
            {
                NeighborBuffer.Clear();
                layer.GetTileNeighbors(current, NeighborBuffer);
                PlanetTile best = current;
                float bestNeighborDot = bestDot;
                for (int i = 0; i < NeighborBuffer.Count; i++)
                {
                    float dot = Vector3.Dot(layer.GetTileCenter(NeighborBuffer[i]).normalized, targetDir);
                    if (dot > bestNeighborDot + 1e-4f)
                    {
                        bestNeighborDot = dot;
                        best = NeighborBuffer[i];
                    }
                }
                if (best == current)
                {
                    break;
                }
                current = best;
                bestDot = bestNeighborDot;
            }
            return current;
        }

        /// <summary>
        /// 全部飞船的背景 tile 重锚（UniverseManager 每 60 tick 调用）。
        /// 目标 tile 与当前一致、无效或被其他世界对象/地图占用时不动。
        /// WorldObject.Tile setter 自带渲染器/FastTileFinder/PositionChanged 收尾，运行时安全。
        /// </summary>
        public static void ReanchorShips(UniverseManager universe)
        {
            if (universe.hostMap == null)
            {
                return;
            }
            PlanetTile hostTile = universe.hostMap.Tile;
            if (!hostTile.Valid)
            {
                return;
            }
            PlanetLayer layer = hostTile.Layer;
            foreach (ShipState ship in universe.ships)
            {
                if (!(ship.map?.Parent is PocketMapParent parent))
                {
                    continue;
                }
                PlanetTile projected = ProjectToTile(layer, hostTile, ship.pose.position, parent.Tile);
                if (projected == parent.Tile || !projected.Valid)
                {
                    continue;
                }
                if (Find.WorldObjects.AnyWorldObjectAt(projected) || Current.Game.FindMap(projected) != null)
                {
                    continue;
                }
                parent.Tile = projected;
            }
        }

        /// <summary>
        /// 在锚点 tile 附近找最近空闲 tile（无世界对象、无地图）。
        /// ShipTestScene 场景创建用：把"宿主附近随机"改为"投影位置附近最近"。
        /// </summary>
        public static PlanetTile FindFreeTileNear(PlanetLayer layer, PlanetTile anchor)
        {
            if (!Find.WorldObjects.AnyWorldObjectAt(anchor) && Current.Game.FindMap(anchor) == null)
            {
                return anchor;
            }
            PlanetTile best = PlanetTile.Invalid;
            float bestDist = float.MaxValue;
            Vector3 anchorCenter = layer.GetTileCenter(anchor);
            for (int i = 0; i < layer.TilesCount; i++)
            {
                var candidate = new PlanetTile(i, layer);
                if (Find.WorldObjects.AnyWorldObjectAt(candidate) || Current.Game.FindMap(candidate) != null)
                {
                    continue;
                }
                float dist = (layer.GetTileCenter(candidate) - anchorCenter).sqrMagnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best;
        }
    }
}

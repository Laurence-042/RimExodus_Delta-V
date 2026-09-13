using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Universe Layer 正式映射（阶段2 立项，阶段3 扩展动态锚；权威规范见 doc/阶段2/3 专题文档）。
    ///
    /// 语义：宇宙系的记账锚 = 宿主图 tile（宿主图 WorldObject/tile 永不移动）。
    /// 相对偏移 = SpaceObject.pose.position（floating origin 平移后保持有界）；
    /// 绝对偏移 = pose.position + UniverseManager.anchorDisplacement（平移不改变绝对值，
    /// 这是"宿主图位置记录随玩家主船同步运动"的记账：displacement 累计了坐标系整体
    /// 随主船的平移量，宿主 tile 本身不动）。
    ///
    /// 两个换算入口用途不同，不得混用：
    /// - tile 投影（ProjectToTile / ReanchorShips）用**相对**偏移——口袋图挂载 tile
    ///   表示"相对宿主的方向与距离"，长途航行后仍落在宿主附近，不会绕星球出界；
    /// - 背景球面视角（AbsoluteSpherePoint）用**绝对**偏移——星球背景是宇宙绝对
    /// 位置的连续函数，floating origin 平移时刻绝对值不变、背景无跳变。
    ///
    /// 映射方向唯一：universe → tile（AGENTS 不变量 #9，tile 绝不反驱宇宙状态）。
    /// 映射尺度是显示层常量（CellsPerTile），不是宇宙坐标协议的一部分。
    /// </summary>
    public static class UniverseLayerMapping
    {
        /// <summary>1 个星球 tile ≈ 多少宇宙格。控制背景换 tile 的空间尺度（阶段2 常量）。</summary>
        public const float CellsPerTile = 100f;

        // 东向基向量手性：若实测星球背景移动方向与飞行方向相反，翻转此常量（阶段2 验证项，
        // 阶段3 背景相机连续同步沿用同一结论）。
        private const float EastSign = 1f;

        private static readonly System.Collections.Generic.List<PlanetTile> NeighborBuffer =
            new System.Collections.Generic.List<PlanetTile>(8);

        /// <summary>
        /// 绝对偏移 → 星球背景球面点（半径 = tile 中心模长）。背景相机（MapParent.
        /// WorldCameraPosition 覆写）的唯一入口；切平面线性外推后归一化回球面，
        /// 对单轴偏移是精确的大圆参数化，任意偏移量下连续。
        /// 返回 Vector3.zero 表示无宿主锚（调用方回退原版 tile 中心）。
        /// </summary>
        public static Vector3 AbsoluteSpherePoint(UniverseManager universe, Vector2 absoluteOffset)
        {
            if (universe?.hostMap == null || !universe.hostMap.Tile.Valid)
            {
                return Vector3.zero;
            }
            PlanetLayer layer = universe.hostMap.Tile.Layer;
            Vector3 point = centerTangent(layer, universe.hostMap.Tile, absoluteOffset);
            return point.normalized * layer.GetTileCenter(universe.hostMap.Tile).magnitude;
        }

        /// <summary>
        /// 相对宇宙位置 → 宿主 tile 切平面偏移后的球面方向（单位向量）。tile 投影专用。
        /// </summary>
        public static Vector3 UniverseToSphereDirection(PlanetLayer layer, PlanetTile hostTile, Vector2 universePos)
        {
            return centerTangent(layer, hostTile, universePos).normalized;
        }

        // 宿主 tile 中心 + 切平面偏移（east·x + north·y，尺度 = AverageTileSize / CellsPerTile）。
        private static Vector3 centerTangent(PlanetLayer layer, PlanetTile hostTile, Vector2 offset)
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
            Vector3 tangent = east * offset.x + north * offset.y;
            return center + tangent * (layer.AverageTileSize / CellsPerTile);
        }

        /// <summary>
        /// 相对宇宙位置 → 最近 tile。从 start 出发贪心走向目标方向（tile 数有限、步数上限保护）；
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
        /// 全部飞船的背景 tile 重锚（UniverseManager 每 60 tick 调用）。POI 静止，不参与重锚。
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
            foreach (SpaceObject obj in universe.objects)
            {
                if (!(obj is ShipState ship) || ship.docked || ship.docking || !(ship.map?.Parent is PocketMapParent parent))
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

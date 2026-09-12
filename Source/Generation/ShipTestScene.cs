using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段1 双船测试场景：轨道层真实 tile 上的宿主图 + 两张 DeltaV_ShipPocket 口袋地图
    /// （sourceMap=宿主图）+ 初始化宇宙状态 + 聚焦玩家船。
    /// 经 DebugActions 以 LongEventHandler 调用（同步生成，与原版 gravship 到达一致）。
    /// 幂等：重复创建时先销毁旧场景。
    /// </summary>
    public static class ShipTestScene
    {
        public const int MapSize = 150;
        public const int HostMapSize = 150;

        // 敌舰初始偏移 60/25：换算到聚焦玩家帧的地图坐标约 (135, 100)，在 150 地图视野内
        // （CameraDriver 会把镜头 clamp 在当前地图内，超出地图矩形的代理不可见——
        // 宿主图就是为此准备的"空间画面"，聚焦宿主图可同时看到两艘船的代理）。
        public static readonly Vector2 EnemyInitialOffset = new Vector2(60f, 25f);
        public const float EnemyInitialAngleDeg = 35f;

        public static void Create()
        {
            var universe = Current.Game.GetComponent<UniverseManager>();

            foreach (ShipState ship in universe.ships.ToList())
            {
                if (ship.map != null)
                {
                    PocketMapUtility.DestroyPocketMap(ship.map);
                }
            }
            universe.ships.Clear();
            DestroyHostMap(universe);

            Map hostMap = CreateHostMap();
            if (hostMap == null)
            {
                Log.Error("[DeltaV] Test scene aborted: no space host map.");
                return;
            }
            universe.hostMap = hostMap;

            Map playerMap = GenerateShipPocketMap(new IntVec3(MapSize, 1, MapSize), hostMap);
            if (playerMap == null) return;
            ShipStructureStamp.StampPlayerShip(playerMap);

            Map enemyMap = GenerateShipPocketMap(new IntVec3(MapSize, 1, MapSize), hostMap);
            if (enemyMap == null) return;
            ShipStructureStamp.StampEnemyShip(enemyMap);

            var playerShip = new ShipState(1, "Player ship", playerMap, new SpacePose(Vector2.zero, 0f));
            var enemyShip = new ShipState(2, "Enemy ship", enemyMap,
                new SpacePose(EnemyInitialOffset, EnemyInitialAngleDeg * Mathf.Deg2Rad));

            universe.ResetShips(new List<ShipState> { playerShip, enemyShip }, playerShip.shipId);
            universe.SetFocused(playerShip);
            CameraJumper.TryJump(playerMap.Center, playerMap, CameraJumper.MovementMode.Cut);
        }

        /// <summary>
        /// 宿主图：星球外层空间（轨道层）真实 PlanetTile 上的普通地图。
        /// 真实 tile 让 MapParent.Tile 有效（renderWorld 星球背景合法）、
        /// storyteller/财富/任务逻辑有原生锚点（story：口袋地图需要宿主）。
        /// </summary>
        private static Map CreateHostMap()
        {
            PlanetLayer orbitLayer = Find.WorldGrid.PlanetLayers.Values
                .FirstOrDefault(layer => layer.Def == PlanetLayerDefOf.Orbit);
            if (orbitLayer == null)
            {
                Log.Error("[DeltaV] This game has no orbit layer (Odyssey start scenarios provide one); cannot create space host map.");
                return null;
            }
            PlanetTile tile = TileFinder.RandomSettlementTileFor(orbitLayer, null, false,
                t => !Find.WorldObjects.AnyWorldObjectAt(t));
            if (!tile.Valid)
            {
                Log.Error("[DeltaV] Failed to find a free tile on the orbit layer.");
                return null;
            }
            return GetOrGenerateMapUtility.GetOrGenerateMap(
                tile, new IntVec3(HostMapSize, 1, HostMapSize), DeltaVDefOf.DeltaV_SpaceHostParent);
        }

        private static void DestroyHostMap(UniverseManager universe)
        {
            if (universe.hostMap == null || !Find.Maps.Contains(universe.hostMap))
            {
                universe.hostMap = null;
                return;
            }
            MapParent parent = universe.hostMap.Parent;
            // DeinitAndRemoveMap 不销毁 WorldObject（RimExodus RemoveTileMap 同款结论），需显式 Destroy。
            Current.Game.DeinitAndRemoveMap(universe.hostMap, false);
            parent?.Destroy();
            universe.hostMap = null;
        }

        /// <summary>
        /// PocketMapUtility.GeneratePocketMap 的本地复制，两点区别：
        /// 1. WorldObject 用 DeltaV_ShipMapParent def（便于按 def 识别 Delta-V 飞船图）；
        /// 2. parent.Tile 设为宿主图附近的真实轨道层空 tile——MapInfo.Tile 由 parent 派生，
        ///    Map.Tile 因此有效，renderWorld=true 的星球背景与轨道层视差参数全部走原版正路，
        ///    无需任何 override。tile 不得与其他地图/世界对象重复（Game.FindMap 按 map.Tile 匹配）。
        /// </summary>
        private static Map GenerateShipPocketMap(IntVec3 size, Map sourceMap)
        {
            PlanetTile shipTile = FindFreeOrbitTileNear(sourceMap.Tile);
            if (!shipTile.Valid)
            {
                Log.Error("[DeltaV] No free orbit tile near the host map; aborting scene.");
                return null;
            }
            var parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.Tile = shipTile;
            parent.sourceMap = sourceMap;
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            Map map = MapGenerator.GenerateMap(size, parent, DeltaVDefOf.DeltaV_ShipPocket, null, null, isPocketMap: true);
            Find.World.pocketMaps.Add(parent);
            return map;
        }

        /// <summary>
        /// 找离锚点最近的空闲轨道层 tile（跳过已有世界对象或已有地图的 tile）。
        /// 飞船锚在宿主附近 → 各船透出的星球背景与宿主图几乎一致。
        /// </summary>
        private static PlanetTile FindFreeOrbitTileNear(PlanetTile anchor)
        {
            PlanetLayer layer = anchor.Layer;
            Vector3 anchorCenter = layer.GetTileCenter(anchor);
            PlanetTile best = PlanetTile.Invalid;
            float bestDist = float.MaxValue;
            for (int i = 0; i < layer.TilesCount; i++)
            {
                PlanetTile candidate = new PlanetTile(i, layer);
                if (Find.WorldObjects.AnyWorldObjectAt(candidate) || Current.Game.FindMap(candidate) != null)
                {
                    continue;
                }
                float dist = (anchorCenter - layer.GetTileCenter(candidate)).sqrMagnitude;
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

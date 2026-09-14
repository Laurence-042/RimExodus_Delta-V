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

            // 幂等：销毁旧场景的全部对象图与宿主图（含 Packed POI 的轻量状态一并清除）。
            foreach (SpaceObject obj in universe.objects.ToList())
            {
                if (obj.map != null)
                {
                    PocketMapUtility.DestroyPocketMap(obj.map);
                }
            }
            universe.objects.Clear();
            // 孤儿口袋图清理：旧版本存档迁移等情况会留下 sourceMap=宿主图、但已无宇宙状态
            // 引用的口袋图（Map.ChildPocketMaps 遍历 pocketMaps 按 sourceMap 匹配）。
            if (universe.hostMap != null)
            {
                foreach (Map orphan in universe.hostMap.ChildPocketMaps.ToList())
                {
                    PocketMapUtility.DestroyPocketMap(orphan);
                }
            }
            DestroyHostMap(universe);

            Map hostMap = CreateHostMap();
            if (hostMap == null)
            {
                Log.Error("[DeltaV] Test scene aborted: no space host map.");
                return;
            }
            universe.hostMap = hostMap;

            Map playerMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), hostMap, Vector2.zero);
            if (playerMap == null) return;
            ShipStructureStamp.StampPlayerShip(playerMap);

            Map enemyMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), hostMap, EnemyInitialOffset);
            if (enemyMap == null) return;
            ShipStructureStamp.StampEnemyShip(enemyMap);

            var playerShip = new ShipState(1, "Player ship", playerMap, new SpacePose(Vector2.zero, 0f), isPlayerShip: true);
            var enemyShip = new ShipState(2, "Enemy ship", enemyMap,
                new SpacePose(EnemyInitialOffset, EnemyInitialAngleDeg * Mathf.Deg2Rad));

            universe.ResetObjects(new List<SpaceObject> { playerShip, enemyShip }, playerShip.objectId);
            CompDeltaVPilotConsole.EnsurePrimaryConsole(playerShip);
            universe.primaryShipId = playerShip.objectId; // 动态锚跟随玩家船（阶段3）
            universe.anchorDisplacement = Vector2.zero;
            universe.SetFocused(playerShip);
            CameraJumper.TryJump(playerMap.Center, playerMap, CameraJumper.MovementMode.Cut);
        }

        /// <summary>
        /// 宿主图：星球外层空间（轨道层）真实 PlanetTile 上的普通地图，也是 Universe Layer
        /// 的记账锚点（宇宙原点 = 宿主图中心，UniverseLayerMapping）。
        /// 真实 tile 让 MapParent.Tile 有效（renderWorld 星球背景合法）、
        /// storyteller/财富/任务逻辑有原生锚点（story：口袋地图需要宿主）。
        /// 尺寸来自 Mod 设置（显示/性能参数，不是宇宙坐标协议）。
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
            int hostSize = Mathf.Clamp(DeltaVMod.Settings?.hostMapSize ?? HostMapSize, 100, 1000);
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(
                tile, new IntVec3(hostSize, 1, hostSize), DeltaVDefOf.DeltaV_SpaceHostParent);
            // 原版 Gravship 选点要求船体至少与一格非 Space 地形连接。宿主图中心这一格是
            // 固定轨道锚，不另造选点/合法性逻辑；其余格仍由原版判断能否铺 Substructure。
            map.terrainGrid.SetTerrain(map.Center, DeltaVDefOf.Substructure);
            return map;
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

    }
}

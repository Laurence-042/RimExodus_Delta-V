using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 阶段1 双船测试场景：两张无 tile 口袋图（sourceMap=专用宿主，由
    /// ShipPocketMapFactory 惰性创建）+ 球面宇宙状态 + 聚焦玩家船。
    /// 经 DebugActions 以 LongEventHandler 调用（同步生成，与原版 gravship 到达一致）。
    /// 幂等：重复创建时先销毁旧场景。
    /// </summary>
    public static class ShipTestScene
    {
        public const int MapSize = 150;

        // 敌舰初始切空间偏移 60/25（东/北）：换算到聚焦玩家帧的地图坐标约 (135, 100)，
        // 在 150 地图视野内（CameraDriver 会把镜头 clamp 在当前地图内）。
        public static readonly Vector2 EnemyInitialOffset = new Vector2(60f, 25f);
        public const float EnemyInitialAngleDeg = 35f;

        public static void Create()
        {
            var universe = Current.Game.GetComponent<UniverseManager>();

            // 幂等：销毁旧场景的全部对象图（含 Packed POI 的轻量状态一并清除）。
            foreach (SpaceObject obj in universe.objects.ToList())
            {
                if (obj.map != null)
                {
                    PocketMapUtility.DestroyPocketMap(obj.map);
                }
            }
            universe.objects.Clear();
            // 孤儿口袋图清理：已无宇宙状态引用、但 sourceMap 仍是宿主的口袋图。
            if (universe.UniverseHostMap != null)
            {
                foreach (Map orphan in universe.UniverseHostMap.ChildPocketMaps.ToList())
                {
                    PocketMapUtility.DestroyPocketMap(orphan);
                }
            }

            // 玩家船锚定在一个空闲轨道 tile 的中心方向（仅为背景视角选个合理位置）。
            PlanetLayer orbitLayer = Find.WorldGrid.PlanetLayers.Values
                .FirstOrDefault(layer => layer.Def == PlanetLayerDefOf.Orbit);
            if (orbitLayer == null)
            {
                Log.Error("[DeltaV] This game has no orbit layer (Odyssey start scenarios provide one); cannot create test scene.");
                return;
            }
            PlanetTile anchorTile = TileFinder.RandomSettlementTileFor(orbitLayer, null, false,
                t => !Find.WorldObjects.AnyWorldObjectAt(t));
            if (!anchorTile.Valid)
            {
                Log.Error("[DeltaV] Failed to find a free tile on the orbit layer.");
                return;
            }
            Vector3 playerDir = OrbitSphere.TileCenterDirection(anchorTile);

            Map playerMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), universe);
            if (playerMap == null) return;
            ShipStructureStamp.StampPlayerShip(playerMap);

            Map enemyMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), universe);
            if (enemyMap == null) return;
            ShipStructureStamp.StampEnemyShip(enemyMap);

            var playerShip = new ShipState(1, "Player ship", playerMap, new SpacePose(playerDir, 0f), isPlayerShip: true)
            {
                faction = Faction.OfPlayer
            };
            var enemyShip = new ShipState(2, "Enemy ship", enemyMap,
                new SpacePose(OrbitSphere.Move(playerDir, EnemyInitialOffset), EnemyInitialAngleDeg * Mathf.Deg2Rad));

            universe.ResetObjects(new List<SpaceObject> { playerShip, enemyShip }, playerShip.objectId);
            CompDeltaVPilotConsole.EnsurePrimaryConsole(playerShip);
            WorldShipMarkerRenderer.RebuildFor(playerShip);
            universe.primaryShipId = playerShip.objectId;
            universe.SetFocused(playerShip);
            CameraJumper.TryJump(playerMap.Center, playerMap, CameraJumper.MovementMode.Cut);
        }
    }
}

using System;
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
    /// 布局数据源：先试 prefab 导入接口（HomePrefab.SpawnInto）；接口未实现、prefab
    /// 缺失等一切失败以异常表达，捕获后回退程序生成布局（ShipProceduralLayout）。
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
                    universe.Lifecycle.DestroyPocketMap(obj.map);
                }
                universe.UnregisterObject(obj);
            }
            // 孤儿口袋图清理：已无宇宙状态引用、但 sourceMap 仍是宿主的口袋图。
            if (universe.UniverseHostMap != null)
            {
                foreach (Map orphan in universe.UniverseHostMap.ChildPocketMaps.ToList())
                {
                    universe.Lifecycle.DestroyPocketMap(orphan);
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

            // 容器先注册、结构后铺设：驾驶台 spawn 认领要求 FrameOf 已解析（ClaimOnSpawn
            // 守卫）。归属 ID 与容器对象 ID 解耦（各经 AllocateOwnerId 独立发放）。
            int playerOwnerId = universe.AllocateOwnerId();
            Map playerMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), universe);
            if (playerMap == null) return;
            var playerContainer = new SpaceObject(universe.AllocateOwnerId(), playerMap,
                new SpacePose(playerDir, 0f));
            universe.RegisterObject(playerContainer);
            universe.NoteStructureName(playerOwnerId, "Player ship");
            SpawnShipLayout(playerMap, Faction.OfPlayer,
                () => ShipProceduralLayout.GeneratePlayerShip(playerMap, playerOwnerId));

            int enemyOwnerId = universe.AllocateOwnerId();
            Map enemyMap = ShipPocketMapFactory.Generate(new IntVec3(MapSize, 1, MapSize), universe);
            if (enemyMap == null) return;
            Faction enemyFaction = Faction.OfPirates
                ?? Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => f != null && f.HostileTo(Faction.OfPlayer));
            var enemyContainer = new SpaceObject(universe.AllocateOwnerId(), enemyMap,
                new SpacePose(OrbitSphere.Move(playerDir, EnemyInitialOffset), EnemyInitialAngleDeg * Mathf.Deg2Rad));
            universe.RegisterObject(enemyContainer);
            universe.NoteStructureName(enemyOwnerId, "Enemy ship");
            SpawnShipLayout(enemyMap, enemyFaction,
                () => ShipProceduralLayout.GenerateEnemyShip(enemyMap, enemyOwnerId, enemyFaction));

            universe.ResetObjects(new List<SpaceObject> { playerContainer, enemyContainer });
            Ship playerShip = ShipRegistry.ShipByOwner(playerOwnerId);
            if (playerShip.IsValid)
            {
                WorldShipMarkerRenderer.RebuildFor(playerShip);
            }
            universe.SetFocused(playerContainer);
            CameraJumper.TryJump(playerMap.Center, playerMap, CameraJumper.MovementMode.Cut);
        }

        /// <summary>布局数据源统一口径：先试 prefab 导入接口，抛异常（未实现/无 prefab）
        /// 即回退程序生成布局。</summary>
        private static void SpawnShipLayout(Map map, Faction faction, Action fallbackLayout)
        {
            try
            {
                HomePrefab.SpawnInto(faction, map, map.Center);
            }
            catch (Exception ex)
            {
                Log.Warning($"[DeltaV] HomePrefab import unavailable ({ex.GetType().Name}: {ex.Message})"
                    + " - falling back to procedural layout.");
                fallbackLayout();
            }
        }
    }
}

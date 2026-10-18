using System;
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
    /// 经 UniverseManager 排空旧场景帧动作并销图，再以 LongEventHandler 同步生成。
    /// 重建保持暂停；原版初始化回调完成后才交接聚焦。
    /// 两舰分别读取设置中的 prefab；自动选择时加载最新导出，加载失败回退程序布局。
    /// 指定文件加载失败在重建前中止；落位失败销毁失败测试图，不叠加另一套布局。
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
            if (universe.IsResettingScene) return;
            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            if (!TryLoadLayout(settings?.testScenePlayerPrefab, out MapPrefabDocument playerPrefab)
                || !TryLoadLayout(settings?.testSceneEnemyPrefab, out MapPrefabDocument enemyPrefab)) return;
            universe.BeginSceneReset(() => Generate(universe, playerPrefab, enemyPrefab));
        }

        private static void Generate(UniverseManager universe, MapPrefabDocument playerPrefab,
            MapPrefabDocument enemyPrefab)
        {
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
            Map playerMap = ShipPocketMapFactory.Generate(LayoutMapSize(playerPrefab), universe);
            if (playerMap == null) return;
            var playerContainer = new MapSpaceObject(universe.AllocateOwnerId(), playerMap,
                new SpacePose(playerDir, 0f));
            universe.RegisterObject(playerContainer);
            universe.NoteStructureName(playerOwnerId, "Player ship");
            if (!SpawnShipLayout(playerPrefab, playerMap, Faction.OfPlayer, playerOwnerId, universe,
                () => ShipProceduralLayout.GeneratePlayerShip(playerMap, playerOwnerId)))
            {
                QueueFailedSceneCleanup(universe, playerContainer);
                return;
            }

            int enemyOwnerId = universe.AllocateOwnerId();
            Map enemyMap = ShipPocketMapFactory.Generate(LayoutMapSize(enemyPrefab), universe);
            if (enemyMap == null)
            {
                QueueFailedSceneCleanup(universe, playerContainer);
                return;
            }
            Faction enemyFaction = Faction.OfPirates
                ?? Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => f != null && f.HostileTo(Faction.OfPlayer));
            var enemyContainer = new MapSpaceObject(universe.AllocateOwnerId(), enemyMap,
                new SpacePose(OrbitSphere.Move(playerDir, EnemyInitialOffset), EnemyInitialAngleDeg * Mathf.Deg2Rad));
            universe.RegisterObject(enemyContainer);
            universe.NoteStructureName(enemyOwnerId, "Enemy ship");
            if (!SpawnShipLayout(enemyPrefab, enemyMap, enemyFaction, enemyOwnerId, universe,
                () => ShipProceduralLayout.GenerateEnemyShip(enemyMap, enemyOwnerId, enemyFaction)))
            {
                QueueFailedSceneCleanup(universe, enemyContainer, playerContainer);
                return;
            }

            universe.QueueFrameAction(() =>
            {
                universe.Weapons.RebuildWeaponIndex();
                universe.Controls.Rebuild();
                Ship playerShip = ShipRegistry.ShipByOwner(playerOwnerId);
                if (playerShip.IsValid) WorldShipMarkerRenderer.RebuildFor(playerShip);
                universe.SetFocused(playerContainer);
                CameraJumper.TryJump(playerMap.Center, playerMap, CameraJumper.MovementMode.Cut);
            });
        }

        private static void QueueFailedSceneCleanup(UniverseManager universe, params SpaceObject[] containers)
        {
            // Map.FinalizeInit queues native power/mesh initialization after the
            // generation long event. Destroying here would dispose an uninitialized
            // MapDrawer, then let those queued callbacks render disposed grids.
            universe.QueueFrameAction(delegate
            {
                foreach (SpaceObject container in containers)
                {
                    universe.Lifecycle.DestroyPocketMap(container.map);
                    universe.UnregisterObject(container);
                }
            });
        }

        private static IntVec3 LayoutMapSize(MapPrefabDocument prefab)
        {
            return new IntVec3(Mathf.Max(MapSize, (prefab?.size.x ?? 0) + 10), 1,
                Mathf.Max(MapSize, (prefab?.size.z ?? 0) + 10));
        }

        // Resolve before BeginSceneReset so a missing configured file cannot erase the old scene.
        private static bool TryLoadLayout(string path, out MapPrefabDocument prefab)
        {
            prefab = null;
            try
            {
                prefab = string.IsNullOrEmpty(path) ? MapPrefab.LoadLatest() : MapPrefab.LoadRelative(path);
                return true;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    Log.Error($"[DeltaV] Test scene prefab '{path}' could not be loaded: {ex}");
                    Messages.Message("DeltaV_TestScenePrefabFailed".Translate(path),
                        MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                Log.Warning($"[DeltaV] MapPrefab import unavailable ({ex.GetType().Name}: {ex.Message})"
                    + " - falling back to procedural layout.");
                return true;
            }
        }

        private static bool SpawnShipLayout(MapPrefabDocument prefab, Map map, Faction faction,
            int ownerId, UniverseManager universe, Action fallbackLayout)
        {
            if (prefab == null)
            {
                fallbackLayout();
                return true;
            }
            try
            {
                MapPrefab.Spawn(prefab, map, map.Center, faction, ownerId, Rot4.North, universe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] MapPrefab '{prefab.name}' placement failed on "
                    + $"{UniverseManager.MapTag(map)}; canceling test scene: {ex}");
                return false;
            }
        }
    }
}

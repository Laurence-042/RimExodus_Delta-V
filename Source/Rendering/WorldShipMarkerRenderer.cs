using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 星球视图中的玩家飞船剪影标记。位置/姿态只读取 SpacePose；剪影格子取引擎的
    /// 相连基架连通域（与转换捕获同口径，超力场扩建与停靠时相连的结构都计入），
    /// 纹理经 ShipSilhouette 唯一入口生成并缓存。缓存可随时销毁重建，既不序列化，
    /// 也不创建占用轨道 tile 的 WorldObject。
    /// </summary>
    public static class WorldShipMarkerRenderer
    {
        private const float MarkerSizeInTiles = 1.25f;
        private const float AltitudeOffset = 0.055f;

        private sealed class MarkerCache
        {
            public Texture2D texture;
            public Material material;
            /// <summary>纹理源 = 停靠图（形状嵌入宿主图坐标系）而非口袋图规范化形状。</summary>
            public bool sourceFromDockedMap;
            public bool initialized;
        }

        private static readonly Dictionary<ShipState, MarkerCache> Cache =
            new Dictionary<ShipState, MarkerCache>();

        private static Game cachedGame;

        public static void DrawPlayerShips(UniverseManager universe)
        {
            if (universe == null || !WorldRendererUtility.WorldSelected
                || !DebugViewSettings.drawWorldObjects || !OrbitSphere.Valid)
            {
                return;
            }

            PlanetLayer layer = OrbitSphere.Layer;
            if (PlanetLayer.Selected != layer)
            {
                return;
            }

            EnsureCurrentGame();
            foreach (ShipState ship in universe.Ships)
            {
                if (!ship.isPlayerShip || ship.docking)
                {
                    continue;
                }

                MarkerCache marker = MarkerFor(ship);
                if (marker?.material == null)
                {
                    continue;
                }

                Vector3 radial = ship.pose.position.normalized;
                Vector3 drawPos = layer.Origin + OrbitSphere.SpherePoint(radial)
                    + radial * AltitudeOffset;
                // 朝向配对（ShipSilhouette 类注释的三自由度规则）：口袋图快照是规范化
                // 形状（+x = 船头）→ 按船姿态角追踪，停靠后继续有效；读档时从停靠图
                // 惰性补建的快照嵌入宿主图坐标系（含落点朝向）→ 按宿主帧角旋转，不得
                // 叠加船姿态角（旧实现取 0，宿主角非 0 时偏差 = 宿主角，2026-09-18 修正）。
                float angle;
                if (marker.sourceFromDockedMap)
                {
                    SpaceObject host = universe.SpaceObjectOnMap(ship.dockedMap);
                    angle = host != null ? host.pose.angle : ship.pose.angle;
                }
                else
                {
                    angle = ship.pose.angle;
                }
                Quaternion rotation = SpaceTransform.UniverseWorldMarkerRotation(radial, angle);
                float size = layer.AverageTileSize * MarkerSizeInTiles;
                Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rotation,
                    new Vector3(size, 1f, size));
                Graphics.DrawMesh(MeshPool.plane10, matrix, marker.material,
                    WorldCameraManager.WorldLayer);
            }
        }

        /// <summary>
        /// 剪影的屏幕位置与命中半径（聚焦点击用）。剪影不是 WorldObject，刻意不走
        /// RimExodus「useDynamicDrawer 真对象」路线（不进原版世界选择/检查面板），
        /// 手动做原版 GenWorldUI 同款换算：WorldToScreenPoint / Prefs.UIScale + y 翻转，
        /// 遮挡用地平线剔除（WorldRendererUtility.HiddenBehindTerrainNow）。
        /// </summary>
        public static bool TryGetScreenPosition(ShipState ship, out Vector2 screenPos, out float hitRadiusPx)
        {
            screenPos = Vector2.zero;
            hitRadiusPx = 0f;
            PlanetLayer layer = OrbitSphere.Layer;
            if (ship == null || ship.docking || layer == null || PlanetLayer.Selected != layer)
            {
                return false;
            }
            Vector3 radial = ship.pose.position.normalized;
            Vector3 world = layer.Origin + OrbitSphere.SpherePoint(radial) + radial * AltitudeOffset;
            if (WorldRendererUtility.HiddenBehindTerrainNow(world))
            {
                return false;
            }
            Camera camera = Find.WorldCamera;
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                return false; // 相机背面
            }
            screenPos = new Vector2(screen.x / Prefs.UIScale, UI.screenHeight - screen.y / Prefs.UIScale);
            // 半径 = 剪影半尺寸（沿相机 up 方向）投影像素差 + 余量。
            Vector3 edge = camera.WorldToScreenPoint(world + camera.transform.up
                * (layer.AverageTileSize * MarkerSizeInTiles * 0.5f));
            hitRadiusPx = Mathf.Abs(UI.screenHeight - edge.y / Prefs.UIScale - screenPos.y) + 4f;
            return true;
        }

        private static MarkerCache MarkerFor(ShipState ship)
        {
            if (Cache.TryGetValue(ship, out MarkerCache marker) && marker.initialized)
            {
                return marker;
            }

            // 读档后运行时缓存为空，而船可能已经处于移动/固定模式；第一次显示时
            // 允许惰性补建一次。正常游戏流程则由移动模式入口主动调用 RebuildFor。
            RebuildFor(ship);
            Cache.TryGetValue(ship, out marker);
            return marker;
        }

        /// <summary>
        /// 移动模式入口调用：以进入瞬间的完整船体重建一次剪影。此后无论建造或
        /// 战斗如何改变结构，标记都继续沿用该快照，直到下一次进入移动模式。
        /// </summary>
        public static void RebuildFor(ShipState ship)
        {
            if (ship == null)
            {
                return;
            }
            EnsureCurrentGame();

            if (!Cache.TryGetValue(ship, out MarkerCache marker))
            {
                marker = new MarkerCache();
                Cache.Add(ship, marker);
            }
            Release(marker);
            marker.initialized = true;

            Map map = ship.InteriorMap;
            if (map == null || map.Disposed)
            {
                return;
            }

            Building_GravEngine engine = ShipStructureResolver.EngineOf(ship);
            HashSet<IntVec3> liveCells = engine?.AllConnectedSubstructureNoRegen;
            if (liveCells == null || liveCells.Count == 0)
            {
                return;
            }

            marker.texture = ShipSilhouette.BuildTexture(liveCells,
                $"DeltaV_PlayerShipSilhouette_{ship.objectId}");
            if (marker.texture == null)
            {
                return;
            }
            marker.material = new Material(ShaderDatabase.WorldOverlayTransparentLit)
            {
                name = $"DeltaV_PlayerShipMarker_{ship.objectId}",
                mainTexture = marker.texture,
                color = Faction.OfPlayer?.Color ?? Color.white,
                renderQueue = 3550
            };
            marker.sourceFromDockedMap = ship.docked;
        }

        private static void EnsureCurrentGame()
        {
            if (ReferenceEquals(cachedGame, Current.Game))
            {
                return;
            }
            ClearRuntimeCaches();
            cachedGame = Current.Game;
        }

        public static void ClearRuntimeCaches()
        {
            foreach (MarkerCache marker in Cache.Values)
            {
                Release(marker);
            }
            Cache.Clear();
            cachedGame = null;
        }

        private static void Release(MarkerCache marker)
        {
            if (marker.material != null)
            {
                Object.Destroy(marker.material);
                marker.material = null;
            }
            if (marker.texture != null)
            {
                Object.Destroy(marker.texture);
                marker.texture = null;
            }
        }
    }
}

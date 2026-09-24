using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 星球视图中的玩家飞船剪影标记（解耦重构：按船 = 结构归属渲染）。位置/姿态只读
    /// 派生查询（ShipRegistry.ShipPose——容器位姿 ⊕ 船体锚）；剪影格子取船的控制域
    /// （与捕获同口径），纹理经 ShipSilhouette 唯一入口生成并缓存。缓存可随时销毁重建，
    /// 既不序列化，也不创建占用轨道 tile 的 WorldObject。飞船级剪影用于识别成员，
    /// 不伪装成独立运动对象（§10）。
    /// </summary>
    public static class WorldShipMarkerRenderer
    {
        private const float MarkerSizeInTiles = 1.25f;
        private const float AltitudeOffset = 0.055f;

        private sealed class MarkerCache
        {
            public Texture2D texture;
            public Material material;
            public bool initialized;
        }

        private static readonly Dictionary<int, MarkerCache> Cache =
            new Dictionary<int, MarkerCache>();

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
            foreach (Ship ship in universe.PlayerShips())
            {
                if (universe.ShipTransitions.IsDockingFlowShip(ship))
                {
                    continue;
                }

                MarkerCache marker = MarkerFor(ship);
                if (marker?.material == null)
                {
                    continue;
                }

                SpacePose pose = ShipRegistry.ShipPose(ship);
                Vector3 radial = pose.position.normalized;
                Vector3 drawPos = layer.Origin + OrbitSphere.SpherePoint(radial)
                    + radial * AltitudeOffset;
                // 朝向配对（ShipSilhouette 类注释的三自由度规则）：剪影纹理在船所在
                // 图的图帧内扫描 → 按该图容器角旋转（口袋图与停靠宿主图同一规则——
                // 多船图上各船同容器角、位置按各自船体锚）。
                SpaceObject frame = universe.FrameOf(ship.map);
                float angle = frame != null ? frame.pose.angle : pose.angle;
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
        public static bool TryGetScreenPosition(Ship ship, out Vector2 screenPos, out float hitRadiusPx)
        {
            screenPos = Vector2.zero;
            hitRadiusPx = 0f;
            PlanetLayer layer = OrbitSphere.Layer;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (!ship.IsValid || universe?.ShipTransitions.IsDockingFlowShip(ship) == true
                || layer == null || PlanetLayer.Selected != layer)
            {
                return false;
            }
            Vector3 radial = ShipRegistry.ShipPose(ship).position.normalized;
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

        private static MarkerCache MarkerFor(Ship ship)
        {
            if (Cache.TryGetValue(ship.ownerId, out MarkerCache marker) && marker.initialized)
            {
                return marker;
            }

            // 读档后运行时缓存为空，第一次显示时允许惰性补建一次。
            RebuildFor(ship);
            Cache.TryGetValue(ship.ownerId, out marker);
            return marker;
        }

        /// <summary>
        /// 自由飞行入口调用：以脱离停靠瞬间的完整船体（控制域）重建一次剪影。
        /// </summary>
        public static void RebuildFor(Ship ship)
        {
            if (!ship.IsValid)
            {
                return;
            }
            EnsureCurrentGame();

            if (!Cache.TryGetValue(ship.ownerId, out MarkerCache marker))
            {
                marker = new MarkerCache();
                Cache.Add(ship.ownerId, marker);
            }
            Release(marker);
            marker.initialized = true;

            Map map = ship.map;
            if (map == null || map.Disposed)
            {
                return;
            }

            HashSet<IntVec3> liveCells = ShipRegistry.ControlDomain(ship);
            if (liveCells == null || liveCells.Count == 0)
            {
                return;
            }

            marker.texture = ShipSilhouette.BuildTexture(liveCells,
                $"DeltaV_PlayerShipSilhouette_{ship.ownerId}");
            if (marker.texture == null)
            {
                return;
            }
            marker.material = new Material(ShaderDatabase.WorldOverlayTransparentLit)
            {
                name = $"DeltaV_PlayerShipMarker_{ship.ownerId}",
                mainTexture = marker.texture,
                color = Faction.OfPlayer?.Color ?? Color.white,
                renderQueue = 3550
            };
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

using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 星球视图中的玩家飞船剪影标记。位置/姿态只读取 SpacePose；剪影读取原版
    /// GravEngine.ValidSubstructure，并缓存成透明纹理。缓存可随时销毁重建，既不
    /// 序列化，也不创建占用轨道 tile 的 WorldObject。
    /// </summary>
    public static class WorldShipMarkerRenderer
    {
        private const int TexturePadding = 1;
        private const float MarkerSizeInTiles = 1.25f;
        private const float AltitudeOffset = 0.055f;

        private sealed class MarkerCache
        {
            public Texture2D texture;
            public Material material;
            public bool sourceAlreadyWorldAligned;
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
                // 正常缓存来自移动模式 identity 图，因此固定后仍用 pose 追踪姿态；只有
                // 读档时从固定图惰性补建的源格已按 Rot4 旋转，不能再重复叠加角度。
                float angle = marker.sourceAlreadyWorldAligned ? 0f : ship.pose.angle;
                Quaternion rotation = SpaceTransform.UniverseWorldMarkerRotation(radial, angle);
                float size = layer.AverageTileSize * MarkerSizeInTiles;
                Matrix4x4 matrix = Matrix4x4.TRS(drawPos, rotation,
                    new Vector3(size, 1f, size));
                Graphics.DrawMesh(MeshPool.plane10, matrix, marker.material,
                    WorldCameraManager.WorldLayer);
            }
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

            Map map = ship.docked ? ship.dockedMap : ship.map;
            if (map == null || map.Disposed)
            {
                return;
            }

            Building_GravEngine engine = DockController.FindEngineForShip(map, ship.primaryConsole);
            HashSet<IntVec3> liveCells = engine?.ValidSubstructureNoRegen;
            if (liveCells == null || liveCells.Count == 0)
            {
                return;
            }

            Rebuild(marker, liveCells, ship.objectId);
            marker.sourceAlreadyWorldAligned = ship.docked;
        }

        private static void Rebuild(MarkerCache marker, HashSet<IntVec3> liveCells, int shipId)
        {
            Release(marker);

            CellRect bounds = CellRect.FromCellList(liveCells);
            int side = Mathf.Max(bounds.Width, bounds.Height) + TexturePadding * 2;
            var pixels = new Color32[side * side];
            int offsetX = (side - bounds.Width) / 2;
            int offsetY = (side - bounds.Height) / 2;
            Color32 opaque = new Color32(255, 255, 255, 255);
            foreach (IntVec3 cell in liveCells)
            {
                int x = cell.x - bounds.minX + offsetX;
                int y = cell.z - bounds.minZ + offsetY;
                pixels[x + y * side] = opaque;
            }

            marker.texture = new Texture2D(side, side, TextureFormat.RGBA32, false)
            {
                name = $"DeltaV_PlayerShipSilhouette_{shipId}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            marker.texture.SetPixels32(pixels);
            marker.texture.Apply(false, true);

            marker.material = new Material(ShaderDatabase.WorldOverlayTransparentLit)
            {
                name = $"DeltaV_PlayerShipMarker_{shipId}",
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

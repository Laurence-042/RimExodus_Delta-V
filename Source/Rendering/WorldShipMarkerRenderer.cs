using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 星球视图中的玩家飞船剪影标记（解耦重构：按共同船体身份渲染）。位置/姿态只读
    /// 派生查询（ShipRegistry.ShipPose——容器位姿 ⊕ 船体锚）；剪影格子取 ShipBase.HullCells
    /// （与捕获同口径），纹理经 ShipSilhouette 唯一入口生成并缓存。缓存可随时销毁重建，
    /// 既不序列化，也不创建占用轨道 tile 的 WorldObject。飞船级剪影用于识别成员，
    /// 不伪装成独立运动对象（§10）。
    /// </summary>
    public static class WorldShipMarkerRenderer
    {
        private const float MarkerSizeInTiles = 1.25f;
        private const float AltitudeOffset = 0.055f;

        internal readonly struct ScreenMarker
        {
            internal readonly ShipBase Ship;
            internal readonly DriveSubject Identity;
            internal readonly Vector2 Position;
            internal readonly float HitRadius;
            internal readonly bool Current;

            internal ScreenMarker(ShipBase ship, Vector2 position, float hitRadius, bool current)
            {
                Ship = ship;
                Identity = ship.Identity;
                Position = position;
                HitRadius = hitRadius;
                Current = current;
            }

            internal ScreenMarker(DriveSubject identity, Vector2 position, float hitRadius, bool current = false)
            {
                Ship = ShipBase.Invalid;
                Identity = identity;
                Position = position;
                HitRadius = hitRadius;
                Current = current;
            }

            internal Rect HitRect => new Rect(Position.x - HitRadius, Position.y - HitRadius,
                HitRadius * 2f, HitRadius * 2f);
        }

        internal sealed class ScreenMarkerGroup
        {
            internal readonly List<ScreenMarker> Members;
            internal readonly ScreenMarker Representative;

            internal ScreenMarkerGroup(List<ScreenMarker> members, ScreenMarker representative)
            {
                Members = members;
                Representative = representative;
            }

            internal bool Clustered => Members.Count > 1;
            internal Rect HitRect => Representative.HitRect;
        }

        private sealed class MarkerCache
        {
            public Texture2D texture;
            public Material material;
            public bool initialized;
            public System.IDisposable structureChanges;
            public Map map;
            public Rot4 rotation;
        }

        private static readonly Dictionary<DriveSubject, MarkerCache> Cache =
            new Dictionary<DriveSubject, MarkerCache>();

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
            foreach (ScreenMarkerGroup group in ScreenMarkerGroups(universe))
            {
                ShipBase ship = group.Representative.Ship;

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
                SpaceObject frame = universe.FrameOf(ship.Map);
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
        /// 世界剪影的 IMGUI 覆盖层。重叠成员只在代表剪影旁显示数量并共享提示；
        /// 实际选择菜单由 UniverseManager 的输入通道打开。
        /// </summary>
        public static void DrawPlayerShipMarkerUi(UniverseManager universe)
        {
            if (Event.current.type != EventType.Repaint || universe == null
                || !WorldRendererUtility.WorldSelected || !DebugViewSettings.drawWorldObjects
                || !OrbitSphere.Valid || PlanetLayer.Selected != OrbitSphere.Layer)
            {
                return;
            }

            GameFont formerFont = Text.Font;
            TextAnchor formerAnchor = Text.Anchor;
            Color formerColor = GUI.color;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.white;
            foreach (ScreenMarkerGroup group in ScreenMarkerGroups(universe))
            {
                if (!group.Clustered) continue;
                float radius = group.Representative.HitRadius;
                Rect badge = new Rect(group.Representative.Position.x + radius * 0.35f,
                    group.Representative.Position.y - radius - 3f, 30f, 18f);
                Widgets.DrawBoxSolid(badge, new Color(0.08f, 0.1f, 0.14f, 0.86f));
                GUI.color = Color.white;
                Widgets.Label(badge, "×" + group.Members.Count);
                TooltipHandler.TipRegion(group.HitRect,
                    string.Join("\n", group.Members.Select(member => member.Ship.Label)));
            }
            GUI.color = formerColor;
            Text.Anchor = formerAnchor;
            Text.Font = formerFont;
        }

        internal static List<ScreenMarkerGroup> ScreenMarkerGroups(UniverseManager universe)
        {
            var markers = new List<ScreenMarker>();
            if (universe == null || !WorldRendererUtility.WorldSelected
                || !DebugViewSettings.drawWorldObjects || !OrbitSphere.Valid
                || PlanetLayer.Selected != OrbitSphere.Layer)
            {
                return new List<ScreenMarkerGroup>();
            }
            foreach (ShipBase ship in universe.PlayerWorldMarkerShips())
            {
                if (universe.Approach.FlowTarget.Identity == ship.Identity
                    || !TryGetScreenPosition(ship, out Vector2 position, out float radius))
                {
                    continue;
                }
                markers.Add(new ScreenMarker(ship, position, radius, ship.Map == Find.CurrentMap));
            }

            DriveSubject? preferred = null;
            if (Find.CurrentMap != null
                && ShipStatusPanel.TryGetSelectedShip(Find.CurrentMap, out ShipBase selected)
                && selected.PlayerAccessLevel == PlayerAccessLevel.Player)
            {
                preferred = selected.Identity;
            }
            return ClusterScreenMarkers(markers, preferred);
        }

        /// <summary>
        /// 只按投影命中区聚合；输入先按身份排序，再做连通分量，避免枚举顺序改变代表或分组。
        /// </summary>
        internal static List<ScreenMarkerGroup> ClusterScreenMarkers(
            IEnumerable<ScreenMarker> source, DriveSubject? preferred = null)
        {
            List<ScreenMarker> markers = source.OrderBy(marker => marker.Identity.key)
                .ThenBy(marker => marker.Identity.shuttle).ToList();
            var result = new List<ScreenMarkerGroup>();
            var assigned = new bool[markers.Count];
            for (int root = 0; root < markers.Count; root++)
            {
                if (assigned[root]) continue;
                assigned[root] = true;
                var members = new List<ScreenMarker>();
                var pending = new Queue<int>();
                pending.Enqueue(root);
                while (pending.Count > 0)
                {
                    int index = pending.Dequeue();
                    ScreenMarker marker = markers[index];
                    members.Add(marker);
                    for (int candidate = 0; candidate < markers.Count; candidate++)
                    {
                        if (assigned[candidate] || !marker.HitRect.Overlaps(markers[candidate].HitRect)) continue;
                        assigned[candidate] = true;
                        pending.Enqueue(candidate);
                    }
                }

                ScreenMarker representative = members
                    .OrderByDescending(marker => preferred.HasValue && marker.Identity == preferred.Value)
                    .ThenByDescending(marker => marker.Current)
                    .ThenBy(marker => marker.Identity.key)
                    .ThenBy(marker => marker.Identity.shuttle)
                    .First();
                result.Add(new ScreenMarkerGroup(members, representative));
            }
            return result.OrderBy(group => group.Representative.Identity.key)
                .ThenBy(group => group.Representative.Identity.shuttle).ToList();
        }

        /// <summary>
        /// 剪影的屏幕位置与命中半径（聚焦点击用）。剪影不是 WorldObject，刻意不走
        /// RimExodus「useDynamicDrawer 真对象」路线（不进原版世界选择/检查面板），
        /// 手动做原版 GenWorldUI 同款换算：WorldToScreenPoint / Prefs.UIScale + y 翻转，
        /// 遮挡用地平线剔除（WorldRendererUtility.HiddenBehindTerrainNow）。
        /// </summary>
        public static bool TryGetScreenPosition(ShipBase ship, out Vector2 screenPos, out float hitRadiusPx)
        {
            screenPos = Vector2.zero;
            hitRadiusPx = 0f;
            PlanetLayer layer = OrbitSphere.Layer;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (!ship.IsValid || universe?.Approach.FlowTarget.Identity == ship.Identity
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

        private static MarkerCache MarkerFor(ShipBase ship)
        {
            if (Cache.TryGetValue(ship.Identity, out MarkerCache marker) && marker.initialized
                && marker.map == ship.Map && marker.rotation == ship.Anchor.Rotation)
            {
                return marker;
            }

            // 读档后运行时缓存为空，第一次显示时允许惰性补建一次。
            RebuildFor(ship);
            Cache.TryGetValue(ship.Identity, out marker);
            return marker;
        }

        /// <summary>
        /// 自由飞行入口调用：以脱离停靠瞬间的完整船体（控制域）重建一次剪影。
        /// </summary>
        public static void RebuildFor(ShipBase ship)
        {
            if (!ship.IsValid)
            {
                return;
            }
            EnsureCurrentGame();

            if (!Cache.TryGetValue(ship.Identity, out MarkerCache marker))
            {
                marker = new MarkerCache();
                Cache.Add(ship.Identity, marker);
            }
            Release(marker);
            marker.initialized = true;
            marker.structureChanges = ship.StructuralState?.Watch(ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                | ShipStructureAspect.Primary | ShipStructureAspect.Footprint | ShipStructureAspect.Membership
                | ShipStructureAspect.Representation, _ => marker.initialized = false);
            marker.map = ship.Map;
            marker.rotation = ship.Anchor.Rotation;

            Map map = ship.Map;
            if (map == null || map.Disposed)
            {
                return;
            }

            HashSet<IntVec3> liveCells = ship.GetType() == typeof(FoundationShip) ? ship.StructuralState?.Foundation : null;
            liveCells ??= new HashSet<IntVec3>(ship.HullCells);
            if (liveCells.Count == 0)
            {
                return;
            }

            marker.texture = ShipSilhouette.BuildTexture(liveCells,
                $"DeltaV_PlayerShipSilhouette_{ship.Identity}");
            if (marker.texture == null)
            {
                return;
            }
            marker.material = new Material(ShaderDatabase.WorldOverlayTransparentLit)
            {
                name = $"DeltaV_PlayerShipMarker_{ship.Identity}",
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

        internal static void ForgetShip(DriveSubject identity)
        {
            if (!Cache.TryGetValue(identity, out MarkerCache marker)) return;
            Release(marker);
            Cache.Remove(identity);
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
            marker.structureChanges?.Dispose();
            marker.structureChanges = null;
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

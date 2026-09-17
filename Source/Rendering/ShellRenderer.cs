using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>宇宙对象变换适配层；实际 Map 内容绘制由通用 MapOverlayRenderer 完成。</summary>
    public static class ShellRenderer
    {
        internal static readonly MapOverlayRenderConfig ShipConfig = new MapOverlayRenderConfig
        {
            requiredFoundation = DeltaVDefOf.Substructure,
            includeWallsAndDoors = true
        };

        internal static readonly MapOverlayRenderConfig PoiConfig = new MapOverlayRenderConfig
        {
            drawVisibleTerrain = true,
            includeWallsAndDoors = true,
            includeBuildings = true,
            includeItems = true
        };

        public static void DrawNonFocusedShells(UniverseManager universe)
        {
            Map current = Find.CurrentMap;
            if (current == null || !WorldRendererUtility.DrawingMap) return;
            SpaceObject frame = universe.FrameOf(current);
            if (frame == null) return;
            foreach (SpaceObject obj in universe.objects)
                if (obj != frame) DrawObjectShell(obj, frame, current);
        }

        private static void DrawObjectShell(SpaceObject obj, SpaceObject frame, Map frameMap)
        {
            if (obj is ShipState dockingShip && dockingShip.docking) return;
            ShipState ship = obj as ShipState;
            bool docked = ship != null && ship.docked;
            Map map = docked ? ship.dockedMap : obj.map;
            if (map == null || map.Disposed || IncrementalMapGenerationService.IsGenerating(map)
                || docked && map == frameMap) return;

            HashSet<IntVec3> allowedCells = null;
            Quaternion rotation;
            Vector3 drawPos;
            Vector2 sourceOrigin;
            if (docked)
            {
                Building_GravEngine engine = DockController.FindEngineForShip(map, ship.primaryConsole);
                if (engine == null) return;
                allowedCells = engine.AllConnectedSubstructure;
                rotation = SpaceTransform.FocusedMeshRotation(frame, 0f);
                drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                sourceOrigin = DockController.DockedOriginMapPoint(ship);
            }
            else
            {
                rotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
                drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
                sourceOrigin = SpaceTransform.ShipLocalOrigin(map);
            }

            Vector3 translation = drawPos - rotation * new Vector3(sourceOrigin.x, 0f, sourceOrigin.y);
            Matrix4x4 matrix = Matrix4x4.TRS(translation, rotation, Vector3.one);
            CellRect localView = TransformRectInverse(matrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(4);
            MapOverlayRenderer.Draw(map, matrix, rotation, localView,
                obj is PoiState ? PoiConfig : ShipConfig, allowedCells);
        }

        public static void ClearRuntimeCaches()
        {
            ShipConfig.requiredFoundation = DeltaVDefOf.Substructure;
            MapOverlayRenderer.ClearRuntimeCaches();
        }

        private static CellRect TransformRectInverse(Matrix4x4 matrix, CellRect rect)
        {
            Matrix4x4 inverse = matrix.inverse;
            Vector3 a = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.minZ));
            Vector3 b = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.minZ));
            Vector3 c = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.maxZ));
            Vector3 d = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.maxZ));
            return CellRect.FromLimits(
                Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x))),
                Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z))),
                Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x))),
                Mathf.CeilToInt(Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z))));
        }
    }
}

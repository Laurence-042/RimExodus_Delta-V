using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 非聚焦对象的白名单外壳代理。真实 Map 是事实源，但绝不再重放整张
    /// SectionLayer：逆重基架、ThingDef.IsWall / IsDoor 自动接纳，其他 Thing 必须由
    /// ShellRenderRuleDef 显式允许。规则还可替换 GraphicData 并用 zIndex 控制遮挡顺序。
    /// </summary>
    public static class ShellRenderer
    {
        // RimWorld 1.6 Custom/Terrain 顶点程序使用 v0.xz * 0.0625 采样；也就是
        // 一张地形纹理横跨 16×16 格。代理改用普通透明 shader 时必须显式复现。
        private const float TerrainUvScale = 0.0625f;
        private const int TerrainTexturePeriod = 16;

        private static readonly Dictionary<ThingDef, ShellRenderRuleDef> Rules =
            new Dictionary<ThingDef, ShellRenderRuleDef>();

        // 纹理每 16 格循环，只需缓存 16×16 种局部 UV plane。它们是纯派生表现缓存。
        private static readonly Mesh[] SubstructureMeshes =
            new Mesh[TerrainTexturePeriod * TerrainTexturePeriod];

        private static int cachedRuleCount = -1;

        public static void DrawNonFocusedShells(UniverseManager universe)
        {
            Map current = Find.CurrentMap;
            if (current == null || !WorldRendererUtility.DrawingMap) return;
            EnsureRules();

            SpaceObject frame = universe.SpaceObjectOnMap(current);
            if (frame != null)
            {
                foreach (SpaceObject obj in universe.objects)
                {
                    if (obj != frame) DrawObjectShell(obj, frame, current);
                }
            }
            else if (universe.hostMap == current)
            {
                ShipState hostFrame = universe.HostFrame;
                foreach (SpaceObject obj in universe.objects) DrawObjectShell(obj, hostFrame, current);
            }
        }

        private static void DrawObjectShell(SpaceObject obj, SpaceObject frame, Map frameMap)
        {
            if (obj.map == null) return;
            if (obj is ShipState ship && (ship.docked || ship.docking)) return;

            Map map = obj.map;
            Quaternion shellRotation = SpaceTransform.FocusedMeshRotation(frame, obj.pose.angle);
            Vector3 drawPos = SpaceTransform.FocusedDrawPosition(frame, frameMap, obj.pose.position, 0f);
            Vector2 center = SpaceTransform.ShipLocalOrigin(map);
            Vector3 translation = drawPos - shellRotation * new Vector3(center.x, 0f, center.y);
            Matrix4x4 shellMatrix = Matrix4x4.TRS(translation, shellRotation, Vector3.one);
            CellRect localView = TransformRectInverse(shellMatrix, Find.CameraDriver.CurrentViewRect).ExpandedBy(4);

            DrawSubstructure(map, shellMatrix, shellRotation, localView);

            List<Thing> things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed || !thing.Spawned || !localView.Contains(thing.Position)) continue;

                bool wallLike = thing.def.IsWall || thing.def.IsDoor;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                if (!wallLike && rule == null) continue;

                Graphic graphic = rule?.GraphicFor(thing) ?? thing.Graphic;
                if (graphic == null) continue;
                Rot4 rot = thing.Rotation;
                Vector3 sourcePos = thing.DrawPos + graphic.DrawOffset(rot);
                Vector3 targetPos = shellMatrix.MultiplyPoint3x4(sourcePos);
                targetPos.y += (rule?.zIndex ?? 0f) * Altitudes.AltInc;
                Quaternion targetRotation = shellRotation * graphic.QuatFromRot(rot);
                // 墙常用 Graphic_Linked；其实际邻接 sprite 只由 MatSingleFor(thing) 解析，
                // 直接 MatAt 会丢掉 link atlas 选择。普通 Multi/Single 仍按 Rot4 取材质。
                Material material = graphic is Graphic_Linked
                    ? graphic.MatSingleFor(thing)
                    : graphic.MatAt(rot, thing);
                if (material != null)
                {
                    Graphics.DrawMesh(graphic.MeshAt(rot), targetPos, targetRotation, material, 0);
                }
            }
        }

        private static void DrawSubstructure(Map map, Matrix4x4 shellMatrix,
            Quaternion shellRotation, CellRect localView)
        {
            TerrainDef substructure = DeltaVDefOf.Substructure;
            Texture texture = substructure?.DrawMatSingle?.mainTexture;
            if (texture == null) return;

            // 不重放 Terrain section；只把 Gravship 的 foundation 层作为船壳白名单的一部分。
            // 使用普通透明材质让纹理随外壳刚体一起旋转，而不是继续使用依赖 Map section
            // 世界坐标的 terrain shader。
            Material material = MaterialPool.MatFrom(
                new MaterialRequest(texture, ShaderDatabase.Transparent, substructure.DrawColor));
            CellRect visible = localView.ClipInsideMap(map);
            foreach (IntVec3 cell in visible)
            {
                if (map.terrainGrid.FoundationAt(cell) != substructure) continue;
                Vector3 sourcePos = new Vector3(cell.x + 0.5f,
                    AltitudeLayer.Terrain.AltitudeFor(), cell.z + 0.5f);
                Vector3 targetPos = shellMatrix.MultiplyPoint3x4(sourcePos);
                Graphics.DrawMesh(SubstructureMeshFor(cell), targetPos, shellRotation, material, 0);
            }
        }

        private static Mesh SubstructureMeshFor(IntVec3 cell)
        {
            int phaseX = cell.x % TerrainTexturePeriod;
            int phaseZ = cell.z % TerrainTexturePeriod;
            if (phaseX < 0) phaseX += TerrainTexturePeriod;
            if (phaseZ < 0) phaseZ += TerrainTexturePeriod;
            int index = phaseX + phaseZ * TerrainTexturePeriod;
            Mesh mesh = SubstructureMeshes[index];
            if (mesh != null) return mesh;

            Mesh source = MeshPool.plane10;
            Vector3[] vertices = source.vertices;
            var uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                // plane10 以格中心为原点、边界为 ±0.5；还原源 Map 顶点坐标后再乘 1/16。
                uvs[i] = new Vector2(
                    (phaseX + 0.5f + vertices[i].x) * TerrainUvScale,
                    (phaseZ + 0.5f + vertices[i].z) * TerrainUvScale);
            }

            mesh = new Mesh
            {
                name = $"DeltaV_SubstructureUV_{phaseX}_{phaseZ}",
                vertices = vertices,
                uv = uvs,
                triangles = source.triangles
            };
            Vector3[] normals = source.normals;
            if (normals != null && normals.Length == vertices.Length) mesh.normals = normals;
            mesh.RecalculateBounds();
            SubstructureMeshes[index] = mesh;
            return mesh;
        }

        private static void EnsureRules()
        {
            List<ShellRenderRuleDef> defs = DefDatabase<ShellRenderRuleDef>.AllDefsListForReading;
            if (cachedRuleCount == defs.Count) return;
            Rules.Clear();
            for (int i = 0; i < defs.Count; i++)
            {
                ShellRenderRuleDef rule = defs[i];
                if (rule.thingDef != null) Rules[rule.thingDef] = rule;
            }
            cachedRuleCount = defs.Count;
        }

        public static void ClearRuntimeCaches()
        {
            Rules.Clear();
            cachedRuleCount = -1;
            for (int i = 0; i < SubstructureMeshes.Length; i++)
            {
                if (SubstructureMeshes[i] != null)
                {
                    Object.Destroy(SubstructureMeshes[i]);
                    SubstructureMeshes[i] = null;
                }
            }
        }

        private static CellRect TransformRectInverse(Matrix4x4 matrix, CellRect rect)
        {
            Matrix4x4 inverse = matrix.inverse;
            Vector3 a = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.minZ));
            Vector3 b = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.minZ));
            Vector3 c = inverse.MultiplyPoint3x4(new Vector3(rect.maxX, 0f, rect.maxZ));
            Vector3 d = inverse.MultiplyPoint3x4(new Vector3(rect.minX, 0f, rect.maxZ));
            float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
            float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
            float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
            float maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
            return CellRect.FromLimits(Mathf.FloorToInt(minX), Mathf.FloorToInt(minZ),
                Mathf.CeilToInt(maxX), Mathf.CeilToInt(maxZ));
        }
    }
}

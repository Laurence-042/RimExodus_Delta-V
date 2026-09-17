using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class MapOverlayRenderConfig
    {
        public TerrainDef requiredFoundation;
        public bool drawVisibleTerrain;
        public bool includeWallsAndDoors;
        public bool includeBuildings;
        public bool includeItems;
        public bool includeRules = true;
    }

    /// <summary>
    /// 与宇宙业务解耦的地图叠加器。输入只有 Map、局部到显示帧矩阵和配置；
    /// 17x17 section 快照是可丢弃运行时缓存。
    /// </summary>
    public static class MapOverlayRenderer
    {
        private const int SectionSize = 17;
        private const int TexturePeriod = 16;
        private const float UvScale = 0.0625f;

        private sealed class SectionSnapshot
        {
            public readonly List<IntVec3> terrainCells = new List<IntVec3>();
            public readonly List<Thing> things = new List<Thing>();
        }

        private sealed class MapCache
        {
            public readonly Dictionary<int, SectionSnapshot> sections = new Dictionary<int, SectionSnapshot>();
            public readonly HashSet<int> dirty = new HashSet<int>();
        }

        private static readonly Dictionary<Map, MapCache> Caches = new Dictionary<Map, MapCache>();
        private static readonly Dictionary<ThingDef, ShellRenderRuleDef> Rules = new Dictionary<ThingDef, ShellRenderRuleDef>();
        private static readonly Dictionary<TerrainDef, Material> TerrainMaterials = new Dictionary<TerrainDef, Material>();
        private static readonly Mesh[] CellMeshes = new Mesh[TexturePeriod * TexturePeriod];
        private static readonly FieldInfo DrawerMapField = AccessTools.Field(typeof(MapDrawer), "map");
        private static int cachedRuleCount = -1;

        public static void Draw(Map map, Matrix4x4 matrix, Quaternion rotation, CellRect localView,
            MapOverlayRenderConfig config, HashSet<IntVec3> allowedCells = null, int layer = 0)
        {
            if (map == null || map.Disposed || IncrementalMapGenerationService.IsGenerating(map)) return;
            if (!localView.Overlaps(CellRect.WholeMap(map))) return;
            EnsureRules();
            CellRect visible = localView.ClipInsideMap(map);
            int minSx = visible.minX / SectionSize;
            int maxSx = visible.maxX / SectionSize;
            int minSz = visible.minZ / SectionSize;
            int maxSz = visible.maxZ / SectionSize;
            for (int sz = minSz; sz <= maxSz; sz++)
            for (int sx = minSx; sx <= maxSx; sx++)
                DrawSection(map, matrix, rotation, visible, config, allowedCells, sx, sz, layer);
        }

        private static void DrawSection(Map map, Matrix4x4 matrix, Quaternion rotation, CellRect visible,
            MapOverlayRenderConfig config, HashSet<IntVec3> allowedCells, int sx, int sz, int layer)
        {
            int key = sx | (sz << 16);
            MapCache cache = CacheFor(map);
            if (!cache.sections.TryGetValue(key, out SectionSnapshot section) || cache.dirty.Remove(key))
            {
                section = BuildSection(map, sx, sz);
                cache.sections[key] = section;
            }

            for (int i = 0; i < section.terrainCells.Count; i++)
            {
                IntVec3 cell = section.terrainCells[i];
                if (!visible.Contains(cell) || allowedCells != null && !allowedCells.Contains(cell)) continue;
                TerrainDef terrain = TerrainForConfig(map, cell, config);
                if (terrain == null || terrain.dontRender) continue;
                Material material = MaterialFor(terrain);
                if (material == null) continue;
                Vector3 source = new Vector3(cell.x + 0.5f, AltitudeLayer.Terrain.AltitudeFor(), cell.z + 0.5f);
                Graphics.DrawMesh(CellMeshFor(cell), matrix.MultiplyPoint3x4(source), rotation, material, layer);
            }

            for (int i = 0; i < section.things.Count; i++)
            {
                Thing thing = section.things[i];
                if (!ShouldDrawThing(thing, config) || !visible.Contains(thing.Position)
                    || allowedCells != null && !allowedCells.Contains(thing.Position)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                Graphic graphic = rule?.GraphicFor(thing) ?? thing.Graphic;
                if (graphic == null) continue;
                Rot4 rot = thing.Rotation;
                Vector3 target = matrix.MultiplyPoint3x4(thing.DrawPos + graphic.DrawOffset(rot));
                target.y += (rule?.zIndex ?? 0f) * Altitudes.AltInc;
                Material material = graphic is Graphic_Linked ? graphic.MatSingleFor(thing) : graphic.MatAt(rot, thing);
                if (material != null)
                    Graphics.DrawMesh(graphic.MeshAt(rot), target, rotation * graphic.QuatFromRot(rot), material, layer);
            }
        }

        public static bool ContainsVisibleContent(Map map, IntVec3 cell, MapOverlayRenderConfig config,
            HashSet<IntVec3> allowedCells = null)
        {
            EnsureRules();
            if (map == null || !cell.InBounds(map) || allowedCells != null && !allowedCells.Contains(cell)) return false;
            TerrainDef terrain = TerrainForConfig(map, cell, config);
            if (terrain != null && !terrain.dontRender) return true;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++) if (ShouldDrawThing(things[i], config)) return true;
            return false;
        }

        private static TerrainDef TerrainForConfig(Map map, IntVec3 cell, MapOverlayRenderConfig config)
        {
            if (config?.requiredFoundation != null)
                return map.terrainGrid.FoundationAt(cell) == config.requiredFoundation
                    ? config.requiredFoundation : null;
            return config?.drawVisibleTerrain == true ? map.terrainGrid.TerrainAt(cell) : null;
        }

        private static bool ShouldDrawThing(Thing thing, MapOverlayRenderConfig config)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing is Pawn || thing is Projectile
                || thing is Mote || thing.def.category == ThingCategory.Filth
                || thing.def.category == ThingCategory.Gas || thing.def.category == ThingCategory.Mote) return false;
            Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
            if (rule?.exclude == true) return false;
            if (config == null) return false;
            if (config.includeWallsAndDoors && (thing.def.IsWall || thing.def.IsDoor)) return true;
            if (config.includeBuildings && thing.def.category == ThingCategory.Building) return true;
            if (config.includeItems && thing.def.category == ThingCategory.Item) return true;
            return config.includeRules && rule != null;
        }

        private static SectionSnapshot BuildSection(Map map, int sx, int sz)
        {
            var result = new SectionSnapshot();
            CellRect rect = CellRect.FromLimits(sx * SectionSize, sz * SectionSize,
                sx * SectionSize + SectionSize - 1, sz * SectionSize + SectionSize - 1).ClipInsideMap(map);
            foreach (IntVec3 cell in rect)
            {
                result.terrainCells.Add(cell);
                List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                for (int i = 0; i < things.Count; i++)
                    if (things[i].Position == cell && !result.things.Contains(things[i])) result.things.Add(things[i]);
            }
            return result;
        }

        public static void MarkDirty(Map map, IntVec3 cell, ulong flags,
            bool adjacentCells = false, bool adjacentSections = false)
        {
            ulong relevant = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings;
            if (map == null || (flags & relevant) == 0) return;
            MapCache cache = CacheFor(map);
            int sx = cell.x / SectionSize;
            int sz = cell.z / SectionSize;
            cache.dirty.Add(sx | (sz << 16));
            if (adjacentSections)
            {
                for (int z = sz - 1; z <= sz + 1; z++)
                for (int x = sx - 1; x <= sx + 1; x++)
                    if (x >= 0 && z >= 0) cache.dirty.Add(x | (z << 16));
            }
            else if (adjacentCells)
            {
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 adjacent = cell + GenAdj.AdjacentCells[i];
                    if (adjacent.InBounds(map))
                        cache.dirty.Add(adjacent.x / SectionSize | (adjacent.z / SectionSize << 16));
                }
            }
        }

        public static Map MapOf(MapDrawer drawer) => DrawerMapField?.GetValue(drawer) as Map;

        public static void Release(Map map)
        {
            if (map != null) Caches.Remove(map);
        }

        public static void ClearRuntimeCaches()
        {
            Caches.Clear();
            Rules.Clear();
            TerrainMaterials.Clear();
            cachedRuleCount = -1;
            for (int i = 0; i < CellMeshes.Length; i++)
            {
                if (CellMeshes[i] != null) Object.Destroy(CellMeshes[i]);
                CellMeshes[i] = null;
            }
        }

        private static MapCache CacheFor(Map map)
        {
            if (!Caches.TryGetValue(map, out MapCache cache)) Caches[map] = cache = new MapCache();
            return cache;
        }

        private static Material MaterialFor(TerrainDef terrain)
        {
            if (terrain?.DrawMatSingle?.mainTexture == null) return null;
            if (!TerrainMaterials.TryGetValue(terrain, out Material material))
            {
                material = MaterialPool.MatFrom(new MaterialRequest(terrain.DrawMatSingle.mainTexture,
                    ShaderDatabase.Transparent, terrain.DrawColor));
                TerrainMaterials[terrain] = material;
            }
            return material;
        }

        private static Mesh CellMeshFor(IntVec3 cell)
        {
            int x = ((cell.x % TexturePeriod) + TexturePeriod) % TexturePeriod;
            int z = ((cell.z % TexturePeriod) + TexturePeriod) % TexturePeriod;
            int index = x + z * TexturePeriod;
            if (CellMeshes[index] != null) return CellMeshes[index];
            Mesh source = MeshPool.plane10;
            Vector3[] vertices = source.vertices;
            var uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                uv[i] = new Vector2((x + 0.5f + vertices[i].x) * UvScale,
                    (z + 0.5f + vertices[i].z) * UvScale);
            var mesh = new Mesh { name = $"DeltaV_OverlayUV_{x}_{z}", vertices = vertices,
                uv = uv, triangles = source.triangles };
            if (source.normals?.Length == vertices.Length) mesh.normals = source.normals;
            mesh.RecalculateBounds();
            return CellMeshes[index] = mesh;
        }

        private static void EnsureRules()
        {
            List<ShellRenderRuleDef> defs = DefDatabase<ShellRenderRuleDef>.AllDefsListForReading;
            if (cachedRuleCount == defs.Count) return;
            Rules.Clear();
            for (int i = 0; i < defs.Count; i++) if (defs[i].thingDef != null) Rules[defs[i].thingDef] = defs[i];
            cachedRuleCount = defs.Count;
        }
    }
}

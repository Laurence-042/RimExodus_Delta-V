using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 与宇宙业务解耦的地图叠加器。输入只有 Map、局部到显示帧矩阵和归属裁剪集；
    /// 17x17 section 快照是可丢弃运行时缓存。所有 SpaceObject 共用同一条管线
    /// （2026-09-18 用户决策：不按对象类型分叉配置），纳入标准 = 结构外观：
    /// 地形走 TerrainAt 实际显示语义（原版回落链让基架顶替太空表面、地板盖住基架，
    /// Space/Void 因 dontRender 天然排除）、墙门、气密结构与自然岩石（推进器/船壳/
    /// 岩石自动入壳）、ShellRenderRuleDef 显式规则（exclude/alternateGraphic/zIndex）。
    /// Thing 按各自 def.Altitude 真实分层；连接状态贴图（含 CornerFiller 角盖）按
    /// 当前 LinkGrid 状态取片。
    /// </summary>
    public static class MapOverlayRenderer
    {
        private const int SectionSize = 17;
        private const int TexturePeriod = 16;
        private const float UvScale = 0.0625f;

        // CornerFiller 角盖板常量（照抄 Graphic_LinkedCornerFiller）：0.5×0.5 盖板
        // 距格中心 (0.5,0.5).magnitude×0.5，UV 退化为 (0.5, 0.6) 单点取样。
        private const float CornerCoverOffsetDist = 0.35355339f;

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
        private static Mesh cornerFillerMesh;
        private static int cachedRuleCount = -1;

        /// <summary>最近一次 CollectShellCommands 收集的四边形数（诊断用：确认壳内容
        /// 真实提交；收集入口清零、三个提交点自增，无字符串构造成本）。</summary>
        internal static int QuadsDrawnLastPass { get; private set; }

        /// <summary>缓冲绘制命令：mesh + 完整 TRS 矩阵 + 材质（props 仅火焰命令非空）；
        /// queue/sequence 供壳内排序。</summary>
        internal struct OverlayCommand
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public MaterialPropertyBlock props;
            public int queue;
            public int sequence;
        }

        private static readonly List<OverlayCommand> Commands = new List<OverlayCommand>(512);
        private static int nextSequence;

        /// <summary>
        /// 收集一壳的全部四边形（地形/建筑/角盖板）到待发命令表。返回是否有可见内容；
        /// 调用方随后可经 AddFlameCommand 追加火焰，再 EmitShellCommands 写入目标缓冲。
        /// </summary>
        public static bool CollectShellCommands(Map map, Matrix4x4 matrix, Quaternion rotation,
            CellRect localView, HashSet<IntVec3> allowedCells = null)
        {
            QuadsDrawnLastPass = 0;
            Commands.Clear();
            nextSequence = 0;
            if (map == null || map.Disposed
                || Current.Game?.GetComponent<UniverseManager>()?.Generation?.IsGenerating(map) == true) return false;
            if (!localView.Overlaps(CellRect.WholeMap(map))) return false;
            EnsureRules();
            CellRect visible = localView.ClipInsideMap(map);
            int minSx = visible.minX / SectionSize;
            int maxSx = visible.maxX / SectionSize;
            int minSz = visible.minZ / SectionSize;
            int maxSz = visible.maxZ / SectionSize;
            for (int sz = minSz; sz <= maxSz; sz++)
            for (int sx = minSx; sx <= maxSx; sx++)
                CollectSection(map, matrix, rotation, visible, allowedCells, sx, sz, Commands);
            return Commands.Count > 0;
        }

        /// <summary>火焰命令追加（ThrusterEffects 调用）：queue 取材质 renderQueue
        /// （火焰材质 3201，经排序天然位于壳内地形 3000 之上）；props 必须是本命令
        /// 独占的池化块（CommandBuffer 对块内容的读取时序不保证记录时快照）。</summary>
        internal static void AddFlameCommand(Mesh mesh, Matrix4x4 matrix, Material material, MaterialPropertyBlock props)
        {
            Commands.Add(new OverlayCommand
            {
                mesh = mesh,
                matrix = matrix,
                material = material,
                props = props,
                queue = material.renderQueue,
                sequence = nextSequence++
            });
        }

        /// <summary>单四边形杂项命令（对接幽灵船提示等，DockingGhostRenderer 调用）：
        /// 独占整条命令表——不经整壳收集，直接装载一条后由调用方 Emit 到目标层槽位。</summary>
        internal static void CollectSingleQuad(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            Commands.Clear();
            nextSequence = 0;
            Commands.Add(new OverlayCommand
            {
                mesh = mesh,
                matrix = matrix,
                material = material,
                queue = material.renderQueue
            });
        }

        /// <summary>
        /// 把收集到的命令写入 commandBuffer（RimExodus SeamlessTileRenderer 同款机制，
        /// 2026-09-20）：按 (renderQueue, sequence) 排序（cutout 先画写深度、transparent
        /// 后画受深度测试，同原版 SectionLayer 语义），再清深度（保色）后按序绘制——
        /// 命令顺序即层序，不依赖 3D 深度排序。正交俯视相机下 Graphics.DrawMesh 的
        /// 透明队列按 bounds 距离排序、x/z 分量会压过海拔差，负海拔高度带方案已实测
        /// 证伪，勿回退（详见 ShellRenderer.DrawNonFocusedShells）。
        /// </summary>
        public static void EmitShellCommands(CommandBuffer buffer)
        {
            if (Commands.Count == 0) return;
            Commands.Sort(CompareCommands);
            buffer.ClearRenderTarget(true, false, Color.clear, 1f);
            for (int i = 0; i < Commands.Count; i++)
            {
                OverlayCommand command = Commands[i];
                if (command.props == null)
                    buffer.DrawMesh(command.mesh, command.matrix, command.material);
                else
                    buffer.DrawMesh(command.mesh, command.matrix, command.material, 0, -1, command.props);
            }
        }

        private static int CompareCommands(OverlayCommand left, OverlayCommand right)
        {
            int queueComparison = left.queue.CompareTo(right.queue);
            return queueComparison != 0 ? queueComparison : left.sequence.CompareTo(right.sequence);
        }

        private static void CollectSection(Map map, Matrix4x4 matrix, Quaternion rotation, CellRect visible,
            HashSet<IntVec3> allowedCells, int sx, int sz, List<OverlayCommand> collector)
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
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain == null || terrain.dontRender) continue;
                Material material = MaterialFor(terrain);
                if (material == null) continue;
                Vector3 source = new Vector3(cell.x + 0.5f, AltitudeLayer.Terrain.AltitudeFor(), cell.z + 0.5f);
                collector.Add(new OverlayCommand { mesh = CellMeshFor(cell), matrix = Matrix4x4.TRS(matrix.MultiplyPoint3x4(source), rotation, Vector3.one), material = material, queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
            }

            for (int i = 0; i < section.things.Count; i++)
            {
                Thing thing = section.things[i];
                if (!ShouldDrawThing(thing) || !visible.Contains(thing.Position)
                    || allowedCells != null && !allowedCells.Contains(thing.Position)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                Graphic graphic = rule?.GraphicFor(thing) ?? thing.Graphic;
                if (graphic == null) continue;
                Rot4 rot = thing.Rotation;
                Vector3 target = matrix.MultiplyPoint3x4(thing.DrawPos + graphic.DrawOffset(rot));
                float zLift = (rule?.zIndex ?? 0f) * Altitudes.AltInc;
                target.y += zLift;
                Material material = graphic is Graphic_Linked ? graphic.MatSingleFor(thing) : graphic.MatAt(rot, thing);
                if (material == null) continue;
                Quaternion quat = rotation * graphic.QuatFromRot(rot);
                collector.Add(new OverlayCommand { mesh = graphic.MeshAt(rot), matrix = Matrix4x4.TRS(target, quat, Vector3.one), material = material, queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
                if (graphic is Graphic_LinkedCornerFiller cornerFiller)
                    DrawCornerFillerCovers(thing, cornerFiller, material, zLift, matrix, rotation, collector);
            }
        }

        public static bool ContainsVisibleContent(Map map, IntVec3 cell, HashSet<IntVec3> allowedCells = null)
        {
            EnsureRules();
            if (map == null || !cell.InBounds(map) || allowedCells != null && !allowedCells.Contains(cell)) return false;
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain != null && !terrain.dontRender) return true;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++) if (ShouldDrawThing(things[i])) return true;
            return false;
        }

        /// <summary>
        /// 结构外观纳入标准（所有 SpaceObject 统一，2026-09-18 用户决策）：动态物剔除
        /// → 显式 exclude 最优先 → 墙/门 → 气密结构与自然岩石（推进器、船壳、岩石
        /// 自动入壳，无规则即用原始贴图）→ 显式规则。物品与非气密建筑（家具、内部
        /// 设备）不属于外壳，不自动显示。
        /// </summary>
        private static bool ShouldDrawThing(Thing thing)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing is Pawn || thing is Projectile
                || thing is Mote || thing.def.category == ThingCategory.Filth
                || thing.def.category == ThingCategory.Gas || thing.def.category == ThingCategory.Mote) return false;
            Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
            if (rule?.exclude == true) return false;
            if (thing.def.IsWall || thing.def.IsDoor) return true;
            if (thing is Building building
                && (building.IsAirtight || building.def.building.isNaturalRock)) return true;
            return rule != null;
        }

        /// <summary>
        /// 复刻 Graphic_LinkedCornerFiller.Print 的对角盖板（1.6 反编译 41-77 行）：
        /// 斜向连接（对角相连且两个相邻正向也相连）时在对角处补 0.5×0.5 盖板，材质
        /// 与主 quad 相同（连接态 atlas 变体），UV 退化为 (0.5, 0.6) 单点取样；地图
        /// 边缘外的对角按原版把对应边放大 5 倍。原版 Print 是 SectionLayer 专属，船壳
        /// 路径按同一判定用 Graphics.DrawMesh 逐块绘制；LinkGrid 变化经 MapMeshDirty
        /// patch 已覆盖脏化。
        /// </summary>
        private static void DrawCornerFillerCovers(Thing thing, Graphic_LinkedCornerFiller graphic,
            Material material, float zLift, Matrix4x4 matrix, Quaternion rotation,
            List<OverlayCommand> collector)
        {
            IntVec3 position = thing.Position;
            Map map = thing.Map;
            Vector3 drawPos = thing.DrawPos;
            Quaternion quat = rotation * graphic.QuatFromRot(thing.Rotation);
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = position + GenAdj.DiagonalDirectionsAround[i];
                if (!graphic.ShouldLinkWith(c, thing)
                    || (i == 0 && (!graphic.ShouldLinkWith(position + IntVec3.West, thing) || !graphic.ShouldLinkWith(position + IntVec3.South, thing)))
                    || (i == 1 && (!graphic.ShouldLinkWith(position + IntVec3.West, thing) || !graphic.ShouldLinkWith(position + IntVec3.North, thing)))
                    || (i == 2 && (!graphic.ShouldLinkWith(position + IntVec3.East, thing) || !graphic.ShouldLinkWith(position + IntVec3.North, thing)))
                    || (i == 3 && (!graphic.ShouldLinkWith(position + IntVec3.East, thing) || !graphic.ShouldLinkWith(position + IntVec3.South, thing))))
                {
                    continue;
                }
                Vector3 center = drawPos + GenAdj.DiagonalDirectionsAround[i].ToVector3().normalized * CornerCoverOffsetDist
                    + new Vector3(0f, Altitudes.AltInc + zLift, 0.09f);
                Vector2 size = new Vector2(0.5f, 0.5f);
                if (!c.InBounds(map))
                {
                    if (c.x == -1)
                    {
                        center.x -= 1f;
                        size.x *= 5f;
                    }
                    if (c.z == -1)
                    {
                        center.z -= 1f;
                        size.y *= 5f;
                    }
                    if (c.x == map.Size.x)
                    {
                        center.x += 1f;
                        size.x *= 5f;
                    }
                    if (c.z == map.Size.z)
                    {
                        center.z += 1f;
                        size.y *= 5f;
                    }
                }
                Matrix4x4 trs = Matrix4x4.TRS(center, quat, new Vector3(size.x * 2f, 1f, size.y * 2f));
                collector.Add(new OverlayCommand { mesh = CornerFillerMesh(), matrix = matrix * trs, material = material, queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
            }
        }

        /// <summary>角盖板专用 mesh：0.5×0.5，顶点序照 Printer_Plane.PrintPlane（底边 y=0、顶边 y=0.01），UV 全 (0.5, 0.6)。</summary>
        private static Mesh CornerFillerMesh()
        {
            if (cornerFillerMesh != null) return cornerFillerMesh;
            var mesh = new Mesh { name = "DeltaV_ShellCornerFillerCover" };
            mesh.vertices = new[]
            {
                new Vector3(-0.25f, 0f, -0.25f),
                new Vector3(-0.25f, 0.01f, 0.25f),
                new Vector3(0.25f, 0.01f, 0.25f),
                new Vector3(0.25f, 0f, -0.25f)
            };
            var uv = new Vector2[4]
            {
                new Vector2(0.5f, 0.6f),
                new Vector2(0.5f, 0.6f),
                new Vector2(0.5f, 0.6f),
                new Vector2(0.5f, 0.6f)
            };
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return cornerFillerMesh = mesh;
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
            if (cornerFillerMesh != null)
            {
                Object.Destroy(cornerFillerMesh);
                cornerFillerMesh = null;
            }
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

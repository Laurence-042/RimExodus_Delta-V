using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 与宇宙业务解耦的地图叠加器。输入只有 Map、局部到显示帧矩阵和归属裁剪集；
    /// 17x17 section 快照是可丢弃运行时缓存。所有 SpaceObject 共用同一条管线
    /// （2026-09-18 用户决策：不按对象类型分叉配置），纳入标准 = 结构外观：
    /// 地形走 TerrainAt 实际显示语义（原版回落链让基架顶替太空表面、地板盖住基架，
    /// Space/Void 因 dontRender 天然排除）、墙门、气密结构与自然岩石（推进器/船壳/
    /// 岩石自动入壳）、ShellRenderRuleDef 显式规则及无顶基架上的建筑。屋顶面逐格
    /// 覆盖甲板层，屋顶上的显式部件继续使用 Exposed 层。建筑的静态层由原版 Print
    /// 生成，实时层在限定作用域内采集原版 DrawMesh；连接图与 Comp 附加层共用此路径。
    /// </summary>
    public static class MapOverlayRenderer
    {
        private const int SectionSize = 17;
        private const int TexturePeriod = ShellTerrainBatches.TexturePeriod;
        private const int TopDamageResolution = 256;
        private const int ScratchAtlasReadLimit = 2048;
#if DELTAV_SHELL_TERRAIN_AB_BASELINE
        internal static readonly bool BatchStaticTerrain = false;
#else
        internal static readonly bool BatchStaticTerrain = true;
#endif

        private sealed class SectionSnapshot : System.IDisposable
        {
            public readonly List<IntVec3> terrainCells = new List<IntVec3>();
            public readonly List<IntVec3> moonPoolCells = new List<IntVec3>();
            public readonly List<Thing> things = new List<Thing>();
            public readonly HashSet<Thing> deckThings = new HashSet<Thing>();
            public readonly List<PrintBatch> printed = new List<PrintBatch>();
            public Dictionary<Thing, TurretTopDamage> turretTopDamage;
            public SectionLayer gravshipHull;
            public SectionLayer substructureProps;
            public ShellHullVisuals.EdgeMeshes hullEdges;
            public Mesh roofEdges;
            public ShellTerrainBatches terrain;

            public void Dispose()
            {
                foreach (PrintBatch batch in printed) batch.layer.Dispose();
                if (turretTopDamage != null)
                    foreach (TurretTopDamage damage in turretTopDamage.Values) damage.Dispose();
                gravshipHull?.Dispose();
                substructureProps?.Dispose();
                hullEdges?.Dispose();
                terrain?.Dispose();
                if (roofEdges != null) Object.Destroy(roofEdges);
            }
        }

        private sealed class PrintBatch
        {
            public ShellSurfaceLayer surface;
            public float zIndex;
            public bool shared;
            public SectionLayer layer;
        }

        // The full vanilla layer also prints damage on equipment hidden by the shell.
        private static class BuildingDamagePrinter
        {
            internal static readonly System.Action<SectionLayer_BuildingsDamage, Building> Print =
                AccessTools.MethodDelegate<System.Action<SectionLayer_BuildingsDamage, Building>>(
                    AccessTools.Method(typeof(SectionLayer_BuildingsDamage), "PrintDamageVisualsFrom"));
        }

        private sealed class TurretTopDamage : System.IDisposable
        {
            private readonly SectionLayer_BuildingsDamage nativeLayer;
            private readonly Rect damageRect;
            private Texture2D bodyTexture;
            private Texture2D decalTexture;
            private Material decalMaterial;

            internal TurretTopDamage(Section sourceSection, Building building)
            {
                damageRect = BuildingsDamageSectionLayerUtility.GetDamageRect(building);
                nativeLayer = new SectionLayer_BuildingsDamage(sourceSection);
                try { BuildingDamagePrinter.Print(nativeLayer, building); }
                catch
                {
                    nativeLayer.Dispose();
                    throw;
                }
            }

            internal Material MaterialFor(Texture2D texture)
            {
                if (ReferenceEquals(texture, bodyTexture)) return decalMaterial;
                ReleaseDecal();
                bodyTexture = texture;
                if (texture == null || nativeLayer.subMeshes.Count == 0
                    || damageRect.width <= 0f || damageRect.height <= 0f) return null;

                try
                {
                    TexturePixels body = TopDamageBodyPixels.GetValue(texture,
                        source => ReadDamagePixels(source, TopDamageResolution));
                    int size = TopDamageResolution;
                    var pixels = new Color32[size * size];
                    foreach (LayerSubMesh submesh in nativeLayer.subMeshes)
                    {
                        if (submesh.disabled || submesh.material?.mainTexture is not Texture2D scratch) continue;
                        TexturePixels source = TopDamageScratchPixels.GetValue(scratch,
                            value => ReadDamagePixels(value, ScratchAtlasReadLimit));
                        Color32 tint = submesh.material.color;
                        // Vanilla's damage printer emits independent Printer_Plane quads.
                        for (int corner = 0; corner + 3 < submesh.verts.Count; corner += 4)
                            RasterizeTopDamageQuad(pixels, size, damageRect, submesh, corner, source, tint);
                    }
                    if (!MaskTopDamage(pixels, size, body)) return null;
                    decalTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    decalTexture.SetPixels32(pixels);
                    decalTexture.Apply(false, true);
                    decalMaterial = new Material(ShaderDatabase.Transparent) { mainTexture = decalTexture };
                    return decalMaterial;
                }
                catch (System.Exception error)
                {
                    ReleaseDecal();
                    Log.ErrorOnce($"[DeltaV] Cannot mask turret damage with {texture.name}: {error}",
                        texture.GetInstanceID() ^ 0x5A31D3);
                    return null;
                }
            }

            private void ReleaseDecal()
            {
                if (decalMaterial != null) Object.Destroy(decalMaterial);
                if (decalTexture != null) Object.Destroy(decalTexture);
                decalMaterial = null;
                decalTexture = null;
            }

            public void Dispose()
            {
                ReleaseDecal();
                nativeLayer.Dispose();
            }
        }

        private sealed class TexturePixels
        {
            private readonly Color32[] pixels;
            private readonly int width, height;

            internal TexturePixels(Color32[] pixels, int width, int height)
            {
                this.pixels = pixels;
                this.width = width;
                this.height = height;
            }

            internal Color32 Sample(Vector2 uv)
            {
                float x = Mathf.Clamp(uv.x * width - 0.5f, 0f, width - 1);
                float y = Mathf.Clamp(uv.y * height - 0.5f, 0f, height - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Mathf.Min(x0 + 1, width - 1), y1 = Mathf.Min(y0 + 1, height - 1);
                Color32 bottom = Color32.Lerp(pixels[y0 * width + x0], pixels[y0 * width + x1], x - x0);
                Color32 top = Color32.Lerp(pixels[y1 * width + x0], pixels[y1 * width + x1], x - x0);
                return Color32.Lerp(bottom, top, y - y0);
            }
        }

        private sealed class MapCache : System.IDisposable
        {
            public readonly MapSectionCache<SectionSnapshot> snapshots = new MapSectionCache<SectionSnapshot>();
            private readonly Map map;
            private readonly MapEvents events;
            private Section printContext;

            public MapCache(Map map)
            {
                this.map = map;
                events = map.events;
                if (events == null) return;
                events.DoorOpened += OnDoorChanged;
                events.DoorClosed += OnDoorChanged;
            }

            public Section PrintContext()
                => printContext ??= new Section(IntVec3.Zero, map);

            public void MarkSectionsDirty(CellRect area)
            {
                area = area.ClipInsideMap(map);
                for (int z = area.minZ / SectionSize; z <= area.maxZ / SectionSize; z++)
                    for (int x = area.minX / SectionSize; x <= area.maxX / SectionSize; x++)
                        snapshots.MarkDirty(x, z);
            }

            private void OnDoorChanged(Building_Door door) =>
                MarkSectionsDirty(door.OccupiedRect().ExpandedBy(1));

            public void Dispose()
            {
                if (events != null)
                {
                    events.DoorOpened -= OnDoorChanged;
                    events.DoorClosed -= OnDoorChanged;
                }
                snapshots.Dispose();
            }
        }

        private static readonly Dictionary<Map, MapCache> Caches = new Dictionary<Map, MapCache>();
        private static readonly Dictionary<ThingDef, ShellRenderRuleDef> Rules = new Dictionary<ThingDef, ShellRenderRuleDef>();
        private static readonly Dictionary<TerrainDef, Material> TerrainMaterials = new Dictionary<TerrainDef, Material>();
        private static ConditionalWeakTable<Texture2D, TexturePixels> TopDamageBodyPixels =
            new ConditionalWeakTable<Texture2D, TexturePixels>();
        private static ConditionalWeakTable<Texture2D, TexturePixels> TopDamageScratchPixels =
            new ConditionalWeakTable<Texture2D, TexturePixels>();
        private static readonly Mesh[] CellMeshes = new Mesh[TexturePeriod * TexturePeriod];
        private static readonly FieldInfo DrawerMapField = AccessTools.Field(typeof(MapDrawer), "map");
        private static Texture2D powerConnectorWireTexture;
        private static int cachedRuleCount = -1;

        /// <summary>最近一次 CollectShellCommands 收集的四边形数（诊断用；
        /// 静态网格按三角形数估算，实时图形按绘制命令计数）。</summary>
        internal static int QuadsDrawnLastPass { get; private set; }

        /// <summary>缓冲绘制命令：mesh + 完整 TRS 矩阵 + 材质及可选逐命令属性；
        /// surface/queue/sequence 供壳内排序。</summary>
        internal struct OverlayCommand
        {
            public RimExodus.Maps.MapDrawCommand? shared;
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public MaterialPropertyBlock props;
            public int submeshIndex;
            public ShellSurfaceLayer surface;
            public bool damageOverlay;
            public int queue;
            public int sequence;
        }

        private static readonly List<OverlayCommand> Commands = new List<OverlayCommand>(512);
        private static int nextSequence;
        private static int deckTerrainCommands, hullTerrainCommands;

        /// <summary>
        /// 收集一壳的全部四边形（地形/建筑/角盖板）到待发命令表。返回是否有可见内容；
        /// 调用方随后可经 AddFlameCommand 追加火焰，再交由绘制接收器同步消费。
        /// </summary>
        public static bool CollectShellCommands(Map map, Matrix4x4 matrix, Quaternion rotation,
            CellRect localView, HashSet<IntVec3> allowedCells = null, Vector2 localSun = default)
        {
            QuadsDrawnLastPass = 0;
            Commands.Clear();
            nextSequence = 0;
            deckTerrainCommands = hullTerrainCommands = 0;
            if (map == null || map.Disposed
                || Current.Game?.GetComponent<UniverseManager>()?.Generation?.IsGenerating(map) == true) return false;
            // A field can extend beyond its projector's section and map bounds.
            var structure = ShipStructureIndex.Read(map);
            if (structure != null)
                foreach (CompProjectileInterceptor field in structure.All.Interceptors)
                {
                    if (field.parent.TryGetComp<CompSpaceShield>() == null
                        || allowedCells != null && !allowedCells.Contains(field.parent.Position)) continue;
                    ShellDrawCapture.Capture(field.PostDraw, matrix, ShellSurfaceLayer.Exposed, 0f);
                }
            if (!localView.Overlaps(CellRect.WholeMap(map))) return Commands.Count > 0;
            EnsureRules();
            CellRect visible = localView.ClipInsideMap(map);
            int minSx = visible.minX / SectionSize;
            int maxSx = visible.maxX / SectionSize;
            int minSz = visible.minZ / SectionSize;
            int maxSz = visible.maxZ / SectionSize;
            var performance = Current.Game?.GetComponent<UniverseManager>()?.Performance;
            for (int sz = minSz; sz <= maxSz; sz++)
            for (int sx = minSx; sx <= maxSx; sx++)
                CollectSection(map, matrix, rotation, visible, allowedCells, sx, sz, localSun, Commands, performance);
            return Commands.Count > 0;
        }

        /// <summary>火焰命令追加（ThrusterEffects 调用）：queue 取材质 renderQueue
        /// （火焰材质 3201，经排序天然位于壳内地形 3000 之上）；props 必须是本命令
        /// 独占的池化块，保证后端同步消费时每条命令的属性独立。</summary>
        internal static void AddFlameCommand(Mesh mesh, Matrix4x4 matrix, Material material, MaterialPropertyBlock props)
        {
            Commands.Add(new OverlayCommand
            {
                mesh = mesh,
                matrix = matrix,
                material = material,
                props = props,
                surface = ShellSurfaceLayer.Effects,
                queue = material.renderQueue,
                sequence = nextSequence++
            });
        }

        /// <summary>单四边形杂项命令：
        /// 独占整条命令表——不经整壳收集，直接装载一条后由调用方 Emit 到目标层槽位。</summary>
        internal static void CollectSingleQuad(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            BeginQuadBatch();
            AddQuadToBatch(mesh, matrix, material);
        }

        internal static void BeginQuadBatch()
        {
            Commands.Clear();
            nextSequence = 0;
        }

        internal static void AddQuadToBatch(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            Commands.Add(new OverlayCommand
            {
                mesh = mesh,
                matrix = matrix,
                material = material,
                queue = material.renderQueue,
                sequence = nextSequence++
            });
        }

        /// <summary>
        /// 按 (surface, renderQueue, sequence) 排序后整批同步交给绘制后端。
        /// 当前命令表会被下一次收集复用，火焰属性块也在帧内池化；后端不得保留命令引用。
        /// 表面层与整壳间的深度处理由后端实现，内容收集不依赖具体绘制 API。
        /// </summary>
        internal static void EmitShellCommands(IShellDrawBackend backend, bool mapShell)
        {
            if (Commands.Count == 0) return;
            var performance = Current.Game?.GetComponent<UniverseManager>()?.Performance;
            performance?.RecordOverlayCommands(Commands.Count);
            long sortStart = performance?.Start() ?? 0;
            Commands.Sort(CompareCommands);
            long submitStart = performance?.Start() ?? 0;
            backend.DrawSorted(Commands);
            if (sortStart != 0)
            {
                long submit = performance.Elapsed(submitStart);
                long sort = submitStart - sortStart;
                performance.RecordElapsed(PerformanceStage.OverlaySort, sort);
                performance.RecordElapsed(PerformanceStage.OverlaySubmit, submit);
                if (mapShell)
                {
                    performance.RecordShellCommands(Commands.Count, deckTerrainCommands, hullTerrainCommands);
                    performance.RecordElapsed(PerformanceStage.ShellOverlaySort, sort);
                    performance.RecordElapsed(PerformanceStage.ShellOverlaySubmit, submit);
                }
            }
        }

        private static int CompareCommands(OverlayCommand left, OverlayCommand right)
        {
            int surfaceComparison = left.surface.CompareTo(right.surface);
            if (surfaceComparison != 0) return surfaceComparison;
            int damageComparison = left.damageOverlay.CompareTo(right.damageOverlay);
            if (damageComparison != 0) return damageComparison;
            int queueComparison = left.queue.CompareTo(right.queue);
            return queueComparison != 0 ? queueComparison : left.sequence.CompareTo(right.sequence);
        }

        private static void CollectSection(Map map, Matrix4x4 matrix, Quaternion rotation, CellRect visible,
            HashSet<IntVec3> allowedCells, int sx, int sz, Vector2 localSun,
            List<OverlayCommand> collector, DeltaVPerformanceProbe performance)
        {
            MapCache cache = CacheFor(map);
            bool rebuild = !cache.snapshots.TryGet(sx, sz, out SectionSnapshot section);
            performance?.RecordShellSection(rebuild);
            if (rebuild)
            {
                section = RebuildSection(cache, map, sx, sz, performance);
            }

            long staticStart = performance?.Start() ?? 0;
            if (BatchStaticTerrain && allowedCells == null)
            {
                if (section.terrain != null)
                {
                    CollectTerrainBatches(section.terrain.Deck, matrix, ShellSurfaceLayer.Deck, collector);
                    CollectTerrainBatches(section.terrain.Hull, matrix, ShellSurfaceLayer.Hull, collector);
                }
                foreach (IntVec3 cell in section.moonPoolCells)
                    if (visible.Contains(cell)) CollectMoonPool(map, cell, matrix, rotation, collector);
            }
            else
            {
                foreach (IntVec3 cell in section.terrainCells)
                {
                    if (!visible.Contains(cell) || allowedCells != null && !allowedCells.Contains(cell)) continue;
                    bool moonPool = TerrainMaterialsAt(map, cell, out Material deck, out Material cover);
                    if (moonPool) CollectMoonPool(map, cell, matrix, rotation, collector);
                    if (deck == null && cover == null) continue;
                    var center = new Vector3(cell.x + 0.5f, ShellTerrainBatches.Altitude, cell.z + 0.5f);
                    Matrix4x4 cellMatrix = Matrix4x4.TRS(matrix.MultiplyPoint3x4(center), rotation, Vector3.one);
                    Mesh mesh = CellMeshFor(cell);
                    if (deck != null) AddTerrainCommand(mesh, cellMatrix, deck, ShellSurfaceLayer.Deck, 1, collector);
                    if (cover != null) AddTerrainCommand(mesh, cellMatrix, cover, ShellSurfaceLayer.Hull, 1, collector);
                }
            }

            CollectLayer(section.substructureProps, matrix, ShellSurfaceLayer.Hull, collector);

            AddHullEdgeCommands(section.hullEdges?.floor, matrix, localSun, true, collector);
            AddHullCornerCommands(section.hullEdges?.corners, matrix, localSun, collector, outer: false);

            foreach (PrintBatch batch in section.printed)
            {
                Matrix4x4 lift = Matrix4x4.Translate(new Vector3(0f,
                    batch.zIndex * Altitudes.AltInc, 0f));
                bool damage = batch.layer is SectionLayer_BuildingsDamage;
                CollectLayer(batch.layer, matrix * lift, batch.surface, collector,
                    damageOverlay: damage, filterThingMaterials: !damage);
            }

            AddHullEdgeCommands(section.hullEdges?.slopes, matrix, localSun, false, collector);
            AddHullCornerCommands(section.hullEdges?.outerCorners, matrix, localSun, collector, outer: true);
            if (section.roofEdges != null)
            {
                Material material = ShellRoofEdges.Material;
                collector.Add(new OverlayCommand { mesh = section.roofEdges, matrix = matrix,
                    material = material, surface = ShellSurfaceLayer.Hull,
                    queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += section.roofEdges.vertexCount / 4;
            }

            // Odyssey draws angled hull pieces in its own section layer, not in
            // GravshipHull.Print. Reuse that layer's mesh and neighbor rules.
            CollectLayer(section.gravshipHull, matrix, ShellSurfaceLayer.Hull, collector);
            AddHullEdgeCommands(section.hullEdges?.angledSlopes, matrix, localSun, false,
                collector, angled: true);

            performance?.Record(PerformanceStage.ShellStatic, staticStart);
            using var dynamicTiming = performance?.Measure(PerformanceStage.ShellDynamic) ?? default;
            for (int i = 0; i < section.things.Count; i++)
            {
                Thing thing = section.things[i];
                bool deck = section.deckThings.Contains(thing);
                if (!ShouldDrawThing(thing, deck) || !visible.Contains(thing.Position)
                    || allowedCells != null && !allowedCells.Contains(thing.Position)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                ShellRenderConfig config = rule?.render;
                if (config?.shadowHeight > 0f)
                    CollectExteriorShadow(thing, config.shadowHeight, matrix, localSun, collector);
                if (config?.source == ShellGraphicSource.TurretTop)
                {
                    if (deck && thing.def.drawerType != DrawerType.MapMeshOnly
                        && thing.def.drawerType != DrawerType.None)
                        ShellDrawCapture.Capture(() => thing.Graphic.Draw(thing.DrawPos, thing.Rotation, thing),
                            matrix, ShellSurfaceLayer.Deck, 0f);
                    TurretTopDamage damage = null;
                    section.turretTopDamage?.TryGetValue(thing, out damage);
                    CollectTurretTop(thing, config, matrix, rotation, damage, collector);
                    // A projector's dome is a comp effect, separate from its turret top.
                    CompProjectileInterceptor interceptor = thing.TryGetComp<CompProjectileInterceptor>();
                    if (interceptor != null && thing.TryGetComp<CompSpaceShield>() == null)
                        ShellDrawCapture.Capture(interceptor.PostDraw, matrix, config.layer, config.zIndex);
                    continue;
                }
                if (deck) config = null;
                if (config?.alternateGraphics != null || thing.def.drawerType == DrawerType.MapMeshOnly
                    || thing.def.drawerType == DrawerType.None) continue;
                try
                {
                    ShellDrawCapture.Capture(() => thing.DrawNowAt(thing.DrawPos), matrix,
                        deck ? ShellSurfaceLayer.Deck : config?.layer ?? ShellSurfaceLayer.Hull,
                        config?.zIndex ?? 0f);
                }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce($"[DeltaV] Cannot capture shell draw for {thing}: {ex}",
                        thing.thingIDNumber ^ 0x5A31D0);
                }
            }
        }

        private static void AddHullEdgeCommands(Mesh[] meshes, Matrix4x4 matrix, Vector2 localSun,
            bool floor, List<OverlayCommand> collector, bool angled = false)
        {
            if (meshes == null) return;
            for (int side = 0; side < 4; side++)
            {
                Mesh mesh = meshes[side];
                if (mesh == null) continue;
                float incidence = HullIncidence(localSun, side, floor, angled);
                Material material = ShellHullVisuals.EdgeMaterial;
                collector.Add(new OverlayCommand { mesh = mesh, matrix = matrix,
                    material = material, props = ShellHullVisuals.EdgeTint(incidence, floor),
                    surface = ShellSurfaceLayer.Hull,
                    queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += mesh.vertexCount / 4;
            }
        }

        private static void AddHullCornerCommands(Mesh[] meshes, Matrix4x4 matrix,
            Vector2 localSun, List<OverlayCommand> collector, bool outer)
        {
            if (meshes == null) return;
            for (int corner = 0; corner < 4; corner++)
            {
                Mesh mesh = meshes[corner];
                if (mesh == null) continue;
                for (int axis = 0; axis < 2; axis++)
                {
                    int side = axis == 0 ? ShellHullVisuals.CornerSideX(corner)
                        : ShellHullVisuals.CornerSideZ(corner);
                    Material material = ShellHullVisuals.CornerMaterial(axis, outer);
                    collector.Add(new OverlayCommand { mesh = mesh, matrix = matrix,
                        material = material,
                        props = ShellHullVisuals.EdgeTint(HullIncidence(localSun, side, !outer), !outer),
                        surface = ShellSurfaceLayer.Hull,
                        queue = material.renderQueue, sequence = nextSequence++ });
                    QuadsDrawnLastPass += mesh.vertexCount / 4;
                }
            }
        }

        private static float HullIncidence(Vector2 localSun, int side, bool floor, bool angled = false)
        {
            Vector2 normal = angled ? ShellHullVisuals.AngledNormal(side) : ShellHullVisuals.SideNormal(side);
            float incidence = Vector2.Dot(localSun, normal);
            // A floor edge points toward the wall: light behind it darkens the floor.
            return floor ? -incidence : incidence;
        }



        internal static void AddSharedDraw(RimExodus.Maps.MapDrawCommand command, ShellSurfaceLayer surface)
        {
            if (command.UsesMaterial(MatBases.SunShadow) || command.UsesMaterial(MatBases.SunShadowFade)) return;
            Commands.Add(new OverlayCommand { shared = command, surface = surface,
                queue = command.RenderQueue, sequence = nextSequence++ });
            QuadsDrawnLastPass++;
        }

        private static bool IsSunShadowMaterial(Material material)
            => material == MatBases.SunShadow || material == MatBases.SunShadowFade;

        private static void CollectExteriorShadow(Thing thing, float height, Matrix4x4 matrix,
            Vector2 localSun, List<OverlayCommand> collector)
        {
            Material material = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Transparent,
                new Color(0f, 0f, 0f, .35f));
            for (int side = 0; side < 2; side++)
            {
                if (!ShellExteriorShadow.TryMatrix(thing.OccupiedRect(), height, localSun, side,
                    out Matrix4x4 local)) continue;
                collector.Add(new OverlayCommand { mesh = MeshPool.plane10, matrix = matrix * local,
                    material = material, surface = ShellSurfaceLayer.ExteriorShadow,
                    queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
            }
        }

        private static void CollectTurretTop(Thing thing, ShellRenderConfig config, Matrix4x4 matrix,
            Quaternion rotation, TurretTopDamage damage, List<OverlayCommand> collector)
        {
            IShellTurretTopSource source = SpaceTurretTopRenderer.SourceFor(thing);
            if (source == null || thing.def.building == null) return;
            Material material = (config.alternateGraphics?.Count == 1
                    ? config.alternateGraphics[0].GraphicColoredFor(thing).MatSingleFor(thing) : null)
                ?? source.ShellTopMaterial;
            if (material == null) return;
            Vector3 recoil = thing.TryGetComp<CompSpaceWeapon>()?.VisualRecoilOffset ?? Vector3.zero;
            Matrix4x4 topMatrix = SpaceTurretTopRenderer.MatrixFor(thing, source, thing.DrawPos,
                recoil, 0f, matrix, rotation, config.zIndex);
            collector.Add(new OverlayCommand
            {
                mesh = MeshPool.plane10,
                matrix = topMatrix,
                material = material,
                surface = config.layer,
                queue = material.renderQueue,
                sequence = nextSequence++
            });
            QuadsDrawnLastPass++;
            Material decal = damage?.MaterialFor(material.mainTexture as Texture2D);
            if (decal == null) return;
            collector.Add(new OverlayCommand
            {
                mesh = MeshPool.plane10,
                matrix = topMatrix * Matrix4x4.Translate(Altitudes.AltIncVect),
                material = decal,
                surface = config.layer,
                queue = decal.renderQueue,
                damageOverlay = true,
                sequence = nextSequence++
            });
            QuadsDrawnLastPass++;
        }

        public static bool ContainsVisibleContent(Map map, IntVec3 cell, HashSet<IntVec3> allowedCells = null)
        {
            EnsureRules();
            if (map == null || !cell.InBounds(map) || allowedCells != null && !allowedCells.Contains(cell)) return false;
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain != null && !terrain.dontRender) return true;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
                if (ShouldDrawThing(things[i], HasExposedDeck(things[i]))) return true;
            return false;
        }

        /// <summary>
        /// 结构外观纳入标准（所有 SpaceObject 统一，2026-09-18 用户决策）：动态物剔除
        /// → 开放甲板建筑 → 显式 exclude → 实心结构（墙/门/气密建筑/自然岩石，判定收口
        /// SpaceStructureRules.IsSolidStructure）→ 显式规则。碰撞占用另纳入全部建筑，
        /// 物品仍隐藏，非气密建筑（家具、内部设备）只在屋顶缺口处显示。
        /// </summary>
        private static bool ShouldDrawThing(Thing thing, bool exposedDeck = false)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing is Pawn || thing is Projectile
                || thing is Mote || thing.def.category == ThingCategory.Filth
                || thing.def.category == ThingCategory.Gas || thing.def.category == ThingCategory.Mote) return false;
            Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
            if (exposedDeck && thing is Building) return true;
            if (rule?.exclude == true) return false;
            if ((thing is Building building && ShellHullVisuals.IsWallSegment(building))
                || SpaceStructureRules.IsSolidStructure(thing)) return true;
            return rule != null;
        }

        private static bool HasExposedDeck(Thing thing)
        {
            if (thing is not Building building || !thing.Spawned || thing.Destroyed) return false;
            Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
            // Exterior structures keep their existing art. Interior exclusions and
            // turret bases become visible through roof openings like other equipment.
            if (rule?.exclude != true && rule?.render?.source != ShellGraphicSource.TurretTop
                && (ShellHullVisuals.IsWallSegment(building) || SpaceStructureRules.IsSolidStructure(thing)
                    || rule != null)) return false;
            foreach (IntVec3 cell in thing.OccupiedRect())
                if (SubstructureOwnership.HasFoundation(thing.Map, cell)
                    && !ShellHullVisuals.HasShellRoof(thing.Map, cell)) return true;
            return false;
        }

        private static bool UsesDamageOverlay(Building building) =>
            building.def.useHitPoints && building.def.drawDamagedOverlay
            && building.def.graphicData?.damageData?.enabled != false;

        private static bool HasDamageOverlayBuildingAt(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return false;
            foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                if (thing.Position == cell && thing is Building building && UsesDamageOverlay(building))
                    return true;
            return false;
        }

        private static void CollectLayer(MapDrawLayer layer, Matrix4x4 matrix, ShellSurfaceLayer surface,
            List<OverlayCommand> collector, bool damageOverlay = false, bool filterThingMaterials = false)
        {
            if (layer == null) return;
            foreach (LayerSubMesh submesh in layer.subMeshes)
            {
                if (!submesh.finalized || submesh.disabled) continue;
                if (filterThingMaterials && (IsSunShadowMaterial(submesh.material)
                    || IsPowerConnectorWireMaterial(submesh.material))) continue;
                collector.Add(new OverlayCommand { mesh = submesh.mesh, matrix = matrix,
                    material = submesh.material, surface = surface, damageOverlay = damageOverlay,
                    queue = submesh.material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += submesh.tris.Count / 6;
            }
        }

        // CompPower prints appliance-to-conduit wires into the building's static layer.
        private static bool IsPowerConnectorWireMaterial(Material material)
        {
            powerConnectorWireTexture ??= ContentFinder<Texture2D>.Get("Things/Special/Power/Wire", false);
            return powerConnectorWireTexture != null && material.mainTexture == powerConnectorWireTexture;
        }

        private static void CompleteLayer(MapDrawLayer layer)
        {
            if (layer == null) return;
            foreach (LayerSubMesh submesh in layer.subMeshes)
                if (submesh.verts.Count > 0) submesh.FinalizeMesh(MeshParts.All);
        }

        private static void CollectTerrainBatches(IReadOnlyList<LayerSubMesh> batches, Matrix4x4 matrix,
            ShellSurfaceLayer surface, List<OverlayCommand> collector)
        {
            for (int i = 0; i < batches.Count; i++)
            {
                LayerSubMesh batch = batches[i];
                AddTerrainCommand(batch.mesh, matrix, batch.material, surface, batch.tris.Count / 6, collector);
            }
        }

        private static void AddTerrainCommand(Mesh mesh, Matrix4x4 matrix, Material material,
            ShellSurfaceLayer surface, int quads, List<OverlayCommand> collector)
        {
            collector.Add(new OverlayCommand { mesh = mesh, matrix = matrix, material = material,
                surface = surface, queue = material.renderQueue, sequence = nextSequence++ });
            if (surface == ShellSurfaceLayer.Deck) deckTerrainCommands++;
            else hullTerrainCommands++;
            QuadsDrawnLastPass += quads;
        }

        private static void CollectMoonPool(Map map, IntVec3 cell, Matrix4x4 matrix,
            Quaternion rotation, List<OverlayCommand> collector)
        {
            Vector3 fieldPos = matrix.MultiplyPoint3x4(new Vector3(cell.x + 0.5f,
                ShellTerrainBatches.Altitude, cell.z + 0.5f));
            AddPlane(MoonPoolGraphics.FieldMaterial, fieldPos);
            AddPlane(MoonPoolGraphics.RimMaterialFor(map, cell),
                fieldPos + rotation * new Vector3(0f, Altitudes.AltInc, 0f));

            void AddPlane(Material material, Vector3 position)
            {
                if (material == null || material == BaseContent.BadMat) return;
                collector.Add(new OverlayCommand { mesh = MeshPool.plane10,
                    matrix = Matrix4x4.TRS(position, rotation, Vector3.one),
                    material = material, queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
            }
        }

        private static bool TerrainSurfacesAt(Map map, IntVec3 cell, out TerrainDef deck, out TerrainDef cover)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            deck = terrain != null && !terrain.dontRender ? terrain : null;
            cover = deck == null ? ShellHullVisuals.RoofSurfaceTerrain(map, cell)
                : ShellHullVisuals.HasShellRoof(map, cell) ? deck : null;
            return terrain?.dontRender == true && terrain == MoonPoolUtility.MoonPoolTerrain;
        }

        private static bool TerrainMaterialsAt(Map map, IntVec3 cell, out Material deck, out Material cover)
        {
            bool moonPool = TerrainSurfacesAt(map, cell, out TerrainDef deckTerrain, out TerrainDef coverTerrain);
            deck = MaterialFor(deckTerrain);
            cover = deckTerrain != null ? (coverTerrain != null ? deck : null) : MaterialFor(coverTerrain);
            return moonPool;
        }

        private static SectionSnapshot RebuildSection(MapCache cache, Map map, int sx, int sz,
            DeltaVPerformanceProbe performance)
        {
            using var timing = performance?.Measure(PerformanceStage.ShellRebuild) ?? default;
            return cache.snapshots.Rebuild(sx, sz, () => BuildSection(cache, map, sx, sz, performance));
        }

        private static SectionSnapshot BuildSection(MapCache cache, Map map, int sx, int sz,
            DeltaVPerformanceProbe performance)
        {
            var result = new SectionSnapshot();
            try
            {
                PopulateSection(result, cache, map, sx, sz, performance);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void PopulateSection(SectionSnapshot result, MapCache cache, Map map, int sx, int sz,
            DeltaVPerformanceProbe performance)
        {
            CellRect rect = CellRect.FromLimits(sx * SectionSize, sz * SectionSize,
                sx * SectionSize + SectionSize - 1, sz * SectionSize + SectionSize - 1).ClipInsideMap(map);
            foreach (IntVec3 cell in rect)
            {
                result.terrainCells.Add(cell);
                List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                for (int i = 0; i < things.Count; i++)
                    if (things[i].Position == cell && !result.things.Contains(things[i])) result.things.Add(things[i]);
            }
            // Only the SectionLayer material/mesh collector is used; no source-map layer is redrawn.
            Section sourceSection = cache.PrintContext();
            if (BatchStaticTerrain)
            {
                using var terrainTiming = performance?.Measure(PerformanceStage.ShellTerrainBuild) ?? default;
                foreach (IntVec3 cell in result.terrainCells)
                {
                    if (TerrainMaterialsAt(map, cell, out Material deck, out Material cover))
                        result.moonPoolCells.Add(cell);
                    if (deck == null && cover == null) continue;
                    (result.terrain ??= new ShellTerrainBatches(sourceSection)).Add(cell, deck, cover);
                }
                result.terrain?.Complete();
            }
            SectionLayer_ThingsGeneral SharedLayer(ShellSurfaceLayer surface, float zIndex)
            {
                PrintBatch batch = result.printed.Find(candidate => candidate.surface == surface
                    && candidate.zIndex == zIndex && candidate.shared);
                if (batch != null) return (SectionLayer_ThingsGeneral)batch.layer;
                var layer = new SectionLayer_ThingsGeneral(sourceSection);
                result.printed.Add(new PrintBatch
                    { surface = surface, zIndex = zIndex, shared = true, layer = layer });
                return layer;
            }

            void PrintDamage(Building building, ShellSurfaceLayer surface, float zIndex)
            {
                PrintBatch batch = result.printed.Find(candidate => candidate.surface == surface
                    && candidate.zIndex == zIndex && candidate.layer is SectionLayer_BuildingsDamage);
                var layer = (SectionLayer_BuildingsDamage)batch?.layer;
                if (layer == null)
                {
                    layer = new SectionLayer_BuildingsDamage(sourceSection);
                    result.printed.Add(new PrintBatch { surface = surface, zIndex = zIndex, layer = layer });
                }
                BuildingDamagePrinter.Print(layer, building);
            }

            // Keep floor contacts and the joins between angled and straight slopes
            // consistent across section boundaries.
            CellRect neighborhood = rect.ExpandedBy(1).ClipInsideMap(map);
            bool odyssey = ModsConfig.OdysseyActive;
            var angles = odyssey ? ShellHullVisuals.FindAngledCorners(map, neighborhood)
                : new Dictionary<IntVec3, int>();
            var wallAdjacency = ShellHullVisuals.AnalyzeWalls(map, neighborhood, angles);
            foreach (Thing thing in result.things)
            {
                bool deck = HasExposedDeck(thing);
                if (deck) result.deckThings.Add(thing);
                if (!ShouldDrawThing(thing, deck)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                ShellRenderConfig config = rule?.render;
                bool turretBase = config?.source == ShellGraphicSource.TurretTop;
                Building damaged = thing is Building building && UsesDamageOverlay(building)
                    && building.HitPoints < building.MaxHitPoints ? building : null;
                if (turretBase && !deck)
                {
                    if (damaged != null)
                        (result.turretTopDamage ??= new Dictionary<Thing, TurretTopDamage>())
                            .Add(damaged, new TurretTopDamage(sourceSection, damaged));
                    continue;
                }
                if (deck) config = null;
                if (config?.alternateGraphics == null && thing.def.drawerType == DrawerType.None) continue;
                ShellSurfaceLayer surface = deck ? ShellSurfaceLayer.Deck : config?.layer ?? ShellSurfaceLayer.Hull;
                float zIndex = config?.zIndex ?? 0f;
                if (damaged != null) PrintDamage(damaged, surface, zIndex);
                if (config?.alternateGraphics == null && thing.def.drawerType == DrawerType.RealtimeOnly) continue;
                if (thing.def == ThingDefOf.GravshipHull
                    && wallAdjacency.TryGetValue(thing.Position, out ShellHullVisuals.Adjacency wall))
                {
                    Material material = ShellHullVisuals.WallMaterial(thing, wall);
                    if (material != null)
                    {
                        ShellHullVisuals.PrintWall(SharedLayer(surface, zIndex), thing, wall, material);
                        continue;
                    }
                }
                if (config?.alternateGraphics != null)
                {
                    foreach (GraphicData graphic in config.alternateGraphics)
                    {
                        var replacement = new PrintBatch { surface = surface, zIndex = zIndex,
                            layer = new SectionLayer_ThingsGeneral(sourceSection) };
                        result.printed.Add(replacement);
                        try { graphic.GraphicColoredFor(thing).Print(replacement.layer, thing, 0f); }
                        catch (System.Exception ex)
                        {
                            Log.ErrorOnce($"[DeltaV] Cannot print shell replacement for {thing}: {ex}",
                                thing.thingIDNumber ^ 0x5A31D2);
                        }
                    }
                    continue;
                }
                SectionLayer_ThingsGeneral layer = SharedLayer(surface, zIndex);
                try
                {
                    if (turretBase) thing.Graphic.Print(layer, thing, 0f);
                    else thing.Print(layer);
                }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce($"[DeltaV] Cannot print shell graphic for {thing}: {ex}",
                        thing.thingIDNumber ^ 0x5A31D1);
                }
            }
            result.hullEdges = ShellHullVisuals.BuildEdges(map, rect, wallAdjacency, angles,
                () => SharedLayer(ShellSurfaceLayer.Hull, 0f));
            foreach (PrintBatch batch in result.printed) CompleteLayer(batch.layer);
            result.roofEdges = ShellRoofEdges.Build(map, rect);
            if (odyssey)
            {
                result.gravshipHull = new SectionLayer_ThingsGeneral(sourceSection);
                result.substructureProps = new SectionLayer_ThingsGeneral(sourceSection);
                ShellHullVisuals.PrintAngles(map, rect, result.gravshipHull);
                ShellHullVisuals.PrintFoundationEdges(map, rect, result.substructureProps);
                CompleteLayer(result.gravshipHull);
                CompleteLayer(result.substructureProps);
            }
        }

        private static TexturePixels ReadDamagePixels(Texture2D source, int maxSize)
        {
            int longestSide = Mathf.Max(source.width, source.height);
            if (longestSide <= maxSize && source.isReadable && source.format == TextureFormat.RGBA32)
                return new TexturePixels(source.GetPixels32(), source.width, source.height);

            float scale = Mathf.Min(1f, (float)maxSize / longestSide);
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return new TexturePixels(copy.GetPixels32(), width, height);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (copy != null) Object.Destroy(copy);
            }
        }

        private static void RasterizeTopDamageQuad(Color32[] pixels, int size, Rect damageRect,
            LayerSubMesh mesh, int corner, TexturePixels source, Color32 tint)
        {
            Vector2 Project(Vector3 vertex) => new Vector2(
                (vertex.x - damageRect.xMin) * size / damageRect.width,
                (vertex.z - damageRect.yMin) * size / damageRect.height);

            Vector2 bottomLeft = Project(mesh.verts[corner]);
            Vector2 topLeft = Project(mesh.verts[corner + 1]);
            Vector2 topRight = Project(mesh.verts[corner + 2]);
            Vector2 bottomRight = Project(mesh.verts[corner + 3]);
            Vector2 right = bottomRight - bottomLeft;
            Vector2 up = topLeft - bottomLeft;
            float determinant = right.x * up.y - right.y * up.x;
            if (Mathf.Abs(determinant) < 0.0001f) return;
            float inverse = 1f / determinant;
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(Mathf.Min(bottomLeft.x, topLeft.x),
                Mathf.Min(topRight.x, bottomRight.x))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(Mathf.Min(bottomLeft.y, topLeft.y),
                Mathf.Min(topRight.y, bottomRight.y))));
            int maxX = Mathf.Min(size, Mathf.CeilToInt(Mathf.Max(Mathf.Max(bottomLeft.x, topLeft.x),
                Mathf.Max(topRight.x, bottomRight.x))));
            int maxY = Mathf.Min(size, Mathf.CeilToInt(Mathf.Max(Mathf.Max(bottomLeft.y, topLeft.y),
                Mathf.Max(topRight.y, bottomRight.y))));
            Vector2 uvBase = mesh.uvs[corner];
            Vector2 uvRight = (Vector2)mesh.uvs[corner + 3] - uvBase;
            Vector2 uvUp = (Vector2)mesh.uvs[corner + 1] - uvBase;
            for (int y = minY; y < maxY; y++)
            for (int x = minX; x < maxX; x++)
            {
                float dx = x + 0.5f - bottomLeft.x;
                float dy = y + 0.5f - bottomLeft.y;
                float u = (dx * up.y - dy * up.x) * inverse;
                float v = (right.x * dy - right.y * dx) * inverse;
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                Color32 scratch = source.Sample(uvBase + u * uvRight + v * uvUp);
                int index = y * size + x;
                pixels[index] = BlendTopDamage(pixels[index], scratch, tint);
            }
        }

        private static Color32 BlendTopDamage(Color32 underneath, Color32 scratch, Color32 tint)
        {
            const float channel = 1f / 255f;
            float overAlpha = scratch.a * tint.a * channel * channel;
            if (overAlpha <= 0f) return underneath;
            float underAlpha = underneath.a * channel * (1f - overAlpha);
            float alpha = overAlpha + underAlpha;
            float blend = overAlpha / alpha;
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(underneath.r, scratch.r * tint.r * channel, blend)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(underneath.g, scratch.g * tint.g * channel, blend)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(underneath.b, scratch.b * tint.b * channel, blend)),
                (byte)Mathf.RoundToInt(alpha * 255f));
        }

        private static bool MaskTopDamage(Color32[] pixels, int size, TexturePixels body)
        {
            bool visible = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a == 0) continue;
                var uv = new Vector2((i % size + 0.5f) / size, (i / size + 0.5f) / size);
                pixels[i].a = (byte)((pixels[i].a * body.Sample(uv).a + 127) / 255);
                visible |= pixels[i].a != 0;
            }
            return visible;
        }

        public static void MarkDirty(Map map, IntVec3 cell, ulong flags,
            bool adjacentCells = false, bool adjacentSections = false)
        {
            ulong relevant = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Roofs;
            ulong damage = (ulong)MapMeshFlagDefOf.BuildingsDamage;
            if (Patch_ShipHullRendering.MarkingFocusedSections || map == null || (flags & (relevant | damage)) == 0
                || !Caches.TryGetValue(map, out MapCache cache)) return;
            if ((flags & relevant) == 0 && !HasDamageOverlayBuildingAt(map, cell)) return;
            int sx = cell.x / SectionSize;
            int sz = cell.z / SectionSize;
            cache.snapshots.MarkDirty(sx, sz);
            // A wall or foundation can change the neighboring corner, whose
            // inset joins then extend one more cell into another section.
            ulong angleFlags = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings;
            int radius = (flags & angleFlags) != 0 ? 2 : 1;
            cache.MarkSectionsDirty(CellRect.CenteredOn(cell, radius));
            // The native mesh callback also runs during spawn, before the structure batch is published.
            // Consult just this cell to invalidate the section containing a covered building's root.
            if ((flags & ((ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.Terrain)) != 0
                && cell.InBounds(map))
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                    if (thing is Building)
                        cache.snapshots.MarkDirty(thing.Position.x / SectionSize, thing.Position.z / SectionSize);
            if (adjacentSections)
            {
                for (int z = sz - 1; z <= sz + 1; z++)
                for (int x = sx - 1; x <= sx + 1; x++)
                    if (x >= 0 && z >= 0) cache.snapshots.MarkDirty(x, z);
            }
        }

        internal static void MarkHullSectionDirty(Map map, IntVec3 section)
        {
            if (Caches.TryGetValue(map, out var cache)) cache.snapshots.MarkDirty(section.x, section.z);
        }

        public static Map MapOf(MapDrawer drawer) => DrawerMapField?.GetValue(drawer) as Map;

        public static void Release(Map map)
        {
            GuardShieldRenderer.Release(map);
            if (map == null || !Caches.TryGetValue(map, out MapCache cache)) return;
            cache.Dispose();
            Caches.Remove(map);
        }

        public static void ClearRuntimeCaches()
        {
            GuardShieldRenderer.Clear();
            foreach (MapCache cache in Caches.Values)
                cache.Dispose();
            Caches.Clear();
            Rules.Clear();
            TerrainMaterials.Clear();
            TopDamageBodyPixels = new ConditionalWeakTable<Texture2D, TexturePixels>();
            TopDamageScratchPixels = new ConditionalWeakTable<Texture2D, TexturePixels>();
            powerConnectorWireTexture = null;
            ShellHullVisuals.ClearRuntimeCaches();
            ShellRoofEdges.ClearRuntimeCaches();
            cachedRuleCount = -1;
            for (int i = 0; i < CellMeshes.Length; i++)
            {
                if (CellMeshes[i] != null) Object.Destroy(CellMeshes[i]);
                CellMeshes[i] = null;
            }
        }

        private static MapCache CacheFor(Map map)
        {
            if (!Caches.TryGetValue(map, out MapCache cache)) Caches[map] = cache = new MapCache(map);
            return cache;
        }

        private static Material MaterialFor(TerrainDef terrain)
        {
            // dontRender terrains keep vanilla's BadGraphic: never copy that
            // placeholder into an apparently valid shell material.
            if (terrain == null || terrain.dontRender || terrain.graphic == BaseContent.BadGraphic
                || terrain.DrawMatSingle?.mainTexture == null) return null;
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
            int x = ShellTerrainBatches.TextureCoordinate(cell.x);
            int z = ShellTerrainBatches.TextureCoordinate(cell.z);
            int index = x + z * TexturePeriod;
            if (CellMeshes[index] != null) return CellMeshes[index];
            Mesh source = MeshPool.plane10;
            Vector3[] vertices = source.vertices;
            var uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                uv[i] = ShellTerrainBatches.CellUv(cell, vertices[i]);
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
            foreach (MapCache cache in Caches.Values) cache.snapshots.Clear();
            Rules.Clear();
            for (int i = 0; i < defs.Count; i++) if (defs[i].thingDef != null) Rules[defs[i].thingDef] = defs[i];
            cachedRuleCount = defs.Count;
        }
    }
}

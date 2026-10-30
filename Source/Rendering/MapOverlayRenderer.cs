using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    /// 岩石自动入壳）、ShellRenderRuleDef 显式规则。建筑的静态层由原版 Print
    /// 生成，实时层在限定作用域内采集原版 DrawMesh；连接图与 Comp 附加层共用此路径。
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
            public readonly List<PrintBatch> printed = new List<PrintBatch>();
            public SectionLayer_GravshipHull gravshipHull;
            public SectionLayer_SubstructureProps substructureProps;
            public ShellHullVisuals.EdgeMeshes hullEdges;

            public void Dispose()
            {
                foreach (PrintBatch batch in printed) batch.layer.Dispose();
                gravshipHull?.Dispose();
                substructureProps?.Dispose();
                hullEdges?.Dispose();
            }
        }

        private sealed class PrintBatch
        {
            public ShellSurfaceLayer surface;
            public float zIndex;
            public bool shared;
            public SectionLayer_ThingsGeneral layer;

            public void Complete()
            {
                foreach (LayerSubMesh submesh in layer.subMeshes)
                    if (submesh.verts.Count > 0) submesh.FinalizeMesh(MeshParts.All);
            }
        }

        private sealed class MapCache
        {
            public readonly Dictionary<int, SectionSnapshot> sections = new Dictionary<int, SectionSnapshot>();
            public readonly HashSet<int> dirty = new HashSet<int>();
            private Section printContext;

            public Section PrintContext(Map map)
                => printContext ??= new Section(IntVec3.Zero, map);
        }

        private static readonly Dictionary<Map, MapCache> Caches = new Dictionary<Map, MapCache>();
        private static readonly Dictionary<ThingDef, ShellRenderRuleDef> Rules = new Dictionary<ThingDef, ShellRenderRuleDef>();
        private static readonly Dictionary<TerrainDef, Material> TerrainMaterials = new Dictionary<TerrainDef, Material>();
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
            public int queue;
            public int sequence;
        }

        private static readonly List<OverlayCommand> Commands = new List<OverlayCommand>(512);
        private static int nextSequence;

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
            if (map == null || map.Disposed
                || Current.Game?.GetComponent<UniverseManager>()?.Generation?.IsGenerating(map) == true) return false;
            if (!localView.Overlaps(CellRect.WholeMap(map))) return false;
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
        internal static void EmitShellCommands(IShellDrawBackend backend)
        {
            if (Commands.Count == 0) return;
            var performance = Current.Game?.GetComponent<UniverseManager>()?.Performance;
            performance?.RecordOverlayCommands(Commands.Count);
            using (performance?.Measure(PerformanceStage.OverlaySort) ?? default)
                Commands.Sort(CompareCommands);
            using (performance?.Measure(PerformanceStage.OverlaySubmit) ?? default)
                backend.DrawSorted(Commands);
        }

        private static int CompareCommands(OverlayCommand left, OverlayCommand right)
        {
            int surfaceComparison = left.surface.CompareTo(right.surface);
            if (surfaceComparison != 0) return surfaceComparison;
            int queueComparison = left.queue.CompareTo(right.queue);
            return queueComparison != 0 ? queueComparison : left.sequence.CompareTo(right.sequence);
        }

        private static void CollectSection(Map map, Matrix4x4 matrix, Quaternion rotation, CellRect visible,
            HashSet<IntVec3> allowedCells, int sx, int sz, Vector2 localSun,
            List<OverlayCommand> collector, DeltaVPerformanceProbe performance)
        {
            int key = sx | (sz << 16);
            MapCache cache = CacheFor(map);
            bool dirty = cache.dirty.Remove(key);
            bool rebuild = !cache.sections.TryGetValue(key, out SectionSnapshot section) || dirty;
            performance?.RecordShellSection(rebuild);
            if (rebuild)
            {
                using var rebuildTiming = performance?.Measure(PerformanceStage.ShellRebuild) ?? default;
                section?.Dispose();
                section = BuildSection(map, sx, sz);
                cache.sections[key] = section;
            }

            long staticStart = performance?.Start() ?? 0;
            for (int i = 0; i < section.terrainCells.Count; i++)
            {
                IntVec3 cell = section.terrainCells[i];
                if (!visible.Contains(cell) || allowedCells != null && !allowedCells.Contains(cell)) continue;
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain == null || terrain.dontRender)
                {
                    // 月池：地形本体 dontRender（透出宇宙），力场走墙壁式连接图
                    //（MoonPoolGraphics 唯一材质入口；外缘包边/中心力场按邻接自动取片）。
                    if (terrain == MoonPoolUtility.MoonPoolTerrain)
                    {
                        Vector3 fieldPos = matrix.MultiplyPoint3x4(new Vector3(cell.x + 0.5f,
                            AltitudeLayer.Terrain.AltitudeFor(), cell.z + 0.5f));
                        Material fieldMaterial = MoonPoolGraphics.FieldMaterial;
                        if (fieldMaterial != null && fieldMaterial != BaseContent.BadMat)
                        {
                            collector.Add(new OverlayCommand
                            {
                                mesh = MeshPool.plane10,
                                matrix = Matrix4x4.TRS(fieldPos, rotation, Vector3.one),
                                material = fieldMaterial,
                                queue = fieldMaterial.renderQueue,
                                sequence = nextSequence++
                            });
                            QuadsDrawnLastPass++;
                        }
                        Material rimMaterial = MoonPoolGraphics.RimMaterialFor(map, cell);
                        if (rimMaterial != null && rimMaterial != BaseContent.BadMat)
                        {
                            Vector3 rimPos = fieldPos + rotation * new Vector3(0f, Altitudes.AltInc, 0f);
                            collector.Add(new OverlayCommand
                            {
                                mesh = MeshPool.plane10,
                                matrix = Matrix4x4.TRS(rimPos, rotation, Vector3.one),
                                material = rimMaterial,
                                queue = rimMaterial.renderQueue,
                                sequence = nextSequence++
                            });
                            QuadsDrawnLastPass++;
                        }
                    }
                    continue;
                }
                Material material = MaterialFor(terrain);
                if (material == null) continue;
                Vector3 source = new Vector3(cell.x + 0.5f, AltitudeLayer.Terrain.AltitudeFor(), cell.z + 0.5f);
                collector.Add(new OverlayCommand { mesh = CellMeshFor(cell), matrix = Matrix4x4.TRS(matrix.MultiplyPoint3x4(source), rotation, Vector3.one), material = material, queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass++;
            }

            if (section.substructureProps != null)
            foreach (LayerSubMesh submesh in section.substructureProps.subMeshes)
            {
                if (!submesh.finalized || submesh.disabled) continue;
                collector.Add(new OverlayCommand { mesh = submesh.mesh, matrix = matrix,
                    material = submesh.material, surface = ShellSurfaceLayer.Hull,
                    queue = submesh.material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += submesh.tris.Count / 6;
            }

            AddHullEdgeCommands(section.hullEdges?.floor, matrix, localSun, true, collector);
            AddHullCornerCommands(section.hullEdges?.corners, matrix, localSun, collector);

            foreach (PrintBatch batch in section.printed)
            foreach (LayerSubMesh submesh in batch.layer.subMeshes)
            {
                if (!submesh.finalized || submesh.disabled) continue;
                if (IsSunShadowMaterial(submesh.material)) continue;
                // CompPower.PostPrintOnto adds the short appliance-to-conduit
                // wire to the same static layer as the appliance itself.
                powerConnectorWireTexture ??= ContentFinder<Texture2D>.Get(
                    "Things/Special/Power/Wire", false);
                if (powerConnectorWireTexture != null
                    && submesh.material.mainTexture == powerConnectorWireTexture) continue;
                Matrix4x4 lift = Matrix4x4.Translate(new Vector3(0f,
                    batch.zIndex * Altitudes.AltInc, 0f));
                collector.Add(new OverlayCommand { mesh = submesh.mesh, matrix = matrix * lift,
                    material = submesh.material, surface = batch.surface,
                    queue = submesh.material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += submesh.tris.Count / 6;
            }

            AddHullEdgeCommands(section.hullEdges?.slopes, matrix, localSun, false, collector);

            // Odyssey draws angled hull pieces in its own section layer, not in
            // GravshipHull.Print. Reuse that layer's mesh and neighbor rules.
            if (section.gravshipHull != null)
            foreach (LayerSubMesh submesh in section.gravshipHull.subMeshes)
            {
                if (!submesh.finalized || submesh.disabled) continue;
                collector.Add(new OverlayCommand { mesh = submesh.mesh, matrix = matrix,
                    material = submesh.material, surface = ShellSurfaceLayer.Hull,
                    queue = submesh.material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += submesh.tris.Count / 6;
            }

            performance?.Record(PerformanceStage.ShellStatic, staticStart);
            using var dynamicTiming = performance?.Measure(PerformanceStage.ShellDynamic) ?? default;
            for (int i = 0; i < section.things.Count; i++)
            {
                Thing thing = section.things[i];
                if (!ShouldDrawThing(thing) || !visible.Contains(thing.Position)
                    || allowedCells != null && !allowedCells.Contains(thing.Position)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                ShellRenderConfig config = rule?.render;
                if (config?.source == ShellGraphicSource.TurretTop)
                {
                    CollectTurretTop(thing, config, matrix, rotation, collector);
                    continue;
                }
                if (config?.alternateGraphics != null || thing.def.drawerType == DrawerType.MapMeshOnly
                    || thing.def.drawerType == DrawerType.None) continue;
                try
                {
                    ShellDrawCapture.Capture(() => thing.DrawNowAt(thing.DrawPos), matrix,
                        config?.layer ?? ShellSurfaceLayer.Hull, config?.zIndex ?? 0f);
                }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce($"[DeltaV] Cannot capture shell draw for {thing}: {ex}",
                        thing.thingIDNumber ^ 0x5A31D0);
                }
            }
        }

        private static void AddHullEdgeCommands(Mesh[] meshes, Matrix4x4 matrix, Vector2 localSun,
            bool floor, List<OverlayCommand> collector)
        {
            if (meshes == null) return;
            for (int side = 0; side < 4; side++)
            {
                Mesh mesh = meshes[side];
                if (mesh == null) continue;
                float incidence = HullIncidence(localSun, side, floor);
                Material material = ShellHullVisuals.EdgeMaterial;
                collector.Add(new OverlayCommand { mesh = mesh, matrix = matrix,
                    material = material, props = ShellHullVisuals.EdgeTint(incidence, floor),
                    surface = ShellSurfaceLayer.Hull,
                    queue = material.renderQueue, sequence = nextSequence++ });
                QuadsDrawnLastPass += mesh.vertexCount / 4;
            }
        }

        private static void AddHullCornerCommands(Mesh[] meshes, Matrix4x4 matrix,
            Vector2 localSun, List<OverlayCommand> collector)
        {
            if (meshes == null) return;
            for (int corner = 0; corner < 4; corner++)
            {
                Mesh mesh = meshes[corner];
                if (mesh == null) continue;
                // UV axes follow the horizontal and vertical walls bordering
                // this floor cell. Both use the same signed light as straight seams.
                int sideA = corner < 2 ? 1 : 3;
                int sideB = corner == 0 || corner == 3 ? 0 : 2;
                for (int axis = 0; axis < 2; axis++)
                {
                    int side = axis == 0 ? sideA : sideB;
                    Material material = ShellHullVisuals.CornerMaterial(axis);
                    collector.Add(new OverlayCommand { mesh = mesh, matrix = matrix,
                        material = material,
                        props = ShellHullVisuals.EdgeTint(HullIncidence(localSun, side, true), true),
                        surface = ShellSurfaceLayer.Hull,
                        queue = material.renderQueue, sequence = nextSequence++ });
                    QuadsDrawnLastPass += mesh.vertexCount / 4;
                }
            }
        }

        private static float HullIncidence(Vector2 localSun, int side, bool floor)
        {
            float incidence = side switch
            {
                0 => localSun.y,
                1 => localSun.x,
                2 => -localSun.y,
                _ => -localSun.x
            };
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

        private static void CollectTurretTop(Thing thing, ShellRenderConfig config,
            Matrix4x4 matrix, Quaternion rotation, List<OverlayCommand> collector)
        {
            if (thing is not IShellTurretTopSource source || thing.def.building == null) return;
            Material material = (config.alternateGraphics?.Count == 1
                    ? config.alternateGraphics[0].GraphicColoredFor(thing).MatSingleFor(thing) : null)
                ?? source.ShellTopMaterial;
            if (material == null) return;
            Vector2 offset = thing.def.building.turretTopOffset;
            Vector3 local = thing.DrawPos + Altitudes.AltIncVect
                + new Vector3(offset.x, config.zIndex * Altitudes.AltInc, offset.y);
            if (thing is ThingWithComps comps)
                local += comps.TryGetComp<CompSpaceWeapon>()?.VisualRecoilOffset ?? Vector3.zero;
            float angle = TurretTop.ArtworkRotation + source.ShellTopAngle;
            float size = thing.def.building.turretTopDrawSize;
            collector.Add(new OverlayCommand
            {
                mesh = MeshPool.plane10,
                matrix = Matrix4x4.TRS(matrix.MultiplyPoint3x4(local), rotation * angle.ToQuat(),
                    new Vector3(size, 1f, size)),
                material = material,
                surface = config.layer,
                queue = material.renderQueue,
                sequence = nextSequence++
            });
            QuadsDrawnLastPass++;
            for (int part = 0; part < 2; part++)
                if (WeaponHeatVisuals.TryPart(thing as ThingWithComps, material, part, out Mesh heatMesh, out Material heatMaterial))
                    collector.Add(new OverlayCommand { mesh = heatMesh,
                        matrix = Matrix4x4.TRS(matrix.MultiplyPoint3x4(local), rotation * angle.ToQuat(), new Vector3(size, 1f, size)),
                        material = heatMaterial, surface = config.layer, queue = heatMaterial.renderQueue, sequence = nextSequence++ });
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
        /// → 显式 exclude 最优先 → 实心结构（墙/门/气密建筑/自然岩石，判定收口
        /// SpaceStructureRules.IsSolidStructure）→ 显式规则。碰撞占用另纳入全部建筑，
        /// 但物品与非气密建筑（家具、内部设备）不因此进入外壳。
        /// </summary>
        private static bool ShouldDrawThing(Thing thing)
        {
            if (thing?.Spawned == true && !thing.Destroyed && SpaceScatterService.IsCargo(thing)
                && Current.Game?.GetComponent<UniverseManager>()?.FrameOf(thing.Map)?.IsScattered == true) return true;
            if (thing == null || thing.Destroyed || !thing.Spawned || thing is Pawn || thing is Projectile
                || thing is Mote || thing.def.category == ThingCategory.Filth
                || thing.def.category == ThingCategory.Gas || thing.def.category == ThingCategory.Mote) return false;
            Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
            if (rule?.exclude == true) return false;
            if (SpaceStructureRules.IsSolidStructure(thing)) return true;
            return rule != null;
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
            // Only the SectionLayer material/mesh collector is used; no source-map layer is redrawn.
            Section sourceSection = CacheFor(map).PrintContext(map);
            // One-cell halo lets a floor contact in another section read the same wall
            // adjacency as the wall's own linked variant.
            var wallAdjacency = new Dictionary<IntVec3, ShellHullVisuals.Adjacency>();
            foreach (IntVec3 cell in rect.ExpandedBy(1).ClipInsideMap(map))
            {
                Thing wall = cell.GetEdificeSafe(map);
                if (wall?.def == ThingDefOf.GravshipHull)
                    wallAdjacency[cell] = ShellHullVisuals.Analyze(map, wall);
            }
            foreach (Thing thing in result.things)
            {
                if (!ShouldDrawThing(thing)) continue;
                Rules.TryGetValue(thing.def, out ShellRenderRuleDef rule);
                ShellRenderConfig config = rule?.render;
                if (config?.source == ShellGraphicSource.TurretTop) continue;
                if (config?.alternateGraphics == null && (thing.def.drawerType == DrawerType.RealtimeOnly
                    || thing.def.drawerType == DrawerType.None)) continue;
                ShellSurfaceLayer surface = config?.layer ?? ShellSurfaceLayer.Hull;
                float zIndex = config?.zIndex ?? 0f;
                if (thing.def == ThingDefOf.GravshipHull
                    && wallAdjacency.TryGetValue(thing.Position, out ShellHullVisuals.Adjacency wall))
                {
                    Material material = ShellHullVisuals.WallMaterial(thing, wall);
                    if (material != null)
                    {
                        PrintBatch beam = result.printed.Find(candidate => candidate.surface == ShellSurfaceLayer.Hull
                            && candidate.zIndex == 0f && candidate.shared);
                        if (beam == null)
                        {
                            beam = new PrintBatch { surface = ShellSurfaceLayer.Hull, shared = true,
                                layer = new SectionLayer_ThingsGeneral(sourceSection) };
                            result.printed.Add(beam);
                        }
                        Printer_Plane.PrintPlane(beam.layer, thing.TrueCenter(), Vector2.one, material);
                        ShellHullVisuals.PrintCornerFill(beam.layer, thing, wall, material);
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
                PrintBatch batch = result.printed.Find(candidate => candidate.surface == surface
                    && candidate.zIndex == zIndex && candidate.shared);
                if (batch == null)
                {
                    batch = new PrintBatch { surface = surface, zIndex = zIndex,
                        shared = true, layer = new SectionLayer_ThingsGeneral(sourceSection) };
                    result.printed.Add(batch);
                }
                try { thing.Print(batch.layer); }
                catch (System.Exception ex)
                {
                    Log.ErrorOnce($"[DeltaV] Cannot print shell graphic for {thing}: {ex}",
                        thing.thingIDNumber ^ 0x5A31D1);
                }
            }
            foreach (PrintBatch batch in result.printed) batch.Complete();
            result.hullEdges = ShellHullVisuals.BuildEdges(map, rect, wallAdjacency);
            if (ModsConfig.OdysseyActive)
            {
                // Corner cells can be empty and are absent from result.things.
                // Use the source map's section coordinates so vanilla evaluates
                // the same 17x17 cells as its focused-map layer.
                result.gravshipHull = new SectionLayer_GravshipHull(map.mapDrawer.SectionAt(rect.Min));
                result.gravshipHull.Regenerate();
                result.substructureProps = new SectionLayer_SubstructureProps(
                    map.mapDrawer.SectionAt(rect.Min));
                result.substructureProps.Regenerate();
            }
            return result;
        }

        public static void MarkDirty(Map map, IntVec3 cell, ulong flags,
            bool adjacentCells = false, bool adjacentSections = false)
        {
            ulong relevant = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Roofs;
            if (map == null || (flags & relevant) == 0) return;
            MapCache cache = CacheFor(map);
            int sx = cell.x / SectionSize;
            int sz = cell.z / SectionSize;
            cache.dirty.Add(sx | (sz << 16));
            // Wall/foundation contact affects adjoining floor meshes; Odyssey's
            // angled corner pieces also read diagonal cells. Mark the whole ring
            // even when vanilla did not request adjacent section regeneration.
            for (int i = 0; i < 8; i++)
            {
                IntVec3 neighbor = cell + GenAdj.AdjacentCells[i];
                if (neighbor.InBounds(map))
                    cache.dirty.Add(neighbor.x / SectionSize | (neighbor.z / SectionSize << 16));
            }
            if (adjacentSections)
            {
                for (int z = sz - 1; z <= sz + 1; z++)
                for (int x = sx - 1; x <= sx + 1; x++)
                    if (x >= 0 && z >= 0) cache.dirty.Add(x | (z << 16));
            }
        }

        public static Map MapOf(MapDrawer drawer) => DrawerMapField?.GetValue(drawer) as Map;

        public static void Release(Map map)
        {
            if (map == null || !Caches.TryGetValue(map, out MapCache cache)) return;
            foreach (SectionSnapshot section in cache.sections.Values) section.Dispose();
            Caches.Remove(map);
        }

        public static void ClearRuntimeCaches()
        {
            foreach (MapCache cache in Caches.Values)
                foreach (SectionSnapshot section in cache.sections.Values) section.Dispose();
            Caches.Clear();
            Rules.Clear();
            WeaponHeatVisuals.Clear();
            TerrainMaterials.Clear();
            powerConnectorWireTexture = null;
            ShellHullVisuals.ClearRuntimeCaches();
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
            foreach (MapCache cache in Caches.Values)
            {
                foreach (SectionSnapshot section in cache.sections.Values) section.Dispose();
                cache.sections.Clear();
                cache.dirty.Clear();
            }
            Rules.Clear();
            for (int i = 0; i < defs.Count; i++) if (defs[i].thingDef != null) Rules[defs[i].thingDef] = defs[i];
            cachedRuleCount = defs.Count;
        }
    }
}

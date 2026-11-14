using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 距离策略；地图生成、休眠和父对象语义分别委托通用服务。
    ///
    /// **POI 容器与物化容器彻底分离（2026-09-25 用户定案）**：
    /// - marker（未物化 PoiState）= 无图触发器：按设置的类型显示图标/名称，接近后
    ///   物化——产物是**普通 SpaceObject 容器**（无 POI 图标身份，名称 = 图上结构
    ///   拼接、ambientLabel 兜底纯环境）。
    /// - 物化容器（普通 SpaceObject）：MustStayActive（玩家侧）恒活跃；离开活跃
    ///   距离休眠，超删除距离清理小行星和零碎残骸，其他地图整图归档。
    /// - NativeParent 型 PoiState 例外：map 字段跟随原版 MapParent 镜像
    ///   （SynchronizeNative/UpdateFromParent），保留原版图标与生命周期。
    /// </summary>
    public sealed partial class SpaceLifecycleService
    {
        public const int PoiMapSize = 90;
        private const int SmallWreckMaxSupportCells = 64;

        private static float DeleteRadius => Mathf.Max(DeltaVMod.CurrentSettings.poiDeleteRadius,
            DeltaVMod.CurrentSettings.poiActiveRadius + 1f);

        /// <summary>物化失败后的退避期（tick）：期内 EnsureActive 不再重试，防止每 60 tick
        /// 无限重生成 + pocketMaps 死 parent 积累。</summary>
        private const int GenerationFailureBackoffTicks = 2500;

        private readonly UniverseManager universe;
        private readonly MapDormancyService dormancy = new MapDormancyService();

        /// <summary>物化失败退避（marker.objectId → 可重试 tick；运行时不存档）。</summary>
        private readonly Dictionary<int, int> generationRetryAfterTick = new Dictionary<int, int>();

        public SpaceLifecycleService(UniverseManager universe)
        {
            this.universe = universe;
        }

        public bool IsDormant(Map map) => dormancy.IsDormant(map);
        public bool IsFrozen(Map map) => IsDormant(map) || IsUniverseHost(map)
            || universe.MapPool.IsIdle(map)
            || universe.Generation?.IsGenerating(map) == true;
        public static bool IsUniverseHost(Map map) => map?.Parent is UniverseHostParent_DeltaV;
        public void Wake(Map map, string reason)
        {
            if (universe.SpaceObjectOnMap(map)?.IsScattered != true) dormancy.Wake(map, reason);
        }
        internal void FreezeScattered(Map map) => dormancy.FreezeScattered(map);

        public void Tick()
        {
            OrbitalPoiRegistry.Synchronize(universe);
            float activeRadius = DeltaVMod.CurrentSettings.poiActiveRadius;
            float deleteRadius = DeleteRadius;
            foreach (SpaceObject obj in universe.objects.ToList())
            {
                if (obj is PoiState poi)
                {
                    TickPoiState(poi, activeRadius, deleteRadius);
                }
                else
                {
                    TickMaterializedContainer(obj, activeRadius, deleteRadius);
                }
            }
        }

        /// <summary>marker（无图 SnapshotPocket）按距离物化；NativeParent 跟随 vanilla。</summary>
        private void TickPoiState(PoiState poi, float activeRadius, float deleteRadius)
        {
            SynchronizeNative(poi);
            // 悬死自愈（2026-09-25）：物化 NativeParent 的图被外部销毁 → SynchronizeNative
            // 已清 map（POI 回到无图态，原版语义下等待重物化）。SnapshotPocket 恒无图
            //（有图残留 = 旧档非法形态，读档转换负责）。
            float distance = MinPlayerDistance(poi);
            if (float.IsPositiveInfinity(distance)) return;
            string key = GenerationKey(poi);
            universe.Generation.UpdatePriority(key, distance);
            if (distance > activeRadius) universe.Generation.CancelQueued(key);
            if (poi.map != null && SpaceMapGovernance.MustStayActive(poi.map, universe))
            {
                dormancy.Wake(poi.map, "protected map");
                return;
            }
            if (IsPendingGeneration(poi)) return;
            if (distance <= activeRadius) EnsureActive(poi, distance);
            else if (distance <= deleteRadius) EnsureDormant(poi);
            else TryPack(poi);
        }

        /// <summary>
        /// 物化容器（普通 SpaceObject）：舰船、空间站与拆分残骸统一使用距离生命周期。
        /// </summary>
        private void TickMaterializedContainer(SpaceObject container, float activeRadius, float deleteRadius)
        {
            Map map = container.map;
            if (map == null)
            {
                CheckArchivedProximity(container);
                return;
            }
            if (IsUniverseHost(map)) return;
            if (map.Parent is not ShipPocketParent_DeltaV) return;
            // 在飞弹体图按 tag 豁免距离休眠/归档（太空战架构 §3.2）：弹体生命周期由
            // 弹体服务（超时/碰撞结算）独占管理，生命周期扫描不得收纳/销毁在飞弹体的图
            //（图池空闲图的豁免同样见弹体服务）。
            if (container.HasTag(SpaceObjectTag.Projectile) == true) return;
            // 悬死自愈：图被外部销毁 → 容器随图消亡。
            if (map.Disposed || !Find.Maps.Contains(map))
            {
                container.map = null;
                universe.UnregisterObject(container);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{container.label}: container's map was destroyed externally; object removed");
                return;
            }
            if (container.IsScattered)
            {
                float scatteredDistance = MinPlayerDistance(container);
                if (!float.IsPositiveInfinity(scatteredDistance) && scatteredDistance > deleteRadius)
                    universe.Scatter.RetireLost(container);
                return;
            }
            if (!HasMapSupport(container))
            {
                QueueStructureCheck(map);
                return;
            }
            if (SpaceMapGovernance.MustStayActive(map, universe))
            {
                dormancy.Wake(map, "protected map");
                return;
            }
            float distance = MinPlayerDistance(container);
            if (float.IsPositiveInfinity(distance)) return;
            if (distance <= activeRadius)
            {
                dormancy.Wake(map, "inside load radius");
                return;
            }
            if (distance <= deleteRadius)
            {
                dormancy.Sleep(map, "outside load radius");
                return;
            }
            ArchiveContainer(container, map);
        }

        public float MinPlayerDistance(SpaceObject obj)
            => MinPlayerDistance(obj, universe.PlayerReferenceObjects());

        private static float MinPlayerDistance(SpaceObject obj, List<SpaceObject> references)
        {
            float best = float.PositiveInfinity;
            foreach (SpaceObject reference in references)
            {
                best = Mathf.Min(best,
                    OrbitSphere.ArcCells(reference.pose.position, obj.pose.position));
            }
            return best;
        }

        internal static float MapRetentionScore(int area, float distance, float radius) =>
            Mathf.Sqrt(Mathf.Max(1, area)) / (1f + distance / Mathf.Max(1f, radius));

        private static float CapacityScore(int area, float distance) =>
            MapRetentionScore(area, distance, DeltaVMod.CurrentSettings.poiActiveRadius);

        internal float ScatterCapacityScore(SpacePose pose)
        {
            float distance = float.PositiveInfinity;
            foreach (SpaceObject reference in universe.PlayerReferenceObjects())
                distance = Mathf.Min(distance, OrbitSphere.ArcCells(reference.pose.position, pose.position));
            return MapRetentionScore(1, distance, DeltaVMod.CurrentSettings.poiActiveRadius);
        }

        private static void SynchronizeNative(PoiState poi)
        {
            if (poi.kind != PoiKind.NativeParent || poi.nativeParent == null) return;
            poi.map = poi.nativeParent.Map;
            if (poi.nativeParent.Tile.Valid)
                poi.pose.position = OrbitSphere.TileCenterDirection(poi.nativeParent.Tile);
        }

        private void EnsureActive(PoiState poi, float priority = 0f)
        {
            if (poi.map != null)
            {
                dormancy.Wake(poi.map, "inside POI load radius");
                return;
            }
            // 物化失败退避期内不重试（防每 60 tick 无限重生成）。
            if (generationRetryAfterTick.TryGetValue(poi.objectId, out int retryAfter)
                && Find.TickManager.TicksGame < retryAfter)
            {
                return;
            }
            if (poi.kind == PoiKind.NativeParent) QueueNativeGeneration(poi, priority);
            else QueueSnapshotGeneration(poi, priority);
        }

        private void QueueNativeGeneration(PoiState poi, float priority)
        {
            MapParent parent = poi.nativeParent;
            if (parent == null || parent.Destroyed || parent.HasMap) return;
            IntVec3 size = SpaceMapGovernance.GenerationSize(parent);
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = size, generator = parent.MapGeneratorDef,
                capacityScore = distance => CapacityScore(size.x * size.z, distance),
                extraGenSteps = parent.ExtraGenStepDefs, isPocketMap = false,
                onComplete = map =>
                {
                    poi.map = map;
                    OrbitalPoiRegistry.EnsureForMap(universe, map);
                    dormancy.Wake(map, "generation complete");
                    Messages.Message("DeltaV_PoiActivated".Translate(poi.label), MessageTypeDefOf.NeutralEvent, false);
                },
                onFailure = ex => ReportGenerationFailure(poi.label, ex)
            });
        }

        private void QueueSnapshotGeneration(PoiState marker, float priority)
        {
            // 物化目标图尺寸随快照几何走（快照格是源图绝对坐标，目标图小于源图时
            // 越界写经 CellToIndex 静默回卷坏图——2026-09-24 事故）；无快照 marker
            //（首次物化）按选定 prefab 尺寸（+边距）。池不可用（空/失效）时不入队：
            // 报错并取消该 POI（prefab-only 定案）——生成期已拦截池空，此处兜配置
            // 事后变更的存量 marker。
            IntVec3 size;
            if (marker.snapshot != null && marker.snapshot.HasSourceSize)
            {
                size = marker.snapshot.SourceMapSize;
            }
            else if (TryGetPrefabPoolSize(marker, out IntVec3 prefabSize))
            {
                size = prefabSize;
            }
            else
            {
                Log.Error($"[DeltaV] {marker.label}: content type '{marker.contentTypeId}' has no usable " +
                    "prefab pool (empty or unloadable); POI canceled instead of materialized.");
                universe.UnregisterObject(marker);
                return;
            }
            Map host = universe.GetOrCreateHostMap(CapacityScore(size.x * size.z, priority));
            if (host == null) return;
            var parent = (ShipPocketParent_DeltaV)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = host;
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(marker), priority = priority, parent = parent,
                size = size, generator = DeltaVDefOf.DeltaV_ShipPocket,
                capacityScore = distance => CapacityScore(size.x * size.z, distance),
                isPocketMap = true,
                onComplete = map =>
                {
                    Find.World.pocketMaps.Add(parent);
                    // 孤儿图防护：排队期间 marker 被注销（注销入口会取消排队，此处双
                    // 保险）→ 丢弃本次生成的图，不给死实例赋 map。
                    if (universe.ObjectById(marker.objectId) != marker)
                    {
                        DestroyPocketMap(map);
                        return;
                    }
                    MaterializeMarker(marker, map);
                },
                onFailure = ex =>
                {
                    // onComplete 开头的 pocketMaps.Add 不会回滚：onComplete 中途抛出时
                    // parent 已入 pocketMaps 而 CleanupFailed 只 Deinit 图——显式摘除，
                    // 防止重试积累死 parent。
                    if (Find.World.pocketMaps.Contains(parent))
                    {
                        Find.World.pocketMaps.Remove(parent);
                    }
                    ReportGenerationFailure(marker.label, ex);
                    if (!(ex is OperationCanceledException) && universe.ObjectById(marker.objectId) == marker)
                    {
                        generationRetryAfterTick[marker.objectId] =
                            Find.TickManager.TicksGame + GenerationFailureBackoffTicks;
                    }
                }
            });
        }

        /// <summary>
        /// Publish the same POI as an ordinary map container before stamping content.
        /// Pilot console claim and prefab marker expansion both resolve FrameOf(map).
        /// </summary>
        private void MaterializeMarker(PoiState marker, Map map)
        {
            var container = new MapSpaceObject(marker.objectId, map, marker.pose);
            marker.CopyFactsTo(container);
            container.baseTag = SpaceObjectTag.Container;
            container.ambientLabel = marker.envLabel;
            universe.ReplaceRepresentation(marker, container);
            generationRetryAfterTick.Remove(marker.objectId);

            int ownerId = marker.objectId;
            string poolPath = null;
            try
            {
                if (!marker.envLabel.NullOrEmpty())
                    universe.NoteStructureName(ownerId, marker.envLabel);
                if (marker.snapshot != null)
                {
                    marker.snapshot.ApplyTo(map, ownerId);
                }
                else
                {
                    poolPath = ResolvePoolPrefabPath(marker);
                    if (poolPath == null)
                        throw new InvalidOperationException($"Content type '{marker.contentTypeId}' has no usable prefab pool.");
                    MapPrefabDocument doc = MapPrefab.LoadRelative(poolPath);
                    Rot4 rotation = new Rot4(marker.seed & 3);
                    MapPrefab.Spawn(doc, map, MapPrefab.CenteredPosition(doc, map, rotation),
                        marker.faction, ownerId, rotation, universe, blankSpace: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[DeltaV] {marker.label} #{container.objectId}: materialization canceled " +
                    $"({poolPath ?? (marker.snapshot != null ? "snapshot" : "prefab pool")}): {ex}");
                // FinalizeInit may still have queued drawing setup for this map.
                universe.QueueFrameAction(() =>
                {
                    if (universe.SpaceObjectOnMap(map) == null) DestroyPocketMap(map);
                });
                universe.UnregisterObject(container);
                return;
            }

            universe.NotifyObjectMaterialized(container);
            dormancy.Wake(map, "generation complete");
            Messages.Message((marker.snapshot != null ? "DeltaV_PoiRestored" : "DeltaV_PoiActivated")
                .Translate(container.label), MessageTypeDefOf.NeutralEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{container.label}: POI #{container.objectId} materialized ({UniverseManager.MapTag(map)})");
        }

        /// <summary>无快照 marker 首次物化时的类型 prefab 池目标图边距（格）。</summary>

        /// <summary>
        /// 无快照 marker 的类型 prefab 池路径（seed 确定性解析；与盖章同一算法，
        /// 保证入队测尺寸与最终放置取到同一份）。类型未注册或池为空/全失效返回 null。
        /// </summary>
        private static string ResolvePoolPrefabPath(PoiState marker)
        {
            // Prefab object markers carry an explicit path and do not need to belong to a
            // global sector-generation leaf. Sector POIs continue to resolve their pool by
            // content type when no explicit path was frozen at creation time.
            if (!marker.sourcePrefabPath.NullOrEmpty()) return marker.sourcePrefabPath;
            SectorContentType type = SectorContentTypes.ById(marker.contentTypeId);
            if (type == null) return null;
            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            if (settings == null) return null;
            return MapPrefab.ResolvePoolPath(settings.EffectiveSectorRule(type).prefabPaths, marker.seed);
        }

        /// <summary>池可用时按选定 prefab 尺寸 + 边距决定目标图尺寸（下限 PoiMapSize）。</summary>
        private static bool TryGetPrefabPoolSize(PoiState marker, out IntVec3 size)
        {
            size = IntVec3.Invalid;
            string path = ResolvePoolPrefabPath(marker);
            if (path == null) return false;
            try
            {
                MapPrefabDocument doc = MapPrefab.LoadRelative(path);
                size = ShipPocketMapFactory.CompactSize(MapPrefab.ContentBounds(doc, new Rot4(marker.seed & 3)).Size);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[DeltaV] prefab pool size probe failed for '{path}' ({marker.label}): {ex.Message}");
                return false;
            }
        }

        private void EnsureDormant(PoiState poi)
        {
            if (poi.map != null) dormancy.Sleep(poi.map, "outside POI load radius");
        }

        /// <summary>
        /// NativeParent POI 的 vanilla 移除语义（map 跟随原版镜像，替换制例外）。
        /// SnapshotPocket marker 无图（map==null 直接返回）；普通容器归档走
        /// <see cref="ArchiveContainer"/>。
        /// </summary>
        private void TryPack(PoiState poi)
        {
            Map map = poi.map;
            if (map == null || poi.kind != PoiKind.NativeParent) return;
            if (SpaceMapGovernance.MustStayActive(map, universe)) return;
            MapParent parent = poi.nativeParent;
            if (parent == null || parent.Destroyed) return;
            if (!parent.ShouldRemoveMapNow(out bool alsoRemoveWorldObject))
            {
                dormancy.Sleep(map, "native removal blocked");
                return;
            }
            poi.map = null;
            DeinitMap(map, true);
            if (!parent.Destroyed && (alsoRemoveWorldObject || parent.forceRemoveWorldObjectWhenMapRemoved))
                parent.Destroy();
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{poi.label}: POI map removed (worldObject={parent.Destroyed})");
        }

        internal void ReleaseForCapacity(int targetCount, float admissionScore)
        {
            if (MapGenerator.mapBeingGenerated != null || RimExodus.Maps.MapRuntime.Current.Generation.IsBusy) return;
            var references = universe.PlayerReferenceObjects();
            float radius = DeltaVMod.CurrentSettings.poiActiveRadius;
            var candidates = universe.objects.Where(obj => obj.map != null && !obj.map.Disposed
                    && (obj is PoiState || obj is MapSpaceObject && obj.map.Parent is ShipPocketParent_DeltaV)
                    && !obj.HasTag(SpaceObjectTag.Projectile))
                .Select(obj => (obj, map: obj.map, version: obj.RepresentationVersion,
                    score: MapRetentionScore(obj.map.Size.x * obj.map.Size.z, MinPlayerDistance(obj, references), radius)))
                .Where(candidate => candidate.score < admissionScore)
                .OrderBy(candidate => candidate.score).ThenBy(candidate => candidate.map.Index).ToArray();
            foreach (var candidate in candidates)
            {
                if (Find.Maps.Count <= targetCount) break;
                var (obj, map, version, _) = candidate;
                if (obj.RepresentationVersion != version || obj.map != map || universe.ObjectById(obj.objectId) != obj
                    || map.Disposed || !Find.Maps.Contains(map) || SpaceMapGovernance.MustStayActive(map, universe)
                    || universe.Missions.Protects(obj) || universe.Approach.ApproachShipMap == map
                    || universe.Approach.ActivePlan?.targetMap == map || universe.SoftDock.UsesMap(map)
                    || !ArchivedMapStore.CanUnloadLosslessly(map)) continue;
                if (obj.IsScattered)
                {
                    universe.Scatter.TryRetireCargoForCapacity(obj);
                }
                else if (obj is PoiState poi)
                {
                    // Native sites use the vanilla removal contract and only unload outside activation range.
                    if (MinPlayerDistance(poi, references) > radius) TryPack(poi);
                }
                else if (IsDisposableContainer(obj, map)
                    && MinPlayerDistance(obj, references) > DeleteRadius) RemoveDistantContainer(obj, map);
                else CaptureAndUnload((MapSpaceObject)obj, map);
            }
        }

        /// <summary>
        /// 整图归档：Map.ExposeData 原样存入对象，然后从 Game.maps 卸载。
        /// 失败时保留原图与身份；不经 prefab/浅快照重建。
        /// </summary>
        internal void ArchiveContainer(SpaceObject container, Map map, bool force = false, bool departing = false)
        {
            if (container.IsScattered) { universe.Scatter.RetireLost(container, departing); return; }
            int objectId = container.objectId, version = container.RepresentationVersion;
            universe.QueueFrameAction(delegate
            {
                container = universe.ObjectById(objectId);
                if (container?.RepresentationVersion != version || container.map != map) return; // 已被替换/改变 → 丢弃本任务
                if (map.Disposed || !Find.Maps.Contains(map))
                {
                    universe.UnregisterObject(container);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"{container.label}: map destroyed externally before archiving; object removed");
                    return;
                }
                float distance = MinPlayerDistance(container);
                bool beyondDeleteRadius = departing || (!float.IsPositiveInfinity(distance) && distance > DeleteRadius);
                if ((!force && !beyondDeleteRadius)
                    || SpaceMapGovernance.MustStayActive(map, universe)
                    || container.HasTag(SpaceObjectTag.Projectile) == true
                    || map.Parent is not ShipPocketParent_DeltaV
                    || !ArchivedMapStore.CanUnloadLosslessly(map))
                {
                    return;
                }
                if (IsDisposableContainer(container, map))
                {
                    if (beyondDeleteRadius) RemoveDistantContainer(container, map);
                    return;
                }
                CaptureAndUnload((MapSpaceObject)container, map);
            });
        }

        private void CaptureAndUnload(MapSpaceObject container, Map map)
        {
            int objectId = container.objectId;
            // Keep the object identity adjacent to any standalone save cross-reference warnings.
            string shipIds = string.Join(",", container.Ships.Select(ship => ship.Identity.key));
            Log.Message($"[DeltaV] Archive capture begin: object=#{objectId} label='{container.label}' "
                + $"ships=[{shipIds}] tag={container.Tag} {UniverseManager.MapTag(map)} "
                + $"parent={map.Parent?.GetUniqueLoadID() ?? "null"}");
            if (!ArchivedMapStore.TryCapture(map, out string data))
            {
                Log.Warning($"[DeltaV] Archive capture aborted: object=#{objectId} {UniverseManager.MapTag(map)}; original map retained.");
                return;
            }
            string carriedLabel = container.label;
            string mapTag = UniverseManager.MapTag(map);
            var archive = container.CaptureArchive(data);
            universe.NpcShips.PrepareForArchive(container, archive);
            if (!ArchivedMapStore.TryUnload(map))
            {
                Log.Warning($"[DeltaV] Archive unload deferred: object=#{objectId} {UniverseManager.MapTag(map)}; original map retained.");
                return;
            }
            universe.ReplaceRepresentation(container, archive);
            try { Forget(map); }
            catch (Exception ex)
            { Log.Error($"[DeltaV] Archived map runtime cleanup failed ({UniverseManager.MapTag(map)}): {ex}"); }
            finally
            {
                // 快照已独立保存；断开废弃 Thing → spawnedThings → Map 的持有链。
                map.spawnedThings.Clear();
            }
            Log.Message($"[DeltaV] Archive capture complete: object=#{archive.objectId} label='{carriedLabel}' "
                + $"{mapTag} parent={archive.parent?.GetUniqueLoadID() ?? "null"}; map unloaded.");
        }

        private bool IsDisposableContainer(SpaceObject container, Map map)
        {
            if (container.Ships.Any() || universe.Missions.Protects(container)
                || map.mapPawns.AllPawnsCount != 0 || map.listerBuildings.allBuildingsColonist.Count != 0)
                return false;
            return container.contentTypeId == "Asteroid"
                || (!GlobeDisplayInfo.IsStationType(container.contentTypeId)
                    && map.AllCells.Where(cell => SubstructureOwnership.SupportsAt(map, cell))
                        .Take(SmallWreckMaxSupportCells + 1).Count() <= SmallWreckMaxSupportCells);
        }

        private readonly HashSet<int> restoreQueued = new HashSet<int>();

        internal void CheckArchivedProximity(SpaceObject container)
        {
            if (container is ArchivedSpaceObject archived
                && MinPlayerDistance(container) <= DeltaVMod.CurrentSettings.poiActiveRadius)
                QueueRestore(archived);
        }
        private bool TryRestoreContainer(ArchivedSpaceObject archive, out MapSpaceObject current)
        {
            current = null;
            if (!universe.MapPool.TryMakeRoom(score: CapacityScore(archive.mapArea, MinPlayerDistance(archive)))) return false;
            Log.Message($"[DeltaV] Archive restore begin: object=#{archive.objectId} label='{archive.label}' "
                + $"archivedMap=#{archive.mapId} {UniverseManager.MapTag(archive.parent?.Map)} parent={archive.parent?.GetUniqueLoadID() ?? "null"}");
            if (!ArchivedMapStore.TryRestore(archive.mapData, archive.mapId, archive.parent, out Map restored))
            {
                Log.Warning($"[DeltaV] Archive restore failed: object=#{archive.objectId} label='{archive.label}' "
                    + $"archivedMap=#{archive.mapId} {UniverseManager.MapTag(restored)} parent={archive.parent?.GetUniqueLoadID() ?? "null"}");
                return false;
            }
            current = archive.RestoreRepresentation(restored);
            universe.ReplaceRepresentation(archive, current);
            // 成功后原快照永远不能再恢复。即使后续清理失败，也只保留恢复图。
            archive.mapData = null;
            archive.dynamics = null;
            Log.Message($"[DeltaV] Archive restore complete: object=#{current.objectId} label='{current.label}' "
                + $"{UniverseManager.MapTag(restored)} parent={restored.Parent?.GetUniqueLoadID() ?? "null"}");
            return true;
        }
        private void ActivateRestoredContainer(SpaceObject container)
        {
            dormancy.Wake(container.map, "full map archive restored");
            universe.Weapons.RegisterMapWeapons(container.map);
            universe.Controls.RegisterMap(container.map);
        }
        private void QueueRestore(ArchivedSpaceObject container)
        {
            int id = container.objectId, version = container.RepresentationVersion;
            if (!restoreQueued.Add(id)) return;
            universe.QueueFrameAction(() =>
            {
                restoreQueued.Remove(id);
                if (!(universe.ObjectById(id) is ArchivedSpaceObject archive)
                    || archive.RepresentationVersion != version) return;
                if (!TryRestoreContainer(archive, out MapSpaceObject current)) return;
                ActivateRestoredContainer(current);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{current.label}: full map restored ({UniverseManager.MapTag(current.map)})");
            });
        }

        /// <summary>
        /// 按调用方请求移除一个归档容器，复用原图恢复与原版人员交接。
        /// 本方法不判断请求原因；玩家距离与地图保护仍优先于移除。仅在帧边界调用。
        /// </summary>
        internal bool TryRemoveArchivedContainer(ArchivedSpaceObject archive)
        {
            if (universe.ObjectById(archive.objectId) != archive || archive.mapData.NullOrEmpty()) return false;
            if (restoreQueued.Contains(archive.objectId)
                || MinPlayerDistance(archive) <= DeltaVMod.CurrentSettings.poiActiveRadius)
            {
                QueueRestore(archive);
                return false;
            }
            PocketMapParent parent = archive.parent;
            if (parent == null || parent.Destroyed) return false;
            if (!TryRestoreContainer(archive, out MapSpaceObject obj)) return false;
            Map restored = obj.map;
            if (SpaceMapGovernance.MustStayActive(restored, universe))
            {
                ActivateRestoredContainer(obj);
                return false;
            }
            // PassPawnsToWorld 不摘除容器内 Pawn 的 holdingOwner；后续递归
            // Notify_MyMapRemoved 会再次处理它。先用原版放出 API 解除容器持有，
            // 再由同一个 MapDeiniter 完成人员清理和 WorldPawns 交接。
            foreach (Pawn pawn in restored.mapPawns.AllPawnsUnspawned.ToList())
            {
                if (pawn.Spawned) continue;
                if (pawn.holdingOwner == null || !pawn.holdingOwner.TryDrop(pawn,
                    pawn.PositionHeld, restored, ThingPlaceMode.Near, out Thing released,
                    playDropSound: false) || released != pawn || !pawn.Spawned)
                {
                    Log.Error($"[DeltaV] Could not release crew {pawn.GetUniqueLoadID()}; "
                        + $"keeping restored {UniverseManager.MapTag(restored)}");
                    ActivateRestoredContainer(obj);
                    return false;
                }
            }
            RemoveDistantContainer(obj, restored);
            return true;
        }

        private void RemoveDistantContainer(SpaceObject obj, Map map)
        {
            var parent = (PocketMapParent)map.Parent;
            // 不走 PocketMapUtility 的视图切换副作用：这里始终是后台清理。
            DeinitMap(map, notifyPlayer: false);
            Find.World.pocketMaps.Remove(parent);
            if (!parent.Destroyed) parent.Destroy();
            universe.UnregisterObject(obj);
            obj.map = null;
        }

        /// <summary>外部销图的统一注销；归档已换表示时不得移除同 ID 的新对象。</summary>
        internal void NotifyMapRemoved(Map map)
        {
            SpaceObject container = universe.FrameOf(map);
            if (container == null || container is PoiState) return; // 原生 POI 镜像由 parent 生命周期维护。
            universe.Projectiles.NotifyContainerRemoving(container);
            universe.QueueFrameAction(() =>
            {
                if (universe.objects.Contains(container) && container.map == map)
                    universe.UnregisterObject(container);
            });
        }

        public void Forget(Map map)
        {
            pendingStructureChecks.Remove(map);
            dormancy.Forget(map);
            universe.ForgetMapRuntimeState(map);
            universe.Weapons.ForgetMap(map);
            ShipStatusPanel.Forget(map);
            MapOverlayRenderer.Release(map);
            SpaceCollision.Forget(map);
            ShipRegistry.Forget(map);
            ShipPhysics.Forget(map);
            ThrusterAudio.Forget(map);
            PlanetGlobeRenderer.ClearRuntimeCaches();
            universe.Structures.Forget(map);
        }

        public void DestroyPocketMap(Map map)
        {
            if (map == null || map.Disposed) return;
            if (DeltaVLog.Enabled(DeltaVLogModule.Lifecycle))
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"pocket map destroy tick={Find.TickManager.TicksGame} " +
                    $"{UniverseManager.MapTag(map)} holder=#{universe.FrameOf(map)?.objectId}");
            Forget(map);
            PocketMapUtility.DestroyPocketMap(map);
        }

        public void DeinitMap(Map map, bool notifyPlayer)
        {
            if (map == null || map.Disposed) return;
            Forget(map);
            Current.Game.DeinitAndRemoveMap(map, notifyPlayer);
        }

        /// <summary>
        /// 逆重起飞离场善后（星区标准流程第 5 步，2026-09-20）：起飞船所在星区内其余
        /// SpaceObject 地图一律按「距离 = 超删除半径」处理——复用距离生命周期同一条
        /// 收纳/删除路径（TryPack：快照 POI 收快照销毁口袋图、对象保留为 packed 可
        /// 重访；MustStayActive 与滞留 Pawn 守卫照常生效，删不掉的只休眠）。
        /// worldtile 图由锚规则（HandleStrategicTravelDeparture 第 ② 段）另行处理，
        /// 经 excludeMap 排除。旅行期间 MinPlayerDistance 为 +Infinity 且评估整体
        /// 跳过，不能依赖自然距离触发——离场时刻必须显式执行。
        /// </summary>
        public void PackDepartedSectorPois(PlanetTile sector, Map excludeMap)
        {
            if (universe == null || !sector.Valid) return;
            foreach (SpaceObject obj in SectorContentService.ObjectsInSector(universe, sector).ToList())
            {
                Map map = obj.map;
                if (map == null || map.Disposed || !Find.Maps.Contains(map)) continue;
                if (map == excludeMap || IsUniverseHost(map)) continue;
                // PoiState 走 NativeParent 原版语义；普通物化容器走 ArchiveContainer
                //（marker 替换；有船/滞留 Pawn/MustStayActive 在帧上下文复核，删不掉的只休眠）。
                if (obj is PoiState poi) TryPack(poi);
                else ArchiveContainer(obj, map, departing: true);
            }
        }
        private bool IsPendingGeneration(PoiState poi) => poi != null
            && universe.Generation?.IsQueued(GenerationKey(poi)) == true;
        private static string GenerationKey(PoiState poi) => poi.kind == PoiKind.NativeParent
            ? $"orbital-parent:{poi.nativeParent?.ID ?? poi.objectId}" : $"snapshot-poi:{poi.objectId}";

        /// <summary>
        /// 对象注销通知（UniverseManager.UnregisterObject 调用）：取消其排队中的生成
        /// （生成闭包捕获实例，不取消则 onComplete 会给死实例赋 map，产出无注册容器的
        /// 孤儿图——2026-09-25 审计加固）并清理失败退避记录。
        /// </summary>
        internal void NotifyUnregistered(PoiState poi)
        {
            universe.Generation?.CancelQueued(GenerationKey(poi));
            generationRetryAfterTick.Remove(poi.objectId);
        }

        private static void ReportGenerationFailure(string label, Exception error)
        {
            if (error is OperationCanceledException)
                DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"map generation canceled: {label}");
            else
                Log.Error($"[DeltaV] Failed to generate POI {label}: {error}");
        }
        public void Reset()
        {
            pendingStructureChecks.Clear();
            dormancy.Reset();
            generationRetryAfterTick.Clear();
            restoreQueued.Clear();
        }
    }
}

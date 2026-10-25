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
    ///   距离休眠，超删除距离使用原版 Map 整图序列化并卸载，保留容器身份与姿态；
    ///   靠近时从同一地图内容恢复，不经 prefab 重盖。
    /// - NativeParent 型 PoiState 例外：map 字段跟随原版 MapParent 镜像
    ///   （SynchronizeNative/UpdateFromParent），保留原版图标与生命周期。
    /// </summary>
    public sealed partial class SpaceLifecycleService
    {
        public const int PoiMapSize = 90;

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
        public void Wake(Map map, string reason) => dormancy.Wake(map, reason);

        public void Tick()
        {
            OrbitalPoiRegistry.Synchronize(universe);
            float activeRadius = DeltaVMod.CurrentSettings.poiActiveRadius;
            float deleteRadius = Mathf.Max(DeltaVMod.CurrentSettings.poiDeleteRadius, activeRadius + 1f);
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
                if (float.IsPositiveInfinity(scatteredDistance)) return;
                if (scatteredDistance > deleteRadius) universe.Scatter.RetireLost(container);
                else if (scatteredDistance > activeRadius) dormancy.Sleep(map, "distant scattered contents");
                else dormancy.Wake(map, "nearby scattered contents");
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
        {
            float best = float.PositiveInfinity;
            foreach (SpaceObject reference in universe.PlayerReferenceObjects())
            {
                best = Mathf.Min(best,
                    OrbitSphere.ArcCells(reference.pose.position, obj.pose.position));
            }
            return best;
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
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(poi), priority = priority, parent = parent,
                size = SpaceMapGovernance.GenerationSize(parent), generator = parent.MapGeneratorDef,
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
            var parent = (ShipPocketParent_DeltaV)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_ShipMapParent);
            parent.sourceMap = universe.GetOrCreateHostMap();
            parent.mapGenerator = DeltaVDefOf.DeltaV_ShipPocket;
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
            universe.Generation.Enqueue(new IncrementalMapGenerationRequest
            {
                key = GenerationKey(marker), priority = priority, parent = parent,
                size = size, generator = DeltaVDefOf.DeltaV_ShipPocket,
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
                    if (!(ex is OperationCanceledException))
                    {
                        generationRetryAfterTick[marker.objectId] =
                            Find.TickManager.TicksGame + GenerationFailureBackoffTicks;
                    }
                }
            });
        }

        /// <summary>
        /// 物化 = 用**普通容器**替换 marker（2026-09-25 用户定案：物化产物没有 POI
        /// 图标身份——名称 = 图上结构拼接、图标按图内容）。快照或 prefab 池盖进图
        ///（盖章用容器自身 objectId，自洽）。**容器先注册、结构后铺设**（ShipTestScene
        /// 同序，2026-09-30 修复）：驾驶台 spawn 认领要求 FrameOf 已解析（ClaimOnSpawn
        /// 守卫），先盖章后注册会让敌舰/空间站的驾驶台永不认领、整图沦为「独立结构」。
        /// 归属名 = marker 生成时决定的完整名（envLabel，归档经 carriedLabel 传承），
        /// 容器显示名（拼接结构名）与 marker 名一致。onComplete 帧上下文执行，objects
        /// 变异不在任何迭代内。
        /// </summary>
        private void MaterializeMarker(PoiState marker, Map map)
        {
            int containerId = universe.AllocateOwnerId();
            var container = new MapSpaceObject(containerId, map, marker.pose)
            {
                faction = marker.faction,
                ambientLabel = marker.envLabel,
                contentTypeId = marker.contentTypeId,
                sourcePrefabPath = marker.sourcePrefabPath,
                npcDoctrines = marker.npcDoctrines,
            };
            universe.RegisterObject(container);
            if (!marker.envLabel.NullOrEmpty())
            {
                // 归属名 = 生成时决定的完整名（在内容盖章前注册，下方消息/日志里的
                // container.label 即显示完整名）；图上结构名注册后不再落「独立结构
                // #N」兜底。归档→再物化经 envLabel 传承同名（新归属 ID 重登记）。
                universe.NoteStructureName(containerId, marker.envLabel);
            }
            if (marker.snapshot != null)
            {
                marker.snapshot.ApplyTo(map, containerId);
                Messages.Message("DeltaV_PoiRestored".Translate(container.label),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                // 首次物化内容链 = 类型 prefab 池（2026-09-30 定案，prefab-only）：
                // 池空在生成期已拦截（SectorContentService）；此处失效（路径删改/损坏）
                // 或防御性兜中池空 → 报错并取消该 POI，不回退程序生成、不生成空壳图。
                string poolPath = ResolvePoolPrefabPath(marker);
                if (poolPath == null)
                {
                    Log.Error($"[DeltaV] {marker.label}: content type '{marker.contentTypeId}' has no " +
                        "prefab pool; materialization canceled (POI removed).");
                    CancelMaterialization(marker, container, map);
                    return;
                }
                try
                {
                    MapPrefabDocument doc = MapPrefab.LoadRelative(poolPath);
                    Rot4 rotation = new Rot4(marker.seed & 3);
                    MapPrefab.Spawn(doc, map, MapPrefab.CenteredPosition(doc, map, rotation), marker.faction, containerId, rotation, universe, blankSpace: true);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"{marker.label}: materialized from prefab pool '{poolPath}' (rot {rotation.AsInt})");
                }
                catch (Exception ex)
                {
                    Log.Error($"[DeltaV] {marker.label}: prefab pool '{poolPath}' failed to load/spawn; " +
                        $"materialization canceled (POI removed): {ex}");
                    CancelMaterialization(marker, container, map);
                    return;
                }
                Messages.Message("DeltaV_PoiActivated".Translate(container.label),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            universe.NotifyObjectMaterialized(marker, container);
            universe.UnregisterObject(marker);
            dormancy.Wake(map, "generation complete");
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{container.label}: marker #{marker.objectId} materialized into plain container "
                + $"#{container.objectId} (map#{map.uniqueID} {map.Size.x}x{map.Size.z})");
        }

        /// <summary>取消一次物化（池空/失效，prefab-only 定案）：销掉刚生成的图、
        /// 注销容器与 marker——该 POI 不复存在，不回退、不重试。</summary>
        private void CancelMaterialization(PoiState marker, SpaceObject container, Map map)
        {
            string label = marker.label;
            DestroyPocketMap(map);
            universe.UnregisterObject(container);
            universe.UnregisterObject(marker);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{label}: materialization canceled; marker #{marker.objectId} and container "
                + $"#{container.objectId} removed");
        }

        /// <summary>无快照 marker 首次物化时的类型 prefab 池目标图边距（格）。</summary>

        /// <summary>
        /// 无快照 marker 的类型 prefab 池路径（seed 确定性解析；与盖章同一算法，
        /// 保证入队测尺寸与最终放置取到同一份）。类型未注册或池为空/全失效返回 null。
        /// </summary>
        private static string ResolvePoolPrefabPath(PoiState marker)
        {
            SectorContentType type = SectorContentTypes.ById(marker.contentTypeId);
            if (type == null) return null;
            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            if (settings == null) return null;
            return marker.sourcePrefabPath
                ?? MapPrefab.ResolvePoolPath(settings.EffectiveSectorRule(type).prefabPaths, marker.seed);
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
                $"{poi.label}: removed at POI delete radius (worldObject={parent.Destroyed})");
        }

        /// <summary>
        /// 整图归档：Map.ExposeData 原样存入对象，然后从 Game.maps 卸载。
        /// 失败时保留原图与身份；不经 prefab/浅快照重建。
        /// </summary>
        internal void ArchiveContainer(SpaceObject container, Map map, bool force = false)
        {
            if (container.IsScattered) { universe.Scatter.RetireLost(container); return; }
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
                if (SpaceMapGovernance.MustStayActive(map, universe)
                    || container.HasTag(SpaceObjectTag.Projectile) == true
                    || map.Parent is not ShipPocketParent_DeltaV
                    || !ArchivedMapStore.CanUnloadLosslessly(map)
                    || (!force && MinPlayerDistance(container)
                        <= DeltaVMod.CurrentSettings.poiDeleteRadius))
                {
                    return;
                }
                if (!ArchivedMapStore.TryCapture(map, out string data)) return;
                string carriedLabel = container.label;
                var archive = ((MapSpaceObject)container).CaptureArchive(data);
                universe.NpcShips.PrepareForArchive(container, archive);
                if (!ArchivedMapStore.TryUnload(map)) return;
                universe.ReplaceRepresentation(container, archive);
                try { Forget(map); }
                catch (Exception ex)
                { Log.Error($"[DeltaV] Archived map runtime cleanup failed ({UniverseManager.MapTag(map)}): {ex}"); }
                finally
                {
                    // 快照已独立保存；断开废弃 Thing → spawnedThings → Map 的持有链。
                    map.spawnedThings.Clear();
                }
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{carriedLabel}: full map #{archive.mapId} archived on container #{container.objectId}");
            });
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
            if (!ArchivedMapStore.TryRestore(archive.mapData, archive.mapId, archive.parent, out Map restored)) return false;
            current = archive.RestoreRepresentation(restored);
            universe.ReplaceRepresentation(archive, current);
            // 成功后原快照永远不能再恢复。即使后续清理失败，也只保留恢复图。
            archive.mapData = null;
            archive.dynamics = null;
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
            // 不走 PocketMapUtility 的视图切换副作用：这里始终是后台清理。
            DeinitMap(restored, notifyPlayer: false);
            Find.World.pocketMaps.Remove(parent);
            if (!parent.Destroyed) parent.Destroy();
            obj.map = null;
            universe.UnregisterObject(obj);
            return true;
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
                if (obj.IsScattered) universe.Scatter.RetireLost(obj, departing: true);
                else if (obj is PoiState poi) TryPack(poi);
                else ArchiveContainer(obj, map);
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

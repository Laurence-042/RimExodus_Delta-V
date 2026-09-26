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
    /// - 物化容器（普通 SpaceObject）的生命周期**按图内容判定，不看对象类型**：
    ///   MustStayActive（玩家侧）恒活跃；图上有任何有效船（含 NPC）或滞留 pawn →
    ///   只休眠冻结、永不归档（快照会拍平归属、丢驾驶台 comp）；无船无 pawn →
    ///   超删除距离归档 = 收快照、新建 marker 替换、销图、注销容器（帧上下文）；
    ///   完全空白图直接注销。
    /// - NativeParent 型 PoiState 例外：map 字段跟随原版 MapParent 镜像
    ///   （SynchronizeNative/UpdateFromParent），保留原版图标与生命周期。
    /// </summary>
    public sealed class SpaceLifecycleService
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
        /// 物化容器（普通 SpaceObject）：生命周期按图内容判定（2026-09-25 用户定案）。
        /// 拆分残骸容器从此也有距离生命周期（修「地图永久加载泄漏」）。
        /// </summary>
        private void TickMaterializedContainer(SpaceObject container, float activeRadius, float deleteRadius)
        {
            Map map = container.map;
            if (map == null) return; // 不应存在（LoadedGame 清扫纯容器丢图即注销）
            if (IsUniverseHost(map)) return;
            // 在飞弹体图按 tag 豁免距离休眠/归档（太空战架构 §3.2）：弹体生命周期由
            // 弹体服务（超时/碰撞结算）独占管理，生命周期扫描不得收纳/销毁在飞弹体的图
            //（图池空闲图的豁免同样见弹体服务）。
            if (container.Tag == SpaceObjectTag.Projectile) return;
            // 悬死自愈：图被外部销毁 → 容器随图消亡。
            if (map.Disposed || !Find.Maps.Contains(map))
            {
                container.map = null;
                universe.UnregisterObject(container);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{container.label}: container's map was destroyed externally; object removed");
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
            // 图上有任何有效船（含 NPC）或滞留 pawn：永不归档（快照会拍平归属、丢
            // 驾驶台 comp），只休眠冻结。
            if (ShipRegistry.ShipsOnMap(map).Count > 0 || map.mapPawns.AllPawnsSpawned.Any())
            {
                dormancy.Sleep(map, "ship or pawns aboard");
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
            foreach (Ship ship in universe.PlayerShips())
            {
                best = Mathf.Min(best,
                    OrbitSphere.ArcCells(ShipRegistry.ShipPose(ship).position, obj.pose.position));
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
            // 越界写经 CellToIndex 静默回卷坏图——2026-09-24 事故）；模板态 marker
            //（无快照）或旧档无尺寸记录时维持 PoiMapSize，越界项由 ApplyTo 守卫兜底。
            IntVec3 size = marker.snapshot != null && marker.snapshot.HasSourceSize
                ? marker.snapshot.SourceMapSize
                : new IntVec3(PoiMapSize, 1, PoiMapSize);
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
        /// 图标身份——名称 = 图上结构拼接、图标按图内容；envLabel 降级为 ambientLabel
        /// 只作纯环境兜底名）。快照或模板盖进图（盖章用容器自身 objectId，自洽），
        /// **先注册容器再注销 marker**（FrameOf 无空窗）。onComplete 帧上下文执行，
        /// objects 变异不在任何迭代内。
        /// </summary>
        private void MaterializeMarker(PoiState marker, Map map)
        {
            int containerId = universe.AllocateOwnerId();
            var container = new SpaceObject(containerId, map, marker.pose)
            {
                faction = marker.faction,
                ambientLabel = marker.envLabel,
            };
            if (marker.snapshot != null)
            {
                marker.snapshot.ApplyTo(map, containerId);
                Messages.Message("DeltaV_PoiRestored".Translate(container.label),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                PoiTemplate.Stamp(map, marker.seed, containerId);
                Messages.Message("DeltaV_PoiActivated".Translate(container.label),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            universe.RegisterObject(container);
            universe.UnregisterObject(marker);
            dormancy.Wake(map, "generation complete");
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{container.label}: marker #{marker.objectId} materialized into plain container "
                + $"#{container.objectId} (map#{map.uniqueID} {map.Size.x}x{map.Size.z})");
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
        /// 普通物化容器的归档（B→marker 替换，2026-09-25 用户定案）：收快照 → 新建
        /// marker（envLabel = 当前显示名：拼接名或纯环境兜底名）→ 先注册 marker →
        /// 销图 → 注销容器，整段经 QueueFrameAction 帧上下文执行。完全空白的图直接
        /// 注销不建 marker。帧上下文复核：图可能已被销毁/对象已被替换/有船落上。
        /// </summary>
        internal void ArchiveContainer(SpaceObject container, Map map)
        {
            universe.QueueFrameAction(delegate
            {
                if (container.map != map) return; // 已被替换/改变 → 丢弃本任务
                if (map.Disposed || !Find.Maps.Contains(map))
                {
                    universe.UnregisterObject(container);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"{container.label}: map destroyed externally before archiving; object removed");
                    return;
                }
                if (SpaceMapGovernance.MustStayActive(map, universe)
                    || ShipRegistry.ShipsOnMap(map).Count > 0
                    || map.mapPawns.AllPawnsSpawned.Any())
                {
                    return; // 帧间隙里有船落上/结构变化 → 放弃归档，下轮重判
                }
                PoiSnapshot snapshot = PoiSnapshot.Capture(map);
                if (!snapshot.HasContent)
                {
                    // 完全空白：无基架无建筑/物品——不值得归档，直接注销销图。
                    string blankLabel = container.label;
                    DestroyPocketMap(map);
                    universe.UnregisterObject(container);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        $"{blankLabel}: blank map destroyed at delete radius (no marker created)");
                    return;
                }
                // 销图前取显示名（拼接名或 ambientLabel 兜底）作为 marker 的 envLabel。
                string carriedLabel = container.label;
                var marker = new PoiState(universe.AllocateOwnerId(), carriedLabel,
                    container.pose.position, container.pose.angle, Rand.Int)
                {
                    faction = container.faction,
                    snapshot = snapshot,
                };
                marker.pose = container.pose; // 完整位姿接管（含速度）
                universe.RegisterObject(marker); // 先注册 marker
                DestroyPocketMap(map);          // 销图
                universe.UnregisterObject(container); // 再注销容器
                Messages.Message("DeltaV_PoiPacked".Translate(marker.label),
                    MessageTypeDefOf.NeutralEvent, false);
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{marker.label}: container #{container.objectId} archived into "
                    + $"marker #{marker.objectId}");
            });
        }

        public void Forget(Map map)
        {
            dormancy.Forget(map);
            MapOverlayRenderer.Release(map);
            SpaceCollision.Forget(map);
        }

        public void DestroyPocketMap(Map map)
        {
            if (map == null || map.Disposed) return;
            Forget(map);
            PocketMapUtility.DestroyPocketMap(map);
        }

        public void DeinitMap(Map map, bool notifyPlayer)
        {
            if (map == null || map.Disposed) return;
            Forget(map);
            Current.Game.DeinitAndRemoveMap(map, notifyPlayer);
        }

        public void ForceActivate(PoiState poi) => EnsureActive(poi);
        public void ForceDormant(PoiState poi)
        {
            if (poi.map != null && poi.map != Find.CurrentMap) dormancy.Sleep(poi.map, "dev action");
        }
        public void ForcePack(PoiState poi)
        {
            TryPack(poi);
        }

        /// <summary>dev：强制休眠一个物化容器（图上冻结 tick；仅非当前图）。</summary>
        public void ForceDormantContainer(SpaceObject container)
        {
            if (container?.map != null && container.map != Find.CurrentMap)
            {
                dormancy.Sleep(container.map, "dev action");
            }
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
            dormancy.Reset();
            generationRetryAfterTick.Clear();
        }
    }
}

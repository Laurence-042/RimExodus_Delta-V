using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 统一宇宙状态管理器。GameComponent 由 Game.FillComponents 反射自动注册，
    /// 无需 XML。objects 列表（飞船 + POI）是权威宇宙状态；外壳代理只是缓存。
    ///
    /// 职责边界：
    /// - 非聚焦对象代理绘制在 GameComponentUpdate 分发（WorldComponent_GravshipController 同款节奏）；
    /// - SetFocused 只切换参考系（CurrentMap），绝不修改任何 pose 数值（不变量 #5）；
    /// - POI 地图生命周期统一委托 SpaceLifecycle（单一管理入口，AGENTS §11）；
    /// - 宿主：所有 Delta-V 口袋图（飞船/POI）共用的 sourceMap 锚——一张专用的
    ///   无 tile 口袋图（UniverseHostParent_DeltaV，仅存在于 Find.World.pocketMaps），
    ///   永不聚焦、永不出现在世界视图、tick 常冻结。它不是宇宙坐标锚（坐标已是
    ///   球面绝对坐标），只承载原版 storyteller/财富/移除级联从口袋图重定向到
    ///   sourceMap 的引擎契约。
    /// </summary>
    public class UniverseManager : GameComponent
    {
        /// <summary>权威宇宙对象列表（ShipState + PoiState，多态深序列化）。存档事实。</summary>
        public List<SpaceObject> objects = new List<SpaceObject>();

        /// <summary>
        /// 已触发生成的星区（持久事实，星区架构 §4）：元素为 PlanetTile 的 "tileId,layerId"。
        /// 「该星区已触发生成」不能从现存对象推导（对象会被销毁），必须持久化防重复刷对象；
        /// 「该星区当前有哪些对象」则由 objects + TileFromDirection 查询时重建，不序列化。
        /// </summary>
        public List<string> generatedSectorTiles = new List<string>();

        public int focusedShipId = -1;

        /// <summary>驾驶中的船（阶段2）；-1 = 未驾驶。持久化，读档后驾驶状态恢复。</summary>
        public int pilotShipId = -1;

        /// <summary>
        /// 玩家主船：背景相机跟随对象、POI 生命周期距离参考。失效时回退第一艘活着的船。
        /// </summary>
        public int primaryShipId = -1;

        /// <summary>
        /// 专用宿主口袋图（运行时引用，不序列化——按 def 在 pocketMaps 中重扫即可恢复，
        /// 空图无事实价值可随时重建，AGENTS §15）。经 GetOrCreateHostMap 惰性获取。
        /// </summary>
        [NonSerialized] private PocketMapHostService pocketHostService;

        /// <summary>全部飞船（objects 的 ShipState 视图；每次调用有少量分配，勿在热循环内反复取）。</summary>
        public IEnumerable<ShipState> Ships => objects.OfType<ShipState>();

        public ShipState Focused => ShipById(focusedShipId);

        /// <summary>主船（背景相机跟随对象）；失效时回退第一艘活着的船，保持显示有参考。</summary>
        public ShipState PrimaryShip =>
            (objects.FirstOrDefault(o => o is ShipState s && s.objectId == primaryShipId
                && (s.map != null || s.dockedMap != null)) as ShipState)
            ?? objects.FirstOrDefault(o => o is ShipState s2 && (s2.map != null || s2.dockedMap != null)) as ShipState;

        public ShipState ShipById(int id)
        {
            return objects.FirstOrDefault(o => o.objectId == id) as ShipState;
        }

        /// <summary>为运行时接入的新飞船/POI 分配稳定且不重复的对象 ID。</summary>
        public int NextObjectId()
        {
            int max = 0;
            foreach (SpaceObject obj in objects)
            {
                max = Mathf.Max(max, obj.objectId);
            }
            return max + 1;
        }

        // ---- 基架归属 ID（2026-09-18 设计，与 SpaceObject 共用 ID 空间）----

        /// <summary>
        /// 已分配的归属 ID 高水位（持久化）。归属 ID 可能没有对应 SpaceObject（裸 ID
        /// 平台），NextObjectId 只扫已注册对象，故必须独立计数防碰撞；分配时再与
        /// NextObjectId 抬平。
        /// </summary>
        private int nextFreeOwnerId;

        /// <summary>分配一个新的基架归属 ID（独立新建平台、战略到达落点、归属命令「新建独立 ID」）。</summary>
        public int AllocateOwnerId()
        {
            int objectFloor = NextObjectId() - 1;
            if (nextFreeOwnerId <= objectFloor)
            {
                nextFreeOwnerId = objectFloor + 1;
            }
            return nextFreeOwnerId++;
        }

        /// <summary>归属 ID 的显示名：已注册对象用其 label，裸 ID 显示「独立结构 #N」。</summary>
        public string LabelForOwner(int ownerId)
        {
            if (ownerId <= 0)
            {
                return "DeltaV_OwnerUnassigned".Translate();
            }
            SpaceObject obj = objects.FirstOrDefault(o => o.objectId == ownerId);
            return obj != null ? obj.label : "DeltaV_OwnerUnregistered".Translate(ownerId);
        }

        /// <summary>宇宙对象增删的唯一入口（AGENTS §11 单一管理入口精神）；星区记账与 id 语义在此收口。</summary>
        public void RegisterObject(SpaceObject obj)
        {
            if (obj == null || objects.Contains(obj)) return;
            objects.Add(obj);
        }

        public void UnregisterObject(SpaceObject obj)
        {
            if (obj == null) return;
            objects.Remove(obj);
        }

        // ---- 尺度重标定（星区架构 §8）----

        /// <summary>存档时的 CellsPerTile（读档后与当前设置不同则触发一次位姿重标定）。</summary>
        private int cellsPerTileAtSave;

        /// <summary>读档后待执行的旧尺度（-1 = 无）；对象 cross-ref 解析完成后执行。</summary>
        [NonSerialized] private float pendingScaleRescaleFrom = -1f;

        /// <summary>停靠/起飞流程进行中禁止修改尺度（pending 位姿计算不能中途换尺度）。</summary>
        public bool CanApplyScaleChange()
        {
            if (DockController.MobileLaunchPending) return false;
            foreach (ShipState ship in Ships)
            {
                if (ship.docking) return false;
            }
            WorldComponent_GravshipController controller = Find.GravshipController;
            if (controller != null
                && (controller.LandingAreaConfirmationInProgress || controller.IsGravshipTravelling))
            {
                return false;
            }
            return Current.Game?.Gravship == null;
        }

        /// <summary>
        /// 尺度调整即时生效：全部 SpaceObject 位姿相对所在星区中心按比例缩放，保持各星区
        /// 布局密度（战术格距不变、战略距离随尺度拉长）；速度不缩放（战术手感不变）。
        /// docked 船的位姿偏移即其内部坐标差，同一规则天然保持「悬停↔落点」对应。
        /// 只动位姿，与物化状态无关（未物化是常态）。
        /// </summary>
        public void ApplyCellsPerTileScale(float oldScale, float newScale)
        {
            if (oldScale <= 0f || newScale <= 0f || Mathf.Approximately(oldScale, newScale))
            {
                return;
            }
            // 设置项在调用前已被改写：先清尺度缓存，保证 Offset/Move 统一在新尺度下计量，
            // 再按 old/new 收缩角度——等价于"保持各对象到星区中心的格数不变"。
            OrbitSphere.ClearCache();
            float shrink = oldScale / newScale;
            foreach (SpaceObject obj in objects)
            {
                RescalePoseToSector(ref obj.pose.position, shrink);
                if (obj is ShipState ship)
                {
                    RescalePoseToSector(ref ship.preDockPose.position, shrink);
                }
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"cellsPerTile rescaled {oldScale:F0} -> {newScale:F0}; {objects.Count} object poses rebased to sector centers (velocities untouched)");
        }

        private static void RescalePoseToSector(ref Vector3 position, float shrink)
        {
            PlanetTile tile = OrbitSphere.TileFromDirection(position);
            if (!tile.Valid) return;
            Vector3 center = OrbitSphere.TileCenterDirection(tile);
            Vector2 offset = OrbitSphere.Offset(center, position);
            position = OrbitSphere.Move(center, offset * shrink);
        }

        /// <summary>当前地图对应的宇宙对象（船或 POI；无则 null）。显示参考系以此为准。</summary>
        public SpaceObject SpaceObjectOnMap(Map map)
        {
            return map == null ? null : objects.FirstOrDefault(o => o.map == map);
        }

        /// <summary>当前地图对应的船（无则 null）。驾驶等飞船专属逻辑用。</summary>
        public ShipState ShipOnMap(Map map)
        {
            return SpaceObjectOnMap(map) as ShipState;
        }

        /// <summary>
        /// 承载指定地图的显示参考系：对象图（口袋图）为其自身，真实轨道空间图
        /// （bootstrap 源图、降落图）为该图 tile 的切空间帧，其余 null。
        /// </summary>
        public SpaceObject FrameOf(Map map)
        {
            SpaceObject frame = SpaceObjectOnMap(map);
            if (frame != null)
            {
                return frame;
            }
            return MapFrame(map);
        }

        [NonSerialized] private ShipState mapFrame;

        /// <summary>
        /// 真实轨道空间图的显示帧（tile 中心切空间，角度恒 0）：纯显示层合成参考系，
        /// 只用于把宇宙对象代理/落点投影到该图的地图坐标系，不是宇宙状态（不变量 #9/#5）。
        /// </summary>
        public SpaceObject MapFrame(Map map)
        {
            if (map == null || !map.Tile.Valid || !map.Tile.LayerDef.isSpace)
            {
                return null;
            }
            if (mapFrame == null)
            {
                mapFrame = new ShipState(0, "map frame (display-only)", map, new SpacePose(Vector3.right, 0f));
            }
            mapFrame.map = map;
            mapFrame.pose.position = OrbitSphere.TileCenterDirection(map.Tile);
            return mapFrame;
        }

        /// <summary>frame 是否为合成地图帧（SpaceObjectOnMap 之外的显示帧）。</summary>
        public bool IsSyntheticFrame(SpaceObject frame)
        {
            return ReferenceEquals(frame, mapFrame);
        }

        // ---- 专用宿主 ----

        /// <summary>
        /// 专用宿主口袋图：所有 Delta-V 口袋图 sourceMap 的统一锚。Tile 恒 Invalid、
        /// 只进 Find.World.pocketMaps（绝不进 Find.WorldObjects）、永不被聚焦。
        /// 复用 DeltaV_ShipPocket 生成器（太空 biome、空 Void 图）。
        /// </summary>
        public Map GetOrCreateHostMap()
        {
            if (pocketHostService == null)
            {
                pocketHostService = new PocketMapHostService(
                    parent => parent is UniverseHostParent_DeltaV,
                    () => (PocketMapParent)WorldObjectMaker.MakeWorldObject(DeltaVDefOf.DeltaV_UniverseHostParent),
                    DeltaVDefOf.DeltaV_ShipPocket,
                    new IntVec3(HostMapEdge, 1, HostMapEdge));
            }
            return pocketHostService.GetOrCreateHostMap();
        }

        /// <summary>宿主口袋图边长：只是 sourceMap 契约的载体，越小越好。</summary>
        public const int HostMapEdge = 50;

        /// <summary>当前宿主口袋图（可能尚未创建）。</summary>
        public Map UniverseHostMap => pocketHostService?.Current;

        public UniverseManager(Game game)
        {
        }

        public override void StartedNewGame()
        {
            // 开局出生星区在世界生成时预生成（星区架构 §4 触发时机）。
            Map current = Find.CurrentMap;
            if (current != null && current.Tile.Valid && current.Tile.LayerDef.isSpace)
            {
                SectorContentService.EnsureSectorGenerated(this, current.Tile);
            }
        }

        // ---- 旧平面存档迁移（仅加载期）----

        [NonSerialized] private Map legacyHostMap;
        [NonSerialized] private Vector2 legacyAnchorDisplacement;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref objects, "objects", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && objects == null)
            {
                // 存档由阶段2 及更早版本保存（旧键名 "ships"，字段结构已变、不做迁移）：
                // XML 加载路径绕过字段初始化器，这里必须兜底，否则 LoadedGame/tick 全链 NRE。
                // 宇宙状态按空处理，用 dev action "Create two-ship test scene" 重建即可。
                Log.Warning("[DeltaV] Save predates phase 3 (no 'objects' list); universe state starts empty. Recreate the test scene via Debug actions.");
                objects = new List<SpaceObject>();
            }
            Scribe_Collections.Look(ref generatedSectorTiles, "generatedSectorTiles", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && generatedSectorTiles == null)
            {
                generatedSectorTiles = new List<string>();
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                cellsPerTileAtSave = DeltaVMod.Settings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
            }
            Scribe_Values.Look(ref cellsPerTileAtSave, "cellsPerTileAtSave", 0);
            Scribe_Values.Look(ref focusedShipId, "focusedShipId", -1);
            Scribe_Values.Look(ref pilotShipId, "pilotShipId", -1);
            Scribe_Values.Look(ref primaryShipId, "primaryShipId", -1);
            Scribe_Values.Look(ref nextFreeOwnerId, "nextFreeOwnerId", 0);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 主菜单改过尺度再读档：对象位姿仍按存档时尺度分布，读档后统一重标定。
                // 旧档无此键（0）视为未知，不做重标定（mod 未发布，无兼容负担）。
                float current = DeltaVMod.Settings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
                if (cellsPerTileAtSave > 0 && !Mathf.Approximately(cellsPerTileAtSave, current))
                {
                    pendingScaleRescaleFrom = cellsPerTileAtSave;
                }
            }
            if (Scribe.mode != LoadSaveMode.Saving)
            {
                // 平面坐标旧档的锚记账（新档不再保存）：hostMap tile 为切平面原点，
                // 绝对偏移 = pose.position + anchorDisplacement。LoadedGame 换算后丢弃。
                // Look 必须在 LoadingVars 与 ResolvingCrossRefs 两个阶段都被调用（Look 内部
                // 自行分支），外层只排除写档阶段以维持"新档不写这些键"；只门 LoadingVars
                // 会让旧档 loadID 无人消费，触发 "Not all loadIDs consumed" 警告。
                Scribe_References.Look(ref legacyHostMap, "hostMap");
                Scribe_Values.Look(ref legacyAnchorDisplacement, "anchorDisplacement", Vector2.zero);
            }
        }

        public override void LoadedGame()
        {
            // 白名单/逆重基架 UV mesh 都是可重建的表现缓存，绝不存档。
            ShellRenderer.ClearRuntimeCaches();
            WorldShipMarkerRenderer.ClearRuntimeCaches();
            PlanetGlobeRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            IncrementalMapGenerationService.Reset();
            pocketHostService?.Reset();
            OrbitSphere.ClearCache();
            SectorContentService.Reset();
            ShipAutopilot.ResetOnLoad();
            LandingApproachController.ResetOnLoad();
            mapFrame = null;
            PilotController.SuppressVanillaKeys = false;
            DockController.RestorePendingAfterLoad(this);
            MigratePlanarSaves();
            MigrateSubstructureOwnership();
            if (pendingScaleRescaleFrom > 0f)
            {
                float current = DeltaVMod.Settings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
                ApplyCellsPerTileScale(pendingScaleRescaleFrom, current);
                pendingScaleRescaleFrom = -1f;
            }
            ClearStalePocketMapTiles();
            // 地图被外部移除等异常情况下的收尾：清掉悬空引用，聚焦无效时退回第一艘活着的船。
            foreach (SpaceObject obj in objects.ToList())
            {
                if (obj.map != null && !Find.Maps.Contains(obj.map))
                {
                    Log.Warning($"[DeltaV] Object #{obj.objectId} ({obj.label}) lost its map after load; clearing map reference.");
                    obj.map = null;
                }
                if (obj is ShipState lost && lost.map == null && !lost.docked)
                {
                    // 飞船没有"删除段"——口袋图丢失属异常，整体移除避免悬空状态。
                    Log.Warning($"[DeltaV] Ship #{lost.objectId} ({lost.label}) lost its map; removing from universe state.");
                    UnregisterObject(lost);
                }
            }
            if (Focused == null)
            {
                ShipState first = objects.OfType<ShipState>().FirstOrDefault(s => s.map != null);
                if (first != null)
                {
                    focusedShipId = first.objectId;
                }
            }
            DockController.CleanupObsoleteDockedPocketMaps(this);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            SweepEmptyDepartedMaps();
        }

        /// <summary>
        /// 读档后清扫遗留的空降落图（历史会话起飞后未清理的积累）。判定与起飞后清理
        /// 同一入口（DockController.TryDeleteEmptyDepartedMap）；延后到加载收尾执行。
        /// </summary>
        private void SweepEmptyDepartedMaps()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                foreach (Map map in Find.Maps.ToList())
                {
                    DockController.TryDeleteEmptyDepartedMap(this, map);
                }
            });
        }

        /// <summary>
        /// 基架归属旧档自愈：归属体系上线前的存档没有归属组件——全部基架格按
        /// 「地图默认归属」（宿主 SpaceObject；停靠图归 wreck POI，口袋图归船本身）
        /// 补齐。无宿主对象的图兜底分配新 ID。仅 Delta-V 参考系图参与。
        /// 存档已带归属组件的图不做整图迁移：「未占有（0）」是合法的持久状态
        /// （孤立建造/争议桥），读档吸收会破坏归属语义。
        /// 定向自愈（口袋图）：一图恒一船，船本人在图上没有任何归属格而图上存在基架
        /// = 早期版本盖章缺口的损伤签名（起飞进口袋未盖章 → 邻接规则分配孤儿 ID；
        /// 到达盖章解析失败拿了新 ID）——整图改归船。刻意在口袋图上拆分独立归属的
        /// 存档船自身仍有归属格，不会命中本分支。
        /// </summary>
        private void MigrateSubstructureOwnership()
        {
            SubstructureOwnership.ResetStatic();
            foreach (Map map in Find.Maps)
            {
                if (FrameOf(map) == null)
                {
                    continue;
                }
                if (SubstructureOwnership.TryGet(map) == null)
                {
                    SubstructureOwnership ownership = SubstructureOwnership.For(map);
                    if (ownership.HasUnassignedSubstructure())
                    {
                        int defaultOwner = SpaceObjectOnMap(map)?.objectId ?? AllocateOwnerId();
                        int migrated = ownership.MigrateUnassigned(defaultOwner);
                        if (migrated > 0)
                        {
                            DeltaVLog.Message(DeltaVLogModule.Core,
                                $"map {map.Index}: migrated {migrated} substructure cells to default owner #{defaultOwner}");
                        }
                    }
                }
                ShipState pocketShip = ShipOnMap(map);
                if (pocketShip == null)
                {
                    continue;
                }
                SubstructureOwnership pocketOwnership = SubstructureOwnership.TryGet(map);
                if (pocketOwnership != null && pocketOwnership.HasAnySubstructure()
                    && pocketOwnership.CountCellsOwnedBy(pocketShip.objectId) == 0)
                {
                    int healed = pocketOwnership.ClaimAllSubstructure(pocketShip.objectId);
                    Log.Message($"[DeltaV] Ship #{pocketShip.objectId} '{pocketShip.label}' owns no substructure "
                        + $"on its pocket map (stale data from a pre-fix build); reclaimed {healed} cells.");
                }
            }
        }

        /// <summary>
        /// 平面坐标旧档 → 球面：以旧 hostMap tile 为切平面原点，把每个对象的
        /// 绝对平面偏移（pose.position + anchorDisplacement）换算成球面方向。
        /// 角度/速度基约定不变（旧平面 x/y 就是东/北），直接沿用。
        /// </summary>
        private void MigratePlanarSaves()
        {
            bool anyLegacy = objects.Any(o => o.pose.legacyPlanar)
                || objects.OfType<ShipState>().Any(s => s.preDockPose.legacyPlanar);
            if (!anyLegacy)
            {
                return;
            }
            PlanetTile anchorTile = legacyHostMap != null && legacyHostMap.Tile.Valid
                ? legacyHostMap.Tile
                : PlanetTile.Invalid;
            if (!anchorTile.Valid)
            {
                // 锚图丢失的兜底：任一停靠图的 tile，否则轨道层首个 tile（偏移语义尽力保留）。
                anchorTile = objects.OfType<ShipState>()
                    .Where(s => s.dockedMap != null && s.dockedMap.Tile.Valid)
                    .Select(s => s.dockedMap.Tile).DefaultIfEmpty(PlanetTile.Invalid).First();
                if (!anchorTile.Valid && OrbitSphere.Valid)
                {
                    anchorTile = new PlanetTile(0, OrbitSphere.Layer);
                }
                if (anchorTile.Valid)
                {
                    Log.Warning("[DeltaV] Planar save lost its host map; migrating positions from a fallback tile anchor.");
                }
            }
            if (!anchorTile.Valid)
            {
                Log.Error("[DeltaV] Cannot migrate planar save positions: no orbit layer tile available.");
                return;
            }
            int migrated = 0;
            foreach (SpaceObject obj in objects)
            {
                if (obj.pose.legacyPlanar)
                {
                    obj.pose.position = PlanarToDirection(anchorTile, obj.pose.legacyPosition + legacyAnchorDisplacement);
                    obj.pose.legacyPlanar = false;
                    migrated++;
                }
                if (obj is ShipState ship && ship.preDockPose.legacyPlanar)
                {
                    ship.preDockPose.position = PlanarToDirection(anchorTile,
                        ship.preDockPose.legacyPosition + legacyAnchorDisplacement);
                    ship.preDockPose.legacyPlanar = false;
                }
            }
            legacyHostMap = null;
            legacyAnchorDisplacement = Vector2.zero;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"migrated {migrated} planar poses to sphere coordinates (anchor tile {anchorTile})");
        }

        /// <summary>旧平面偏移 → 球面方向：tile 中心 + 切平面外推后归一（旧 centerTangent 语义）。</summary>
        private static Vector3 PlanarToDirection(PlanetTile anchorTile, Vector2 absoluteOffset)
        {
            PlanetLayer layer = anchorTile.Layer;
            Vector3 center = layer.GetTileCenter(anchorTile);
            Vector3 up = center.normalized;
            Vector3 north = Vector3.up - up * Vector3.Dot(Vector3.up, up);
            if (north.sqrMagnitude < 1e-6f)
            {
                north = Vector3.forward;
            }
            north.Normalize();
            Vector3 east = Vector3.Cross(up, north);
            Vector3 tangent = east * absoluteOffset.x + north * absoluteOffset.y;
            // 旧平面档全部保存在 CellsPerTile = 100 时代：迁移换算钉死旧尺度字面量，
            // 不随设置项（默认 20000）变化，否则旧档对象会被外推到完全错误的球面位置。
            return (center + tangent * (layer.AverageTileSize / 100f)).normalized;
        }

        /// <summary>
        /// 旧档口袋图残留的真实 tile 清理：去 tile 化后 Delta-V 口袋图不再持有 tile，
        /// 残留会让 Game.FindMap(PlanetTile) 误中口袋图（降落 GetOrGenerateMap 语义被破坏）。
        /// 必须反射直写私有字段——WorldObject.Tile setter 对新 tile 无 Valid 守卫，
        /// 走 setter 置 Invalid 有 Surface 层 FastTileFinder 越界风险。
        /// </summary>
        private static void ClearStalePocketMapTiles()
        {
            var tileField = AccessTools.Field(typeof(WorldObject), "tile");
            if (tileField == null)
            {
                Log.ErrorOnce("[DeltaV] WorldObject.tile field not found; stale pocket map tiles not cleared.", 0x44564343);
                return;
            }
            foreach (PocketMapParent parent in Find.World.pocketMaps)
            {
                if (parent is ShipPocketParent_DeltaV && parent.Tile.Valid)
                {
                    tileField.SetValue(parent, PlanetTile.Invalid);
                    DeltaVLog.Message(DeltaVLogModule.Core,
                        $"{parent.Label}: stale orbit tile cleared from legacy pocket map");
                }
            }
        }

        public override void GameComponentUpdate()
        {
            // 玩家可能经殖民者栏/地图按钮等原版途径切图：focusedShipId 跟随当前船图，
            // 只同步参考系字段，不触碰任何 pose（不变量 #5）。
            ShipState onMap = ShipOnMap(Find.CurrentMap);
            if (onMap != null && onMap.objectId != focusedShipId)
            {
                focusedShipId = onMap.objectId;
            }
            ShipState primary = PrimaryShip;
            if (primary != null && primaryShipId != primary.objectId)
            {
                primaryShipId = primary.objectId;
            }
            UpdateUniverseZoom();
            // 驾驶状态维护（自动退出 + Harmony 按键抑制开关）。
            PilotController.UpdateFrameState(this);
            DebugActions_DeltaV.UpdateFrameMeter();
            // 轨道层通用自动驾驶（帧驱动：降落过场期间 tick 冻结），先于船壳绘制推进位姿。
            ShipAutopilot.Update();
            ShellRenderer.DrawNonFocusedShells(this);
            WorldShipMarkerRenderer.DrawPlayerShips(this);
            ThrusterEffects.Draw(this);
        }

        // 相机缩放放宽（宇宙视野）：进入 Delta-V 参考系（对象图/真实空间图）时记录原 max 并放宽，
        // 离开时恢复。每帧幂等——config 对象可能被原版在切图等时机替换。
        private static float savedVanillaZoomMax = float.NaN;

        private void UpdateUniverseZoom()
        {
            CameraDriver driver = Find.CameraDriver;
            if (driver?.config == null)
            {
                return;
            }
            float target = Mathf.Max(driver.config.sizeRange.min + 1f, DeltaVMod.Settings.universeZoomMax);
            if (FrameOf(Find.CurrentMap) != null)
            {
                if (float.IsNaN(savedVanillaZoomMax))
                {
                    savedVanillaZoomMax = driver.config.sizeRange.max;
                }
                if (driver.config.sizeRange.max < target)
                {
                    driver.config.sizeRange.max = target;
                }
            }
            else if (!float.IsNaN(savedVanillaZoomMax))
            {
                driver.config.sizeRange.max = savedVanillaZoomMax;
                savedVanillaZoomMax = float.NaN;
            }
        }

        /// <summary>
        /// 当前相机上限是否处于 Delta-V 放宽状态；是则给出放宽前的原版值。
        /// 供 GravshipCapturer 快照补丁在捕获窗口内临时还原原版缩放包络。
        /// </summary>
        internal static bool TryGetVanillaZoomMax(out float vanillaMax)
        {
            vanillaMax = savedVanillaZoomMax;
            return !float.IsNaN(savedVanillaZoomMax);
        }

        /// <summary>
        /// 阶段2 物理积分（TickManager 每 tick 调用，暂停时冻结——正确的模拟语义）。
        /// 驾驶船按输入+辅助出力积分；其余船只在有速度/角速度时漂移积分。
        /// 只写 SpacePose（Map 外的宇宙状态），绝不触碰任何 Map 内 Thing（不变量 #6）。
        /// </summary>
        public override void GameComponentTick()
        {
            const float dt = 1f / 60f;
            ShipState piloted = PilotController.PilotedShip(this);
            bool piloting = piloted?.map != null && Find.CurrentMap == piloted.map;
            foreach (SpaceObject obj in objects)
            {
                if (!(obj is ShipState ship) || ship.map == null)
                {
                    continue;
                }
                if (ship.docked || ship.docking)
                {
                    continue; // 停靠/选点中：船体不在活动口袋图上，宇宙状态冻结
                }
                if (ShipAutopilot.IsActive(ship))
                {
                    continue; // 自动驾驶中的船由 ShipAutopilot 帧驱动，避免双重积分
                }
                bool isPiloted = piloting && ship == piloted;
                bool drifting = ship.pose.velocity.sqrMagnitude > 1e-10f
                    || Mathf.Abs(ship.pose.angularVelocity) > 1e-10f;
                if (!isPiloted && !drifting)
                {
                    continue;
                }
                ShipPhysics.MassReport report = ShipPhysics.GetReport(ship);
                if (report == null)
                {
                    continue;
                }
                Vector2 force = Vector2.zero;
                float torque = 0f;
                bool assistX = false;
                bool assistZ = false;
                bool assistW = false;
                if (isPiloted)
                {
                    PilotController.PilotCommand command = PilotController.ComposeCommand(ship);
                    ShipPhysics.Allocate(report, command.command, out force, out torque);
                    assistX = command.assistX;
                    assistZ = command.assistZ;
                    assistW = command.assistW;
                    LogEngineFiringTransition(ship, report, force, torque);
                }
                ShipPhysics.Integrate(ship, report, force, torque, dt, assistX, assistZ, assistW);
            }
            // POI 地图生命周期三段扫描（单一入口 SpaceLifecycle）+ 星区记账触发
            //（移动模式下 tile 地址变化进入新星区时首次生成，星区架构 §4）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                SpaceLifecycle.Tick(this);
                SectorContentService.TickSectorTracking(this);
            }
        }

        // 物理埋点（触发式）：只记录引擎点火/停火的转换沿，杜绝持续出力期间的每 tick 输出。
        // 非 readonly：GameComponent 的 XML 加载路径（GetUninitializedObject）不跑字段初始化器，
        // readonly 字段会停留在 null 且无法补偿；这里用惰性属性兜底。
        private Dictionary<int, bool> enginesFiringByShip;

        private Dictionary<int, bool> EnginesFiringByShip =>
            enginesFiringByShip ?? (enginesFiringByShip = new Dictionary<int, bool>());

        private void LogEngineFiringTransition(ShipState ship, ShipPhysics.MassReport report, Vector2 force, float torque)
        {
            bool firing = force.sqrMagnitude > 1e-6f || Mathf.Abs(torque) > 1e-6f;
            bool was = EnginesFiringByShip.TryGetValue(ship.objectId, out bool previous) && previous;
            if (firing == was)
            {
                return;
            }
            EnginesFiringByShip[ship.objectId] = firing;
            DeltaVLog.Message(DeltaVLogModule.Physics, firing
                ? $"{ship.label}: thrusters firing | F=({force.x:F0},{force.y:F0}) tau={torque:F0} | mass={report.mass:F0} I={report.inertia:F0} thrusters={report.thrusters.Count}"
                : $"{ship.label}: engines stopped | v=({ship.pose.velocity.x:F2},{ship.pose.velocity.y:F2}) omega={ship.pose.angularVelocity:F3}");
        }

        public override void GameComponentOnGUI()
        {
            // 驾驶模式切换键（本 mod 分类的绑定不受抑制影响，驾驶中仍可退出）。
            if (DeltaVDefOf.DeltaV_TogglePilotMode != null && DeltaVDefOf.DeltaV_TogglePilotMode.KeyDownEvent)
            {
                PilotController.Toggle(this);
            }
            ShipStatusPanel.Draw(this);
            Designator_ReassignSubstructure.DrawPendingLabels();
            DebugActions_DeltaV.DrawFrameMeter();
            HandleShellClick();
            HandleWorldMarkerClick();
        }

        // ---- 世界视图剪影点选（聚焦切图；与地图外壳点选对称的世界视图通道）----

        private ShipState pendingWorldClickShip;
        private Vector2 pendingWorldClickScreen;

        /// <summary>
        /// 星球视图里点击玩家飞船剪影 → 聚焦切图。让位规则与 HandleShellClick 同型：
        /// 悬停 UI 优先、按下-抬起间拖拽取消、命中才动作——剪影不是 WorldObject，
        /// 不吞并无命中时的原版世界点击（拾取数学见 WorldShipMarkerRenderer.TryGetScreenPosition）。
        /// </summary>
        private void HandleWorldMarkerClick()
        {
            if (Find.World?.renderer == null
                || Find.World.renderer.wantedMode != RimWorld.Planet.WorldRenderMode.Planet)
            {
                pendingWorldClickShip = null;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingWorldClickShip = null;
                if (Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(ev.mousePosition)) != null) return;
                pendingWorldClickShip = PickWorldMarker(ev.mousePosition);
                pendingWorldClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingWorldClickShip = null;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingWorldClickShip != null)
            {
                ShipState target = pendingWorldClickShip;
                pendingWorldClickShip = null;
                bool noDrag = (ev.mousePosition - pendingWorldClickScreen).sqrMagnitude < 100f;
                if (noDrag && PickWorldMarker(ev.mousePosition) == target)
                {
                    Map targetMap = target.docked ? target.dockedMap : target.map;
                    if (targetMap != null)
                    {
                        SetFocused(target);
                        CameraJumper.TryJump(targetMap.Center, targetMap, CameraJumper.MovementMode.Cut);
                    }
                }
            }
        }

        private ShipState PickWorldMarker(Vector2 mousePos)
        {
            if (!OrbitSphere.Valid) return null;
            ShipState best = null;
            float bestDistance = float.MaxValue;
            foreach (ShipState ship in Ships)
            {
                if (!ship.isPlayerShip || ship.docking) continue;
                if (!WorldShipMarkerRenderer.TryGetScreenPosition(ship, out Vector2 pos, out float radius)) continue;
                float distance = (mousePos - pos).magnitude;
                if (distance <= radius && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = ship;
                }
            }
            return best;
        }

        // ---- 外壳点选（选择层；瞄准兼容契约见 ShellPicking 类注释）----

        private SpaceObject pendingClickObject;
        private Vector2 pendingClickScreen;

        /// <summary>
        /// 点击显示中的宇宙对象外壳 → 聚焦切换到对应地图（飞船与 POI 通用）。
        /// 让位规则：瞄准会话（Targeter 激活）或建造设计器选中时完全不参与；
        /// 悬停 UI、原版可选物优先；按下-抬起间发生拖拽（框选）即取消。
        /// 不消费事件，原版点击流程不受影响。
        /// </summary>
        private void HandleShellClick()
        {
            Map current = Find.CurrentMap;
            if (current == null || !RimWorld.Planet.WorldRendererUtility.DrawingMap)
            {
                pendingClickObject = null;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingClickObject = null;
                bool targeting = Find.Targeter.IsTargeting;
                bool designator = Find.DesignatorManager.SelectedDesignator != null;
                bool overWindow = Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(ev.mousePosition)) != null;
                if (targeting || designator || overWindow)
                {
                    return;
                }
                IntVec3 mouseCell = UI.MouseMapPosition().ToIntVec3();
                bool selectableHere = mouseCell.InBounds(current) && mouseCell.GetThingList(current).Any(ThingSelectionUtility.SelectableByMapClick);
                if (selectableHere)
                {
                    return;
                }
                pendingClickObject = ShellPicking.ShellAtFramePoint(this, current, UI.MouseMapPosition());
                pendingClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingClickObject = null;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingClickObject != null)
            {
                SpaceObject target = pendingClickObject;
                pendingClickObject = null;
                bool noDrag = (ev.mousePosition - pendingClickScreen).sqrMagnitude < 100f;
                SpaceObject recheck = ShellPicking.ShellAtFramePoint(this, current, UI.MouseMapPosition());
                if (noDrag && recheck == target)
                {
                    // 相机跳到点击位置对应的对象内格（clamp 进地图），落点即所见。
                    SpaceObject frame = FrameOf(current);
                    Vector2 frameLocal = SpaceTransform.MapToShipLocal(current, new Vector2(UI.MouseMapPosition().x, UI.MouseMapPosition().z));
                    Vector3 universeDir = SpaceTransform.FocusedToUniverse(frame, frameLocal);
                    ShipState dockedTarget = target as ShipState;
                    Map targetMap = dockedTarget != null && dockedTarget.docked
                        ? dockedTarget.dockedMap
                        : target.map;
                    Vector2 mapPoint = dockedTarget != null && dockedTarget.docked
                        ? SpaceTransform.UniverseToDockedMapPoint(this, dockedTarget, universeDir)
                        : SpaceTransform.UniverseToMapPoint(target, universeDir);
                    int x = Mathf.Clamp(Mathf.FloorToInt(mapPoint.x), 2, targetMap.Size.x - 3);
                    int z = Mathf.Clamp(Mathf.FloorToInt(mapPoint.y), 2, targetMap.Size.z - 3);
                    // 缩放同步（RimExodus SeamlessCameraFocus 同款结论）：CurrentMap setter 的
                    // Notify_SwitchedMap 会用目标图 rememberedCameraPos 同时恢复位置+缩放，
                    // 只 TryJump 恢复位置不够，必须记录 RootSize 并在跳转后显式重设。
                    float zoom = Find.CameraDriver.RootSize;
                    SetFocused(target);
                    CameraJumper.TryJump(new IntVec3(x, 0, z), targetMap, CameraJumper.MovementMode.Cut);
                    Find.CameraDriver.SetRootSize(zoom);
                }
            }
        }

        /// <summary>
        /// 切换聚焦参考系。只设置 CurrentMap——原版 setter 负责关闭 targeter、清选区、
        /// 恢复该图记忆的相机位置。宇宙姿态不受影响。
        /// </summary>
        public void SetFocused(SpaceObject obj)
        {
            Map targetMap = obj is ShipState dockedShip && dockedShip.docked
                ? dockedShip.dockedMap
                : obj?.map;
            if (targetMap == null)
            {
                Log.Error($"[DeltaV] SetFocused: object #{obj?.objectId} has no live map.");
                return;
            }
            if (obj is ShipState ship)
            {
                focusedShipId = ship.objectId;
            }
            Current.Game.CurrentMap = targetMap;
            // 与 PocketMapUtility 一致：确保回到地图视图而不是世界球面。
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
        }

        public void ResetObjects(List<SpaceObject> newObjects, int focusId)
        {
            ShellRenderer.ClearRuntimeCaches();
            WorldShipMarkerRenderer.ClearRuntimeCaches();
            PlanetGlobeRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            IncrementalMapGenerationService.Reset();
            SectorContentService.Reset();
            PilotController.Exit(this);
            objects = newObjects;
            focusedShipId = focusId;
            mapFrame = null;
        }
    }
}

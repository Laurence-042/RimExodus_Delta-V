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
    /// 无需 XML。objects 列表（**地图容器** + POI，解耦架构 §2）是权威宇宙状态；
    /// 飞船不是宇宙对象——船 = 主台 + 同归属连通域（ShipRegistry 派生查询）。
    /// 外壳代理、拼接名、控制域等全部是可重建缓存。
    ///
    /// 职责边界：
    /// - 非聚焦对象代理绘制在 GameComponentUpdate 分发（WorldComponent_GravshipController 同款节奏）；
    /// - SetFocused 只切换参考系（CurrentMap），绝不修改任何 pose 数值（不变量 #5）；
    /// - 聚焦 = 当前聚焦地图的承载容器（FrameOf(CurrentMap)），不再持久化聚焦 ID；
    /// - POI 地图生命周期统一委托 SpaceLifecycle（单一管理入口，AGENTS §11）；
    /// - 宿主：所有 Delta-V 口袋图（飞船/POI）共用的 sourceMap 锚——一张专用的
    ///   无 tile 口袋图（UniverseHostParent_DeltaV，仅存在于 Find.World.pocketMaps），
    ///   永不聚焦、永不出现在世界视图、tick 常冻结。它不是宇宙坐标锚（坐标已是
    ///   球面绝对坐标），只承载原版 storyteller/财富/移除级联从口袋图重定向到
    ///   sourceMap 的引擎契约。
    /// </summary>
    public class UniverseManager : GameComponent
    {
        internal ShipPlannerService ShipPlanner { get; } = new ShipPlannerService();
        /// <summary>权威宇宙对象列表（地图容器 + POI，多态深序列化）。存档事实。</summary>
        public List<SpaceObject> objects = new List<SpaceObject>();

        [NonSerialized] private Dictionary<int, SpaceObject> objectById;
        [NonSerialized] private Dictionary<Map, SpaceObject> objectByMap;

        /// <summary>
        /// 已触发生成的星区（持久事实，星区架构 §4）：元素为 PlanetTile 的 "tileId,layerId"。
        /// 「该星区已触发生成」不能从现存对象推导（对象会被销毁），必须持久化防重复刷对象；
        /// 「该星区当前有哪些对象」则由 objects + TileFromDirection 查询时重建，不序列化。
        /// </summary>
        public List<string> generatedSectorTiles = new List<string>();

        /// <summary>
        /// 专用宿主口袋图（运行时引用，不序列化——按 def 在 pocketMaps 中重扫即可恢复，
        /// 空图无事实价值可随时重建，AGENTS §15）。经 GetOrCreateHostMap 惰性获取。
        /// </summary>
        [NonSerialized] private PocketMapHostService pocketHostService;
        private void ConfigureMapFramework()
        {
            // REF is the only backend. Installation failure must not silently select retired code.
            RimExodus.Maps.MapModule.Install();
            RimExodus.Maps.MapRuntime.Current.SetStopPolicy(SharedMapBackend.Owner, map =>
                SpaceLifecycleService.IsUniverseHost(map) || Generation.IsGenerating(map));
        }

        // ---- 流程服务实例（2026-09-22 实例化迁移）----
        // 对接计划/自动驾驶/跳帮舱/模式互转流程状态全部收进实例服务：读档时反序列化器
        // 重建组件，未进 ExposeData 的实例字段即自动归零——「读其他档残留旧流程」不可
        // 表示，不再需要读档清扫。GameComponent 构造路径存在不跑字段初始化器的情况，一律走惰性属性自愈。

        [NonSerialized] private ShipAutopilot autopilot;

        public ShipAutopilot Autopilot => autopilot ?? (autopilot = new ShipAutopilot());

        [NonSerialized] private NpcShipAiService npcShips;

        public NpcShipAiService NpcShips => npcShips ?? (npcShips = new NpcShipAiService(this));

        [field: NonSerialized]
        internal event Action<SpaceObject, SpaceObject> RepresentationChanged;

        /// <summary>帧边界表示替换；身份与绝对位姿不变，不触发注销／新生。</summary>
        internal void ReplaceRepresentation(SpaceObject previous, SpaceObject current)
        {
            int index = objects.IndexOf(previous);
            if (index < 0 || previous.objectId != current.objectId || !previous.pose.Equals(current.pose))
                throw new InvalidOperationException("Invalid space object representation replacement");
            current.RepresentationVersion = previous.RepresentationVersion + 1;
            globeDisplay?.Forget(previous.map);
            foundationTopology?.Forget(previous.map);
            stationTrade?.Forget(previous.map);
            PlanetGlobeRenderer.InvalidateSilhouette(previous.map);
            objects[index] = current;
            globeNavigation?.InvalidateEntries();
            if (objectById != null) objectById[current.objectId] = current;
            if (objectByMap != null)
            {
                if (previous.map != null) objectByMap.Remove(previous.map);
                if (current.map != null) objectByMap[current.map] = current;
            }
            scatter?.Remove(previous);
            if (current.IsScattered) Scatter.Register(current);
            SubstructureOwnership.BumpStructureVersion(current.map);
            try { Autopilot.Detach(previous); previous.ClearDrive(); }
            catch (Exception ex) { Log.Error($"[DeltaV] Representation #{current.objectId} changed, but old drive cleanup failed: {ex}"); }
            finally { if (previous.map != current.map) structures?.Forget(previous.map); }
            if (RepresentationChanged == null) return;
            foreach (Action<SpaceObject, SpaceObject> listener in RepresentationChanged.GetInvocationList())
            {
                try { listener(previous, current); }
                catch (Exception ex) { Log.Error($"[DeltaV] Representation #{current.objectId} change listener failed: {ex}"); }
            }
        }

        [NonSerialized] private ShipControlService controls;

        [NonSerialized] private FoundationTopologyService foundationTopology;
        [NonSerialized] private StructureVersions structures;
        internal StructureVersions Structures => structures ?? (structures = new StructureVersions());
        internal FoundationTopologyService FoundationTopology => foundationTopology
            ?? (foundationTopology = new FoundationTopologyService());

        public ShipControlService Controls => controls ?? (controls = new ShipControlService(this));

        internal SpaceMissionState missionState = new SpaceMissionState();
        [NonSerialized] private SpaceMissionService missions;
        internal SpaceMissionService Missions => missions ?? (missions = new SpaceMissionService(this));
        internal TutorialRunState tutorialRun { get => missionState.tutorial; set => missionState.tutorial = value; }
        [NonSerialized] private DeltaVTutorialService tutorial;
        internal DeltaVTutorialService Tutorial => tutorial ?? (tutorial = new DeltaVTutorialService(this));

        [NonSerialized] private GlobeDisplayInfo globeDisplay;
        internal GlobeDisplayInfo GlobeDisplay => globeDisplay ?? (globeDisplay = new GlobeDisplayInfo());
        private List<GlobeTarget> globeBookmarks = new List<GlobeTarget>();
        internal GlobeFilters GlobeFilters = new GlobeFilters();
        [NonSerialized] private GlobeNavigation globeNavigation;
        internal GlobeNavigation GlobeNavigation => globeNavigation ?? (globeNavigation =
            new GlobeNavigation(globeBookmarks ?? (globeBookmarks = new List<GlobeTarget>())));
        [NonSerialized] internal TargetCameraFocus TargetCamera;

        [NonSerialized] private SpaceProjectileService projectiles;

        [NonSerialized] private DeltaVPerformanceProbe performance;
        internal DeltaVPerformanceProbe Performance => performance ?? (performance = new DeltaVPerformanceProbe());

        [NonSerialized] private SpaceMapPool mapPool;
        public SpaceMapPool MapPool => mapPool ?? (mapPool = new SpaceMapPool(this));
        [NonSerialized] private SpaceScatterService scatter;
        public SpaceScatterService Scatter => scatter ?? (scatter = new SpaceScatterService(this));
        private int unpublishedPayloads;
        internal bool HasUnpublishedPayload => unpublishedPayloads > 0;
        internal Pawn TransferringPawn { get; private set; }

        internal SpaceObject CapturePayload(Thing entity, SpacePose pose, Faction faction,
            Map source = null, IntVec3 cell = default, ThingOwner holder = null, bool publishAtFrame = false)
        {
            AbstractThing payload = AbstractThing.Create(entity);
            if (payload == null) return null;
            var obj = new SpaceObject(AllocateOwnerId(), pose) { Payload = payload, faction = faction };
            Pawn previousPawn = TransferringPawn;
            if (entity is Pawn pawn) TransferringPawn = pawn;
            unpublishedPayloads++;
            bool queued = false;
            try
            {
                if (!payload.Capture(entity, source, cell, holder)) return null;
                if (publishAtFrame)
                {
                    QueueFrameAction(() =>
                    {
                        try { RegisterObject(obj); }
                        finally { unpublishedPayloads--; }
                    });
                    queued = true;
                }
                else RegisterObject(obj);
                return obj;
            }
            finally
            {
                if (!queued) unpublishedPayloads--;
                TransferringPawn = previousPawn;
            }
        }
        [NonSerialized] private SoftDockService softDock;
        public SoftDockService SoftDock => softDock ?? (softDock = new SoftDockService(this));
        internal bool SoftDockEnabled;
        [NonSerialized] private StationTradeService stationTrade;
        internal StationTradeService StationTrade => stationTrade ?? (stationTrade = new StationTradeService());
        [NonSerialized] private SpaceCargoMassUpdates cargoMassUpdates;
        internal SpaceCargoMassUpdates CargoMassUpdates => cargoMassUpdates ??= new SpaceCargoMassUpdates();
        internal bool ShowThrusterDebugOverlay;
        internal bool ShowCoordinateGrid = true;
        internal bool ConfirmSplitWarnings = true;
        internal bool ShowWeaponArcOverlay;
        internal bool ShowCollisionDebugOverlay;
        [NonSerialized] private OrbitalWorkService worksites;
        public OrbitalWorkService Worksites => worksites ?? (worksites = new OrbitalWorkService(this));

        /// <summary>
        /// 弹体服务（太空战架构 §4.1：取代 BoardingPods 槽位，段7 完成接管）：弹体
        /// 创建/注册、制导、超时、碰撞调度对接、渲染、图池。实例服务不序列化、读档归零。
        /// </summary>
        public SpaceProjectileService Projectiles
        {
            get
            {
                if (projectiles == null)
                {
                    projectiles = new SpaceProjectileService(this);
                    RepresentationChanged += projectiles.OnRepresentationChanged;
                }
                return projectiles;
            }
        }

        [NonSerialized] private SpaceWeaponService weapons;

        /// <summary>
        /// 武器服务（太空战架构 §5/§6）：武器索引、炮手分配与唯一发射事务。
        /// 逐 tick 战斗调度归 Controls；本服务不序列化、读档重建（武器持久配置在
        /// 武器 comp、激活位在主驾驶台 comp）。
        /// </summary>
        public SpaceWeaponService Weapons => weapons ?? (weapons = new SpaceWeaponService(this));

        [NonSerialized] private SpaceShuttleService shuttles;

        /// <summary>
        /// shuttle 服务（太空战架构 §7/§10）：起飞/侵入编排（弹体角色接管）；停靠直接用
        /// 对接计划服务。实例服务不序列化、读档归零（侵入计划 = 运行时数据读档丢弃）。
        /// </summary>
        public SpaceShuttleService Shuttles => shuttles ?? (shuttles = new SpaceShuttleService(this));

        [NonSerialized] private LandingApproachController approach;

        public LandingApproachController Approach => approach ?? (approach = new LandingApproachController());

        [NonSerialized] private PilotService pilot;

        public PilotService Pilot => pilot ?? (pilot = new PilotService(this));

        [NonSerialized] private PlayerFrameInput playerInput;
        internal PlayerFrameInput PlayerInput => playerInput ?? (playerInput = new PlayerFrameInput(this));

        internal void ForgetPilotController(ShipController controller) => pilot?.ForgetController(controller);

        [NonSerialized] private ShipFlightTransitionService shipTransitions;

        internal ShipFlightTransitionService ShipTransitions =>
            shipTransitions ?? (shipTransitions = new ShipFlightTransitionService(this));

        [NonSerialized] private SpaceLifecycleService lifecycle;

        public SpaceLifecycleService Lifecycle => lifecycle ?? (lifecycle = new SpaceLifecycleService(this));

        public IncrementalMapGenerationService Generation =>
            Current.Game?.GetComponent<IncrementalMapGenerationService>();

        /// <summary>当前聚焦的承载容器（= 当前聚焦地图的容器；无则 null）。聚焦不再持久化。</summary>
        public SpaceObject Focused => SpaceObjectOnMap(Find.CurrentMap);

        /// <summary>已加载图上的玩家船（显示与交互用，非生命周期参考）。</summary>
        public List<ShipBase> PlayerShips() => objects.Where(obj => obj.map != null)
            .SelectMany(obj => obj.Ships)
            .Where(ship => ship.PlayerMayOperate).ToList();

        /// <summary>POI 距离与星区生成共用的对象参考点；不读取船型、驾驶台或驾驶状态。</summary>
        public List<SpaceObject> PlayerReferenceObjects()
        {
            Faction player = Faction.OfPlayerSilentFail;
            return objects.Where(obj => obj.HasTag(SpaceObjectTag.Ship)
                && (obj.HasFaction(player) || obj.map != null
                    && obj.Ships.Any(ship => ship.PlayerMayOperate)
                    && (obj.map == Find.CurrentMap || SpaceMapGovernance.HasPlayerPawn(obj.map)))).ToList();
        }

        internal readonly Dictionary<int, PlanetTile> sectorReferenceTiles = new Dictionary<int, PlanetTile>();

        /// <summary>
        /// 玩家主参考船（背景相机的回退锚）：优先聚焦图上的玩家船，
        /// 否则任意一艘活着的玩家船；无则 Invalid。
        /// </summary>
        public ShipBase PrimaryPlayerShip()
        {
            List<ShipBase> ships = PlayerShips();
            return ships.FirstOrDefault(ship => ship.Map == Find.CurrentMap)
                ?? ships.FirstOrDefault() ?? ShipBase.Invalid;
        }

        public SpaceObject ObjectById(int id)
        {
            if (id <= 0) return null;
            if (objectById == null)
            {
                objectById = new Dictionary<int, SpaceObject>(objects.Count);
                foreach (SpaceObject obj in objects)
                    if (obj != null && obj.objectId > 0 && !objectById.ContainsKey(obj.objectId))
                        objectById.Add(obj.objectId, obj);
            }
            return objectById.TryGetValue(id, out SpaceObject found) ? found : null;
        }

        // ---- 对象/结构 ID（单一计数器；语义分离）----

        /// <summary>
        /// 已分配 ID 的高水位（持久化、单调递增、永不回收）。对象 ID 与结构归属 ID
        /// 共用同一计数器与号段（防撞号），但**语义分离**（解耦架构 §2）：不得用
        /// ObjectById(ownerId) 推断船名或地图归属。曾经的双计数器事故（2026-09-20：
        /// 裸 ID 与新注册对象撞号 → 域名反查错对象、异主域同号合域）由此防复发。
        /// </summary>
        private int nextFreeOwnerId;

        /// <summary>
        /// 分配新的对象/结构归属 ID（唯一入口；新容器/新归属/残骸断片一律经此，
        /// 不得再各自扫 objects 取 max+1）。读档后由 SyncOwnerIdFloor 对齐下限。
        /// </summary>
        public int AllocateOwnerId()
        {
            if (nextFreeOwnerId <= 0)
            {
                nextFreeOwnerId = 1; // 0 = Unassigned，绝不能发出
            }
            return nextFreeOwnerId++;
        }

        /// <summary>
        /// 读档对齐：抬到 max(已存值, 全部注册对象 ID, 全部图归属格最大 ID, 全部驾驶台身份)。
        /// 旧档撞号不自动拆（无法消歧），玩家经「修改基架归属」修复。
        /// </summary>
        private void SyncOwnerIdFloor()
        {
            int maxOccupied = 0;
            foreach (SpaceObject obj in objects)
            {
                maxOccupied = Mathf.Max(maxOccupied, obj.objectId);
            }
            foreach (Map map in Find.Maps)
            {
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
                if (ownership != null)
                {
                    maxOccupied = Mathf.Max(maxOccupied, ownership.MaxAssignedOwner());
                }
                // 驾驶台 comp 持久持有的结构身份 ID 也要占据号段。
                foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
                {
                    CompDeltaVPilotConsole console = thing.TryGetComp<CompDeltaVPilotConsole>();
                    if (console != null)
                    {
                        maxOccupied = Mathf.Max(maxOccupied, console.shipId);
                    }
                }
            }
            nextFreeOwnerId = Mathf.Max(Mathf.Max(nextFreeOwnerId, 1), maxOccupied + 1);
        }

        // ---- 结构名称注册表（归属 ID → 名称；单一名称事实，解耦架构 §3.3/§10）----

        private Dictionary<int, string> structureNames = new Dictionary<int, string>();

        private static int structureNameVersion;

        /// <summary>名称版本（单调递增）：拼接名等派生缓存据此失效。</summary>
        public static int StructureNameVersion => structureNameVersion;

        /// <summary>登记/更新结构名称（船名 = 归属名）。改名唯一入口见 RenameStructure。</summary>
        public void NoteStructureName(int ownerId, string name)
        {
            if (ownerId > 0 && !name.NullOrEmpty()
                && (!structureNames.TryGetValue(ownerId, out string existing) || existing != name))
            {
                structureNames[ownerId] = name;
                structureNameVersion++;
            }
        }

        /// <summary>
        /// 改名唯一入口（驾驶台改名 gizmo 与引擎 RenamableLabel 同步都走这里）：
        /// 修改归属名 = 修改船名，并同步本船控制域范围内的全部逆重引擎 RenamableLabel
        /// （只同步属于该船的引擎，不取同图任意引擎）。
        /// </summary>
        public void RenameStructure(int ownerId, string name)
        {
            if (ownerId <= 0 || name.NullOrEmpty()) return;
            NoteStructureName(ownerId, name.Trim());
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (ship.IsValid)
            {
                foreach (Thing thing in ship.map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
                {
                    if (ShipRegistry.Contains(ship, thing) && thing is Building_GravEngine engine)
                    {
                        engine.RenamableLabel = name.Trim();
                    }
                }
            }
            DeltaVLog.Message(DeltaVLogModule.Core, $"structure #{ownerId} renamed to '{name.Trim()}'");
        }

        /// <summary>新残骸断片的默认名：「残骸 #N」（N = 新归属 ID，确定性、无需额外计数器）。</summary>
        public string NewWreckName(int ownerId)
        {
            return "DeltaV_WreckDefaultName".Translate(ownerId);
        }

        /// <summary>归属 ID 的显示名：名称注册表 → 「未注册结构」兜底。不再反查对象（ID 语义分离）。</summary>
        public string LabelForOwner(int ownerId)
        {
            if (ownerId <= 0)
            {
                return "DeltaV_OwnerUnassigned".Translate();
            }
            return structureNames.TryGetValue(ownerId, out string name)
                ? name
                : "DeltaV_OwnerUnregistered".Translate(ownerId);
        }

        /// <summary>
        /// 诊断用的图标识（全项目单实现，日志/dump 统一）：uniqueID 全会话稳定（图增删
        /// 不变），Index 是 Find.Maps 列表位（增删洗牌，仅供对照 PerfProbe）。两套数字
        /// 不共线——只看 uniqueID 定位图，Index 只用于和性能日志对表。
        /// </summary>
        public static string MapTag(Map map)
        {
            return map == null ? "map=null" : $"map#{map.uniqueID}[{map.Index}]{map.Size.x}x{map.Size.z}";
        }

        /// <summary>宇宙对象增删的唯一入口（AGENTS §11 单一管理入口精神）；星区记账与 id 语义在此收口。</summary>
        public void RegisterObject(SpaceObject obj)        {
            if (obj == null || objects.Contains(obj)) return;
            SpaceObject collision = ObjectById(obj.objectId);
            if (obj.objectId <= 0 || collision != null)
            {
                Log.Error($"[DeltaV] Refusing to register object '{obj.label}' with invalid/colliding ID " +
                          $"#{obj.objectId} (existing='{collision?.label ?? "none"}').");
                return;
            }
            // map 单一持有（2026-09-25 不变量）：一张图只允许一个承载对象——SpaceObjectOnMap
            // 取列表首个命中，双持有会让第二个对象绕过所有「自身容器」排除（对接目标
            // 泄漏事故）并遮蔽 FrameOf。注册期硬校验，复现即在此报错点名泄漏源。
            if (obj.map != null)
            {
                SpaceObject mapHeld = SpaceObjectOnMap(obj.map);
                if (mapHeld != null)
                {
                    Log.Error($"[DeltaV] Refusing to register object '{obj.label}' #{obj.objectId}: " +
                              $"map already held by '{mapHeld.label}' #{mapHeld.objectId}.");
                    return;
                }
            }
            objects.Add(obj);
            objectById?.Add(obj.objectId, obj);
            if (obj.map != null && objectByMap != null) objectByMap[obj.map] = obj;
            if (obj.map != null && obj.map == Find.CurrentMap) ApplyCameraZoomScope();
            if (obj.IsScattered) Scatter.Register(obj);
            if (obj.map != null && Find.UIRoot is UIRoot_Play)
                HostColonistBarVisibility.NotifyDockingStateChanged();
            if (obj.map != null) SubstructureOwnership.BumpStructureVersion(obj.map);
            if (obj.objectId >= nextFreeOwnerId) nextFreeOwnerId = obj.objectId + 1;
            if (obj.map != null && obj.HasTag(SpaceObjectTag.Ship) == true)
            {
                Weapons.RegisterMapWeapons(obj.map);
                foreach (ShipBase ship in obj.Ships) Controls.For(ship);
            }
        }

        public void UnregisterObject(SpaceObject obj)
        {
            if (obj == null || !objects.Contains(obj)) return;
            if (!obj.IsTransient
                && DeltaVLog.Enabled(DeltaVLogModule.Lifecycle))
            {
                var caller = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod();
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"object unregister tick={Find.TickManager.TicksGame} id=#{obj.objectId} " +
                    $"label='{obj.label}' baseTag={obj.baseTag} " +
                    $"projectileRole={obj.projectileRoleOverride} " +
                    $"map={MapTag(obj.map)} registered={objects.Contains(obj)} " +
                    $"caller={caller?.DeclaringType?.Name}.{caller?.Name}");
            }
            // Resolve projectile resources while the old identity is still registered.
            Projectiles.NotifyContainerRemoving(obj);
            objects.Remove(obj);
            globeNavigation?.ForgetObject(obj.objectId);
            if (globeNavigation == null) globeBookmarks?.RemoveAll(b => b.objectId == obj.objectId);
            objectById?.Remove(obj.objectId);
            if (obj.map != null && objectByMap != null
                && objectByMap.TryGetValue(obj.map, out SpaceObject mapped) && mapped == obj)
                objectByMap.Remove(obj.map);
            if (obj.map != null && obj.map == Find.CurrentMap) ApplyCameraZoomScope();
            scatter?.Remove(obj);
            obj.Payload?.DestroyContents();
            if (obj.map != null && Find.UIRoot is UIRoot_Play)
                HostColonistBarVisibility.NotifyDockingStateChanged();
            globeDisplay?.Forget(obj.map);
            PlanetGlobeRenderer.InvalidateSilhouette(obj.map);
            Autopilot.Detach(obj);
            Shuttles.ForgetContainer(obj);
            foundationTopology?.Forget(obj.map);
            stationTrade?.Forget(obj.map);
            playerInput?.Clear();
            // 最后一艘船消失后 Ship tag 已移除，释放不能再以当前角色为前提。
            if (obj.map != null) Weapons.ForgetMap(obj.map);
            if (obj.map != null) { softDock?.Forget(obj.map); scatter?.Forget(obj.map); }
            // 被注销对象的 ID 仍被驾驶台 comp / 图上归属格持有（战略飞行中的船、走距离
            // 生命周期的残骸），推过高水位绝不再发——否则新星区生成物会领走同号，
            // 异主域同号合并（2026-09-20 撞号实测）。
            if (obj.objectId >= nextFreeOwnerId)
            {
                nextFreeOwnerId = obj.objectId + 1;
            }
            // POI 注销时取消其排队中的生成（生成闭包捕获实例，不取消会给死实例赋
            // map 产出孤儿图）并清理失败退避记录。
            if (obj is PoiState poi)
            {
                Lifecycle.NotifyUnregistered(poi);
            }
            structures?.Forget(obj.map);
        }

        // ---- 尺度重标定（星区架构 §8）----

        /// <summary>存档时的 CellsPerTile（读档后与当前设置不同则触发一次位姿重标定）。</summary>
        private int cellsPerTileAtSave;
        // 上一次生命周期批量检查的原版游戏 tick；只记录时间戳，不维护第二套时钟。
        private int lastLifecycleScanTick = -1;

        /// <summary>读档后待执行的旧尺度（-1 = 无）；对象 cross-ref 解析完成后执行。</summary>
        [NonSerialized] private float pendingScaleRescaleFrom = -1f;

        /// <summary>停靠/起飞流程进行中禁止修改尺度（pending 位姿计算不能中途换尺度）。</summary>
        public bool CanApplyScaleChange()
        {
            if (ShipTransitions.FreeFlightTransitionPending() || Approach.AnyActive) return false;
            WorldComponent_GravshipController controller = Find.GravshipController;
            if (controller != null
                && (controller.LandingAreaConfirmationInProgress || controller.IsGravshipTravelling))
            {
                return false;
            }
            return Current.Game?.Gravship == null;
        }

        /// <summary>
        /// 尺度调整即时生效：全部容器位姿相对所在星区中心按比例缩放，保持各星区
        /// 布局密度（战术格距不变、战略距离随尺度拉长）；速度不缩放（战术手感不变）。
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
                RescalePoseToSector(ref obj.pose.direction, shrink);
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"cellsPerTile rescaled {oldScale:F0} -> {newScale:F0}; {objects.Count} object poses rebased to sector centers (velocities untouched)");
        }

        private static void RescalePoseToSector(ref OrbitSphere.Direction position, float shrink)
        {
            PlanetTile tile = OrbitSphere.TileFromDirection(position.Vector);
            if (!tile.Valid) return;
            var center = new OrbitSphere.Direction(OrbitSphere.TileCenterDirection(tile));
            Vector2 offset = OrbitSphere.Offset(center, position);
            position = OrbitSphere.Move(center, offset * shrink);
        }

        /// <summary>当前地图对应的宇宙容器（无则 null）。显示参考系以此为准。</summary>
        public SpaceObject SpaceObjectOnMap(Map map)
        {
            if (map == null) return null;
            if (objectByMap != null && objectByMap.TryGetValue(map, out SpaceObject cached))
            {
                if (cached.map == map) return cached;
                objectByMap.Remove(map);
            }
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i].map != map) continue;
                if (objectByMap == null) objectByMap = new Dictionary<Map, SpaceObject>();
                objectByMap[map] = objects[i];
                return objects[i];
            }
            return null;
        }

        /// <summary>
        /// 承载指定地图的唯一 SpaceObject；口袋图与真实轨道图同一语义。
        /// 地图生成/读档时由 OrbitalPoiRegistry 预先注册，不能临时合成 tile 帧。
        /// </summary>
        public SpaceObject FrameOf(Map map)
        {
            SpaceObject frame = SpaceObjectOnMap(map);
            if (frame == null && SpaceMapGovernance.IsGoverned(map, this))
            {
                // 生成中的图尚未完成注册：跳过投影，不从 tile 猜一个替身位姿。
                if (unframedMaps == null) unframedMaps = new HashSet<Map>();
                if (unframedMaps.Add(map))
                    Log.Warning($"[DeltaV] Orbital map {map.Index} has no SpaceObject; skipping projection until registered.");
            }
            else if (frame != null)
            {
                unframedMaps?.Remove(map);
            }
            return frame;
        }

        [NonSerialized] private HashSet<Map> unframedMaps;

        internal void ForgetMapRuntimeState(Map map, bool projectileTargetsAlreadyReviewed)
        {
            if (map == null) return;
            ShipPlanner.Forget(map);
            unframedMaps?.Remove(map);
            playerInput?.Clear();
            foundationTopology?.Forget(map);
            globeDisplay?.Forget(map);
            globeNavigation?.InvalidateEntries();
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            projectiles?.ForgetMap(map, projectileTargetsAlreadyReviewed);
            scatter?.Forget(map);
            softDock?.Forget(map);
            stationTrade?.Forget(map);
            cargoMassUpdates?.Forget(map);
            structures?.Forget(map);
        }

        internal void NotifyFoundationChanged(Map map, IntVec3 cell) => foundationTopology?.NotifyChanged(map, cell);
        internal void ForgetFoundationTopology(Map map) => foundationTopology?.Forget(map);
        internal void NotifySupportChanged(Map map, IntVec3 cell) => scatter?.NotifySupportChanged(map, cell);
        internal void NotifyCargoRegistered(Map map, Thing thing, IntVec3 cell) =>
            scatter?.NotifyCargoRegistered(map, thing, cell);
        internal void NotifyCargoDeregistered(Map map, Thing thing, IntVec3 cell) =>
            scatter?.NotifyCargoDeregistered(map, thing, cell);

        // ---- 专用宿主 ----

        /// <summary>
        /// 专用宿主口袋图：所有 Delta-V 口袋图 sourceMap 的统一锚。Tile 恒 Invalid、
        /// 只进 Find.World.pocketMaps（绝不进 Find.WorldObjects）、永不被聚焦。
        /// 复用 DeltaV_ShipPocket 生成器（太空 biome、空 Void 图）。
        /// </summary>
        public Map GetOrCreateHostMap() => GetOrCreateHostMap(float.PositiveInfinity);

        internal Map GetOrCreateHostMap(float capacityScore)
        {
            bool hasHost = (UniverseHostMap != null && !UniverseHostMap.Disposed && Find.Maps.Contains(UniverseHostMap))
                || Find.World.pocketMaps.Any(parent => parent is UniverseHostParent_DeltaV && parent.HasMap);
            if (!MapPool.TryMakeRoom(hasHost ? 1 : 2, capacityScore)) return null;
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
            ConfigureMapFramework();
            ApplyCameraZoomScope();
            OrbitalPoiRegistry.Synchronize(this);
            Weapons.RebuildWeaponIndex();
            Controls.Rebuild();
            // 开局出生星区在世界生成时预生成（星区架构 §4 触发时机）。
            Map current = Find.CurrentMap;
            if (current != null && current.Tile.Valid && current.Tile.LayerDef.isSpace)
            {
                SectorContentService.EnsureSectorGenerated(this, current.Tile);
            }
            if (tutorialRun != null) Tutorial.Resume();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 弹体是瞬态成员（太空战架构 §4.4）：写档按 tag 过滤剔除纯弹体
                // 侵入船体只有临时弹体角色，仍保存真实飞行图和位姿。
                var saveView = new List<SpaceObject>(objects.Count);
                for (int i = 0; i < objects.Count; i++)
                {
                    if (!objects[i].IsTransient) saveView.Add(objects[i]);
                }
                Scribe_Collections.Look(ref saveView, "objects", LookMode.Deep);
            }
            else
            {
                Scribe_Collections.Look(ref objects, "objects", LookMode.Deep);
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars && objects == null)
            {
                Log.Warning("[DeltaV] Save has no 'objects' list; universe state starts empty. " +
                            "Recreate the test scene via Debug actions.");
                objects = new List<SpaceObject>();
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                objectById = null;
                objectByMap = null;
            }
            Scribe_Collections.Look(ref generatedSectorTiles, "generatedSectorTiles", LookMode.Value);
            Scribe_Deep.Look(ref missionState, "missions");
            missionState ??= new SpaceMissionState();
            Scribe_Collections.Look(ref globeBookmarks, "globeBookmarks", LookMode.Deep);
            Scribe_Deep.Look(ref GlobeFilters, "globeFilters");
            GlobeFilters ??= new GlobeFilters();
            Scribe_Values.Look(ref SoftDockEnabled, "softDockEnabled", false);
            Scribe_Values.Look(ref ShowThrusterDebugOverlay, "showThrusterDebugOverlay", false);
            Scribe_Values.Look(ref ShowCoordinateGrid, "showCoordinateGrid", true);
            Scribe_Values.Look(ref ConfirmSplitWarnings, "confirmSplitWarnings", true);
            Scribe_Values.Look(ref ShowWeaponArcOverlay, "showWeaponArcOverlay", false);
            Scribe_Values.Look(ref ShowCollisionDebugOverlay, "showCollisionDebugOverlay", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && generatedSectorTiles == null)
            {
                generatedSectorTiles = new List<string>();
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                cellsPerTileAtSave = DeltaVMod.CurrentSettings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
            }
            Scribe_Values.Look(ref cellsPerTileAtSave, "cellsPerTileAtSave", 0);
            Scribe_Values.Look(ref lastLifecycleScanTick, "lastLifecycleScanTick", -1);
            Scribe_Values.Look(ref nextFreeOwnerId, "nextFreeOwnerId", 0);
            Scribe_Collections.Look(ref structureNames, "structureNames", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && structureNames == null)
            {
                structureNames = new Dictionary<int, string>();
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 主菜单改过尺度再读档：对象位姿仍按存档时尺度分布，读档后统一重标定。
                float current = DeltaVMod.CurrentSettings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
                if (cellsPerTileAtSave > 0 && !Mathf.Approximately(cellsPerTileAtSave, current))
                {
                    pendingScaleRescaleFrom = cellsPerTileAtSave;
                }
            }
        }

        public override void LoadedGame()
        {
            ConfigureMapFramework();
            objectById = null;
            objectByMap = null;
            ResetRuntimeServices();
            pocketHostService?.Reset();
            OrbitSphere.ClearCache();
            // 正常存读档仅重建派生索引，不迁移旧开发存档。
            SyncOwnerIdFloor();
            OrbitalPoiRegistry.Synchronize(this);

            if (pendingScaleRescaleFrom > 0f)
            {
                float current = DeltaVMod.CurrentSettings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
                ApplyCellsPerTileScale(pendingScaleRescaleFrom, current);
                pendingScaleRescaleFrom = -1f;
            }

            // 地图被外部移除等异常情况下的收尾：清掉悬空引用；丢失地图的纯容器整体
            // 移除避免悬空状态。PoiState（marker/NativeParent）保留等待物化/同步。
            foreach (SpaceObject obj in objects.ToList())
            {
                if (obj.map != null && !Find.Maps.Contains(obj.map))
                {
                    Log.Warning($"[DeltaV] Object #{obj.objectId} ({obj.label}) lost its map after load; clearing map reference.");
                    obj.map = null;
                }
                if (obj.map == null && obj.Payload?.Entity == null && !(obj is PoiState)
                    && !obj.IsArchived)
                {
                    Log.Warning($"[DeltaV] Container #{obj.objectId} ({obj.label}) lost its map; removing from universe state.");
                    UnregisterObject(obj);
                }
                else if (obj.IsScattered) Scatter.Register(obj);
            }
            HostColonistBarVisibility.NotifyDockingStateChanged();
            SweepEmptyDepartedMaps();
            Weapons.RebuildWeaponIndex();
            Controls.Rebuild();
            if (SoftDockEnabled) SoftDock.RestoreEnabled();
            ApplyCameraZoomScope();
            Missions.Resume();
            if (tutorialRun != null) Tutorial.Resume();
        }

        /// <summary>
        /// 读档后清扫遗留的空白口袋图（历史会话起飞后未清理的积累；worldtile 图不受
        /// 固定↔移动转换影响，读档不清）。判定与起飞后清理同一入口
        /// （ShipFlightTransitionService.HandleDepartedSourceMap）；延后到加载收尾执行。
        /// </summary>
        private void SweepEmptyDepartedMaps()
        {
            // 读档收尾：帧动作队列在加载完成后的首个 GameComponentUpdate 消费
            //（QueueFrameAction 统一机制，不占 ExecuteWhenFinished 内联语义的坑）。
            QueueFrameAction(delegate
            {
                foreach (Map map in Find.Maps.ToList())
                {
                    ShipTransitions.HandleDepartedSourceMap(this, map);
                }
                // 弹体孤儿图回收（写档剔除了弹体容器，小图随档残留；帧上下文收尾）。
            });
        }

        // ---- 帧上下文动作队列（唯一的「离开当前调用栈」延迟机制）----

        /// <summary>
        /// 待执行的帧上下文动作。GameComponent 的 XML 加载路径（GetUninitializedObject）
        /// 不跑字段初始化器，必须按惰性属性模式访问。
        /// </summary>
        private List<Action> frameActions;
        private Queue<Action> sceneResetSteps;
        public bool IsResettingScene => sceneResetSteps != null;

        private List<Action> FrameActions =>
            frameActions ?? (frameActions = new List<Action>());

        /// <summary>
        /// 把动作延迟到 GameComponentUpdate 开头（帧上下文）执行——**本项目唯一**的
        /// 「离开当前 tick/加载调用栈」延迟机制，供会变更宇宙流程状态（容器注册/注销、
        /// 图生成/销毁、聚焦切换）的收尾使用：出力 gate 位于容器积分 foreach 内、
        /// 对接完成位于 Approach.Tick 内，迭代/调用栈安全都要求先离开再执行。
        /// 不是帧驱动模拟（不变量 #20）：入队动作源自 tick 触发，此处只做调用栈安全
        /// 延迟。**不得用 LongEventHandler.ExecuteWhenFinished 代替**——无正在执行的
        /// 长事件时它会立即内联执行（2026-09-24 实测事故，见 AGENTS §21）。
        /// </summary>
        internal void QueueFrameAction(Action action)
        {
            if (action != null)
            {
                FrameActions.Add(action);
            }
        }

        /// <summary>消费帧上下文动作（拷贝后清空：动作内再入队者下一帧执行）。</summary>
        private void RunFrameActions()
        {
            if (frameActions == null || frameActions.Count == 0) return;
            using var batchTiming = Performance.Measure(PerformanceStage.FrameActions);
            Action[] batch = frameActions.ToArray();
            frameActions.Clear();
            for (int i = 0; i < batch.Length; i++)
            {
                using var actionTiming = Performance.Measure(PerformanceStage.FrameAction);
                try { batch[i](); }
                catch
                {
                    // Preserve the unexecuted tail, ahead of work spawned by this batch.
                    FrameActions.InsertRange(0, batch.Skip(i + 1));
                    throw;
                }
            }
        }

        public override void GameComponentUpdate()
        {
            if (IsResettingScene)
            {
                Performance.FrameFinished(0L);
                Find.TickManager.Pause();
                // Native map finalization must finish before any queued disposal.
                if (LongEventHandler.AnyEventNowOrWaiting) return;
                RunFrameActions();
                AdvanceSceneReset();
                return;
            }
            long performanceStart = Performance.BeginFrame();
            // 帧上下文动作消费（分离执行、对接完成收尾、源图善后、战略离场善后等
            // 「必须离开 tick 调用栈」的收尾唯一出口），先消费再处理渲染/输入。
            RunFrameActions();
            if (TargetCamera != null && !WorldRendererUtility.DrawingMap) TargetCamera = null;
            // 驾驶状态维护（自动退出 + Harmony 按键抑制开关）。聚焦跟随由 CurrentMap 天然承载。
            Pilot.UpdateFrameState(this);
            ShellRenderer.DrawNonFocusedShells(this);
            Pilot.DrawNavigationPreview();
            WorldShipMarkerRenderer.DrawPlayerShips(this);
            ThrusterEffects.Draw(this);
            // 月池力场（墙壁式连接图；聚焦图每帧绘制，外壳路径在 MapOverlayRenderer）。
            // 推进器/船体范围调试叠加（右下角 PlaySettings 开关控制）。
            ThrusterDebugOverlay.DrawFocused(this);
            if (ShowWeaponArcOverlay) WeaponArcOverlay.DrawFocused(this);
            softDock?.Draw();
            CollisionDebugOverlay.DrawFocused(this);
            // 推进器工作音（与火焰同源：只读分配器油门；聚焦图逐推进器 + 船壳整船）。
            ThrusterAudio.Update(this);
            Performance.FrameFinished(performanceStart);
        }

        // 缩放设置属于共享 CameraMapConfig；按切图/设置事件在宇宙图覆盖，离图恢复。
        private CameraMapConfig zoomConfig;
        private float outsideZoomMax = float.NaN;

        internal void ApplyCameraZoomScope() => ApplyCameraZoomScope(Find.CurrentMap);

        internal void ApplyCameraZoomScope(Map targetMap)
        {
            CameraMapConfig config = Find.CameraDriver?.config;
            if (config == null) return;
            if (zoomConfig != null && zoomConfig != config)
            {
                zoomConfig.sizeRange.max = outsideZoomMax;
                zoomConfig = null;
                outsideZoomMax = float.NaN;
            }
            if (FrameOf(targetMap) != null)
            {
                if (zoomConfig == null)
                {
                    zoomConfig = config;
                    outsideZoomMax = config.sizeRange.max;
                }
                config.sizeRange.max = Mathf.Max(config.sizeRange.min + 1f,
                    DeltaVMod.CurrentSettings.universeZoomMax);
            }
            else if (zoomConfig != null)
            {
                config.sizeRange.max = outsideZoomMax;
                zoomConfig = null;
                outsideZoomMax = float.NaN;
            }
        }

        // SCS 在设置保存时先写入共享 config；保存该新值，再重施 Delta-V 上限。
        internal void NotifyExternalCameraZoomMaxWritten(CameraMapConfig config)
        {
            if (config == null || config != Find.CameraDriver?.config) return;
            if (zoomConfig == config) outsideZoomMax = config.sizeRange.max;
            ApplyCameraZoomScope();
        }

        /// <summary>
        /// 物理 tick（TickManager 每 tick 调用，暂停时冻结——正确的模拟语义）。
        /// 积分对象 = **容器**（一张图一个刚体，§5.3「每个对象只积分一次」）：驾驶船
        /// 的容器按输入+辅助出力积分；其余容器只在有速度/角速度时漂移积分（对接组合体
        /// 随宿主容器惯性漂移）。只写 SpacePose（Map 外的宇宙状态），绝不触碰任何
        /// Map 内 Thing（不变量 #6）。
        /// </summary>
        public override void GameComponentTick()
        {
            if (IsResettingScene) return;
            long performanceStart = Performance.BeginTick();
            // 原版 TickManager 每次只调用一次；速度单位为格/秒，故单次积分步长为 1/60 秒。
            // 倍速增加调用次数而非放大单次 dt，暂停时本方法不被调用。
            const float dt = 1f / 60f;
            // 宇宙运动只属于游戏时钟（AGENTS §18 不变量 #20）：自动驾驶/弹体制导/跳帮舱按
            // 游戏 tick 推进——暂停冻结、倍速加速；先于计划判据与物理积分
            //（IsActive 跳过防双驱动，计划判据看到本 tick 最新位姿）。
            long stageStart = Performance.Start();
            NpcShips.Tick();
            Performance.Record(PerformanceStage.Npc, stageStart);
            stageStart = Performance.Start();
            softDock?.Tick();
            Performance.Record(PerformanceStage.SoftDock, stageStart);
            stageStart = Performance.Start();
            Projectiles.Tick();
            Performance.Record(PerformanceStage.Projectiles, stageStart);
            stageStart = Performance.Start();
            Autopilot.Tick();
            Performance.Record(PerformanceStage.Autopilot, stageStart);
            stageStart = Performance.Start();
            Weapons.TickPendingMissileReloads();
            Performance.Record(PerformanceStage.Weapons, stageStart);
            // 同一归属 controller 调度瞄准、齐射、点防与炮手事件。
            stageStart = Performance.Start();
            Controls.Tick();
            Performance.Record(PerformanceStage.Controls, stageStart);
            // 对接计划（几何完成判据 + 取消守卫；2026-09-21 阶段4）。
            Approach.Tick(this);
            PilotTarget piloted = Pilot.PilotedTarget(this);
            bool piloting = piloted.IsValid && Find.CurrentMap == piloted.Map;
            stageStart = Performance.Start();
            long objectDriveTime = 0L, objectDriftTime = 0L, objectArchiveTime = 0L;
            int thrustingObjects = 0, driftingObjects = 0, archivedObjects = 0;
            foreach (SpaceObject obj in objects)
            {
                if (piloting && obj.map == piloted.Map && !Autopilot.IsActive(obj))
                {
                    long driveStart = Performance.Start();
                    DriveSubject subject = DriveSubject.Of(piloted);
                    long composeStart = Performance.Start();
                    var command = Pilot.ComposeCommand(piloted);
                    Performance.Record(PerformanceStage.DriveCompose, composeStart);
                    obj.ApplyDrive(subject, DriveControl.FromInput(command.command,
                        command.assistX, command.assistZ, command.assistW, command.inputBasis), dt, out _);
                    objectDriveTime += Performance.Elapsed(driveStart);
                }
                // 对象自身保证每 tick 只积分一次；等待分离、失效任务仍保留真实漂移。
                long driftStart = Performance.Start();
                if (obj.AdvanceDrift(dt)) driftingObjects++;
                objectDriftTime += Performance.Elapsed(driftStart);
                if (obj.PropulsionUsedThisTick) thrustingObjects++;
                if (obj.IsArchived)
                {
                    long archiveStart = Performance.Start();
                    archivedObjects++;
                    Lifecycle.CheckArchivedProximity(obj);
                    NpcShips.NotifyArchivedMovement((ArchivedSpaceObject)obj);
                    objectArchiveTime += Performance.Elapsed(archiveStart);
                }
            }
            Performance.RecordElapsed(PerformanceStage.ObjectDrive, objectDriveTime);
            Performance.RecordElapsed(PerformanceStage.ObjectDrift, objectDriftTime);
            Performance.RecordElapsed(PerformanceStage.ObjectArchive, objectArchiveTime);
            Performance.Record(PerformanceStage.Objects, stageStart);
            softDock?.ApplyAlignmentAssist();
            // 碰撞扫掠与结算（太空战架构 §3.3：先动后撞——本 tick 位移段对全部有碰撞面
            // 容器求交，命中经联合效果注册表对称结算；销毁/归池经帧边界回调）。
            scatter?.Tick();
            stageStart = Performance.Start();
            Projectiles.ResolveCollisions();
            Performance.Record(PerformanceStage.Collision, stageStart);
            stageStart = Performance.Start();
            Projectiles.TickMotionFeedback();
            Performance.Record(PerformanceStage.MotionFeedback, stageStart);
            // 生命周期只按设置周期扫描；归档对象已在上方与其他容器一起逐 tick 推进。
            int now = Find.TickManager.TicksGame;
            if (lastLifecycleScanTick < 0) lastLifecycleScanTick = now;
            int lifecycleInterval = Mathf.Clamp(
                DeltaVMod.CurrentSettings?.lifecycleScanIntervalTicks ?? 60, 1, 600);
            if (now - lastLifecycleScanTick >= lifecycleInterval)
            {
                lastLifecycleScanTick = now;
                stageStart = Performance.Start();
                Lifecycle.Tick();
                Performance.Record(PerformanceStage.Lifecycle, stageStart);
            }
            // 星区记账仍按原有周期触发（自由飞行跨 tile 首次生成）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                SectorContentService.TickSectorTracking(this);
            }
            Performance.TickFinished(performanceStart, now, objects.Count,
                Projectiles.ActiveCount, SubstructureOwnership.GlobalStructureVersion,
                thrustingObjects, driftingObjects, archivedObjects);
        }

        public override void GameComponentOnGUI()
        {
            if (IsResettingScene) return;
            // 驾驶模式切换键（本 mod 分类的绑定不受抑制影响，驾驶中仍可退出）。
            // 作用于「当前查看/操作的船」：面板选择或图上默认（玩家船优先，SelectedShipOnMap）。
            if (DeltaVDefOf.DeltaV_TogglePilotMode != null && DeltaVDefOf.DeltaV_TogglePilotMode.KeyDownEvent)
            {
                Map pilotMap = Focused?.InteriorMap;
                Pilot.ToggleControl(this, pilotMap != null ? Pilot.SelectedShipOnMap(pilotMap) : Ship.Invalid);
            }
            // 火控切换键与面板作用于同一选中船；手动瞄准会话独立于驾驶任务。
            if (DeltaVDefOf.DeltaV_AimMode != null && DeltaVDefOf.DeltaV_AimMode.KeyDownEvent)
            {
                ShipBase selectedShip = Find.CurrentMap != null
                    ? Pilot.SelectedShipOnMap(Find.CurrentMap) : Ship.Invalid;
                Controls.ToggleFireControl(selectedShip);
            }
            ShipStatusPanel.Draw(this);
            Pilot.DrawNavigationHint();
            Designator_ReassignSubstructure.DrawPendingLabels();
            HandleShellClick();
            HandleWorldMarkerClick();
        }

        // ---- 世界视图剪影点选（聚焦切图；与地图外壳点选对称的世界视图通道）----

        private ShipBase pendingWorldClickShipBase = ShipBase.Invalid;
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
                pendingWorldClickShipBase = ShipBase.Invalid;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingWorldClickShipBase = ShipBase.Invalid;
                if (Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(ev.mousePosition)) != null) return;
                pendingWorldClickShipBase = PickWorldMarker(ev.mousePosition);
                pendingWorldClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingWorldClickShipBase = ShipBase.Invalid;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingWorldClickShipBase.IsValid)
            {
                ShipBase target = pendingWorldClickShipBase;
                pendingWorldClickShipBase = ShipBase.Invalid;
                bool noDrag = (ev.mousePosition - pendingWorldClickScreen).sqrMagnitude < 100f;
                if (noDrag && PickWorldMarker(ev.mousePosition).Identity == target.Identity)
                {
                    Map targetMap = target.Map;
                    if (targetMap != null && ShipRegistry.ShipByIdentity(target.Identity).IsValid)
                    {
                        SetFocused(FrameOf(targetMap));
                        CameraJumper.TryJump(targetMap.Center, targetMap, CameraJumper.MovementMode.Cut);
                    }
                }
            }
        }

        private ShipBase PickWorldMarker(Vector2 mousePos)
        {
            if (!OrbitSphere.Valid) return ShipBase.Invalid;
            ShipBase best = ShipBase.Invalid;
            float bestDistance = float.MaxValue;
            foreach (ShipBase ship in PlayerShips())
            {
                if (Approach.FlowTarget.Identity == ship.Identity) continue;
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
        /// 点击显示中的宇宙对象外壳 → 聚焦切换到对应地图（容器与 POI 通用）。
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
                // 瞄准会话让位（§6.1）：瞄准会话作用于本图时左键 = 射击，不作聚焦切换。
                bool aimSession = Controls.AimSessionOnFocusedMap;
                if (targeting || designator || overWindow || aimSession)
                {
                    return;
                }
                IntVec3 mouseCell = UI.MouseMapPosition().ToIntVec3();
                bool selectableHere = mouseCell.InBounds(current) && mouseCell.GetThingList(current).Any(ThingSelectionUtility.SelectableByMapClick);
                if (selectableHere)
                {
                    return;
                }
                // 聚焦图内容遮挡（2026-09-26 用户报障）：背景船壳无条件位于聚焦图内容
                // 之下——点击处聚焦图自身渲染了内容（甲板/结构；ContainsVisibleContent
                // 与外壳渲染同一语义）时，被覆盖的船壳在视觉上不可见，点击不穿透切焦。
                // 空 Space 格透明（dontRender 不画），照常点击穿通选择后方船壳。
                if (mouseCell.InBounds(current)
                    && MapOverlayRenderer.ContainsVisibleContent(current, mouseCell))
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
                    if (target.Payload?.Recoverable == true)
                    {
                        TargetCameraFocus.QueueStart(this, current, target);
                        return;
                    }
                    // 相机跳到点击位置对应的对象内格（clamp 进地图），落点即所见。
                    SpaceObject frame = FrameOf(current);
                    Vector3 universeDir = SpaceTransform.MapPointToUniverse(frame, current,
                        new Vector2(UI.MouseMapPosition().x, UI.MouseMapPosition().z));
                    Map targetMap = target.InteriorMap;
                    SpaceObject targetFrame = FrameOf(targetMap);
                    if (targetFrame == null) return;
                    Vector2 mapPoint = SpaceTransform.UniverseToMapPoint(targetFrame, targetMap, universeDir);
                    SetFocusedWithCamera(target, mapPoint, Find.CameraDriver.RootSize);
                }
            }
        }

        /// <summary>
        /// 切换聚焦参考系。只设置 CurrentMap——原版 setter 负责关闭 targeter、清选区、
        /// 恢复该图记忆的相机位置。宇宙姿态不受影响。
        /// </summary>
        public void SetFocused(SpaceObject obj)
        {
            Map targetMap = obj?.InteriorMap;
            if (targetMap == null)
            {
                Log.Error($"[DeltaV] SetFocused: object #{obj?.objectId} has no live map.");
                return;
            }
            Current.Game.CurrentMap = targetMap;
            // 与 PocketMapUtility 一致：确保回到地图视图而不是世界球面。
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
        }

        /// <summary>
        /// 聚焦切换 + 相机连续性（单实现入口，外壳点击聚焦与对接完成交接共用）：
        /// mapPoint 为调用方按宇宙参考系换算好的目标图连续坐标。缩放同步（RimExodus
        /// SeamlessCameraFocus 同款结论）：CurrentMap setter 的 Notify_SwitchedMap 会用
        /// 目标图 rememberedCameraPos 同时恢复位置+缩放，只 TryJump 恢复位置不够，必须
        /// 由调用方先记录 RootSize、跳转后经此显式重设。
        /// </summary>
        public void SetFocusedWithCamera(SpaceObject target, Vector2 mapPoint, float rootSize)
        {
            Map targetMap = target?.InteriorMap;
            if (targetMap == null) return;
            SetFocused(target);
            int x = Mathf.Clamp(Mathf.FloorToInt(mapPoint.x), 2, targetMap.Size.x - 3);
            int z = Mathf.Clamp(Mathf.FloorToInt(mapPoint.y), 2, targetMap.Size.z - 3);
            CameraJumper.TryJump(new IntVec3(x, 0, z), targetMap, CameraJumper.MovementMode.Cut);
            Find.CameraDriver.SetRootSize(rootSize);
        }

        /// <summary>Drain old frame work before destroying its identities or creating new maps.</summary>
        internal void BeginSceneReset(Action createScene)
        {
            if (IsResettingScene) return;
            TargetCamera = null;
            Find.TickManager.Pause();
            if (tutorialRun != null) Tutorial.EndForSceneReset();
            foreach (var mission in Missions.Active.ToList())
            {
                mission.settled = true;
                mission.quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
            }
            missionState.boards.Clear();
            sceneResetSteps = new Queue<Action>();
            sceneResetSteps.Enqueue(() =>
            {
                // Finish an in-progress native map before cancellation/disposal can
                // reach it; partially initialized MapDrawer resources cannot be disposed.
                Generation?.DrainSynchronouslyForExternalGeneration();
                Generation?.Reset();
                Pilot.Exit(this);
                Approach.ResetFlow(this);
                Autopilot.ResetFlow();
                Projectiles.ResetFlow();
                Weapons.ResetFlow();
                Shuttles.ResetFlow();
                ShipTransitions.Reset();
            });
            sceneResetSteps.Enqueue(DestroySceneObjects);
            sceneResetSteps.Enqueue(ResetRuntimeServices);
            sceneResetSteps.Enqueue(() => LongEventHandler.QueueLongEvent(() =>
            {
                try { createScene(); }
                catch (Exception ex)
                {
                    // Return normally so vanilla still runs Map.FinalizeInit callbacks.
                    // Failed new maps can only be removed after those callbacks finish.
                    Log.Error($"[DeltaV] Failed to create test scene: {ex}");
                    QueueFrameAction(DestroySceneObjects);
                }
            }, "GeneratingMap", false, null));
        }

        private void AdvanceSceneReset()
        {
            if (!IsResettingScene || (frameActions != null && frameActions.Count != 0)) return;
            if (sceneResetSteps.Count == 0) sceneResetSteps = null;
            else
            {
                sceneResetSteps.Peek()();
                sceneResetSteps.Dequeue();
            }
        }

        private void DestroySceneObjects()
        {
            foreach (SpaceObject obj in objects.ToList())
            {
                if (obj.map != null) Lifecycle.DestroyPocketMap(obj.map);
                UnregisterObject(obj);
            }
            if (UniverseHostMap != null)
                foreach (Map orphan in UniverseHostMap.ChildPocketMaps.ToList())
                    Lifecycle.DestroyPocketMap(orphan);
        }

        /// <summary>
        /// 清理只在当前游戏实例内有效、且可由持久状态重建的服务与缓存。读档和 dev
        /// 场景重建共用这一入口；重建时必须先排空旧流程及其帧动作。
        /// </summary>
        private void ResetRuntimeServices()
        {
            ShipPlanner.Reset();
            projectiles?.ResetMotionFeedback();
            mapPool = null;
            foundationTopology = null;
            scatter = null;
            softDock = null;
            stationTrade = null;
            cargoMassUpdates = null;
            worksites = null;
            tutorial = null;
            missions = null;
            npcShips = null;
            globeDisplay?.Clear();
            globeDisplay = null;
            globeNavigation = null;
            TargetCamera = null;
            controls?.Reset();
            controls = null;
            pilot = null;
            playerInput?.Clear();
            playerInput = null;
            ShipStatusPanel.ClearRuntimeState();
            AimUiRegions.Clear();
            ShellRenderer.ClearRuntimeCaches();
            WeaponArcOverlay.ClearRuntimeCaches();
            WorldShipMarkerRenderer.ClearRuntimeCaches();
            PlanetGlobeRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearCache();
            ShipRegistry.ClearCaches();
            ThrusterEffects.ClearRuntimeCaches();
            ThrusterAudio.Reset();
            Lifecycle.Reset();
            Generation?.Reset();
            sectorReferenceTiles.Clear();
            SpaceCollision.ClearCaches();
            SpaceProjectileService.ClearRuntimeCaches();
            AimMarkersRenderer.ClearRuntimeCaches();
            PrefabMarkerPreviewCache.ClearRuntimeCaches();
            unframedMaps?.Clear();
            PilotKeyBridge.Reset();
            structures?.Clear();
            // Loading discards old-runtime callbacks; scene reset reaches here only after draining.
            if (!IsResettingScene) frameActions?.Clear();
        }
    }
}

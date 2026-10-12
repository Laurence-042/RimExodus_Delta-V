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
        /// <summary>权威宇宙对象列表（地图容器 + POI，多态深序列化）。存档事实。</summary>
        public List<SpaceObject> objects = new List<SpaceObject>();

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

        // ---- 流程服务实例（2026-09-22 实例化迁移）----
        // 对接计划/自动驾驶/跳帮舱/模式互转流程状态全部收进实例服务：读档时反序列化器
        // 重建组件，未进 ExposeData 的实例字段即自动归零——「读其他档残留旧流程」不可
        // 表示，不再需要读档清扫。GameComponent 构造路径存在不跑字段初始化器的边缘
        // （见 enginesFiringByShip 注释），一律走惰性属性自愈。

        [NonSerialized] private ShipAutopilot autopilot;

        public ShipAutopilot Autopilot => autopilot ?? (autopilot = new ShipAutopilot());

        [NonSerialized] private NpcShipAiService npcShips;

        public NpcShipAiService NpcShips => npcShips ?? (npcShips = new NpcShipAiService(this));

        [NonSerialized] private ShipControlService controls;

        public ShipControlService Controls => controls ?? (controls = new ShipControlService(this));

        [NonSerialized] private SpaceProjectileService projectiles;

        /// <summary>
        /// 弹体服务（太空战架构 §4.1：取代 BoardingPods 槽位，段7 完成接管）：弹体
        /// 创建/注册、制导、超时、碰撞调度对接、渲染、图池。实例服务不序列化、读档归零。
        /// </summary>
        public SpaceProjectileService Projectiles =>
            projectiles ?? (projectiles = new SpaceProjectileService(this));

        [NonSerialized] private SpaceWeaponService weapons;

        /// <summary>
        /// 武器服务（太空战架构 §5/§6）：武器索引、炮手分配与唯一发射事务。
        /// 逐 tick 战斗调度归 Controls；本服务不序列化、读档重建（武器持久配置在
        /// 武器 comp、激活位在主驾驶台 comp）。
        /// </summary>
        public SpaceWeaponService Weapons => weapons ?? (weapons = new SpaceWeaponService(this));

        [NonSerialized] private SpaceShuttleService shuttles;

        /// <summary>
        /// shuttle 服务（太空战架构 §7/§10）：起飞/侵入编排（tag 切换）；停靠直接用
        /// 对接计划服务。实例服务不序列化、读档归零（侵入计划 = 运行时数据读档丢弃）。
        /// </summary>
        public SpaceShuttleService Shuttles => shuttles ?? (shuttles = new SpaceShuttleService(this));

        [NonSerialized] private LandingApproachController approach;

        public LandingApproachController Approach => approach ?? (approach = new LandingApproachController());

        [NonSerialized] private PilotService pilot;

        public PilotService Pilot => pilot ?? (pilot = new PilotService(this));

        internal void ForgetPilotController(ShipController controller) => pilot?.ForgetController(controller);
        internal bool IsPilotControllingOwner(int ownerId) =>
            pilot?.ActiveController?.target.ControlKey == ownerId;

        [NonSerialized] private ShipFlightTransitionService shipTransitions;

        internal ShipFlightTransitionService ShipTransitions =>
            shipTransitions ?? (shipTransitions = new ShipFlightTransitionService(this));

        [NonSerialized] private SpaceLifecycleService lifecycle;

        public SpaceLifecycleService Lifecycle => lifecycle ?? (lifecycle = new SpaceLifecycleService(this));

        public IncrementalMapGenerationService Generation =>
            Current.Game?.GetComponent<IncrementalMapGenerationService>();

        /// <summary>当前聚焦的承载容器（= 当前聚焦地图的容器；无则 null）。聚焦不再持久化。</summary>
        public SpaceObject Focused => SpaceObjectOnMap(Find.CurrentMap);

        /// <summary>全部玩家船（扫全部已加载图；显示/生命周期参考用，非热路径）。</summary>
        public List<Ship> PlayerShips()
        {
            var ships = new List<Ship>();
            foreach (Map map in Find.Maps)
            {
                foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                {
                    if (ShipRegistry.FactionOf(ship) == Faction.OfPlayer) ships.Add(ship);
                }
            }
            return ships;
        }

        /// <summary>
        /// 玩家主参考船（背景相机/POI 距离参考的回退锚）：优先聚焦图上的玩家船，
        /// 否则任意一艘活着的玩家船；无则 Invalid。
        /// </summary>
        public Ship PrimaryPlayerShip()
        {
            Map focused = Find.CurrentMap;
            if (focused != null)
            {
                Ship onFocused = ShipRegistry.ShipsOnMap(focused)
                    .FirstOrDefault(s => ShipRegistry.FactionOf(s) == Faction.OfPlayer);
                if (onFocused.IsValid) return onFocused;
            }
            return PlayerShips().FirstOrDefault();
        }

        public SpaceObject ObjectById(int id)
        {
            return id <= 0 ? null : objects.FirstOrDefault(o => o.objectId == id);
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
            if (obj.objectId >= nextFreeOwnerId) nextFreeOwnerId = obj.objectId + 1;
            if (obj.baseTag == SpaceObjectTag.Shuttle) Shuttles.RegisterContainer(obj);
            if (obj.map != null && obj.Tag == SpaceObjectTag.Ship)
            {
                Weapons.RegisterMapWeapons(obj.map);
                foreach (Ship ship in ShipRegistry.ShipsOnMap(obj.map)) Controls.For(ship);
            }
        }

        public void UnregisterObject(SpaceObject obj)
        {
            if (obj == null) return;
            objects.Remove(obj);
            if (obj.baseTag == SpaceObjectTag.Shuttle)
                Shuttles.ClearShuttlePocketRuntime(obj.map, obj);
            if (obj.map != null && obj.Tag == SpaceObjectTag.Ship)
                Weapons.ForgetMap(obj.map);
            enginesFiringByShip?.Remove(obj.objectId);
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
                RescalePoseToSector(ref obj.pose.position, shrink);
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

        /// <summary>当前地图对应的宇宙容器（无则 null）。显示参考系以此为准。</summary>
        public SpaceObject SpaceObjectOnMap(Map map)
        {
            if (map == null) return null;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i].map == map) return objects[i];
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

        internal void ForgetMapRuntimeState(Map map)
        {
            if (map == null) return;
            unframedMaps?.Remove(map);
            Projectiles.ForgetMap(map);
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
            OrbitalPoiRegistry.Synchronize(this);
            Weapons.RebuildWeaponIndex();
            Controls.Rebuild();
            Shuttles.RebuildVirtualThrusters();
            // 开局出生星区在世界生成时预生成（星区架构 §4 触发时机）。
            Map current = Find.CurrentMap;
            if (current != null && current.Tile.Valid && current.Tile.LayerDef.isSpace)
            {
                SectorContentService.EnsureSectorGenerated(this, current.Tile);
            }
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 弹体是瞬态成员（太空战架构 §4.4）：写档按 tag 过滤剔除纯弹体
                //（IsTransient = baseTag==Projectile；侵入 shuttle 的 baseTag 仍是
                // Shuttle，照常落盘，读档后回 shuttle tag 按存档速度漂移）。已造成的
                // 毁伤是图事实已持久；其弹体小图的读档清扫由弹体服务负责。
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
            Scribe_Collections.Look(ref generatedSectorTiles, "generatedSectorTiles", LookMode.Value);
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
            ResetRuntimeServices();
            pocketHostService?.Reset();
            OrbitSphere.ClearCache();
            // 先对齐 ID 下限再跑归属迁移（迁移会对无组件图兜底分配新 ID）。
            SyncOwnerIdFloor();
            OrbitalPoiRegistry.Synchronize(this);
            MigrateSubstructureOwnership();
            if (pendingScaleRescaleFrom > 0f)
            {
                float current = DeltaVMod.CurrentSettings?.cellsPerTile ?? (int)OrbitSphere.DefaultCellsPerTile;
                ApplyCellsPerTileScale(pendingScaleRescaleFrom, current);
                pendingScaleRescaleFrom = -1f;
            }
            ClearStalePocketMapTiles();
            // 读档形态自愈（2026-09-25 对象模型）：有图 SnapshotPocket PoiState 是旧模型
            //（物化 POI）残留——物化容器 = 普通 SpaceObject（marker 恒无图）。转换为
            // 普通容器（继承 pose/map/faction；envLabel 降级 ambientLabel），同栈注销旧
            // PoiState。NativeParent 例外（map 跟随原版镜像，不转换）。
            foreach (PoiState legacy in objects.OfType<PoiState>()
                .Where(p => p.kind == PoiKind.SnapshotPocket && p.map != null).ToList())
            {
                var converted = new SpaceObject(legacy.objectId, legacy.map, legacy.pose)
                {
                    faction = legacy.faction,
                    ambientLabel = legacy.envLabel,
                };
                objects.Remove(legacy);
                objects.Add(converted);
                // ID 不变（归属章已盖此 ID）；不计入注销（nextFreeOwnerId 不推高）。
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{legacy.envLabel}: legacy materialized POI #{legacy.objectId} converted to plain container");
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
                if (obj.map == null && !(obj is PoiState)
                    && obj.archivedMapData.NullOrEmpty())
                {
                    Log.Warning($"[DeltaV] Container #{obj.objectId} ({obj.label}) lost its map; removing from universe state.");
                    UnregisterObject(obj);
                }
            }
            HostColonistBarVisibility.NotifyDockingStateChanged();
            SweepEmptyDepartedMaps();
            Weapons.RebuildWeaponIndex();
            Controls.Rebuild();
            Shuttles.RebuildVirtualThrusters();
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
                Projectiles.SweepOrphanProjectileMaps();
            });
        }

        /// <summary>
        /// 基架归属旧档自愈：归属体系上线前的存档没有归属组件——全部基架格按新分配的
        /// 结构 ID 补齐（不再关联对象 ID，ID 语义分离）。仅 Delta-V 参考系图参与。
        /// 存档已带归属组件的图不做整图迁移：「未占有（0）」是合法的持久状态
        /// （孤立建造/争议桥），读档吸收会破坏归属语义。
        /// </summary>
        private void MigrateSubstructureOwnership()
        {
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
                        int defaultOwner = AllocateOwnerId();
                        int migrated = ownership.MigrateUnassigned(defaultOwner);
                        if (migrated > 0)
                        {
                            NoteStructureName(defaultOwner, NewWreckName(defaultOwner));
                            DeltaVLog.Message(DeltaVLogModule.Core,
                                $"map {map.Index}: migrated {migrated} substructure cells to structure #{defaultOwner}");
                        }
                    }
                }
            }
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

        // ---- 帧上下文动作队列（唯一的「离开当前调用栈」延迟机制）----

        /// <summary>
        /// 待执行的帧上下文动作。GameComponent 的 XML 加载路径（GetUninitializedObject）
        /// 不跑字段初始化器，必须按惰性属性模式访问（同 EnginesFiringByShip 先例）。
        /// </summary>
        private List<Action> frameActions;

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
            Action[] batch = frameActions.ToArray();
            frameActions.Clear();
            foreach (Action action in batch)
            {
                action();
            }
        }

        public override void GameComponentUpdate()
        {
            // 帧上下文动作消费（分离执行、对接完成收尾、源图善后、战略离场善后等
            // 「必须离开 tick 调用栈」的收尾唯一出口），先消费再处理渲染/输入。
            RunFrameActions();
            UpdateUniverseZoom();
            // 驾驶状态维护（自动退出 + Harmony 按键抑制开关）。聚焦跟随由 CurrentMap 天然承载。
            Pilot.UpdateFrameState(this);
            DebugActions_DeltaV.UpdateFrameMeter();
            ShellRenderer.DrawNonFocusedShells(this);
            WorldShipMarkerRenderer.DrawPlayerShips(this);
            ThrusterEffects.Draw(this);
            // 月池力场（墙壁式连接图；聚焦图每帧绘制，外壳路径在 MapOverlayRenderer）。
            // 推进器/船体范围调试叠加（右下角 PlaySettings 开关控制）。
            ThrusterDebugOverlay.DrawFocused(this);
            if (WeaponArcOverlay.Show) WeaponArcOverlay.DrawFocused(this);
            CollisionDebugOverlay.DrawFocused(this);
            // 推进器工作音（与火焰同源：只读分配器油门；聚焦图逐推进器 + 船壳整船）。
            ThrusterAudio.Update(this);
        }

        // 相机缩放放宽（宇宙视野）：进入 Delta-V 参考系（对象图/真实空间图）时记录原 max 并放宽，
        // 离开时恢复。每帧幂等——config 对象可能被原版在切图等时机替换。
        private float savedVanillaZoomMax = float.NaN;

        private void UpdateUniverseZoom()
        {
            CameraDriver driver = Find.CameraDriver;
            if (driver?.config == null)
            {
                return;
            }
            float target = Mathf.Max(driver.config.sizeRange.min + 1f, DeltaVMod.CurrentSettings.universeZoomMax);
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
        internal bool TryGetVanillaZoomMax(out float vanillaMax)
        {
            vanillaMax = savedVanillaZoomMax;
            return !float.IsNaN(savedVanillaZoomMax);
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
            // 原版 TickManager 每次只调用一次；速度单位为格/秒，故单次积分步长为 1/60 秒。
            // 倍速增加调用次数而非放大单次 dt，暂停时本方法不被调用。
            const float dt = 1f / 60f;
            // 宇宙运动只属于游戏时钟（AGENTS §18 不变量 #20）：自动驾驶/弹体制导/跳帮舱按
            // 游戏 tick 推进——暂停冻结、倍速加速；先于计划判据与物理积分
            //（IsActive 跳过防双驱动，计划判据看到本 tick 最新位姿）。
            NpcShips.Tick();
            Autopilot.Tick();
            Projectiles.Tick();
            Weapons.TickPendingMissileReloads();
            // 同一归属 controller 调度瞄准、齐射、点防与炮手事件。
            Controls.Tick();
            // 对接计划（几何完成判据 + 取消守卫；2026-09-21 阶段4）。
            Approach.Tick(this);
            PilotTarget piloted = Pilot.PilotedTarget(this);
            bool piloting = piloted.IsValid && Find.CurrentMap == piloted.Map;
            foreach (SpaceObject obj in objects)
            {
                if (obj.map == null)
                {
                    // 无图弹体的 tag 快路径（太空战架构 §3.3）：与容器同一条 Integrate
                    // 核心（数据一致性），哑报告 + 零力——纯「速度 × dt 球面平移 +
                    // 平行输运」，被动阻力已在 Integrate 内按 tag 豁免。其余无图对象
                    //（marker）维持跳过。
                    if (obj.baseTag == SpaceObjectTag.Projectile
                        && obj.pose.velocity.sqrMagnitude > 1e-10f)
                    {
                        ShipPhysics.Integrate(obj, ShipPhysics.PointMassReport,
                            Vector2.zero, 0f, dt, false, false, false);
                    }
                    continue;
                }
                if (Autopilot.IsActive(obj))
                {
                    continue; // 自动驾驶中的容器由 ShipAutopilot 驱动，避免双重积分
                }
                bool isPiloted = piloting && obj.map == piloted.Map;
                bool drifting = obj.pose.velocity.sqrMagnitude > 1e-10f
                    || Mathf.Abs(obj.pose.angularVelocity) > 1e-10f;
                if (!isPiloted && !drifting)
                {
                    continue;
                }
                ShipPhysics.MassReport report = ShipPhysics.GetReport(obj.map);
                if (report == null)
                {
                    continue;
                }
                Vector2 force = Vector2.zero;
                float torque = 0f;
                bool assistX = false;
                bool assistZ = false;
                bool assistW = false;
                float pilotFrameAngle = 0f;
                if (isPiloted)
                {
                    PilotService.PilotCommand command = Pilot.ComposeCommand(piloted);
                    // 出力前分离 gate（§7 脱离 + §8 拆分统一；shuttle 独占口袋图免分离）：
                    // 将要施加推进器出力时先过「移动的船与其余部分分离」检查（手控输入/
                    // 主动刹车辅助/已停靠船的首次出力均在此触发）；被拒绝的 tick 只积分
                    // 零力（被动阻力照常、不施推进器动量）。
                    bool wantsThrust = Mathf.Abs(command.command.fx) > 1e-6f
                        || Mathf.Abs(command.command.fz) > 1e-6f
                        || Mathf.Abs(command.command.torque) > 1e-6f;
                    if (wantsThrust && !ShipTransitions.RequestThrust(this, piloted, playerInitiated: true))
                    {
                        command = default;
                    }
                    if (wantsThrust)
                    {
                        ShipPhysics.Allocate(report, command.command, out force, out torque);
                        assistX = command.assistX;
                        assistZ = command.assistZ;
                        assistW = command.assistW;
                        pilotFrameAngle = piloted.FrameAngle;
                        LogEngineFiringTransition(obj, report, force, torque);
                    }
                }
                ShipPhysics.Integrate(obj, report, force, torque, dt, assistX, assistZ, assistW, pilotFrameAngle);
            }
            // 碰撞扫掠与结算（太空战架构 §3.3：先动后撞——本 tick 位移段对全部有碰撞面
            // 容器求交，命中经联合效果注册表对称结算；销毁/归池经帧边界回调）。
            Projectiles.ResolveCollisions();
            // 生命周期只按设置周期扫描；归档航行按实际经过的原版游戏 tick 推进。
            int now = Find.TickManager.TicksGame;
            if (lastLifecycleScanTick < 0) lastLifecycleScanTick = now;
            int lifecycleInterval = Mathf.Clamp(
                DeltaVMod.CurrentSettings?.lifecycleScanIntervalTicks ?? 60, 1, 600);
            if (now - lastLifecycleScanTick >= lifecycleInterval)
            {
                int elapsed = now - lastLifecycleScanTick;
                lastLifecycleScanTick = now;
                Lifecycle.TickArchivedTravel(elapsed);
                Lifecycle.Tick();
            }
            // 星区记账仍按原有周期触发（自由飞行跨 tile 首次生成）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                SectorContentService.TickSectorTracking(this);
            }
        }

        // 物理埋点（触发式）：只记录引擎点火/停火的转换沿，杜绝持续出力期间的每 tick 输出。
        // 非 readonly：GameComponent 的 XML 加载路径（GetUninitializedObject）不跑字段初始化器，
        // readonly 字段会停留在 null 且无法补偿；这里用惰性属性兜底。
        private Dictionary<int, bool> enginesFiringByShip;

        private Dictionary<int, bool> EnginesFiringByShip =>
            enginesFiringByShip ?? (enginesFiringByShip = new Dictionary<int, bool>());

        private void LogEngineFiringTransition(SpaceObject obj, ShipPhysics.MassReport report, Vector2 force, float torque)
        {
            bool firing = force.sqrMagnitude > 1e-6f || Mathf.Abs(torque) > 1e-6f;
            bool was = EnginesFiringByShip.TryGetValue(obj.objectId, out bool previous) && previous;
            if (firing == was)
            {
                return;
            }
            EnginesFiringByShip[obj.objectId] = firing;
            DeltaVLog.Message(DeltaVLogModule.Physics, firing
                ? $"{obj.label}: thrusters firing | F=({force.x:F0},{force.y:F0}) tau={torque:F0} | mass={report.mass:F0} I={report.inertia:F0} thrusters={report.thrusters.Count}"
                : $"{obj.label}: engines stopped | v=({obj.pose.velocity.x:F2},{obj.pose.velocity.y:F2}) omega={obj.pose.angularVelocity:F3}");
        }

        public override void GameComponentOnGUI()
        {
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
                Ship selectedShip = Find.CurrentMap != null
                    ? Pilot.SelectedShipOnMap(Find.CurrentMap) : Ship.Invalid;
                Controls.ToggleFireControl(selectedShip);
            }
            // 选点阶段 ESC 取消（2026-09-25 补齐）：pendingShip 在册且无计划 = 正在选点。
            // 让位条件是「存在会自己消费 ESC 的窗口」（Dialog 层及以上），而非窗口栈非空——
            // Inspect 等主标签窗口常驻 GameUI 层且不吞 ESC，不能因此禁用本处理器。
            // GameComponentOnGUI 先于 DesignatorManager/WindowStack 执行，这里消费后
            // 原版只会 Deselect 设计器而不清流程的路径不会再跑，一次 ESC 退出整个选点。
            if (KeyBindingDefOf.Cancel.KeyDownEvent
                && (ShipTransitions.pendingShip.IsValid || ShipTransitions.pendingShuttleBody != null))
            {
                // 事件边沿诊断（用户实测排查用；只在 ESC 按下且选点流程在册时输出一条）。
                // 只有 closeOnCancel 的窗口（暂停菜单/确认框）会真正消费 ESC——
                // 开发者调色板、性能 mod 的 ImmediateWindow 等常驻 Super/Dialog 层但不响应取消键。
                string blockingWindows = string.Join(",",
                    Find.WindowStack.Windows.Where(w => w.layer > WindowLayer.GameUI && w.closeOnCancel)
                        .Select(w => $"{w.GetType().Name}@{w.layer}"));
                Log.Message($"[DeltaV] esc during selection: pendingShip={ShipTransitions.pendingShip.IsValid}, "
                    + $"pendingShuttle={ShipTransitions.pendingShuttleBody != null}, "
                    + $"activePlan={Approach.ActivePlan != null}, blockingWindows=[{blockingWindows}], "
                    + $"eventUsed={Event.current.type == EventType.Used}, "
                    + $"designator={Find.DesignatorManager.SelectedDesignator?.GetType().Name ?? "none"}");
                if (string.IsNullOrEmpty(blockingWindows) && Approach.ActivePlan == null)
                {
                    Find.DesignatorManager.Deselect();
                    Approach.CancelSelectionOrPlan(this, "esc");
                    Event.current.Use();
                }
            }
            ShipStatusPanel.Draw(this);
            Designator_ReassignSubstructure.DrawPendingLabels();
            DebugActions_DeltaV.DrawFrameMeter();
            HandleShellClick();
            HandleWorldMarkerClick();
        }

        // ---- 世界视图剪影点选（聚焦切图；与地图外壳点选对称的世界视图通道）----

        private Ship pendingWorldClickShip;
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
                pendingWorldClickShip = Ship.Invalid;
                return;
            }
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                pendingWorldClickShip = Ship.Invalid;
                if (Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(ev.mousePosition)) != null) return;
                pendingWorldClickShip = PickWorldMarker(ev.mousePosition);
                pendingWorldClickScreen = ev.mousePosition;
            }
            else if (ev.type == EventType.MouseDrag)
            {
                pendingWorldClickShip = Ship.Invalid;
            }
            else if (ev.type == EventType.MouseUp && ev.button == 0 && pendingWorldClickShip.IsValid)
            {
                Ship target = pendingWorldClickShip;
                pendingWorldClickShip = Ship.Invalid;
                bool noDrag = (ev.mousePosition - pendingWorldClickScreen).sqrMagnitude < 100f;
                if (noDrag && PickWorldMarker(ev.mousePosition).ownerId == target.ownerId)
                {
                    Map targetMap = target.map;
                    if (targetMap != null && ShipRegistry.ShipByOwner(target.ownerId).IsValid)
                    {
                        SetFocused(FrameOf(targetMap));
                        CameraJumper.TryJump(targetMap.Center, targetMap, CameraJumper.MovementMode.Cut);
                    }
                }
            }
        }

        private Ship PickWorldMarker(Vector2 mousePos)
        {
            if (!OrbitSphere.Valid) return Ship.Invalid;
            Ship best = Ship.Invalid;
            float bestDistance = float.MaxValue;
            foreach (Ship ship in PlayerShips())
            {
                if (!ShipRegistry.IsFreeFlight(ship) && !universeMarkerAlsoDocked(ship)) continue;
                if (ShipTransitions.IsDockingFlowShip(ship)) continue;
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

        /// <summary>世界标记只画自由飞行船的剪影快照（WorldShipMarkerRenderer 同口径）。</summary>
        private static bool universeMarkerAlsoDocked(Ship ship) => false;

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

        public void ResetObjects(List<SpaceObject> newObjects)
        {
            ResetRuntimeServices();
            Pilot.Exit(this);
            // 同实例上的流程清零（2026-09-22 实例化迁移补洞）：旧 objects 整体替换，
            // 残留的对接计划/自动驾驶任务/跳帮舱会指向已废弃的容器，必须一并丢弃。
            Approach.ResetFlow(this);
            Autopilot.ResetFlow();
            Projectiles.ResetFlow();
            Weapons.ResetFlow();
            Shuttles.ResetFlow();
            ShipTransitions.Reset();
            objects = newObjects;
            Weapons.RebuildWeaponIndex();
            Controls.Rebuild();
            Shuttles.RebuildVirtualThrusters();
            unframedMaps?.Clear();
            SpaceObject focus = objects.FirstOrDefault(o => o.map != null);
            if (focus != null)
            {
                SetFocused(focus);
            }
        }

        /// <summary>
        /// 清理只在当前游戏实例内有效、且可由持久状态重建的服务与缓存。读档和 dev
        /// ResetObjects 共用这一入口，避免新增运行时状态时只清掉其中一条路径。
        /// </summary>
        private void ResetRuntimeServices()
        {
            npcShips = null;
            controls?.Reset();
            controls = null;
            pilot = null;
            enginesFiringByShip?.Clear();
            ShipStatusPanel.ClearRuntimeState();
            ShellRenderer.ClearRuntimeCaches();
            WorldShipMarkerRenderer.ClearRuntimeCaches();
            PlanetGlobeRenderer.ClearRuntimeCaches();
            ShipPhysics.ClearRuntimeCaches();
            ShipRegistry.ClearCaches();
            ThrusterEffects.ClearRuntimeCaches();
            ThrusterAudio.Reset();
            Lifecycle.Reset();
            Generation?.Reset();
            SectorContentService.Reset();
            MoonPoolUtility.ClearCache();
            SpaceCollision.ClearCaches();
            SpaceProjectileService.ClearRuntimeCaches();
            AimMarkersRenderer.ClearRuntimeCaches();
            DockingGhostRenderer.Destroy();
            unframedMaps?.Clear();
            PilotKeyBridge.Reset();
            // 指向旧宇宙的帧上下文动作一并丢弃（读档 / dev ResetObjects）。
            frameActions?.Clear();
        }
    }
}

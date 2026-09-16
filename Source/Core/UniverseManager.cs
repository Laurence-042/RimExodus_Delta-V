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
            Scribe_Values.Look(ref focusedShipId, "focusedShipId", -1);
            Scribe_Values.Look(ref pilotShipId, "pilotShipId", -1);
            Scribe_Values.Look(ref primaryShipId, "primaryShipId", -1);
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
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            IncrementalMapGenerationService.Reset();
            pocketHostService?.Reset();
            OrbitSphere.ClearCache();
            DeltaVLandingAnimator.ResetTiming();
            mapFrame = null;
            PilotController.SuppressVanillaKeys = false;
            DockController.RestorePendingAfterLoad(this);
            MigratePlanarSaves();
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
                    objects.Remove(lost);
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
            return (center + tangent * (layer.AverageTileSize / OrbitSphere.CellsPerTile)).normalized;
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
            // POI 地图生命周期三段扫描（单一入口 SpaceLifecycle）。
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                SpaceLifecycle.Tick(this);
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
            PilotController.DrawHud(this);
            DebugActions_DeltaV.DrawFrameMeter();
            HandleShellClick();
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
                        ? SpaceTransform.UniverseToDockedMapPoint(dockedTarget, universeDir)
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
            ShipPhysics.ClearCache();
            ThrusterEffects.ClearRuntimeCaches();
            SpaceLifecycle.Reset();
            IncrementalMapGenerationService.Reset();
            PilotController.Exit(this);
            objects = newObjects;
            focusedShipId = focusId;
            mapFrame = null;
        }
    }
}

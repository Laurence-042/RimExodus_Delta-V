using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 固定轨道 / 建造模式。停靠只负责把 Delta-V 的口袋船交给原版 Gravship 到达入口：
    /// GenerateGravship → GravshipUtility.ArriveExistingMap。落点移动、四向旋转、合法性
    /// 预览、确认 UI 与实际放置由原版完成；Delta-V 只替换降落镜头/运动表现。起飞无动画。
    /// 降落目标 = 船当前球面位置对应的真实轨道 tile 图（与宿主解耦）；起飞来源 = ship.dockedMap。
    /// </summary>
    public static class DockController
    {
        private static ShipState pendingShip;
        private static Gravship pendingGravship;
        private static ShipState landedShipAwaitingPocketCleanup;
        private static ShipState pendingMobileShip;
        private static Building_GravEngine pendingMobileEngine;
        private static Map pendingMobileSourceMap;
        private static IntVec3 pendingMobileSourceRoot;
        private static Vector2 pendingMobileOldOrigin;
        private static float pendingMobileHeading;

        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");
        private static readonly FieldInfo ControllerGravship =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "gravship");
        private static readonly FieldInfo ControllerTimeLeft =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "timeLeft");

        public static bool MobileLaunchPending => pendingMobileShip != null;

        public static float SnapAngle(float angle)
        {
            float quarter = Mathf.PI * 0.5f;
            return SpaceTransform.WrapAngle(Mathf.Round(angle / quarter) * quarter);
        }

        public static void TryDock(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship == null || ship.docked || ship.docking || ship.map == null) return;
            if (Find.GravshipController == null || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 降落目标 = 船当前球面位置对应的真实轨道 tile 上的图（原版 gravship 语义：
            // 有图复用、没图生成），与宿主完全解耦。POI/敌舰是无 tile 口袋图，不挡路；
            // 接近对象绝不能改变玩家船的 Map 归属。
            if (!OrbitSphere.Valid)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlanetTile targetTile = OrbitSphere.TileFromDirection(ship.pose.position);
            if (!targetTile.Valid)
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = FindEngineForShip(ship.map, ship.primaryConsole);
            if (engine == null)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            float snappedHeading = SnapAngle(ShipOrientation.HeadingAngle(ship));
            LongEventHandler.QueueLongEvent(delegate
            {
                int landingSize = Mathf.Clamp(DeltaVMod.Settings?.landingMapSize ?? 150, 100, 1000);
                Map targetMap = GetOrGenerateMapUtility.GetOrGenerateMap(
                    targetTile, new IntVec3(landingSize, 1, landingSize), DeltaVDefOf.DeltaV_SpaceHostParent);
                if (targetMap == null)
                {
                    Log.Error("[DeltaV] Docking aborted: failed to obtain or generate the landing map.");
                    return;
                }
                if (targetMap.terrainGrid.TerrainAt(targetMap.Center) == TerrainDefOf.Space)
                {
                    // 原版选点明确要求至少一格船体与非 Space 地形相连；图中心这一格是固定轨道锚。
                    targetMap.terrainGrid.SetTerrain(targetMap.Center, DeltaVDefOf.Substructure);
                }
                BeginOriginalLanding(universe, ship, engine, targetMap, snappedHeading);
            }, "GeneratingGravship", false, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        private static void BeginOriginalLanding(UniverseManager universe, ShipState ship,
            Building_GravEngine engine, Map targetMap, float snappedHeading)
        {
            ship.preDockPose = ship.pose;
            ship.pocketRoot = engine.Position;
            DeltaVLandingAnimator.Prepare(ship);
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            if (engine.launchInfo == null)
            {
                // 原版通常由发射仪式/对话创建；Delta-V 跳过起飞流程，但 LandingEnded 会读取它。
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }

            Gravship gravship = GravshipUtility.GenerateGravship(engine);
            if (gravship == null)
            {
                Log.Error($"[DeltaV] Docking '{ship.label}': GenerateGravship returned null; aborting.");
                return;
            }
            Rot4 sourceFacing = gravship.PilotConsole?.Rotation ?? Rot4.East;
            Rot4 desiredFacing = ShipOrientation.Rot4FromAngle(snappedHeading);
            gravship.Rotation = ShipOrientation.PlacementRotation(sourceFacing, desiredFacing);
            gravship.Tile = targetMap.Tile;
            gravship.destinationTile = targetMap.Tile;

            pendingShip = ship;
            pendingGravship = gravship;
            ship.docking = true;
            PilotController.Exit(universe);

            // 原版入口要求待到达 Gravship 已登记；ArriveExistingMap 随即按自身契约移除它。
            Find.WorldObjects.Add(gravship);
            GravshipUtility.ArriveExistingMap(gravship);

            // 先让原版 designator 锁定目标 Map，再隐藏会把船送去世界旅行的原版 Abort。
            Find.GravshipController.MoveDesignator();
            Find.GravshipController.landingMap = null;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: handed to vanilla landing selector; initial heading={snappedHeading * Mathf.Rad2Deg:F0} deg");
        }

        public static void RestorePendingAfterLoad(UniverseManager universe)
        {
            pendingShip = null;
            pendingGravship = null;
            landedShipAwaitingPocketCleanup = null;
            ClearPendingMobileLaunch();
            Gravship gravship = Current.Game?.Gravship;
            if (universe == null || gravship == null || Find.GravshipController == null) return;
            if (!Find.GravshipController.LandingAreaConfirmationInProgress)
            {
                // BeginLanding 之后保存：落地动画尚未结束，保留旧图供 Animator 使用，
                // 并在恢复后的 LandingEnded Postfix 再销毁。
                landedShipAwaitingPocketCleanup = universe.Ships
                    .FirstOrDefault(s => s.docked && s.dockedMap == Find.CurrentMap);
                return;
            }
            ShipState ship = universe.Ships.FirstOrDefault(s => s.docking);
            if (ship == null) return;
            pendingShip = ship;
            pendingGravship = gravship;
        }

        /// <summary>原版 GravshipLandingMarker.BeginLanding Prefix 的状态桥。</summary>
        public static bool NotifyVanillaLandingBegun(GravshipLandingMarker marker)
        {
            if (marker == null || pendingShip == null || pendingGravship == null
                || !ReferenceEquals(marker.gravship, pendingGravship)) return false;

            ShipState ship = pendingShip;
            Rot4 landedFacing = marker.gravship.PilotConsole != null
                && marker.gravship.ThingPlacements.TryGetValue(marker.gravship.PilotConsole, out PositionData.Data data)
                ? data.rotation
                : ShipOrientation.Rot4FromAngle(ShipOrientation.HeadingAngle(ship));

            ship.dockedRotation = marker.GravshipRotation;
            ship.dockedMap = marker.Map;
            Vector2 pocketCenterFromRoot = SpaceTransform.ShipLocalOrigin(ship.map)
                - new Vector2(ship.pocketRoot.x, ship.pocketRoot.z);
            ship.dockedOrigin = new Vector2(marker.Position.x, marker.Position.z)
                + SpaceTransform.RotatePlacementOffset(pocketCenterFromRoot, marker.GravshipRotation);

            // 降落选点发生在目标图（真实轨道 tile）的地图坐标系中：把真实船体原点
            // 经该图的 tile 帧换回 Universe 球面方向，使“落在敌舰/POI 旁边”成为
            // 真正的宇宙位置变化；其他对象绝对状态不受影响，确认前后不会跳位。
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject mapFrame = universe?.MapFrame(marker.Map);
            if (mapFrame != null)
            {
                Vector2 mapLocal = SpaceTransform.MapToShipLocal(marker.Map, ship.dockedOrigin);
                ship.pose.position = SpaceTransform.FocusedToUniverse(mapFrame, mapLocal);
            }
            ship.pose.angle = ShipOrientation.AngleFromRot4(landedFacing);
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            ship.docking = false;
            ship.docked = true;
            HostColonistBarVisibility.NotifyDockingStateChanged();
            landedShipAwaitingPocketCleanup = ship;
            pendingShip = null;
            pendingGravship = null;
            Messages.Message("DeltaV_DockSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: vanilla landing confirmed; rot={ship.dockedRotation}, heading={ship.pose.angle * Mathf.Rad2Deg:F0} deg");
            return true;
        }

        /// <summary>
        /// 原版 LandingEnded Postfix：动画与 Gravship 已彻底释放后销毁空口袋图。
        /// Prefix 阶段仍有 DeltaVLandingAnimator 读取旧图中心，不能提前销毁。
        /// </summary>
        public static void NotifyVanillaLandingEnded()
        {
            ShipState ship = landedShipAwaitingPocketCleanup;
            landedShipAwaitingPocketCleanup = null;
            DestroyDockedPocketMap(ship);
        }

        /// <summary>旧版本存档迁移：停靠船若仍引用空口袋图，在加载收尾后销毁。</summary>
        public static void CleanupObsoleteDockedPocketMaps(UniverseManager universe)
        {
            if (universe == null || Current.Game?.Gravship != null) return;
            List<ShipState> ships = universe.Ships
                .Where(s => s.docked && s.map != null && s.map != s.dockedMap)
                .ToList();
            if (ships.Count == 0) return;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                foreach (ShipState ship in ships) DestroyDockedPocketMap(ship);
            });
        }

        private static void DestroyDockedPocketMap(ShipState ship)
        {
            Map pocketMap = ship?.map;
            if (ship == null || !ship.docked || pocketMap == null || pocketMap == ship.dockedMap) return;
            // 旧存档可能尚无 dockedOrigin；必须在 map 引用消失前从原口袋中心重建。
            _ = DockedOriginMapPoint(ship);
            if (Find.CurrentMap == pocketMap && ship.dockedMap != null)
            {
                Current.Game.CurrentMap = ship.dockedMap;
            }
            ShipPhysics.ClearCache();
            PocketMapUtility.DestroyPocketMap(pocketMap);
            ship.map = null;
            ship.pocketRoot = IntVec3.Invalid;
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: obsolete flight pocket map destroyed after landing");
        }

        public static void TryLaunch(UniverseManager universe, ShipState ship)
        {
            if (universe == null || ship == null || !ship.docked) return;
            Map sourceMap = ship.dockedMap;
            if (sourceMap == null || !Find.Maps.Contains(sourceMap))
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_GravEngine engine = FindEngineForShip(sourceMap, ship.primaryConsole);
            if (engine == null)
            {
                Messages.Message("DeltaV_DockFailNoEngine".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (MobileLaunchPending || Find.GravshipController == null
                || Find.GravshipController.LandingAreaConfirmationInProgress
                || Find.GravshipController.IsGravshipTravelling || Current.Game.Gravship != null)
            {
                Messages.Message("DeltaV_DockFailBusy".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (engine.launchInfo == null)
            {
                engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            }
            Building console = ship.primaryConsole;
            pendingMobileShip = ship;
            pendingMobileEngine = engine;
            pendingMobileSourceMap = sourceMap;
            pendingMobileSourceRoot = engine.Position;
            pendingMobileOldOrigin = DockedOriginMapPoint(ship);
            pendingMobileHeading = console != null && console.Spawned
                ? ShipOrientation.AngleFromRot4(console.Rotation)
                : ship.pose.angle;

            // 固定 → 移动必须从原版起飞入口开始，完整复用捕获、PreSwapMap、
            // GenerateGravship、Map 清理和 TakeoffEnded。目标 tile 只是满足原版协议；
            // TravelTo Prefix 会在起飞收尾点把 Gravship 接入口袋图。
            PilotController.Exit(universe);
            Find.GravshipController.InitiateTakeoff(engine, sourceMap.Tile);
            // 降落图是持久画布，不能让原版 TakeoffEnded 因没有 GravAnchor 而 AbandonMap；
            // 这只改变源图保留策略，不绕过任何船体搬运步骤。
            ControllerMapHasGravAnchor?.SetValue(Find.GravshipController, true);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label}: mobile conversion handed to vanilla InitiateTakeoff");
        }

        /// <summary>原版捕获完成后跳过可见起飞动画，但仍让 TakeoffEnded 执行全部收尾。</summary>
        public static void SuppressMobileLaunchAnimation()
        {
            if (!MobileLaunchPending || ControllerTimeLeft == null || Find.GravshipController == null) return;
            ControllerTimeLeft.SetValue(Find.GravshipController, 0f);
        }

        /// <summary>兜底：若 BeginCutscene 发生在其他更新边界，下一帧仍强制完成。</summary>
        public static void BeforeGravshipControllerUpdate(WorldComponent_GravshipController controller)
        {
            if (!MobileLaunchPending || !controller.IsGravshipTravelling || ControllerTimeLeft == null) return;
            ControllerTimeLeft.SetValue(controller, 0f);
        }

        /// <summary>
        /// 原版 TakeoffEnded 调用 TravelTo 的扩展点：船已完成原生捕获/移除与源图收尾，
        /// 此处只把世界旅行目的地改为独占口袋图。返回 true 表示已消费该次 TravelTo。
        /// </summary>
        public static bool TryCompleteMobileLaunch(Gravship gravship)
        {
            ShipState ship = pendingMobileShip;
            Building_GravEngine engine = pendingMobileEngine;
            Map sourceMap = pendingMobileSourceMap;
            if (ship == null || engine == null || sourceMap == null
                || gravship == null || gravship.Engine != engine) return false;

            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null)
            {
                Log.Error("[DeltaV] Mobile launch reached TravelTo without UniverseManager; vanilla travel retained.");
                ClearPendingMobileLaunch();
                return false;
            }

            // GenerateGravship 的电力注销是 PowerNetManager 延迟动作。原版十秒动画期间
            // 会自然结算；我们把动画压到零，因此在新图 Spawn/PowerBuildings 前显式走
            // 同一个原生结算入口，避免旧 PowerNet 上重复 RegisterConnector。
            sourceMap.powerNetManager.UpdatePowerNetsAndConnections_First();

            // 固定模式期间允许非对称扩建；新口袋图会以当前船体中心为局部原点，
            // 因而 SpacePose 也必须移动到该中心的既有 Universe 位置，保证每个建筑
            // 起飞前后的绝对位置不变。停靠图 tile 帧角度恒 0，切空间偏移即地图坐标差。
            Vector2 sourceStructureCenter = new Vector2(
                pendingMobileSourceRoot.x + gravship.engineToCenter.x,
                pendingMobileSourceRoot.z + gravship.engineToCenter.z);
            Vector3 launchDir = OrbitSphere.Move(ship.pose.position,
                sourceStructureCenter - pendingMobileOldOrigin);

            // 主台在口袋图规范到 East（局部 +X），避免 180° 往返时将降落旋转重复计入。
            gravship.Rotation = gravship.PilotConsole != null
                ? ShipOrientation.PlacementRotation(gravship.PilotConsole.Rotation, Rot4.East)
                : new Rot4(-ship.dockedRotation.AsInt);
            if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe,
                out Map pocketMap, out IntVec3 pocketRoot))
            {
                // 新容器生成失败时不能吞船：用捕获时的 North 布局原样放回停靠图。
                gravship.Rotation = Rot4.North;
                PlaceIntoMap(gravship, pendingMobileSourceRoot, sourceMap);
                FinishConsumedVanillaTravel();
                ClearPendingMobileLaunch();
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                Log.Error($"[DeltaV] Launching '{ship.label}': failed to generate replacement pocket map; restored ship to docked map.");
                return true;
            }

            ship.map = pocketMap;
            ship.pocketRoot = pocketRoot;
            PlaceIntoMap(gravship, pocketRoot, pocketMap);
            Current.Game.Gravship = null;

            ship.pose.position = launchDir;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            ship.pose.angle = SpaceTransform.WrapAngle(pendingMobileHeading);
            ship.docked = false;
            ship.docking = false;
            ship.dockedMap = null;
            ship.dockedOrigin = Vector2.zero;
            // 世界视图标记是“进入移动模式时的船体快照”，不追踪后续战损/建造变化。
            WorldShipMarkerRenderer.RebuildFor(ship);
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.SetFocused(ship);
            CameraJumper.TryJump(ship.pocketRoot, pocketMap, CameraJumper.MovementMode.Cut);
            FinishConsumedVanillaTravel();
            ClearPendingMobileLaunch();
            QueueEmptyDepartedMapCleanup(universe, sourceMap);
            Messages.Message("DeltaV_MobileSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label} entered mobile mode without takeoff cutscene; heading={ship.pose.angle * Mathf.Rad2Deg:F0} deg");
            return true;
        }

        /// <summary>
        /// 延后到帧边界清点离港地图：此时已完全脱离原版 TakeoffEnded 调用栈，
        /// DeinitAndRemoveMap 不会踩到上层仍在使用的引用。
        /// </summary>
        private static void QueueEmptyDepartedMapCleanup(UniverseManager universe, Map map)
        {
            if (map == null) return;
            LongEventHandler.ExecuteWhenFinished(delegate { TryDeleteEmptyDepartedMap(universe, map); });
        }

        /// <summary>
        /// 离港后的空降落图清理：太空层地图若已彻底为空（无人、无建筑/物品、无其他停靠船，
        /// 只剩 Filth/Gas/Mote 残迹），直接删除——它只是降落后的空画布，走 POI 的休眠/快照
        /// 机制没有意义。任何遗留内容都保留地图（原版持久语义）。
        /// </summary>
        internal static void TryDeleteEmptyDepartedMap(UniverseManager universe, Map map)
        {
            if (universe == null || map == null || map.Disposed || !Find.Maps.Contains(map)) return;
            if (map == Find.CurrentMap) return; // 聚焦/降落选点中的图绝不删
            if (!map.Tile.Valid || !map.Tile.LayerDef.isSpace) return;
            if (SpaceLifecycle.IsUniverseHost(map)) return;
            if (map.mapPawns.AllPawnsSpawned.Any()) return;
            if (universe.Ships.Any(s => s.dockedMap == map)) return;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.Destroyed || !thing.Spawned) continue;
                if (thing.def.category == ThingCategory.Filth
                    || thing.def.category == ThingCategory.Gas
                    || thing.def.category == ThingCategory.Mote) continue;
                return; // 有可留存内容：保留地图
            }
            MapParent parent = map.Parent;
            string label = parent?.Label ?? map.ToString();
            Current.Game.DeinitAndRemoveMap(map, false);
            parent?.Destroy();
            ShipPhysics.ClearCache();
            DeltaVLog.Message(DeltaVLogModule.Lifecycle, $"{label}: empty departed landing map removed");
        }

        private static void FinishConsumedVanillaTravel()
        {
            Current.Game.Gravship = null;
            ControllerGravship?.SetValue(Find.GravshipController, null);
        }

        private static void ClearPendingMobileLaunch()
        {
            pendingMobileShip = null;
            pendingMobileEngine = null;
            pendingMobileSourceMap = null;
            pendingMobileSourceRoot = IntVec3.Invalid;
            pendingMobileOldOrigin = Vector2.zero;
            pendingMobileHeading = 0f;
        }

        /// <summary>停靠图中代表该船口袋图几何中心的 Map 坐标；旧存档缺字段时可重建。</summary>
        public static Vector2 DockedOriginMapPoint(ShipState ship)
        {
            if (ship?.dockedMap == null) return Vector2.zero;
            if (ship.dockedOrigin != Vector2.zero) return ship.dockedOrigin;
            Building_GravEngine engine = FindEngineForShip(ship.dockedMap, ship.primaryConsole);
            if (engine == null) return SpaceTransform.ShipLocalOrigin(ship.dockedMap);
            if (ship.map != null && ship.pocketRoot.IsValid)
            {
                Vector2 pocketCenterFromRoot = SpaceTransform.ShipLocalOrigin(ship.map)
                    - new Vector2(ship.pocketRoot.x, ship.pocketRoot.z);
                ship.dockedOrigin = new Vector2(engine.Position.x, engine.Position.z)
                    + SpaceTransform.RotatePlacementOffset(pocketCenterFromRoot, ship.dockedRotation);
            }
            else
            {
                // 极旧存档的最后兜底：以当前有效船体的几何中心作为局部原点。
                Vector3 center = CellRect.FromCellList(engine.ValidSubstructure).CenterVector3;
                ship.dockedOrigin = new Vector2(center.x, center.z);
            }
            return ship.dockedOrigin;
        }

        internal static Building_GravEngine FindEngineForShip(Map map, Building primaryConsole)
        {
            Building_GravEngine fallback = null;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                var engine = thing as Building_GravEngine;
                if (engine == null) continue;
                if (fallback == null) fallback = engine;
                if (primaryConsole != null && primaryConsole.Spawned && primaryConsole.Map == map
                    && engine.ValidSubstructure.Contains(primaryConsole.Position)) return engine;
            }
            return fallback;
        }

        /// <summary>
        /// 判断宿主图上的 Thing 是否属于指定停靠船。停靠后 ship.map 已销毁，不能再靠
        /// ShipOnMap 反查；原版 GravEngine.ValidSubstructure 是此时实际船体归属的权威集合。
        /// </summary>
        internal static bool ContainsDockedThing(ShipState ship, Thing thing)
        {
            if (ship == null || thing == null || !ship.docked || ship.dockedMap == null
                || thing.MapHeld != ship.dockedMap)
            {
                return false;
            }
            Building_GravEngine engine = FindEngineForShip(ship.dockedMap, ship.primaryConsole);
            return engine != null && engine.ValidSubstructure.Contains(thing.PositionHeld);
        }

        private static void PlaceIntoMap(Gravship gravship, IntVec3 root, Map map)
        {
            GravshipPlacementUtility.PlaceGravshipInMap(gravship, root, map, out List<Thing> spawned);
            GravshipPlacementUtility.ApplyTemperatureVacuumFromBase(gravship, root, map);
            map.listerFilthInHomeArea.RebuildAll();
            map.resourceCounter.UpdateResourceCounts();
            map.wealthWatcher.ForceRecount(allowDuringInit: true);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            foreach (Thing thing in spawned)
            {
                if (thing != null && !thing.Destroyed) thing.PostSwapMap();
            }
            foreach (Pawn pawn in gravship.Pawns)
            {
                if (pawn != null && !pawn.Destroyed) pawn.PostSwapMap();
            }
        }
    }
}

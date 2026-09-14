using System.Collections.Generic;
using System.Linq;
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
    /// </summary>
    public static class DockController
    {
        private static ShipState pendingShip;
        private static Gravship pendingGravship;
        private static ShipState landedShipAwaitingPocketCleanup;

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
            // 固定轨道的承载画布永远是宿主图。POI/敌舰保持独立 Map，只通过
            // Universe 投影出现在宿主图；接近对象绝不能改变玩家船的 Map 归属。
            Map targetMap = universe.hostMap;
            if (targetMap == null || !Find.Maps.Contains(targetMap))
            {
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (targetMap == universe.hostMap && targetMap.terrainGrid.TerrainAt(targetMap.Center) == TerrainDefOf.Space)
            {
                // 原版选点明确要求至少一格船体与非 Space 地形相连；宿主图中心是固定轨道锚。
                targetMap.terrainGrid.SetTerrain(targetMap.Center, DeltaVDefOf.Substructure);
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
                BeginOriginalLanding(universe, ship, engine, targetMap, snappedHeading);
            }, "GeneratingGravship", false, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        private static void BeginOriginalLanding(UniverseManager universe, ShipState ship,
            Building_GravEngine engine, Map targetMap, float snappedHeading)
        {
            ship.preDockPose = ship.pose;
            ship.pocketRoot = engine.Position;
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

            // 降落选点发生在旧 HostFrame 中：把真实船体原点的宿主图坐标换回 Universe，
            // 使“落在敌舰/POI 旁边”成为真正的宇宙位置变化。随后 HostFrame 由
            // pose.position 与 dockedOrigin 共同反推，确认前后其他对象不会跳位。
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            SpaceObject hostFrame = universe?.HostFrame;
            if (hostFrame != null && marker.Map == universe.hostMap)
            {
                Vector2 hostLocal = ship.dockedOrigin - SpaceTransform.ShipLocalOrigin(marker.Map);
                ship.pose.position = SpaceTransform.FocusedToUniverse(hostFrame, hostLocal);
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
            LongEventHandler.QueueLongEvent(delegate { LaunchInternal(universe, ship, engine); },
                "GeneratingGravship", false, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        private static void LaunchInternal(UniverseManager universe, ShipState ship,
            Building_GravEngine engine)
        {
            Map sourceMap = ship.dockedMap;
            IntVec3 sourceRoot = engine.Position;
            Vector2 oldDockedOrigin = DockedOriginMapPoint(ship);
            Building console = ship.primaryConsole;
            float heading = console != null && console.Spawned
                ? ShipOrientation.AngleFromRot4(console.Rotation)
                : ship.pose.angle;
            Gravship gravship = GravshipUtility.GenerateGravship(engine);
            if (gravship == null)
            {
                Log.Error($"[DeltaV] Launching '{ship.label}': GenerateGravship returned null; aborting.");
                return;
            }

            // 固定轨道期间允许非对称扩建；新口袋图会以当前船体中心为局部原点，
            // 因而 SpacePose 也必须移动到该中心的既有 Universe 位置，保证每个建筑
            // 起飞前后的绝对位置不变。
            Vector2 sourceStructureCenter = new Vector2(
                sourceRoot.x + gravship.engineToCenter.x,
                sourceRoot.z + gravship.engineToCenter.z);
            Vector2 launchUniversePosition = ship.pose.position
                + sourceStructureCenter - oldDockedOrigin;

            // 主台在口袋图规范到 East（局部 +X），避免 180° 往返时将降落旋转重复计入。
            gravship.Rotation = gravship.PilotConsole != null
                ? ShipOrientation.PlacementRotation(gravship.PilotConsole.Rotation, Rot4.East)
                : new Rot4(-ship.dockedRotation.AsInt);
            if (!ShipPocketMapFactory.TryGenerateForGravship(gravship, universe.hostMap,
                launchUniversePosition, out Map pocketMap, out IntVec3 pocketRoot))
            {
                // 新容器生成失败时不能吞船：用捕获时的 North 布局原样放回宿主图。
                gravship.Rotation = Rot4.North;
                PlaceIntoMap(gravship, sourceRoot, sourceMap);
                Current.Game.Gravship = null;
                Messages.Message("DeltaV_DockFailHostMap".Translate(), MessageTypeDefOf.RejectInput, false);
                Log.Error($"[DeltaV] Launching '{ship.label}': failed to generate replacement pocket map; restored ship to docked map.");
                return;
            }

            ship.map = pocketMap;
            ship.pocketRoot = pocketRoot;
            PlaceIntoMap(gravship, pocketRoot, pocketMap);
            Current.Game.Gravship = null;

            ship.pose.position = launchUniversePosition;
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
            ship.pose.angle = SpaceTransform.WrapAngle(heading);
            ship.docked = false;
            ship.docking = false;
            ship.dockedMap = null;
            ship.dockedOrigin = Vector2.zero;
            HostColonistBarVisibility.NotifyDockingStateChanged();
            universe.SetFocused(ship);
            CameraJumper.TryJump(ship.pocketRoot, pocketMap, CameraJumper.MovementMode.Cut);
            Messages.Message("DeltaV_LaunchSuccess".Translate(ship.label), MessageTypeDefOf.PositiveEvent, false);
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"{ship.label} launched without takeoff cutscene; heading={ship.pose.angle * Mathf.Rad2Deg:F0} deg");
        }

        public static void DrawHud(UniverseManager universe)
        {
            ShipState piloted = PilotController.PilotedShip(universe);
            if (piloted?.map != null && !piloted.docked && !piloted.docking && Find.CurrentMap == piloted.map)
            {
                var rect = new Rect(8f, UI.screenHeight * 0.42f + 158f, 170f, 30f);
                if (Widgets.ButtonText(rect, "DeltaV_DockButton".Translate())) TryDock(universe, piloted);
            }
            foreach (ShipState ship in universe.Ships)
            {
                if (ship.docked && ship.dockedMap == Find.CurrentMap)
                {
                    var rect = new Rect(8f, UI.screenHeight * 0.42f, 170f, 30f);
                    if (Widgets.ButtonText(rect, "DeltaV_LaunchButton".Translate())) TryLaunch(universe, ship);
                    break;
                }
            }
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

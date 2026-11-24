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
    /// 原版逆重战略起降的唯一适配入口。世界选址、燃料、起飞登记、离场善后与降落
    /// 捕获共用这里解析的源地址和运行时状态；Harmony 只负责转发原版事件。
    /// </summary>
    internal sealed partial class ShipFlightTransitionService
    {
        private static readonly FieldInfo ControllerMapHasGravAnchor =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "mapHasGravAnchor");

        private static readonly FieldInfo ControllerTakeoffTile =
            AccessTools.Field(typeof(WorldComponent_GravshipController), "takeoffTile");

        // 回归夹具直接置入起飞保护窗口以验证地图生命周期门禁；生产写入仍只在本类。
        internal Map pendingStrategicSourceMap;
        private PlanetTile pendingStrategicSourceAddress;
        private SpacePose? pendingStrategicSourcePose;
        private int pendingStrategicOwnPocketId;

        private sealed class StrategicLandingCapture
        {
            internal readonly Building_GravEngine engine;
            internal readonly HashSet<IntVec3> cells;
            internal readonly SubstructureOwnership ownership;

            internal StrategicLandingCapture(Building_GravEngine engine, HashSet<IntVec3> cells,
                SubstructureOwnership ownership)
            {
                this.engine = engine;
                this.cells = cells;
                this.ownership = ownership;
            }
        }

        private StrategicLandingCapture strategicLandingCapture;

        /// <summary>
        /// 原版战略起飞从捕获源图开始到 TravelTo 放行前仍要用该图绘制过场；此窗口内
        /// 结构移除造成的暂时无承载状态不能触发地图生命周期回收。
        /// </summary>
        internal bool IsAwaitingStrategicTakeoff(Map map) => map != null
            && pendingStrategicSourceMap == map;

        /// <summary>战略发射源 = 发射船的自有口袋容器（发射后按离场标准流程销毁）。</summary>
        private SpaceObject PendingStrategicOwnPocket
        {
            get => universe?.ObjectById(pendingStrategicOwnPocketId);
            set => pendingStrategicOwnPocketId = value?.objectId ?? 0;
        }

        /// <summary>
        /// Map 对应的唯一战略起点。真实 worldtile 保留自身 tile；Delta-V 口袋图从承载
        /// SpaceObject 的连续宇宙位姿求当前轨道星区。空轨道格无需 POI 或保留降落图。
        /// </summary>
        internal PlanetTile StrategicSourceAddress(Map map)
        {
            if (map != null && map.Tile.Valid) return map.Tile;
            if (!OrbitSphere.Valid) return PlanetTile.Invalid;
            SpaceObject owner = universe?.SpaceObjectOnMap(map);
            return owner == null ? PlanetTile.Invalid : OrbitSphere.TileFromDirection(owner.pose.position);
        }

        /// <summary>世界选址器两次 Map.Tile 读取共用的中央入口。</summary>
        internal static PlanetTile StrategicSelectionTile(Map map)
        {
            if (map == null || map.Tile.Valid) return map?.Tile ?? PlanetTile.Invalid;
            ShipFlightTransitionService transitions = Current.Game?.GetComponent<UniverseManager>()?.ShipTransitions;
            return transitions?.StrategicSourceAddress(map) ?? PlanetTile.Invalid;
        }

        internal void OpenStrategicDestinationPicker(CompPilotConsole console)
        {
            Map map = console?.parent?.Map;
            if (map == null || map.Tile.Valid) return;
            PlanetTile address = StrategicSourceAddress(map);
            if (address.Valid) CameraJumper.TryJump(address);
        }

        internal void RewriteStrategicSourceTile(Map map, ref PlanetTile from)
        {
            if (from.Valid) return;
            PlanetTile address = StrategicSourceAddress(map);
            if (address.Valid) from = address;
        }

        /// <summary>
        /// 降落放置前冻结 Gravship 自身 foundation，并登记同一集合的结构归属盖章。
        /// 地表 Map 没有 Delta-V 容器，因此只释放可能残留的旧状态，不建立新状态。
        /// </summary>
        internal void BeginStrategicLanding(Gravship gravship, Map map, IntVec3 landingPos)
        {
            ReleaseStrategicLandingCapture();
            // 普通地表从不属于 Delta-V 容器；先按稳定的战略层语义早退，避免为一次
            // 原版降落对全部 SpaceObject 做无命中的 Map 反查。
            if (map != null && map.Tile.Valid && !map.Tile.LayerDef.isSpace) return;
            if (universe?.FrameOf(map) == null || gravship?.Engine == null) return;

            var cells = new HashSet<IntVec3>(gravship.Foundations.Count);
            foreach (IntVec3 cell in gravship.Foundations.Keys) cells.Add(cell + landingPos);

            SubstructureOwnership ownership = SubstructureOwnership.For(map);
            ownership.SetPendingStamp(cells, ResolveLandingOwnerId(gravship));
            strategicLandingCapture = new StrategicLandingCapture(gravship.Engine, cells, ownership);
        }

        private int ResolveLandingOwnerId(Gravship gravship)
        {
            if (gravship.Engine.Spawned)
            {
                SubstructureOwnership ownership = SubstructureOwnership.TryGet(gravship.Engine.Map);
                int owner = ownership?.OwnerAt(gravship.Engine.Position) ?? SubstructureOwnership.Unassigned;
                if (owner != SubstructureOwnership.Unassigned) return owner;
            }
            CompDeltaVPilotConsole identity = CompDeltaVPilotConsole.IdentityConsoleOf(gravship);
            return identity != null ? identity.ShipIdentityId(universe) : universe.AllocateOwnerId();
        }

        public bool TryGetLandingCaptureScope(Building_GravEngine engine, out HashSet<IntVec3> scope)
        {
            StrategicLandingCapture capture = strategicLandingCapture;
            scope = null;
            if (engine == null || capture == null || !ReferenceEquals(engine, capture.engine)) return false;
            scope = capture.cells;
            return true;
        }

        /// <summary>
        /// LandingEnded 的唯一收尾。未建立捕获状态的地表降落安全通过；已有状态直接清理
        /// 保存的归属组件，不再从可能失效或为空的 Map 反查。
        /// </summary>
        internal void CompleteStrategicLanding(Map landedMap)
        {
            ReleaseStrategicLandingCapture();
            MarkStrategicLandingWorksite(landedMap);
            Building_DockingPort.NotifyLandingSettled(landedMap);
        }

        private void ReleaseStrategicLandingCapture()
        {
            StrategicLandingCapture capture = strategicLandingCapture;
            strategicLandingCapture = null;
            capture?.ownership?.ClearPendingStamp();
        }

        internal bool MarkStrategicLandingWorksite(Map map)
        {
            if (map == null || (map.Tile.Valid && !map.Tile.LayerDef.isSpace)) return false;
            return universe?.FrameOf(map) is PoiState { kind: PoiKind.NativeParent }
                && DeltaVPocketMapMetadata.MarkWorksite(map);
        }

        internal void HandleVanillaTakeoffFromDeltaVSource(Building_GravEngine engine,
            WorldComponent_GravshipController controller)
        {
            if (engine?.Map == null || universe == null) return;
            Map sourceMap = engine.Map;
            bool pocketSource = !sourceMap.Tile.Valid;
            if (!pocketSource && !sourceMap.Tile.LayerDef.isSpace) return;

            ControllerMapHasGravAnchor?.SetValue(controller, true);
            pendingStrategicSourceMap = sourceMap;
            pendingStrategicSourceAddress = StrategicSourceAddress(sourceMap);
            pendingStrategicSourcePose = universe.FrameOf(sourceMap)?.pose;
            if (pocketSource && pendingStrategicSourceAddress.Valid)
                ControllerTakeoffTile?.SetValue(controller, pendingStrategicSourceAddress);

            SubstructureOwnership ownership = SubstructureOwnership.TryGet(sourceMap);
            int engineOwner = ownership?.OwnerAt(engine.Position) ?? SubstructureOwnership.Unassigned;
            List<Ship> shipsOnMap = ShipRegistry.FoundationShipsOnMap(sourceMap);
            Ship launcher = shipsOnMap.FirstOrDefault(s => s.ownerId == engineOwner);
            SpaceObject sourceContainer = universe.SpaceObjectOnMap(sourceMap);
            PendingStrategicOwnPocket = launcher.IsValid && !ShipRegistry.AllShipsOnMap(sourceMap)
                .Any(ship => ship.Identity != DriveSubject.Of(launcher) && !ShipRegistry.Contains(launcher, ship.Anchor))
                && sourceContainer != null && !(sourceContainer is PoiState)
                ? sourceContainer
                : null;
            if (launcher.IsValid)
            {
                universe.Pilot.Exit(universe);
                universe.Autopilot.Cancel(DriveSubject.Of(launcher));
                HostColonistBarVisibility.NotifyDockingStateChanged();
            }
            DeltaVLog.Message(DeltaVLogModule.Core,
                $"vanilla strategic takeoff from {(pocketSource ? "pocket" : "tile")} map: " +
                $"source address sector {pendingStrategicSourceAddress}, own pocket recorded={PendingStrategicOwnPocket != null}");
        }

        internal void HandleStrategicTravelDeparture()
        {
            Map sourceMap = pendingStrategicSourceMap;
            PlanetTile address = pendingStrategicSourceAddress;
            SpacePose? pose = pendingStrategicSourcePose;
            int ownPocketId = PendingStrategicOwnPocket?.objectId ?? 0;
            int ownPocketVersion = PendingStrategicOwnPocket?.RepresentationVersion ?? -1;
            ClearPendingStrategicTakeoff();
            if (sourceMap == null) return;

            universe.QueueFrameAction(delegate
            {
                if (universe == null || sourceMap.Disposed || !Find.Maps.Contains(sourceMap)) return;
                SpaceObject ownPocket = universe.ObjectById(ownPocketId);
                if (ownPocketId != 0 && ownPocket?.RepresentationVersion != ownPocketVersion) return;
                if (ownPocket != null && ownPocket.map == sourceMap && !sourceMap.Disposed)
                {
                    if (sourceMap == Find.CurrentMap) Current.Game.CurrentMap = null;
                    AbandonPawnsOn(sourceMap);
                    ConvertLeftoverSubstructureToWreck(universe, sourceMap,
                        pose ?? new SpacePose(Vector3.right, 0f));
                    MapOverlayRenderer.Release(sourceMap);
                    universe.MapStates.DestroyPocketMap(sourceMap);
                    universe.UnregisterObject(ownPocket);
                    ShipPhysics.Forget(sourceMap);
                    DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                        "flight pocket map removed after strategic takeoff (leftovers converted)");
                }

                Map sectorAddressMap = address.Valid && address.LayerDef.isSpace
                    ? Find.WorldObjects.MapParentAt(address)?.Map
                    : null;
                Map sourceWorldtileMap = StrategicDepartureWorldtileSource(sourceMap);
                if (sourceWorldtileMap != null && !sourceWorldtileMap.Disposed
                    && Find.Maps.Contains(sourceWorldtileMap)
                    && SourceWorldtileWillBeAbandoned(sourceWorldtileMap))
                {
                    if (sourceWorldtileMap == Find.CurrentMap) Current.Game.CurrentMap = null;
                    AbandonUnanchoredSourceWorldtile(sourceWorldtileMap);
                }
                if (ownPocket == null && sourceMap != sectorAddressMap && sourceMap == Find.CurrentMap)
                    Current.Game.CurrentMap = null;
                universe.MapStates.PackDepartedSectorPois(address, sectorAddressMap);
            });
        }

        private void ClearPendingStrategicTakeoff()
        {
            pendingStrategicSourceMap = null;
            pendingStrategicSourceAddress = PlanetTile.Invalid;
            pendingStrategicSourcePose = null;
            PendingStrategicOwnPocket = null;
        }

        internal static Map StrategicDepartureWorldtileSource(Map sourceMap) =>
            sourceMap != null && sourceMap.Tile.Valid ? sourceMap : null;

        internal bool SourceWorldtileWillBeAbandoned(Map sourceMap)
        {
            return StrategicDepartureWorldtileSource(sourceMap) == sourceMap
                && Find.Maps.Contains(sourceMap)
                && !sourceMap.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor)
                && universe != null && !ShipRegistry.AllShipsOnMap(sourceMap).Any();
        }

        private void AbandonUnanchoredSourceWorldtile(Map map)
        {
            string mapLabel = map.Parent?.Label ?? map.ToString();
            if (map == Find.CurrentMap)
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (currently focused)");
                return;
            }
            if (SpaceMapStateManager.IsUniverseHost(map))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (universe host)");
                return;
            }
            if (ShipRegistry.AllShipsOnMap(map).Any())
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (docked ship present)");
                return;
            }
            if (map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor))
            {
                DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                    $"{mapLabel}: unanchored abandon skipped (grav anchor present)");
                return;
            }
            ConvertLeftoverSubstructureToWreck(universe, map,
                new SpacePose(OrbitSphere.TileCenterDirection(map.Tile), 0f));
            GravshipUtility.AbandonMap(map);
            ShipPhysics.Forget(map);
            DeltaVLog.Message(DeltaVLogModule.Lifecycle,
                $"{mapLabel}: unanchored source worldtile map abandoned after strategic takeoff");
        }

        private void ResetStrategicGravshipState()
        {
            ReleaseStrategicLandingCapture();
            ClearPendingStrategicTakeoff();
        }
    }
}

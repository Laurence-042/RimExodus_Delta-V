using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 开发者模式调试入口（Dev mode → Debug actions → Delta-V）。
    /// 保留明确标注的场景/生命周期回归入口和只读诊断；不提供绕过控制链的捷径。
    /// </summary>
    public static class DebugActions_DeltaV
    {
        private static UniverseManager Um()
        {
            return Current.Game.GetComponent<UniverseManager>();
        }

        private static ShipBase PlayerShip()
        {
            return Um().PrimaryPlayerShip();
        }

        /// <summary>最近的物化容器（非自身容器；物化 POI 图 = 普通容器，2026-09-25 起）。</summary>
        private static SpaceObject NearestMaterializedContainer()
        {
            UniverseManager um = Um();
            ShipBase player = PlayerShip();
            if (!player.IsValid) return null;
            SpaceObject best = null;
            float bestDist = float.MaxValue;
            foreach (SpaceObject obj in um.objects)
            {
                if (obj is PoiState || obj.map == null || obj.map == player.Map) continue;
                float distance = um.Lifecycle.MinPlayerDistance(obj);
                if (distance < bestDist)
                {
                    bestDist = distance;
                    best = obj;
                }
            }
            return best;
        }

        [DebugAction("Delta-V", "Create two-ship test scene", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreateTwoShipTestScene()
        {
            LongEventHandler.QueueLongEvent(ShipTestScene.Create, "GeneratingMap", false, null);
        }

        // 通用地图 prefab 导出：把当前聚焦图居住区（Home Area 掩码）导出为
        // mod 根 Prefabs/ 下的时间戳文件，供 MapPrefab.Spawn 重建（无需重启）。
        [DebugAction("Delta-V", "Prefab: export home area as timestamped file", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ExportHomePrefabTimestamped()
        {
            MapPrefab.Export("Prefab_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                Find.CurrentMap.areaManager.Home.ActiveCells);
        }

        [DebugAction("Delta-V", "Prefab: replace focused map...", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReplaceFocusedMapFromPrefab()
        {
            UniverseManager universe = Um();
            SpaceObject source = universe.FrameOf(Find.CurrentMap);
            if (!universe.Lifecycle.CanReplaceFocusedMap(source, out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            try
            {
                var files = MapPrefab.ListPrefabFiles();
                if (files.Count == 0)
                {
                    Messages.Message("DeltaV_PrefabReplaceNoFiles".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Find.WindowStack.Add(new Dialog_DebugOptionListLister(files.Select(path =>
                    new DebugMenuOption(path, DebugMenuOptionMode.Action,
                        () => SelectPrefabReplacementFaction(universe, source, path))),
                    "DeltaV_PrefabReplaceSelect".Translate()));
            }
            catch (System.Exception ex)
            {
                Log.Error($"[DeltaV] Cannot list map prefabs: {ex}");
                Messages.Message("DeltaV_PrefabReplaceFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void SelectPrefabReplacementFaction(UniverseManager universe, SpaceObject source, string path)
        {
            if (!universe.Lifecycle.CanReplaceFocusedMap(source, out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Faction currentFaction = source.faction ?? source.map.Parent?.Faction;
            if (currentFaction == null)
            {
                // Plain ship containers have no environment faction; resolve their ship instead.
                ShipBase selected = universe.Pilot.SelectedShipOnMap(source.map);
                if (selected.IsValid) currentFaction = selected.Faction;
            }
            string noFaction = "DeltaV_PrefabReplaceNoFaction".Translate();
            var options = new System.Collections.Generic.List<DebugMenuOption>
            {
                new DebugMenuOption("DeltaV_PrefabReplaceKeepFaction".Translate(currentFaction?.Name ?? noFaction),
                    DebugMenuOptionMode.Action,
                    () => universe.Lifecycle.ReplaceFocusedMapFromPrefab(source, path, currentFaction)),
                new DebugMenuOption(noFaction, DebugMenuOptionMode.Action,
                    () => universe.Lifecycle.ReplaceFocusedMapFromPrefab(source, path, null))
            };
            foreach (Faction faction in Find.FactionManager.AllFactionsListForReading.OrderBy(f => f.Name))
            {
                options.Add(new DebugMenuOption(faction.Name, DebugMenuOptionMode.Action,
                    () => universe.Lifecycle.ReplaceFocusedMapFromPrefab(source, path, faction)));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options,
                "DeltaV_PrefabReplaceSelectFaction".Translate(path)));
        }

        /// <summary>
        /// 按键抑制链路逐层体检（启动时另有一条补丁挂载常开日志）：
        /// ① 补丁是否真的挂在三个 getter 上（owner=RimExodus.DeltaV）；
        /// ② 抑制开关/命中计数；③ 驾驶状态与当前地图；④ 驾驶键集合与本 mod 按键实际绑定；
        /// ⑤ 原版冲突绑定的重叠判定样例。断在哪一层一目了然。
        /// </summary>
        [DebugAction("Delta-V", "Diagnose key suppression", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseKeySuppression()
        {
            var sb = new StringBuilder("[DeltaV:Diag] Key suppression diagnostics");
            // ① 补丁挂载
            string[] getters = { "IsDown", "JustPressed", "KeyDownEvent" };
            foreach (string name in getters)
            {
                var method = AccessTools.PropertyGetter(typeof(KeyBindingDef), name);
                var info = method == null ? null : Harmony.GetPatchInfo(method);
                string owners = info == null || info.Owners.Count == 0
                    ? "NOT PATCHED"
                    : string.Join(",", info.Owners);
                sb.AppendLine($"\n- patch KeyBindingDef.get_{name}: {(owners == "RimExodus.DeltaV" ? "OK (owner=RimExodus.DeltaV)" : owners)}");
            }
            UniverseManager um = Um();
            // ② 抑制状态
            sb.AppendLine($"- SuppressVanillaKeys flag: {PilotKeyBridge.SuppressVanillaKeys}; suppressed reads so far: {um.Pilot.SuppressedReads}");
            // ③ 驾驶状态
            ShipBase piloted = um.Pilot.PilotedShip(um);
            sb.AppendLine($"- piloted structure: #{(piloted.IsValid ? piloted.Identity.ToString() : "none")}, piloted map alive: {piloted.IsValid}, CurrentMap == piloted map: {piloted.IsValid && Find.CurrentMap == piloted.Map}");
            // ④ 键集合 + 本 mod 绑定
            sb.AppendLine($"- ActivePilotKeys: [{(PilotKeyBridge.ActiveKeys.Count == 0 ? "EMPTY" : string.Join(", ", PilotKeyBridge.ActiveKeys))}]");
            foreach (string defName in new[] { "DeltaV_ThrustForward", "DeltaV_ThrustBackward", "DeltaV_StrafeLeft", "DeltaV_StrafeRight", "DeltaV_RotateLeft", "DeltaV_RotateRight", "DeltaV_TogglePilotMode" })
            {
                KeyBindingDef def = KeyBindingDef.Named(defName);
                string bound = def == null
                    ? "DEF MISSING (XML not loaded?)"
                    : (KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out KeyBindingData d)
                        ? $"{d.keyBindingA}/{d.keyBindingB} (keyPrefs entry OK)"
                        : $"no keyPrefs entry, defaults {def.defaultKeyCodeA}/{def.defaultKeyCodeB}");
                sb.AppendLine($"- def {defName}: {bound}");
            }
            // ⑤ 原版冲突样例
            foreach (string defName in new[] { "MapDolly_Up", "MapDolly_Down", "MapDolly_Left", "MapDolly_Right", "Designator_RotateLeft", "Designator_RotateRight" })
            {
                KeyBindingDef def = KeyBindingDef.Named(defName);
                if (def == null)
                {
                    sb.AppendLine($"- vanilla {defName}: def missing");
                    continue;
                }
                KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out KeyBindingData d);
                sb.AppendLine($"- vanilla {defName}: {d.keyBindingA}/{d.keyBindingB} -> overlap={um.Pilot.OverlapsActiveKeys(def)}");
            }
            Log.Message(sb.ToString());
            Messages.Message("Key suppression diagnostics written to the log (dev console / Ctrl+F12).", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "MPC: diagnostics summary", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.Playing)]
        private static void MpcDiagnosticsSummary()
        {
            UniverseManager universe = Um();
            string summary = "[DeltaV:MPC] " + AutopilotMpcDiagnostics.Summary(
                universe?.Autopilot.Profile ?? AutopilotMpcProfile.Balanced);
            Log.Message(summary);
            Messages.Message("MPC diagnostics written to the log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "Combat autopilot: trajectory snapshot", actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.Playing)]
        private static void CombatAutopilotSnapshot()
        {
            Log.Message(Um()?.Autopilot.CombatDiagnostics()
                ?? "[DeltaV:Combat] universe unavailable");
            Messages.Message("Combat autopilot snapshot written to the log.",
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "MPC: deterministic self-test", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.Playing)]
        private static void MpcSelfTest()
        {
            string report = AutopilotMpcDiagnostics.RunSelfTest();
            Log.Message(report);
            Messages.Message("MPC self-test written to the log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "Coordinates: inverse and landing-root self-test", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CoordinateSelfTest()
        {
            var report = new StringBuilder("[DeltaV:Coordinates] inverse/placement self-test");
            int failures = 0;
            IntVec2[] sizes = { new IntVec2(40, 24), new IntVec2(39, 23),
                new IntVec2(40, 23), new IntVec2(39, 24) };
            IntVec3 root = new IntVec3(73, 0, 55);
            foreach (IntVec2 size in sizes)
            {
                foreach (Rot4 rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
                {
                    IntVec3 input = GravshipInterop.RootToDesignatorInput(root, size, rot);
                    IntVec3 actual = GravshipInterop.DesignatorInputToRoot(input, size, rot);
                    if (actual != root)
                    {
                        failures++;
                        report.AppendLine($"FAIL size={size} rot={rot}: root={root} input={input} actual={actual}");
                    }
                    IntVec3 sampleInput = new IntVec3(31, 0, 47);
                    IntVec3 roundTrip = GravshipInterop.RootToDesignatorInput(
                        GravshipInterop.DesignatorInputToRoot(sampleInput, size, rot), size, rot);
                    if (roundTrip != sampleInput)
                    {
                        failures++;
                        report.AppendLine($"FAIL input inverse size={size} rot={rot}: {roundTrip}");
                    }
                }
            }
            IntVec3 sample = GravshipInterop.RootToDesignatorInput(root, sizes[0], Rot4.North);
            if (sample != new IntVec3(72, 0, 54))
            {
                failures++;
                report.AppendLine($"FAIL 40x24 North regression: root={root}, designator input={sample}");
            }

            SpaceObject owner = Um().FrameOf(Find.CurrentMap);
            if (owner != null && OrbitSphere.Valid)
            {
                Map map = Find.CurrentMap;
                Vector2 mapPoint = SpaceTransform.ShipLocalOrigin(map) + new Vector2(8.25f, -5.75f);
                Vector3 direction = SpaceTransform.MapPointToUniverse(owner, map, mapPoint);
                float mapError = (SpaceTransform.UniverseToMapPoint(owner, map, direction) - mapPoint).magnitude;
                if (mapError > 0.5f)
                {
                    failures++;
                    report.AppendLine($"FAIL map round trip: error={mapError:F4} cells");
                }
                Vector2 longOffset = new Vector2(OrbitSphere.CellsPerRadian * 0.15f, 1200f);
                float arcError = (OrbitSphere.Offset(owner.pose.position,
                    OrbitSphere.Move(owner.pose.position, longOffset)) - longOffset).magnitude;
                if (arcError > 2f)
                {
                    failures++;
                    report.AppendLine($"FAIL spherical log/exp: error={arcError:F3} cells");
                }
                SpacePose rotating = owner.pose;
                rotating.angle = SpaceTransform.WrapAngle(rotating.angle + 0.47f);
                rotating.velocity = new Vector2(20f, -4f);
                rotating.angularVelocity = 10f * Mathf.Deg2Rad;
                Vector2 localPoint = new Vector2(43.25f, -18.5f);
                Vector3 pointDir = SpaceTransform.ShipLocalToUniverse(rotating, localPoint);
                float rotatingError = (SpaceTransform.UniverseToShipLocal(rotating, pointDir) - localPoint).magnitude;
                if (rotatingError > 0.5f)
                {
                    failures++;
                    report.AppendLine($"FAIL rotating host inverse: error={rotatingError:F4} cells");
                }
                report.AppendLine($"map round trip={mapError:F4} cells; spherical log/exp={arcError:F3} cells; rotating host={rotatingError:F4} cells");
            }
            else report.AppendLine("map round trip skipped: current map has no orbital SpaceObject");
            report.AppendLine($"result: {(failures == 0 ? "PASS" : "FAIL")}, failures={failures}");
            Log.Message(report.ToString());
        }

        // ---- 帧时间读数（性能测试用，Prefs.DevMode 门控常驻；UniverseManager Update/OnGUI 调用）----

        private static float avgFrameMs = 16.7f;

        public static void UpdateFrameMeter()
        {
            avgFrameMs = Mathf.Lerp(avgFrameMs, Time.unscaledDeltaTime * 1000f, 0.05f);
        }

        public static void DrawFrameMeter()
        {
            if (!Prefs.DevMode || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 0.4f, 0.9f);
            Widgets.Label(new Rect(UI.screenWidth - 310f, UI.screenHeight - 100f, 300f, 20f),
                $"DeltaV frame: {avgFrameMs:F1} ms ({1000f / Mathf.Max(avgFrameMs, 0.01f):F0} fps)");
            Widgets.Label(new Rect(UI.screenWidth - 310f, UI.screenHeight - 80f, 300f, 20f),
                $"DeltaV key suppression: {(PilotKeyBridge.SuppressVanillaKeys ? "ACTIVE" : "off")} (suppressed reads: {Current.Game?.GetComponent<UniverseManager>()?.Pilot.SuppressedReads ?? 0})");
            GUI.color = Color.white;
        }

        // ---- POI 生命周期验证工具 ----

        [DebugAction("Delta-V", "POI: full archive nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForcePack()
        {
            // 只绕过距离门禁；聚焦图、玩家图和原生 MapParent 仍受保护。
            SpaceObject container = NearestMaterializedContainer();
            if (container?.map != null) Um().Lifecycle.ArchiveContainer(container, container.map, force: true);
        }

        [DebugAction("Delta-V", "Player ship: teleport +1500 east (cross lifecycle ranges)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PlayerTeleportEast1500()
        {
            UniverseManager um = Um();
            ShipBase player = PlayerShip();
            SpaceObject frame = player.IsValid ? um.FrameOf(player.Map) : um.Focused;
            if (frame != null)
            {
                frame.pose.position = OrbitSphere.Move(frame.pose.position, new Vector2(1500f, 0f));
            }
        }

        // ---- 星区记账（验证触发链路；正常触发点 = 移动跨界 / 到达链 / 开局）----

        [DebugAction("Delta-V", "Sector: force generate at player", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SectorForceGenerate()
        {
            UniverseManager um = Um();
            ShipBase player = PlayerShip();
            if (!player.IsValid)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlanetTile tile = OrbitSphere.TileFromDirection(ShipRegistry.ShipPose(player).position);
            if (!tile.Valid)
            {
                Messages.Message("No orbit layer.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            SectorContentService.EnsureSectorGenerated(um, tile);
            Messages.Message($"Sector {tile}: objects={SectorContentService.ObjectsInSector(um, tile).Count}, " +
                $"generatedSectors={um.generatedSectorTiles.Count}", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "Diagnose focused map (force mesh regen)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseFocusedMap()
        {
            Map map = Find.CurrentMap;
            UniverseManager um = Um();
            var sb = new StringBuilder("[DeltaV:Diag] Focused map");
            sb.AppendLine($"\n- map: {map?.Parent?.Label ?? "null"} tile={map?.Tile.ToString() ?? "-"} size={map?.Size.x}x{map?.Size.z}");
            sb.AppendLine($"- frame: {um?.FrameOf(map)?.label ?? "(none)"}");
            sb.AppendLine($"- frozen={um?.Lifecycle.IsFrozen(map) == true} (dormant={um?.Lifecycle.IsDormant(map) == true}" +
                $" generating={um?.Generation?.IsGenerating(map) == true} host={SpaceLifecycleService.IsUniverseHost(map)})");
            sb.AppendLine($"- wantedMode={Find.World?.renderer?.wantedMode} drawingMap={RimWorld.Planet.WorldRendererUtility.DrawingMap}");
            sb.AppendLine($"- camera rootSize={Find.CameraDriver?.RootSize:F0} pos={Find.CameraDriver?.MapPosition}");
            map?.mapDrawer.RegenerateEverythingNow();
            sb.AppendLine("- mapDrawer.RegenerateEverythingNow() executed; if the view recovers, mesh state was stale.");
            Log.Message(sb.ToString());
            Messages.Message("Focused-map diagnostics + forced mesh regeneration written to the log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "Dump universe state", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpUniverseState()
        {
            UniverseManager um = Um();
            var sb = new StringBuilder("[DeltaV:Diag] Universe state");
            sb.AppendLine($"\n- universe host pocket map: {(um.UniverseHostMap == null ? "not created" : "alive")}");
            sb.AppendLine($"- current map (Find.CurrentMap): {UniverseManager.MapTag(Find.CurrentMap)}");
            sb.AppendLine($"- orbit layer: {(OrbitSphere.Layer == null ? "none" : $"tiles={OrbitSphere.Layer.TilesCount} radius={OrbitSphere.Layer.Radius:F1}" +
                $" avgTileSize={OrbitSphere.Layer.AverageTileSize:F2} worldPerCell={OrbitSphere.WorldPerCell:F4} cellsPerRadian={OrbitSphere.CellsPerRadian:F0}")}");
            sb.AppendLine($"- scale: cellsPerTile={OrbitSphere.CellsPerTile:F0} (setting {DeltaVMod.CurrentSettings.cellsPerTile});" +
                $" generated sectors={um.generatedSectorTiles.Count}");
            sb.AppendLine($"- poi radius: active={DeltaVMod.CurrentSettings.poiActiveRadius:F0} delete={DeltaVMod.CurrentSettings.poiDeleteRadius:F0}");
            foreach (SpaceObject obj in um.objects)
            {
                string lifecycle = obj is PoiState poi
                    ? (poi.map == null ? "packed" + (poi.snapshot != null ? " (snapshot)" : " (fresh)") : um.Lifecycle.IsDormant(poi.map) ? "dormant" : "active")
                    : (obj.map == null ? "mapless" : "container");
                float lat = Mathf.Asin(Mathf.Clamp(obj.pose.position.y, -1f, 1f)) * Mathf.Rad2Deg;
                float lon = Mathf.Atan2(obj.pose.position.z, obj.pose.position.x) * Mathf.Rad2Deg;
                string playerDist = obj is PoiState poi2
                    ? $" playerDist={um.Lifecycle.MinPlayerDistance(poi2):F0}"
                    : "";
                string sector = OrbitSphere.Valid
                    ? $" sector={OrbitSphere.TileFromDirection(obj.pose.position)}"
                    : "";
                sb.AppendLine($"\n- #{obj.objectId} {obj.label} [{obj.GetType().Name}] tag={obj.Tag} {lifecycle}" +
                    $" dir=({obj.pose.position.x:F3},{obj.pose.position.y:F3},{obj.pose.position.z:F3}) lat/lon=({lat:F1}°,{lon:F1}°){sector}" +
                    $"{playerDist} angle={SpaceTransform.WrapAngle(obj.pose.angle) * Mathf.Rad2Deg:F0}° {UniverseManager.MapTag(obj.map)}");
            }
            // 全部已加载图（含无船图）+ 每图上的船；图标识与其他日志同口径
            //（MapTag：uniqueID 全会话稳定，Index 仅供对照 PerfProbe，勿混用）。
            sb.AppendLine("\n- ships:");
            foreach (Map map in Find.Maps)
            {
                sb.AppendLine($"  {UniverseManager.MapTag(map)}");
                SpaceObject frame = um.FrameOf(map);
                var ships = ShipRegistry.AllShipsOnMap(map).ToList();
                if (ships.Count == 0 && um.NpcShips.HasUnclaimedNpcConsole(frame))
                    sb.AppendLine($"    NPC console present without valid main ship; "
                        + $"AI gate={um.NpcShips.GateFor(frame, Ship.Invalid)}");
                foreach (ShipBase ship in ships)
                {
                    int hostileTargets = frame == null ? 0
                        : NpcCombatAi.CountEligibleTargets(um, ship, frame);
                    sb.AppendLine($"    ship #{ship.Identity} '{ship.Label}' " +
                        $"relation={ship.State} faction={ship.Faction?.Name ?? "none"} " +
                        $"anchor#{ship.Anchor.thingIDNumber} hull={ship.HullCells.Count()} cells " +
                        $"frozen={um.Lifecycle.IsFrozen(map)} zeroG={FreeFlightBootstrap.IsZeroGravity(um, map)} " +
                        $"playerDist={(frame == null ? float.PositiveInfinity : um.Lifecycle.MinPlayerDistance(frame)):F0} " +
                        $"mapThrust={(frame?.MotionInfo.AvailableThrust ?? 0f):F0} hostileTargets={hostileTargets} " +
                        $"AI gate={um.NpcShips.GateFor(frame, ship)} " +
                        $"autopilot={um.Autopilot.IsActive(DriveSubject.Of(ship))}");
                }
            }
            Log.Message(sb.ToString());
        }

        // ---- 太空战诊断 ----

        [DebugAction("Delta-V", "Combat: dump projectile pool state", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CombatDumpPool()
        {
            Log.Message($"[DeltaV:Combat] {Um()?.Projectiles.PoolStatus()}");
        }

    }
}

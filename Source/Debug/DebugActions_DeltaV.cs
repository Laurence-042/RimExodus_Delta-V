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
    /// 平移/旋转敌舰直接写容器绝对宇宙状态——这正是验证"显示层只是投影"的手段。
    /// </summary>
    public static class DebugActions_DeltaV
    {
        private static UniverseManager Um()
        {
            return Current.Game.GetComponent<UniverseManager>();
        }

        /// <summary>「敌舰」= 非聚焦的其他容器（测试场景第二船的承载对象）。</summary>
        private static SpaceObject OtherContainer()
        {
            UniverseManager um = Um();
            return um.objects.FirstOrDefault(o => o != um.Focused && o.map != null);
        }

        private static Ship PlayerShip()
        {
            return Um().PrimaryPlayerShip();
        }

        private static PoiState FirstPoi()
        {
            // 强制快照/休眠动作只操作 Delta-V 的测试残骸，不能误删自动纳管的任务 POI。
            return Um().objects.OfType<PoiState>()
                .FirstOrDefault(p => p.kind == PoiKind.SnapshotPocket);
        }

        [DebugAction("Delta-V", "Create two-ship test scene", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreateTwoShipTestScene()
        {
            LongEventHandler.QueueLongEvent(ShipTestScene.Create, "GeneratingMap", false, null);
        }

        [DebugAction("Delta-V", "Toggle pilot mode", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TogglePilotMode()
        {
            UniverseManager universe = Um();
            universe?.Pilot.ToggleControl(universe);
        }

        [DebugAction("Delta-V", "Toggle passive drag", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.Playing)]
        private static void TogglePassiveDrag()
        {
            DeltaVSettings settings = DeltaVMod.CurrentSettings;
            if (settings == null) return;
            settings.passiveDrag = !settings.passiveDrag;
            LoadedModManager.GetMod<DeltaVMod>()?.WriteSettings();
            Messages.Message($"Delta-V passive drag: {settings.passiveDrag} "
                + $"(linear half-life {settings.dragLinearHalfLifeTicks} ticks, angular {settings.dragAngularHalfLifeTicks} ticks)",
                MessageTypeDefOf.NeutralEvent, false);
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
            Ship piloted = um.Pilot.PilotedShip(um);
            sb.AppendLine($"- piloted structure: #{(piloted.IsValid ? piloted.ownerId.ToString() : "none")}, piloted map alive: {piloted.IsValid}, CurrentMap == piloted map: {piloted.IsValid && Find.CurrentMap == piloted.map}");
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

        [DebugAction("Delta-V", "Zero focused ship velocity", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ZeroFocusedShipVelocity()
        {
            SpaceObject frame = Um().FrameOf(Find.CurrentMap);
            if (frame == null)
            {
                return;
            }
            frame.pose.velocity = Vector2.zero;
            frame.pose.angularVelocity = 0f;
        }

        [DebugAction("Delta-V", "MPC: cycle developer profile", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.Playing)]
        private static void CycleMpcProfile()
        {
            AutopilotMpcProfile profile = Um().Autopilot.CycleProfile();
            Messages.Message($"Delta-V MPC profile: {profile}", MessageTypeDefOf.NeutralEvent, false);
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

        /// <summary>
        /// 手动修复：本图全部基架改归新分配的结构归属（无主残骸语义）。用于修复盖章
        /// 缺口留下的孤儿归属；随后可用「修改基架归属」命令重新拆分。
        /// </summary>
        [DebugAction("Delta-V", "Reset substructure ownership to fresh owner", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ResetSubstructureOwnership()
        {
            UniverseManager universe = Um();
            Map map = Find.CurrentMap;
            if (universe?.FrameOf(map) == null)
            {
                Log.Message("[DeltaV] Reset ownership: current map is not a Delta-V frame map.");
                return;
            }
            SubstructureOwnership ownership = SubstructureOwnership.For(map);
            if (!ownership.HasAnySubstructure())
            {
                Log.Message("[DeltaV] Reset ownership: no substructure on this map.");
                return;
            }
            int defaultOwner = universe.AllocateOwnerId();
            universe.NoteStructureName(defaultOwner, universe.NewWreckName(defaultOwner));
            int count = ownership.ClaimAllSubstructure(defaultOwner);
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            Log.Message($"[DeltaV] Reset ownership: map {map.Index}, {count} cells -> structure #{defaultOwner} "
                + $"({universe.LabelForOwner(defaultOwner)}).");
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

        // ---- POI 生命周期验证工具（阶段3；全部经 SpaceLifecycle 单一入口）----

        [DebugAction("Delta-V", "POI: create derelict 400 cells east", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreatePoi400East()
        {
            UniverseManager um = Um();
            Ship player = PlayerShip();
            if (!player.IsValid)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var poi = new PoiState(um.AllocateOwnerId(), "Derelict wreck",
                OrbitSphere.Move(ShipRegistry.ShipPose(player).position, new Vector2(400f, 0f)),
                Random.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int);
            um.RegisterObject(poi);
            Messages.Message("DeltaV_PoiCreated".Translate(poi.label, "400"), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("Delta-V", "POI: create derelict 1500 cells north (delete range)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreatePoi1500North()
        {
            UniverseManager um = Um();
            Ship player = PlayerShip();
            if (!player.IsValid)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var poi = new PoiState(um.AllocateOwnerId(), "Distant wreck",
                OrbitSphere.Move(ShipRegistry.ShipPose(player).position, new Vector2(0f, 1500f)),
                Random.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int);
            um.RegisterObject(poi);
            Messages.Message("DeltaV_PoiCreated".Translate(poi.label, "1500"), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("Delta-V", "POI: force activate nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForceActivate()
        {
            PoiState poi = FirstPoi();
            if (poi != null) Um().Lifecycle.ForceActivate(poi);
        }

        [DebugAction("Delta-V", "POI: force dormant nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForceDormant()
        {
            PoiState poi = FirstPoi();
            if (poi != null) Um().Lifecycle.ForceDormant(poi);
        }

        [DebugAction("Delta-V", "POI: force pack (delete) nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForcePack()
        {
            PoiState poi = FirstPoi();
            if (poi != null) Um().Lifecycle.ForcePack(poi);
        }

        [DebugAction("Delta-V", "Player ship: teleport +1500 east (cross lifecycle ranges)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PlayerTeleportEast1500()
        {
            UniverseManager um = Um();
            Ship player = PlayerShip();
            SpaceObject frame = player.IsValid ? um.FrameOf(player.map) : um.Focused;
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
            Ship player = PlayerShip();
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

        /// <summary>拆分测试：把聚焦图上当前船以外的基架块立刻拆为独立残骸容器（§8 公共拆分直驱）。</summary>
        [DebugAction("Delta-V", "Split test: force split residual blocks on focused map", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SplitTestForceSplit()
        {
            UniverseManager um = Um();
            Map map = Find.CurrentMap;
            Ship ship = ShipRegistry.ShipsOnMap(map).FirstOrDefault();
            if (!ship.IsValid)
            {
                Messages.Message("No ship on focused map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            um.ShipTransitions.SplitResiduals(um, ship);
            Messages.Message("Residual blocks split (see log).", MessageTypeDefOf.NeutralEvent, false);
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
            sb.AppendLine($"- orbit layer: {(OrbitSphere.Layer == null ? "none" : $"tiles={OrbitSphere.Layer.TilesCount} radius={OrbitSphere.Radius:F1}" +
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
                sb.AppendLine($"\n- #{obj.objectId} {obj.label} [{obj.GetType().Name}] {lifecycle}" +
                    $" dir=({obj.pose.position.x:F3},{obj.pose.position.y:F3},{obj.pose.position.z:F3}) lat/lon=({lat:F1}°,{lon:F1}°){sector}" +
                    $"{playerDist} angle={SpaceTransform.WrapAngle(obj.pose.angle) * Mathf.Rad2Deg:F0}° map={(obj.map == null ? "none" : "loaded")}");
            }
            sb.AppendLine("\n- ships:");
            foreach (Map map in Find.Maps)
            {
                foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                {
                    sb.AppendLine($"  structure #{ship.ownerId} '{um.LabelForOwner(ship.ownerId)}' map={map.Index} " +
                        $"state={ShipRegistry.StateOf(ship)} faction={ShipRegistry.FactionOf(ship)?.Name ?? "none"} " +
                        $"console#{ship.console.parent.thingIDNumber} domain={ShipRegistry.ControlDomain(ship).Count} cells");
                }
            }
            Log.Message(sb.ToString());
        }

        [DebugAction("Delta-V", "Switch focus to the other ship", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SwitchFocusToOtherShip()
        {
            UniverseManager um = Um();
            SpaceObject other = OtherContainer();
            if (other == null)
            {
                Messages.Message("No other container in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            SpaceObject previous = um.Focused;
            // 缩放同步：Notify_SwitchedMap 恢复目标图记忆的位置+缩放，跳转后需显式重设（RimExodus 同款）。
            float zoom = Find.CameraDriver.RootSize;
            um.SetFocused(other);
            // 相机直接跳到旧船在新参考系里的显示位置（clamp 进地图），切过去立刻看到反向代理。
            if (previous != null && other.map != null)
            {
                Vector3 pos = SpaceTransform.FocusedDrawPosition(other, other.map, previous.pose.position, 0f);
                int x = Mathf.Clamp(Mathf.RoundToInt(pos.x), 2, other.map.Size.x - 3);
                int z = Mathf.Clamp(Mathf.RoundToInt(pos.z), 2, other.map.Size.z - 3);
                CameraJumper.TryJump(new IntVec3(x, 0, z), other.map, CameraJumper.MovementMode.Cut);
            }
            Find.CameraDriver.SetRootSize(zoom);
        }

        [DebugAction("Delta-V", "Enemy ship: forward 10", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyForward10()
        {
            MoveEnemy(10f, 0f);
        }

        [DebugAction("Delta-V", "Enemy ship: backward 10", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyBackward10()
        {
            MoveEnemy(-10f, 0f);
        }

        [DebugAction("Delta-V", "Enemy ship: strafe left 10", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyLeft10()
        {
            MoveEnemy(0f, 10f);
        }

        [DebugAction("Delta-V", "Enemy ship: strafe right 10", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyRight10()
        {
            MoveEnemy(0f, -10f);
        }

        [DebugAction("Delta-V", "Enemy ship: rotate +15 deg (CCW)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyRotatePlus15()
        {
            SpaceObject ship = OtherContainer();
            if (ship != null)
            {
                ship.pose.angle = SpaceTransform.WrapAngle(ship.pose.angle + 15f * Mathf.Deg2Rad);
            }
        }

        [DebugAction("Delta-V", "Enemy ship: rotate -15 deg (CW)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyRotateMinus15()
        {
            SpaceObject ship = OtherContainer();
            if (ship != null)
            {
                ship.pose.angle = SpaceTransform.WrapAngle(ship.pose.angle - 15f * Mathf.Deg2Rad);
            }
        }

        /// <summary>沿敌舰自身参考系平移（前向 + 左向），沿球面写入绝对方向。</summary>
        private static void MoveEnemy(float forward, float left)
        {
            SpaceObject ship = OtherContainer();
            if (ship == null)
            {
                return;
            }
            float angle = ship.pose.angle;
            Vector2 fwd = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 leftDir = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle));
            ship.pose.position = OrbitSphere.Move(ship.pose.position, fwd * forward + leftDir * left);
        }

        // ---- 阶段4：对接计划与跳帮测试设施 ----

        [DebugAction("Delta-V", "Enemy ship: set drift velocity (20 east)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyDriftEast()
        {
            SpaceObject ship = OtherContainer();
            if (ship != null)
            {
                ship.pose.velocity = new Vector2(20f, 0f);
            }
        }

        [DebugAction("Delta-V", "Enemy ship: set spin (+10 deg/s)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemySpin()
        {
            SpaceObject ship = OtherContainer();
            if (ship != null)
            {
                ship.pose.angularVelocity = 10f * Mathf.Deg2Rad;
            }
        }

        [DebugAction("Delta-V", "Enemy ship: hostile faction + spawn crew (3)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyHostileCrew()
        {
            UniverseManager um = Um();
            SpaceObject other = OtherContainer();
            Map enemyMap = other?.map;
            if (enemyMap == null)
            {
                return;
            }
            Faction hostile = Faction.OfPirates
                ?? Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => f != null && f.HostileTo(Faction.OfPlayer));
            if (hostile == null)
            {
                Log.Warning("[DeltaV] No hostile faction available for enemy crew spawn.");
                return;
            }
            // 船的派系事实 = 驾驶台派系：把敌舰全部建筑改派系（含驾驶台）。
            int setFaction = 0;
            foreach (Thing thing in enemyMap.listerThings.AllThings.ToList())
            {
                if (thing is Building building && building.def.CanHaveFaction && building.Faction != hostile)
                {
                    building.SetFaction(hostile);
                    setFaction++;
                }
            }
            int spawned = 0;
            for (int attempt = 0; attempt < 30 && spawned < 3; attempt++)
            {
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    hostile.def.basicMemberKind, hostile, PawnGenerationContext.NonPlayer));
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(enemyMap.Center, enemyMap, 18);
                if (!cell.IsValid || !cell.Standable(enemyMap))
                {
                    continue;
                }
                GenSpawn.Spawn(pawn, cell, enemyMap);
                spawned++;
            }
            Log.Message($"[DeltaV] Enemy container set hostile to player ({setFaction} buildings refactioned); {spawned} defenders spawned.");
        }

        [DebugAction("Delta-V", "Docking plan: force complete", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DockingPlanForceComplete()
        {
            Um()?.Approach.DebugForceComplete();
        }

        [DebugAction("Delta-V", "Docking: cancel (site selection or plan)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DockingPlanCancel()
        {
            UniverseManager universe = Um();
            universe?.Approach.CancelSelectionOrPlan(universe, "dev action");
        }
    }
}

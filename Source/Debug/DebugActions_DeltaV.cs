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
    /// 阶段1 开发者模式调试入口（Dev mode → Debug actions → Delta-V）。
    /// 平移/旋转敌舰直接写绝对宇宙状态——这正是验证"显示层只是投影"的手段。
    /// </summary>
    public static class DebugActions_DeltaV
    {
        private static UniverseManager Um()
        {
            return Current.Game.GetComponent<UniverseManager>();
        }

        private static ShipState OtherShip()
        {
            UniverseManager um = Um();
            return um.Ships.FirstOrDefault(s => s != um.Focused && s.map != null);
        }

        private static ShipState PlayerShip()
        {
            return Um().PrimaryShip;
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
            PilotController.Toggle(Um());
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
            // ② 抑制状态
            sb.AppendLine($"- SuppressVanillaKeys flag: {PilotController.SuppressVanillaKeys}; suppressed reads so far: {Patch_KeyBindingSuppression.SuppressedReads}");
            // ③ 驾驶状态
            UniverseManager um = Um();
            ShipState piloted = PilotController.PilotedShip(um);
            sb.AppendLine($"- pilotShipId: {um.pilotShipId} ({piloted?.label ?? "none"}), piloted map alive: {piloted?.map != null}, CurrentMap == piloted map: {piloted?.map != null && Find.CurrentMap == piloted.map}");
            // ④ 键集合 + 本 mod 绑定
            sb.AppendLine($"- ActivePilotKeys: [{(PilotController.ActivePilotKeys.Count == 0 ? "EMPTY" : string.Join(", ", PilotController.ActivePilotKeys))}]");
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
                sb.AppendLine($"- vanilla {defName}: {d.keyBindingA}/{d.keyBindingB} -> overlap={PilotController.OverlapsActiveKeys(def)}");
            }
            Log.Message(sb.ToString());
            Messages.Message("Key suppression diagnostics written to the log (dev console / Ctrl+F12).", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Delta-V", "Zero focused ship velocity", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ZeroFocusedShipVelocity()
        {
            ShipState ship = Um().ShipOnMap(Find.CurrentMap);
            if (ship == null)
            {
                return;
            }
            ship.pose.velocity = Vector2.zero;
            ship.pose.angularVelocity = 0f;
        }

        /// <summary>
        /// 手动修复：本图全部基架改归地图默认归属（宿主 SpaceObject）。用于修复早期
        /// 版本盖章缺口留下的孤儿归属（如 wreck 图被测试改乱）；随后可用「修改基架
        /// 归属」命令重新拆分。一图多对象的图执行后归属合并，属预期。
        /// </summary>
        [DebugAction("Delta-V", "Reset substructure ownership to map default owner", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
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
            int defaultOwner = universe.SpaceObjectOnMap(map)?.objectId ?? universe.AllocateOwnerId();
            int count = ownership.ClaimAllSubstructure(defaultOwner);
            PlanetGlobeRenderer.InvalidateSilhouette(map);
            Log.Message($"[DeltaV] Reset ownership: map {map.Index}, {count} cells -> owner #{defaultOwner} "
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
                $"DeltaV key suppression: {(PilotController.SuppressVanillaKeys ? "ACTIVE" : "off")} (suppressed reads: {Patch_KeyBindingSuppression.SuppressedReads})");
            GUI.color = Color.white;
        }

        // ---- POI 生命周期验证工具（阶段3；全部经 SpaceLifecycle 单一入口）----

        [DebugAction("Delta-V", "POI: create derelict 400 cells east", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreatePoi400East()
        {
            UniverseManager um = Um();
            ShipState player = PlayerShip();
            if (player == null)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var poi = new PoiState(um.NextObjectId(), "Derelict wreck",
                OrbitSphere.Move(player.pose.position, new Vector2(400f, 0f)),
                Random.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int);
            um.RegisterObject(poi);
            Messages.Message("DeltaV_PoiCreated".Translate(poi.label, "400"), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("Delta-V", "POI: create derelict 1500 cells north (delete range)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreatePoi1500North()
        {
            UniverseManager um = Um();
            ShipState player = PlayerShip();
            if (player == null)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var poi = new PoiState(um.NextObjectId(), "Distant wreck",
                OrbitSphere.Move(player.pose.position, new Vector2(0f, 1500f)),
                Random.Range(0f, 360f) * Mathf.Deg2Rad, Rand.Int);
            um.RegisterObject(poi);
            Messages.Message("DeltaV_PoiCreated".Translate(poi.label, "1500"), MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction("Delta-V", "POI: force activate nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForceActivate()
        {
            PoiState poi = FirstPoi();
            if (poi != null) SpaceLifecycle.ForceActivate(Um(), poi);
        }

        [DebugAction("Delta-V", "POI: force dormant nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForceDormant()
        {
            PoiState poi = FirstPoi();
            if (poi != null) SpaceLifecycle.ForceDormant(poi);
        }

        [DebugAction("Delta-V", "POI: force pack (delete) nearest", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PoiForcePack()
        {
            PoiState poi = FirstPoi();
            if (poi != null) SpaceLifecycle.ForcePack(poi);
        }

        [DebugAction("Delta-V", "Player ship: teleport +1500 east (cross lifecycle ranges)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PlayerTeleportEast1500()
        {
            ShipState player = PlayerShip();
            if (player != null)
            {
                player.pose.position = OrbitSphere.Move(player.pose.position, new Vector2(1500f, 0f));
            }
        }

        // ---- 星区记账（验证触发链路；正常触发点 = 移动跨界 / 到达链 / 开局）----

        [DebugAction("Delta-V", "Sector: force generate at player", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SectorForceGenerate()
        {
            UniverseManager um = Um();
            ShipState player = PlayerShip();
            if (player == null)
            {
                Messages.Message("No player ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlanetTile tile = OrbitSphere.TileFromDirection(player.pose.position);
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
            sb.AppendLine($"- owner: {um?.SpaceObjectOnMap(map)?.label ?? "(none)"}; frame={um?.FrameOf(map)?.label ?? "(none)"}");
            sb.AppendLine($"- frozen={SpaceLifecycle.IsFrozen(map)} (dormant={MapDormancyService.IsDormant(map)}" +
                $" generating={IncrementalMapGenerationService.IsGenerating(map)} host={SpaceLifecycle.IsUniverseHost(map)})");
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
            sb.AppendLine($"- scale: cellsPerTile={OrbitSphere.CellsPerTile:F0} (setting {DeltaVMod.Settings.cellsPerTile});" +
                $" generated sectors={um.generatedSectorTiles.Count}");
            sb.AppendLine($"- poi radius: active={DeltaVMod.Settings.poiActiveRadius:F0} delete={DeltaVMod.Settings.poiDeleteRadius:F0}");
            sb.AppendLine($"- primary ship: #{um.primaryShipId}; pilot: #{um.pilotShipId}; focused: #{um.focusedShipId}");
            foreach (SpaceObject obj in um.objects)
            {
                string lifecycle = obj is PoiState poi
                    ? (poi.map == null ? "packed" + (poi.snapshot != null ? " (snapshot)" : " (fresh)") : SpaceLifecycle.IsDormant(poi.map) ? "dormant" : "active")
                    : ((ShipState)obj).docked ? "docked" : "flying";
                float lat = Mathf.Asin(Mathf.Clamp(obj.pose.position.y, -1f, 1f)) * Mathf.Rad2Deg;
                float lon = Mathf.Atan2(obj.pose.position.z, obj.pose.position.x) * Mathf.Rad2Deg;
                string playerDist = obj is PoiState poi2
                    ? $" playerDist={SpaceLifecycle.MinPlayerDistance(um, poi2):F0}"
                    : "";
                string sector = OrbitSphere.Valid
                    ? $" sector={OrbitSphere.TileFromDirection(obj.pose.position)}"
                    : "";
                sb.AppendLine($"\n- #{obj.objectId} {obj.label} [{obj.GetType().Name}] {lifecycle}" +
                    $" dir=({obj.pose.position.x:F3},{obj.pose.position.y:F3},{obj.pose.position.z:F3}) lat/lon=({lat:F1}°,{lon:F1}°){sector}" +
                    $"{playerDist} angle={SpaceTransform.WrapAngle(obj.pose.angle) * Mathf.Rad2Deg:F0}° map={(obj.map == null ? "none" : "loaded")}");
            }
            Log.Message(sb.ToString());
        }

        [DebugAction("Delta-V", "Switch focus to the other ship", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SwitchFocusToOtherShip()
        {
            UniverseManager um = Um();
            ShipState other = OtherShip();
            if (other == null)
            {
                Messages.Message("No other ship in universe state.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            ShipState previous = um.Focused;
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
            ShipState ship = OtherShip();
            if (ship != null)
            {
                ship.pose.angle = SpaceTransform.WrapAngle(ship.pose.angle + 15f * Mathf.Deg2Rad);
            }
        }

        [DebugAction("Delta-V", "Enemy ship: rotate -15 deg (CW)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyRotateMinus15()
        {
            ShipState ship = OtherShip();
            if (ship != null)
            {
                ship.pose.angle = SpaceTransform.WrapAngle(ship.pose.angle - 15f * Mathf.Deg2Rad);
            }
        }

        /// <summary>沿敌舰自身参考系平移（前向 + 左向），沿球面写入绝对方向。</summary>
        private static void MoveEnemy(float forward, float left)
        {
            ShipState ship = OtherShip();
            if (ship == null)
            {
                return;
            }
            float angle = ship.pose.angle;
            Vector2 fwd = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 leftDir = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle));
            ship.pose.position = OrbitSphere.Move(ship.pose.position, fwd * forward + leftDir * left);
        }
    }
}

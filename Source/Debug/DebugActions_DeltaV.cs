using System.Linq;
using LudeonTK;
using RimWorld;
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
            return um.ships.FirstOrDefault(s => s != um.Focused && s.map != null);
        }

        [DebugAction("Delta-V", "Create two-ship test scene", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CreateTwoShipTestScene()
        {
            LongEventHandler.QueueLongEvent(ShipTestScene.Create, "GeneratingMap", false, null);
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

        [DebugAction("Delta-V", "Focus space host map (all ships as proxies)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FocusSpaceHostMap()
        {
            UniverseManager um = Um();
            if (um.hostMap == null)
            {
                Messages.Message("No space host map. Create the test scene first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            float zoom = Find.CameraDriver.RootSize;
            Current.Game.CurrentMap = um.hostMap;
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
            CameraJumper.TryJump(um.hostMap.Center, um.hostMap, CameraJumper.MovementMode.Cut);
            Find.CameraDriver.SetRootSize(zoom);
        }

        [DebugAction("Delta-V", "View host tile on world map", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ViewHostTileOnWorldMap()
        {
            UniverseManager um = Um();
            if (um.hostMap == null)
            {
                Messages.Message("No space host map. Create the test scene first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            // 轨道层在世界地图上要拉远到太空视角才可见；直接跳到宿主 tile 最直观。
            CameraJumper.TryJump(um.hostMap.Tile, CameraJumper.MovementMode.Cut);
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

        [DebugAction("Delta-V", "Enemy ship: orbit animation toggle", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyOrbitToggle()
        {
            UniverseManager um = Um();
            um.orbitTestEnabled = !um.orbitTestEnabled;
            Messages.Message($"Orbit test: {(um.orbitTestEnabled ? "ON" : "OFF")} (radius 55, snaps enemy onto orbit)", MessageTypeDefOf.SilentInput, false);
        }

        [DebugAction("Delta-V", "Dump ship poses to log", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpPoses()
        {
            Log.Message(Um().BuildPoseDump("pose dump"));
        }

        [DebugAction("Delta-V", "Dump ship map section dirty states", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpSectionStates()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[DeltaV] ship map section states:");
            foreach (ShipState ship in Um().ships)
            {
                if (ship.map == null) continue;
                var sections = (Section[,])typeof(MapDrawer)
                    .GetField("sections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(ship.map.mapDrawer);
                var anyLayerDirtyField = typeof(Section).GetField("anyLayerDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (sections == null) continue;
                int dirtyFlagsCount = 0, anyLayerDirtyCount = 0, layerDirtyCount = 0;
                var dirtyTypes = new System.Collections.Generic.Dictionary<string, int>();
                for (int x = 0; x < sections.GetLength(0); x++)
                {
                    for (int z = 0; z < sections.GetLength(1); z++)
                    {
                        Section s = sections[x, z];
                        if (s.dirtyFlags != 0L) dirtyFlagsCount++;
                        if ((bool)anyLayerDirtyField.GetValue(s)) anyLayerDirtyCount++;
                        var layers = (System.Collections.Generic.List<SectionLayer>)typeof(Section)
                            .GetField("layers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .GetValue(s);
                        foreach (var layer in layers)
                        {
                            if (layer.Dirty)
                            {
                                layerDirtyCount++;
                                string typeName = layer.GetType().Name;
                                dirtyTypes[typeName] = dirtyTypes.TryGetValue(typeName, out int c) ? c + 1 : 1;
                            }
                        }
                    }
                }
                sb.AppendLine($"  ship #{ship.shipId} ({ship.label}): sections={sections.Length} dirtyFlags!=0:{dirtyFlagsCount} anyLayerDirty:{anyLayerDirtyCount} layersDirty:{layerDirtyCount}");
                foreach (var pair in dirtyTypes)
                {
                    sb.AppendLine($"    dirty layer {pair.Key} x{pair.Value}");
                }
            }
            Log.Message(sb.ToString().TrimEnd());
        }

        [DebugAction("Delta-V", "Dump ship map thingGrid contents", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpThingGrids()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[DeltaV] ship map thingGrid ground truth:");
            foreach (ShipState ship in Um().ships)
            {
                if (ship.map == null) continue;
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (Thing t in ship.map.listerThings.AllThings)
                {
                    string key = t.def.defName + (t.def.category == Verse.ThingCategory.Building ? " [bldg]" : "");
                    counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                }
                sb.AppendLine($"  ship #{ship.shipId} ({ship.label}) — {ship.map.listerThings.AllThings.Count} things:");
                foreach (var pair in counts)
                {
                    sb.AppendLine($"    {pair.Key} x{pair.Value}");
                }
            }
            Log.Message(sb.ToString().TrimEnd());
        }

        [DebugAction("Delta-V", "Force regenerate ship map meshes now", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceRegenerateShipMeshes()
        {
            foreach (ShipState ship in Um().ships)
            {
                ship.map?.mapDrawer.RegenerateEverythingNow();
            }
            Log.Message("[DeltaV] RegenerateEverythingNow executed on all ship maps.");
        }

        [DebugAction("Delta-V", "Debug HUD toggle", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugHudToggle()
        {
            UniverseManager um = Um();
            um.debugHudEnabled = !um.debugHudEnabled;
        }

        /// <summary>沿敌舰自身参考系平移（前向 + 左向），写入绝对宇宙坐标。</summary>
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
            ship.pose.position += fwd * forward + leftDir * left;
        }
    }
}

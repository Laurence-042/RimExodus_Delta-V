using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using RimWorld.Planet;

namespace DeltaV
{
    /// <summary>
    /// 地图软冻结的运行时拦截（阶段3；RimExodus Patches_Dormancy 同款模式）。
    ///
    /// 冻结集合 = 休眠 POI 图 + 专用宿主口袋图（SpaceLifecycle.IsFrozen）：
    /// - Map.MapPreTick / MapPostTick / MapUpdate：冻结图早退（原版
    ///   TickManager/Game.UpdatePlay 直接遍历 Find.Maps 调用，无其他拦截点）。
    ///   跳过即停全部组件模拟/更新；外壳代理渲染（ShellRenderer）是自驱动重放，
    ///   不依赖目标图的 MapUpdate，休眠图照常作为代理显示。
    /// - Game.CurrentMap setter：进入休眠 POI 图 = 玩家的显式唤醒意图，先唤醒再切图
    ///   （单点覆盖全部进入路径：殖民者栏分组框、地图按钮、CameraJumper、外壳点击）；
    ///   宿主口袋图则直接拒绝切入（它是纯 sourceMap 锚点画布，不可游玩）。
    /// - Storyteller.AllIncidentTargets：剔除全部冻结图，防事件打到冻结图/空宿主上。
    ///
    /// 判定集合只含 Delta-V 自己的地图（SpaceLifecycle 维护），与 RimExodus 的
    /// 地表休眠体系集合不相交、互不干扰（AGENTS §16/§3）。
    /// </summary>
    public static class Patch_PoiDormancy
    {
        [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
        private static class Patch_Map_MapPreTick
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsFrozen(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
        private static class Patch_Map_MapPostTick
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsFrozen(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        private static class Patch_Map_MapUpdate
        {
            private static bool Prefix(Map __instance)
            {
                return !SpaceLifecycle.IsFrozen(__instance);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
        private static class Patch_Game_CurrentMap_Setter
        {
            private static void Prefix(ref Map value)
            {
                if (value == null) return;
                if (SpaceLifecycle.IsUniverseHost(value))
                {
                    // 宿主口袋图不可聚焦：把切换重定向回当前图（幂等 no-op）。
                    Log.Warning("[DeltaV] Blocked an attempt to focus the universe host pocket map.");
                    value = Find.CurrentMap;
                    return;
                }
                bool wasFrozen = SpaceLifecycle.IsDormant(value);
                if (wasFrozen)
                {
                    // 同步唤醒（轻量，无生成），玩家切进去的图不能是冻结的。
                    SpaceLifecycle.Wake(value, "entering map (CurrentMap switch)");
                }
                if (wasFrozen || Current.Game?.GetComponent<UniverseManager>()?.SpaceObjectOnMap(value) is PoiState)
                {
                    // 冻结图跳过 MapUpdate：休眠期间累积/未刷新的脏 Section 永不重建，
                    // 画面空白但数据与悬停正常（2026-09-17 实测：建造一格触发局部
                    // MeshDirty 后整图恢复）。POI 内部图（含从未休眠的增量生成图，
                    // 根因未定位前）切入时一律全量重建一次，保证玩家看到的是活图
                    //（原版读档同款 RegenerateEverythingNow；仅 Delta-V 的图，
                    // RimExodus 地表图不受影响）。
                    value.mapDrawer.RegenerateEverythingNow();
                }
            }
        }

        [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets), MethodType.Getter)]
        private static class Patch_Storyteller_AllIncidentTargets
        {
            private static void Postfix(List<IIncidentTarget> __result)
            {
                // 返回的是原版静态缓存列表，调用方即用即弃，原地 RemoveAll 安全（RimExodus 同款结论）。
                __result.RemoveAll(t => t is Map m && SpaceLifecycle.IsFrozen(m));
            }
        }

        [HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
        private static class Patch_MapParent_CheckRemoveMapNow
        {
            private static bool Prefix(MapParent __instance)
            {
                return !__instance.HasMap || !SpaceMapGovernance.IsGoverned(__instance);
            }
        }

        [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty),
            new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
        private static class Patch_MapDrawer_MapMeshDirty
        {
            private static void Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags,
                bool regenAdjacentCells, bool regenAdjacentSections)
            {
                MapOverlayRenderer.MarkDirty(MapOverlayRenderer.MapOf(__instance), loc, dirtyFlags,
                    regenAdjacentCells, regenAdjacentSections);
            }
        }

        [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
        private static class Patch_MapGenerator_GenerateMap
        {
            private static void Prefix()
            {
                IncrementalMapGenerationService.DrainSynchronouslyForExternalGeneration();
            }
        }

        [HarmonyPatch(typeof(Dialog_SaveFileList_Save), "DoFileInteraction", new[] { typeof(string) })]
        private static class Patch_SaveDialog_DoFileInteraction
        {
            private static bool Prefix()
            {
                if (!IncrementalMapGenerationService.IsAnyGenerating) return true;
                ShowSaveBlocked();
                return false;
            }
        }

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
        private static class Patch_GameDataSaveLoader_SaveGame
        {
            private static bool Prefix()
            {
                if (!IncrementalMapGenerationService.IsAnyGenerating) return true;
                ShowSaveBlocked();
                return false;
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
        private static class Patch_Game_DeinitAndRemoveMap
        {
            private static void Prefix(Map map)
            {
                IncrementalMapGenerationService.NotifyExternalMapRemoval(map);
                SpaceLifecycle.Forget(map);
            }
        }

        [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.Destroy))]
        private static class Patch_WorldObject_Destroy
        {
            private static void Postfix(WorldObject __instance)
            {
                if (__instance is MapParent parent) OrbitalPoiRegistry.NotifyDestroyed(parent);
            }
        }

        [HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove))]
        private static class Patch_WorldObjectsHolder_Remove
        {
            private static void Postfix(WorldObject o)
            {
                if (o is MapParent parent) OrbitalPoiRegistry.NotifyDestroyed(parent);
            }
        }

        [HarmonyPatch(typeof(PlayDataLoader), nameof(PlayDataLoader.LoadAllPlayData))]
        private static class Patch_PlayDataLoader_LoadAllPlayData
        {
            private static void Postfix()
            {
                ShellRenderer.ClearRuntimeCaches();
            }
        }

        private static void ShowSaveBlocked()
        {
            Messages.Message("DeltaV_SaveBlockedGenerating".Translate(
                IncrementalMapGenerationService.ProgressDescription ?? "Preparing"),
                MessageTypeDefOf.RejectInput, false);
        }
    }
}

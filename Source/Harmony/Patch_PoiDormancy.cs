using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using RimWorld.Planet;

namespace DeltaV
{
    /// <summary>Delta-V incident, lifecycle and shell invalidation policies. Tick gates belong to REF.</summary>
    public static class Patch_PoiDormancy
    {
        [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets), MethodType.Getter)]
        private static class Patch_Storyteller_AllIncidentTargets
        {
            private static void Postfix(List<IIncidentTarget> __result)
            {
                // 返回的是原版静态缓存列表，调用方即用即弃，原地 RemoveAll 安全（RimExodus 同款结论）。
                SpaceMapStateManager lifecycle = Current.Game?.GetComponent<UniverseManager>()?.MapStates;
                if (lifecycle != null)
                    __result.RemoveAll(t => t is Map m && lifecycle.IsFrozen(m));
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
                Current.Game?.GetComponent<UniverseManager>()?.Generation?.DrainSynchronouslyForExternalGeneration();
            }

            private static void Postfix(Map __result)
            {
                OrbitalPoiRegistry.EnsureForMap(Current.Game?.GetComponent<UniverseManager>(), __result);
            }
        }

        [HarmonyPatch(typeof(Dialog_SaveFileList_Save), "DoFileInteraction", new[] { typeof(string) })]
        private static class Patch_SaveDialog_DoFileInteraction
        {
            private static bool Prefix()
            {
                var universe = Current.Game?.GetComponent<UniverseManager>();
                if (universe?.Generation?.IsAnyGenerating != true && universe?.HasUnpublishedPayload != true) return true;
                ShowSaveBlocked();
                return false;
            }
        }

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
        private static class Patch_GameDataSaveLoader_SaveGame
        {
            private static bool Prefix()
            {
                var universe = Current.Game?.GetComponent<UniverseManager>();
                if (universe?.Generation?.IsAnyGenerating != true && universe?.HasUnpublishedPayload != true) return true;
                ShowSaveBlocked();
                return false;
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
        private static class Patch_Game_DeinitAndRemoveMap
        {
            private static void Prefix(Map map)
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                universe?.Generation?.NotifyExternalMapRemoval(map);
                universe?.MapStates.NotifyMapRemoved(map);
                universe?.MapStates.Forget(map);
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
            var universe = Current.Game?.GetComponent<UniverseManager>();
            string progress = universe?.Generation?.ProgressDescription;
            Messages.Message(universe?.HasUnpublishedPayload == true
                    ? "DeltaV_SaveBlockedPayload".Translate()
                    : "DeltaV_SaveBlockedGenerating".Translate(progress ?? "Preparing"),
                MessageTypeDefOf.RejectInput, false);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 专用宿主口袋图只是 sourceMap 锚点画布（无 pawn），不应作为空 Map 分组占据
    /// Colonist Bar；弹体图同样隐藏。其他 SpaceObject 图按设置与玩家 Pawn 的实际存在性过滤。
    /// </summary>
    public static class HostColonistBarVisibility
    {
        public static void NotifyDockingStateChanged()
        {
            (Find.UIRoot as UIRoot_Play)?.mapUI?.colonistBar?.MarkColonistsDirty();
            ColonyGroupsBarVisibility.MarkDirty();
        }

        internal static bool ShouldHide(Map map)
        {
            if (map == null) return false; // Caravan entries have no map.
            if (SpaceMapStateManager.IsUniverseHost(map)) return true;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (DeltaVMod.CurrentSettings?.hideSpaceMapsWithoutPlayerPawns == false)
                return false;
            if (universe?.SpaceObjectOnMap(map) == null
                && map.Parent is not ShipPocketParent_DeltaV
                && !SpaceMapGovernance.IsGoverned(map, universe)) return false;
            // MapPawns.AllPawns includes held pawns in shuttle and other ThingOwners.
            foreach (Pawn pawn in map.mapPawns.AllPawns)
                if (pawn.Faction == Faction.OfPlayer) return false;
            return true;
        }
    }

    /// <summary>
    /// 沿用 RimExodus 隐藏休眠/无人图的缓存过滤方式。原版会为每张 Map 创建一组，
    /// 即使没有殖民者也会放入 pawn=null 的占位 Entry；过滤后必须重编号并重建派生缓存。
    /// </summary>
    [HarmonyPatch(typeof(ColonistBar), "CheckRecacheEntries")]
    public static class Patch_ColonistBar_CheckRecacheEntries
    {
        private static readonly FieldInfo CachedEntriesField =
            AccessTools.Field(typeof(ColonistBar), "cachedEntries");
        private static readonly FieldInfo CachedDrawLocsField =
            AccessTools.Field(typeof(ColonistBar), "cachedDrawLocs");
        private static readonly FieldInfo CachedReorderableGroupsField =
            AccessTools.Field(typeof(ColonistBar), "cachedReorderableGroups");
        private static readonly FieldInfo CachedScaleField =
            AccessTools.Field(typeof(ColonistBar), "cachedScale");
        private static readonly FieldInfo DrawLocsFinderField =
            AccessTools.Field(typeof(ColonistBar), "drawLocsFinder");

        public static void Prefix(bool ___entriesDirty, out bool __state)
        {
            __state = ___entriesDirty;
        }

        public static void Postfix(ColonistBar __instance, bool __state)
        {
            if (!__state) return;
            if (!(CachedEntriesField?.GetValue(__instance) is List<ColonistBar.Entry> entries)
                || !(CachedDrawLocsField?.GetValue(__instance) is List<Vector2> drawLocs)
                || !(CachedReorderableGroupsField?.GetValue(__instance) is List<int> groups)
                || !(DrawLocsFinderField?.GetValue(__instance) is ColonistBarDrawLocsFinder finder)
                || CachedScaleField == null)
            {
                Log.ErrorOnce("[DeltaV] ColonistBar cache fields could not be resolved; host-map filtering was skipped.",
                    0x44564342);
                return;
            }

            // Entries are contiguous per map; evaluate held pawns only once per map and recache.
            Map lastMap = null;
            bool hideMap = false;
            int removed = entries.RemoveAll(entry =>
            {
                if (entry.map != lastMap)
                {
                    lastMap = entry.map;
                    hideMap = HostColonistBarVisibility.ShouldHide(lastMap);
                }
                return hideMap;
            });
            if (removed <= 0) return;

            // Entry 的两个拖动回调会捕获构造时的 group，不能只覆写公开 group 字段。
            int lastOriginalGroup = int.MinValue;
            int newGroup = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                ColonistBar.Entry entry = entries[i];
                if (entry.group != lastOriginalGroup)
                {
                    lastOriginalGroup = entry.group;
                    newGroup++;
                }
                entries[i] = new ColonistBar.Entry(entry.pawn, entry.map, newGroup);
            }

            groups.Clear();
            for (int i = 0; i < entries.Count; i++) groups.Add(-1);
            finder.CalculateDrawLocs(drawLocs, out float scale, newGroup + 1);
            CachedScaleField.SetValue(__instance, scale);
        }
    }

    // Colony Groups renders its own bar; the vanilla cache filter never sees its entries.
    // Keep this optional and bind only when the mod and its verified cache layout exist.
    [HarmonyPatch]
    internal static class ColonyGroupsBarVisibility
    {
        private const string PackageId = "DerekBickley.LTOColonyGroupsFinal";
        private static Type barType, entryType;
        private static FieldInfo barInstanceField, entriesDirtyField, entriesField,
            groupsField, finderField, scaleField, entryMapField, entryPawnField,
            entryGroupField, entryCaravanField, entryColonyField;
        private static PropertyInfo drawLocsProperty;
        private static ConstructorInfo entryConstructor;
        private static MethodInfo calculateDrawLocs, markDirty, target;

        private static bool Prepare()
        {
            if (!ModsConfig.IsActive(PackageId)) return false;
            try { return Bind(); }
            catch (Exception ex)
            {
                Log.Warning("[DeltaV] Colony Groups bar binding failed; space-map filtering is unavailable: " + ex);
                return false;
            }
        }

        private static bool Bind()
        {
            barType = AccessTools.TypeByName("TacticalGroups.TacticalColonistBar");
            entryType = AccessTools.TypeByName("TacticalGroups.TacticalColonistBar+Entry");
            Type utils = AccessTools.TypeByName("TacticalGroups.TacticUtils");
            Type finder = AccessTools.TypeByName("TacticalGroups.ColonistBarDrawLocsFinder");
            Type caravan = AccessTools.TypeByName("TacticalGroups.CaravanGroup");
            Type colony = AccessTools.TypeByName("TacticalGroups.ColonyGroup");
            if (barType == null || entryType == null || utils == null || finder == null
                || caravan == null || colony == null)
            {
                Log.Warning("[DeltaV] Colony Groups bar types changed; space-map filtering is unavailable.");
                return false;
            }
            target = AccessTools.Method(barType, "CheckRecacheEntries");
            barInstanceField = AccessTools.Field(utils, "TacticalColonistBar");
            entriesDirtyField = AccessTools.Field(barType, "entriesDirty");
            entriesField = AccessTools.Field(barType, "cachedEntries");
            groupsField = AccessTools.Field(barType, "cachedReorderableGroups");
            finderField = AccessTools.Field(barType, "drawLocsFinder");
            scaleField = AccessTools.Field(barType, "cachedScale");
            drawLocsProperty = AccessTools.Property(barType, "DrawLocs");
            entryMapField = AccessTools.Field(entryType, "map");
            entryPawnField = AccessTools.Field(entryType, "pawn");
            entryGroupField = AccessTools.Field(entryType, "group");
            entryCaravanField = AccessTools.Field(entryType, "caravanGroup");
            entryColonyField = AccessTools.Field(entryType, "colonyGroup");
            entryConstructor = entryType?.GetConstructor(new[]
                { typeof(Pawn), typeof(Map), typeof(int), caravan, colony });
            if (drawLocsProperty != null)
                calculateDrawLocs = AccessTools.Method(finder, "CalculateDrawLocs",
                    new[] { drawLocsProperty.PropertyType, typeof(float).MakeByRefType() });
            markDirty = AccessTools.Method(barType, "MarkColonistsDirty");
            bool ready = target != null && barInstanceField != null && entriesDirtyField != null
                && entriesField != null && groupsField != null && finderField != null
                && scaleField != null && drawLocsProperty != null
                && entryMapField != null && entryPawnField != null
                && entryGroupField != null && entryCaravanField != null
                && entryColonyField != null && entryConstructor != null
                && calculateDrawLocs != null && markDirty != null;
            if (!ready) Log.Warning("[DeltaV] Colony Groups bar layout changed; space-map filtering is unavailable.");
            return ready;
        }

        private static MethodBase TargetMethod() => target;

        private static void Prefix(object __instance, out bool __state)
        {
            __state = entriesDirtyField.GetValue(__instance) is true;
        }

        private static void Postfix(object __instance, bool __state)
        {
            if (!__state) return;
            try
            {
                if (entriesField.GetValue(__instance) is not IList entries) return;
                bool removed = false;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entryMapField.GetValue(entries[i]) is not Map map
                        || !HostColonistBarVisibility.ShouldHide(map)) continue;
                    entries.RemoveAt(i);
                    removed = true;
                }
                if (!removed) return;

                int oldGroup = int.MinValue, newGroup = -1;
                for (int i = 0; i < entries.Count; i++)
                {
                    object original = entries[i];
                    int group = (int)entryGroupField.GetValue(original);
                    if (group != oldGroup) { oldGroup = group; newGroup++; }
                    entries[i] = entryConstructor.Invoke(new[]
                    {
                        entryPawnField.GetValue(original), entryMapField.GetValue(original),
                        (object)newGroup, entryCaravanField.GetValue(original),
                        entryColonyField.GetValue(original)
                    });
                }
                if (groupsField.GetValue(__instance) is IList groups)
                {
                    groups.Clear();
                    for (int i = 0; i < entries.Count; i++) groups.Add(-1);
                }
                var args = new object[] { drawLocsProperty.GetValue(__instance), 1f };
                calculateDrawLocs.Invoke(finderField.GetValue(__instance), args);
                scaleField.SetValue(__instance, args[1]);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[DeltaV] Colony Groups space-map filter failed: " + ex, 0x44564347);
            }
        }

        internal static void MarkDirty()
        {
            try
            {
                if (barInstanceField?.GetValue(null) is object bar) markDirty?.Invoke(bar, null);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[DeltaV] Colony Groups bar invalidation failed: " + ex, 0x44564348);
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 宿主图在飞行阶段只是显示/生命周期锚，不应作为一个空 Map 分组占据 Colonist Bar。
    /// 停靠船落到宿主图后恢复原版分组，以便正常选择和操作真实船员。
    /// </summary>
    public static class HostColonistBarVisibility
    {
        public static void NotifyDockingStateChanged()
        {
            Find.ColonistBar?.MarkColonistsDirty();
        }

        internal static bool ShouldHide(Map map)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return map != null && universe?.hostMap == map
                && !universe.Ships.Any(ship => ship.docked && ship.dockedMap == map);
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

        public static void Postfix(ColonistBar __instance)
        {
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

            int removed = entries.RemoveAll(entry => HostColonistBarVisibility.ShouldHide(entry.map));
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
}

using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 无 tile 口袋图与 DateNotifier 的兼容补丁。
    ///
    /// 飞船口袋图上有逆重引擎，Map.IsPlayerHome 经 GravshipUtility.PlayerHasGravEngine
    /// 回退判为 true，于是被当作"玩家主基地图"参与日历/季节循环；而原版
    /// DateNotifier 的两个私有助手对 map.Tile 没有 Valid 守卫（第三个助手
    /// AnyPlayerHomeSeasonsAreMeaningful 有），LongLatOf(-1) 会在 GetTileCenter
    /// 每个日历 tick 红字。太空船没有经纬度/季节语义：跳过无 tile 图即可，
    /// 有真实 tile 的玩家主基地图（地表/轨道）行为不变。
    /// </summary>
    public static class Patch_DateNotifier
    {
        [HarmonyPatch(typeof(DateNotifier), "FindPlayerHomeWithMinTimezone")]
        private static class Patch_DateNotifier_FindPlayerHomeWithMinTimezone
        {
            private static bool Prefix(ref Map __result)
            {
                List<Map> maps = Find.Maps;
                Map best = null;
                int bestTimeZone = -1;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    if (!map.IsPlayerHome || !map.Tile.Valid) continue;
                    int timeZone = GenDate.TimeZoneAt(Find.WorldGrid.LongLatOf(map.Tile).x);
                    if (best == null || timeZone < bestTimeZone)
                    {
                        best = map;
                        bestTimeZone = timeZone;
                    }
                }
                __result = best;
                return false;
            }
        }

        [HarmonyPatch(typeof(DateNotifier), "AnyPlayerHomeAvgTempIsLowInWinter")]
        private static class Patch_DateNotifier_AnyPlayerHomeAvgTempIsLowInWinter
        {
            private static bool Prefix(ref bool __result)
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    if (!map.IsPlayerHome || !map.Tile.Valid) continue;
                    if (GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile,
                        Season.Winter.GetMiddleTwelfth(Find.WorldGrid.LongLatOf(map.Tile).y)) < 8f)
                    {
                        __result = true;
                        return false;
                    }
                }
                __result = false;
                return false;
            }
        }
    }

    /// <summary>
    /// IncidentWorker.CanFireNow 的 biome 过滤对 parms.target.Tile 没有 Valid 守卫
    /// （紧随其后的 layer 过滤反而有）：无 tile 口袋图作为事件目标时
    /// Find.WorldGrid[-1] 会 List 越界硬崩。无 tile 目标对带 biome 过滤的事件
    /// 一律不可触发（语义正确）；无 biome 过滤的事件照常走原版其余检查。
    /// forced 事件在原版里跳过全部检查（含 biome），保持直通。
    /// </summary>
    public static class Patch_IncidentWorker
    {
        [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
        private static class Patch_IncidentWorker_CanFireNow_BiomeGuard
        {
            private static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
            {
                if (parms.forced || parms.target == null || parms.target.Tile.Valid) return true;
                if (__instance.def.allowedBiomes.NullOrEmpty() && __instance.def.disallowedBiomes.NullOrEmpty())
                {
                    return true;
                }
                __result = false;
                return false;
            }
        }
    }
}

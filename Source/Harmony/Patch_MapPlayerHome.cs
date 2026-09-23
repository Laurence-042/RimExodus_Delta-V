using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 自由飞行船的口袋图不是玩家主基地（Map.IsPlayerHome 覆写为 false）。
    ///
    /// 原版支持"玩家暂无主基地"的形态（gravship 航行途中），但原版+模组生态普遍
    /// 假设主基地图持有有效世界 tile（DateNotifier 季节检查的其中两个助手对
    /// map.Tile 无有效守卫、Ideo 仪式历法读 AnyPlayerHomeMap.Tile、
    /// TileTemperaturesComp 按层缓存数组按 tileId 索引等；AnyPlayerHomeMap 仅
    /// 原版就有 94 处调用，另有 VEF 等模组）。飞船口袋图无 tile，若经
    /// GravshipUtility.PlayerHasGravEngine 回退被判为 home，这些路径会每 tick
    /// 越界红字/异常（2026-09-14 实测，两条独立调用栈）。
    ///
    /// 处理：Delta-V 口袋图（船/POI）一律返回 false——飞行中与原版 gravship
    /// 航行同形态（无 home）；Game.AnyPlayerHomeMap 的 Odyssey 引擎二次回退另由
    /// Patch_Game_AnyPlayerHomeMap_ValidTile 排除无 tile 图。
    /// 玩家真正降落时，逆重引擎随船体落到真实降落图上，该图经同一条引擎回退
    /// 自然成为 home——与原版 gravship 降落语义一致，无需额外处理。
    /// POI 口袋图共用同一 parent 类型，一并排除（它们本就无引擎、非 home）。
    /// 事件系统同理闭环：口袋图 def 无 IncidentTargetTags，TargetAllowed 的
    /// 标签交集恒为空，storyteller 不会把口袋图选为事件目标。
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.IsPlayerHome), MethodType.Getter)]
    public static class Patch_Map_IsPlayerHome_DeltaVPocket
    {
        public static bool Prefix(Map __instance, ref bool __result)
        {
            if (__instance.Parent is ShipPocketParent_DeltaV)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Odyssey 的 Game.AnyPlayerHomeMap 在 IsPlayerHome 扫描之后还会直接按
    /// PlayerHasGravEngine 再扫一次；因此仅覆写 IsPlayerHome 不足以排除带引擎的
    /// 无 tile 移动船。只在原结果确认为 Delta-V 口袋图时介入；原版已经
    /// 走到 Odyssey 的引擎回退，因此只需在有效 tile 图中重做这一轮。
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.AnyPlayerHomeMap), MethodType.Getter)]
    public static class Patch_Game_AnyPlayerHomeMap_ValidTile
    {
        public static void Postfix(ref Map __result)
        {
            if (!(__result?.Parent is ShipPocketParent_DeltaV))
            {
                return;
            }
            __result = null;
            if (Faction.OfPlayerSilentFail == null || !ModsConfig.OdysseyActive)
            {
                return;
            }
            for (int i = 0; i < Find.Maps.Count; i++)
            {
                Map map = Find.Maps[i];
                if (map.Tile.Valid && GravshipUtility.PlayerHasGravEngine(map))
                {
                    __result = map;
                    return;
                }
            }
        }
    }
}

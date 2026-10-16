using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 太空图的基架建造范围放开（解耦架构 §4 已定）：按 Delta-V 参考系识别太空图，
    /// 包含无 tile 的飞船口袋图，允许逆重基架在引擎覆盖范围外建造——在已有船旁建新船、划新
    /// 归属再建驾驶台成船的玩法前提。天然岩顶检查保留（不能从岩顶下长出基架）；
    /// 地表图（含 RimExodus）完全保留原版引擎范围限制，不把太空放宽规则泄漏到地表。
    /// </summary>
    [HarmonyPatch(typeof(PlaceWorker_InSubstructureFootprint), nameof(PlaceWorker_InSubstructureFootprint.AllowsPlacing))]
    public static class Patch_PlaceWorker_InSubstructureFootprint_SpaceBuild
    {
        public static bool Prefix(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore, Thing thing, ref AcceptanceReport __result)
        {
            if (!Applies(map))
            {
                return true; // 地表/非 Delta-V 图：原版行为不变。
            }
            __result = CheckRoof(map, loc);
            return false;
        }

        internal static bool Applies(Map map)
        {
            // SpaceObject 承载图才属于宇宙语境；口袋图的 Tile.Invalid 不能用来否定它。
            return map != null && Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) != null;
        }

        internal static AcceptanceReport CheckRoof(Map map, IntVec3 loc)
        {
            RoofDef roofDef = map.roofGrid.RoofAt(loc);
            if (roofDef != null && roofDef.isNatural)
                return "MessageCannotPlaceUnderRockRoof".Translate();
            return AcceptanceReport.WasAccepted;
        }
    }

    // 原版鼠标提示独立检查 InsideFootprint，不能只放行 AllowsPlacing 而留下错误红字。
    [HarmonyPatch(typeof(PlaceWorker_InSubstructureFootprint), nameof(PlaceWorker_InSubstructureFootprint.DrawPlaceMouseAttachments))]
    public static class Patch_PlaceWorker_InSubstructureFootprint_SpaceHint
    {
        public static bool Prefix(float curX, ref float curY, IntVec3 center)
        {
            Map map = Find.CurrentMap;
            if (!Patch_PlaceWorker_InSubstructureFootprint_SpaceBuild.Applies(map)) return true;
            if (!center.InBounds(map) || center.Fogged(map)) return false;
            AcceptanceReport report = Patch_PlaceWorker_InSubstructureFootprint_SpaceBuild.CheckRoof(map, center);
            if (!report.Accepted)
            {
                Color previousColor = GUI.color;
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(new Rect(curX, curY, 999f, Text.LineHeight), report.Reason);
                curY += Text.LineHeight;
                GUI.color = previousColor;
            }
            return false;
        }
    }

    /// <summary>
    /// 引擎改名的反向同步（解耦架构 §3.3 方案建议）：原版引擎改名入口与驾驶台改名
    /// 接回同一改名服务——引擎 RenamableLabel 变更时同步为其所属结构的注册名
    /// （按引擎中心格归属解析；RenameStructure 写引擎标签会再次触发本 postfix，
    /// 但 NoteStructureName 同值幂等，无递归风险）。
    /// </summary>
    [HarmonyPatch(typeof(Building_GravEngine), "RenamableLabel", MethodType.Setter)]
    public static class Patch_GravEngine_RenamableLabel_SyncStructureName
    {
        public static void Postfix(Building_GravEngine __instance, string value)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Map map = __instance?.Map;
            if (universe == null || map == null || value.NullOrEmpty())
            {
                return;
            }
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(map);
            int owner = ownership?.OwnerAt(__instance.Position) ?? SubstructureOwnership.Unassigned;
            if (owner != SubstructureOwnership.Unassigned)
            {
                universe.NoteStructureName(owner, value);
            }
        }
    }
}

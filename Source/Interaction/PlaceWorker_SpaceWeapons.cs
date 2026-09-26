using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 炮廓（挂壁武器）放置校验（太空战架构 §5.2）：必须贴墙——朝向反侧（背面）
    /// 的邻格存在实心结构（墙/门/气密建筑，SpaceStructureRules 同一分类器），
    /// 朝向即墙面法线朝外。仅太空图生效（地表保留原版规则，零介入）。
    /// </summary>
    public class PlaceWorker_SpacesideCasemate : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || universe.FrameOf(map) == null) return true; // 地表：原版规则
            IntVec3 behind = center - rot.FacingCell;
            if (!behind.InBounds(map)) return false;
            foreach (Thing t in map.thingGrid.ThingsListAtFast(behind))
            {
                if (t != thingToIgnore && SpaceStructureRules.IsSolidStructure(t)) return true;
            }
            return "DeltaV_CasemateNeedsWall".Translate();
        }
    }

    /// <summary>
    /// 轨道炮部件放置校验（§5.7 直线邻接链）：装弹机构无前置；加速段须四邻接
    /// 装弹机构或加速段；炮口须四邻接装弹机构或加速段。链的直线性与完整性由
    /// 发射门禁的链查询（CompSpaceRailgun.TryGetChain）最终判定——弯曲链建得起来
    /// 但打不出去。仅太空图生效。
    /// </summary>
    public class PlaceWorker_RailgunPart : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || universe.FrameOf(map) == null) return true; // 地表：原版规则
            var props = (def as ThingDef)?.GetCompProperties<CompProperties_SpaceRailgun>();
            if (props == null) return true;
            if (props.role == RailgunPartRole.Loader) return true;
            // 四邻接找装弹机构/加速段（炮口与加速段共用本判据；直线性由链查询最终判定）。
            foreach (IntVec3 cell in GenAdj.CardinalDirectionsAround)
            {
                if (!cell.InBounds(map)) continue;
                foreach (Thing t in map.thingGrid.ThingsListAtFast(cell))
                {
                    CompSpaceRailgun part = (t as Building)?.TryGetComp<CompSpaceRailgun>();
                    if (part == null || t == thingToIgnore) continue;
                    if (part.Role == RailgunPartRole.Loader || part.Role == RailgunPartRole.Accelerator)
                    {
                        return true;
                    }
                }
            }
            return "DeltaV_RailgunNeedsChain".Translate();
        }
    }
}

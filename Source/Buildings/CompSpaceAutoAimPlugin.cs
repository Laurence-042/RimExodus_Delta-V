using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 自动瞄准插件（太空战架构 §5.6）：独立附加建筑，四邻接武器且所在基架与武器
    /// 同归属时生效。def 级别 = 等价射击技能（与 §5.4 同一映射函数
    /// SpaceWeaponService.SpreadFactorForSkill——单实现）；有插件时武器无需
    /// MannedNow；与炮手并存取技能高者。插件自身耗电（CompPowerTrader 原版 comp）。
    /// 生效判定在本 comp（唯一实现），武器侧只消费。
    /// </summary>
    public class CompProperties_SpaceAutoAimPlugin : CompProperties
    {
        /// <summary>等价射击技能（0–20；映射到散布偏差系数）。</summary>
        public int equivalentSkillLevel = 12;

        public CompProperties_SpaceAutoAimPlugin()
        {
            compClass = typeof(CompSpaceAutoAimPlugin);
        }
    }

    public class CompSpaceAutoAimPlugin : ThingComp
    {
        public int SkillLevel => ((CompProperties_SpaceAutoAimPlugin)props).equivalentSkillLevel;

        /// <summary>插件是否供电（原版 comp 状态直读；无供电 comp = 不要求）。</summary>
        public bool Powered
        {
            get
            {
                CompPowerTrader trader = parent.TryGetComp<CompPowerTrader>();
                return trader == null || trader.PowerOn;
            }
        }

        /// <summary>本插件是否对指定武器生效：四邻接武器 ∧ 同归属基架 ∧ 供电。</summary>
        public bool EffectiveFor(CompSpaceWeapon weapon)
        {
            if (!parent.Spawned || parent.Destroyed || !Powered || weapon?.parent == null) return false;
            if (weapon.parent.Map != parent.Map) return false;
            bool adjacent = false;
            foreach (IntVec3 cell in parent.OccupiedRect().ExpandedBy(1).Cells)
            {
                if (parent.OccupiedRect().Contains(cell)) continue;
                if (weapon.parent.OccupiedRect().Contains(cell)) { adjacent = true; break; }
            }
            if (!adjacent) return false;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(parent.Map);
            return ownership != null
                && ownership.OwnerAt(parent.Position) == ownership.OwnerAt(weapon.parent.Position)
                && ownership.OwnerAt(parent.Position) > 0;
        }
    }
}

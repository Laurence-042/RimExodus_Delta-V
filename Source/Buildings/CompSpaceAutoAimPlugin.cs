using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 自动瞄准插件（太空战架构 §5.6）：独立覆盖挂件，占用武器声明槽位且所在基架与武器
    /// 同归属时生效。def 级别 = 等价射击技能（与 §5.4 同一映射函数
    /// SpaceWeaponService.SpreadFactorForSkill——单实现）；有插件时武器无需
    /// MannedNow；与炮手并存取技能高者。插件不注册独立电网节点，而是要求宿主武器供电。
    /// 生效判定在本 comp（唯一实现），武器侧只消费。
    /// </summary>
    public class CompProperties_SpaceAutoAimPlugin : CompProperties_SpaceWeaponAttachment
    {
        /// <summary>等价射击技能（0–20；映射到散布偏差系数）。</summary>
        public int equivalentSkillLevel = 12;

        public CompProperties_SpaceAutoAimPlugin()
        {
            compClass = typeof(CompSpaceAutoAimPlugin);
            requiresHostPower = true;
        }
    }

    public class CompSpaceAutoAimPlugin : CompSpaceWeaponAttachment
    {
        public int SkillLevel => ((CompProperties_SpaceAutoAimPlugin)props).equivalentSkillLevel;
        public override int? AimSkillFor(CompSpaceWeapon weapon) =>
            EffectiveFor(weapon) ? SkillLevel : null;

        /// <summary>本插件是否对指定武器生效：覆盖声明槽位 ∧ 同归属基架 ∧ 启用/供电。</summary>
        public bool EffectiveFor(CompSpaceWeapon weapon)
        {
            if (!parent.Spawned || parent.Destroyed || !Enabled || weapon?.parent == null) return false;
            if (weapon.parent.Map != parent.Map) return false;
            SpaceWeaponLayout layout = weapon.Layout;
            return layout.ownerId > 0 && layout.attachments.Contains(parent);
        }
    }
}

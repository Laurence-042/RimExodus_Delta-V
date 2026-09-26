using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>轨道炮部件角色（太空战架构 §5.7）：装弹机构（弹药装载/存储）→
    /// 加速段（0..N 节）→ 炮口（发射点），直线邻接链。</summary>
    public enum RailgunPartRole : byte
    {
        Loader = 0,
        Accelerator = 1,
        Muzzle = 2
    }

    /// <summary>
    /// 轨道炮部件 comp：同一 comp 挂在三种部件 def 上，role 区分。火力汇总 =
    /// Σ 加速段加成 → 穿深/伤害倍率（唯一查询入口 <see cref="TryGetChain"/>，
    /// 发射时读取）；链断裂按剩余完整链重算、不完整不可发射。供电：炮口 +
    /// 装弹机构供电，任一断电不可发射（方案默认）。弹药 = 轨道炮弹 item 经
    /// 装弹机构装载（原版 CompRefuelable 装填语义）。链各部件位于连通基架
    ///（同归属），随控制域捕获/分离搬运。
    /// </summary>
    public class CompProperties_SpaceRailgun : CompProperties
    {
        public RailgunPartRole role;

        /// <summary>每节加速段的伤害倍率加成（加速段 def 上配置）。</summary>
        public float damageBonusPerSegment = 0.25f;

        /// <summary>每节加速段的穿深倍率加成（加速段 def 上配置）。</summary>
        public float armorPenBonusPerSegment = 0.2f;

        public CompProperties_SpaceRailgun()
        {
            compClass = typeof(CompSpaceRailgun);
        }
    }

    public class CompSpaceRailgun : ThingComp
    {
        public RailgunPartRole Role => ((CompProperties_SpaceRailgun)props).role;

        /// <summary>
        /// 完整链查询（唯一入口）：从炮口沿其朝向的**反轴**逐格收集连续加速段，
        /// 链尾必须是装弹机构——链不完整/弯曲/断电/无弹药返回 false。
        /// 输出倍率（1 + Σ 每节加成）与装弹机构（供弹药消费）。
        /// </summary>
        public static bool TryGetChain(Building muzzle, out float damageScale, out float armorPenScale,
            out CompRefuelable loader)
        {
            damageScale = 1f;
            armorPenScale = 1f;
            loader = null;
            CompSpaceRailgun comp = muzzle?.TryGetComp<CompSpaceRailgun>();
            if (comp == null || comp.Role != RailgunPartRole.Muzzle || !muzzle.Spawned) return false;
            Map map = muzzle.Map;
            IntVec3 cell = muzzle.Position;
            int guard = 0;
            while (guard++ < 64)
            {
                cell -= muzzle.Rotation.FacingCell; // 沿反轴走向链尾
                if (!cell.InBounds(map)) return false;
                Thing part = cell.GetFirstBuilding(map);
                CompSpaceRailgun partComp = (part as Building)?.TryGetComp<CompSpaceRailgun>();
                if (partComp == null) return false;
                var partProps = (CompProperties_SpaceRailgun)partComp.props;
                if (partComp.Role == RailgunPartRole.Loader)
                {
                    loader = part.TryGetComp<CompRefuelable>();
                    // 炮口 + 装弹机构供电门禁：任一断电不可发射。
                    CompPowerTrader loaderPower = part.TryGetComp<CompPowerTrader>();
                    if (loaderPower != null && !loaderPower.PowerOn) return false;
                    return loader != null && loader.HasFuel;
                }
                if (partComp.Role != RailgunPartRole.Accelerator) return false; // 炮口挨着炮口/其他建筑
                var props = (CompProperties_SpaceRailgun)comp.props;
                damageScale += partProps.damageBonusPerSegment;
                armorPenScale += partProps.armorPenBonusPerSegment;
            }
            return false;
        }

        /// <summary>dev/诊断：链摘要（段数与倍率）。</summary>
        public static string ChainSummary(Building muzzle)
        {
            return TryGetChain(muzzle, out float damage, out float ap, out CompRefuelable loader)
                ? $"railgun chain OK: dmg x{damage:F2} AP x{ap:F2} fuel {loader.Fuel:F0}/{loader.Props.fuelCapacity:F0}"
                : "railgun chain INCOMPLETE (bent/powered-off/out of ammo)";
        }
    }
}

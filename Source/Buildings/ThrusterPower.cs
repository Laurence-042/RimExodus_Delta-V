using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 推进器动力来源抽象（星区架构 §9/§11）。
    ///
    /// 物理分配（ShipPhysics）与对接门禁（ShipFlightTransitionService）都经本缝
    /// 读取推进器当前实际可用性，不缓存静态标志。将来的 VEF 燃料管道、电网电推
    /// 只替换实现（含燃料类型从实际消耗管线取），调用方不感知来源；
    /// 默认实现只查推进器自身实际状态（故障/排气堵塞/断电），不要求引擎连接或
    /// 燃料管线——非玩家派系没有逆重引擎也能自由飞行（一致性模型见
    /// doc/星区架构.md §7）。
    /// </summary>
    public interface IThrusterPowerModel
    {
        /// <summary>该推进器当前是否实际可用；每次调用读实时状态。</summary>
        bool IsOperational(Thing thruster);
    }

    public static class ThrusterPower
    {
        /// <summary>当前动力模型；启动时为原版完整性模型，兼容层可替换。</summary>
        public static IThrusterPowerModel Model { get; set; } = new VanillaIntegrityPowerModel();

        /// <summary>
        /// 是否为"可在驾驶模式提供动量的推进器"：def 挂 GravshipThruster comp 者
        /// （凡可作为逆重飞船推进器的建筑皆是）。这是全项目唯一的推进器识别口径。
        /// </summary>
        public static bool IsThruster(Thing thing)
        {
            return thing is Building
                && thing.def.GetCompProperties<CompProperties_GravshipThruster>() != null;
        }

        public static bool IsOperational(Thing thruster)
        {
            return thruster != null && thruster.Spawned && !thruster.Destroyed
                && Model.IsOperational(thruster);
        }

    }

    /// <summary>
    /// 默认动力模型：推进器自身实际状态——故障（CompBreakdownable）、排气堵塞/不露天
    /// （CompGravshipThruster.Blocked）、断电（如有供电 comp；推进器一般没有）。
    /// 不调用原版 CanBeActive：那要求连接殖民者逆重引擎与力场基架，会把无引擎的
    /// 非玩家派系船全部判死。代价是 VGE 燃料条件（挂在 CanBeActive 上）不自动生效，
    /// 将来由 VEF/VGE 兼容层替换 Model 实现。
    /// </summary>
    public sealed class VanillaIntegrityPowerModel : IThrusterPowerModel
    {
        public bool IsOperational(Thing thruster)
        {
            CompGravshipThruster comp = thruster.TryGetComp<CompGravshipThruster>();
            if (comp == null)
            {
                return true;
            }
            if (comp.Breakdownable?.BrokenDown == true)
            {
                return false;
            }
            if (comp.Blocked)
            {
                return false;
            }
            CompPowerTrader power = thruster.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return false;
            }
            return true;
        }
    }
}

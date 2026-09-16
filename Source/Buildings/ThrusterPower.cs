using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 推进器动力来源抽象（星区架构 §9/§11，现阶段只留接口）。
    ///
    /// 物理分配（ShipPhysics）与"无动力"判定（DockController 可降落判定）都经本缝
    /// 读取推进器当前实际可用性，不缓存静态标志。将来的 VEF 燃料管道、电网电推
    /// 只替换实现（含燃料类型从实际消耗管线取），调用方不感知来源；
    /// 默认实现只看原版完整性（spawned 且未毁），不得实现成"所有推进器都必须接管道"。
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

        /// <summary>图上是否存在任一可工作推进器（直扫实时状态，供无动力判定）。</summary>
        public static bool HasWorkingThruster(Map map)
        {
            if (map?.listerThings?.AllThings == null) return false;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (IsThruster(thing) && IsOperational(thing)) return true;
            }
            return false;
        }

        /// <summary>
        /// 图上是否存在任一存活驾驶台。主台被毁时其他台接任（CompDeltaVPilotConsole），
        /// 因此"失去驾驶能力"的口径是全部驾驶台被毁。
        /// </summary>
        public static bool HasWorkingConsole(Map map)
        {
            if (map?.listerThings?.AllThings == null) return false;
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is Building && thing.Spawned && !thing.Destroyed
                    && thing.TryGetComp<CompDeltaVPilotConsole>() != null)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>默认动力模型：只看原版完整性（在场且未毁）。</summary>
    public sealed class VanillaIntegrityPowerModel : IThrusterPowerModel
    {
        public bool IsOperational(Thing thruster)
        {
            return true;
        }
    }
}

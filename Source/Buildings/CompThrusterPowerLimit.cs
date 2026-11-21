using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 推进器出力上限 comp（2026-09-27 用户需求）：持久字段 powerPercent（默认 100%，
    /// 存档事实）；多选推进器后经滑条统一设置（原版燃油自动填充阈值
    /// Command_SetTargetFuelLevel 同款机制——InheritInteractionsFrom 合组 + Dialog_Slider
    /// 确认回调循环赋值）。出力唯一收口：ShipPhysics.Measure 创建 Thruster 时乘
    /// powerPercent——分配器/权威测量/火焰只读 thruster.thrust，自动跟随。
    /// </summary>
    public class CompProperties_ThrusterPowerLimit : CompProperties
    {
        public CompProperties_ThrusterPowerLimit()
        {
            compClass = typeof(CompThrusterPowerLimit);
        }
    }

    public class CompThrusterPowerLimit : ThingComp
    {
        /// <summary>出力百分比 [0,100]（0 = 完全停用；默认 100）。</summary>
        private float limit = 100f;
        public float powerPercent
        {
            get => limit;
            set
            {
                float before = limit;
                if (before == value) return;
                limit = value;
                Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.NotifyProperty(
                    parent, ShipStructureAspect.Propulsion, before, value);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref limit, "DeltaV_powerPercent", 100f);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            bool playerSide = parent.Faction == Faction.OfPlayer || DebugSettings.godMode;
            if (!playerSide || !parent.Spawned)
            {
                yield break;
            }
            // 单选不显示（避免常驻噪音；原版燃油阈值同样只在多选时给滑条）。
            if (Find.Selector.SelectedObjects.Count == 1)
            {
                yield break;
            }
            yield return new Command_SetThrusterPower { thruster = this };
        }
    }

    /// <summary>
    /// 出力滑条命令（逐行仿原版 Command_SetTargetFuelLevel）：多选合组后由被点击的
    /// 一个弹 Dialog_Slider，确认回调对全组统一赋值。
    /// </summary>
    public class Command_SetThrusterPower : Command
    {
        public CompThrusterPowerLimit thruster;

        private readonly List<CompThrusterPowerLimit> thrusters = new List<CompThrusterPowerLimit>();

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            thrusters.Add(thruster);
            string dialogTitle = "DeltaV_ThrusterPowerTitle".Translate();
            int starting = Mathf.Clamp(Mathf.RoundToInt(thruster.powerPercent), 0, 100);
            Find.WindowStack.Add(new Dialog_Slider(
                value => "DeltaV_ThrusterPowerEntry".Translate(value),
                0, 100,
                delegate (int value)
                {
                    for (int i = 0; i < thrusters.Count; i++)
                    {
                        thrusters[i].powerPercent = value;
                    }
                },
                starting));
        }

        public override bool InheritInteractionsFrom(Gizmo other)
        {
            if (other is Command_SetThrusterPower otherCommand)
            {
                thrusters.Add(otherCommand.thruster);
                return false;
            }
            return false;
        }
    }
}

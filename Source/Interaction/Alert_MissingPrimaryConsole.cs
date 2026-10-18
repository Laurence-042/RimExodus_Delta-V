using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>由当前地图事实重建的持续警告；主台恢复即消失，不保存警告状态。</summary>
    public sealed class Alert_MissingPrimaryConsole : Alert
    {
        public Alert_MissingPrimaryConsole()
        {
            defaultLabel = "DeltaV_MissingPrimaryConsole".Translate();
            defaultExplanation = "DeltaV_MissingPrimaryConsoleDesc".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override AlertReport GetReport()
        {
            var targets = new List<Thing>();
            foreach (Map map in Find.Maps)
                targets.AddRange(ShipRegistry.MissingPrimaryConsoleTargets(map));
            return AlertReport.CulpritsAre(targets);
        }
    }
}

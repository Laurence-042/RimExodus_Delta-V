using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_AfterburnerTank : CompProperties_Refuelable
    {
        public CompProperties_AfterburnerTank()
        {
            compClass = typeof(CompAfterburnerTank);
            consumeFuelOnlyWhenUsed = true;
            drawOutOfFuelOverlay = false;
            hideGizmosIfNotPlayerFaction = true;
            showAllowAutoRefuelToggle = true;
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            // Vanilla shuttle launch and travel use the first refuelable comp.
            foreach (CompProperties comp in parentDef.comps)
            {
                if (!(comp is CompProperties_Refuelable)) continue;
                if (comp == this) yield return "The shuttle's launch fuel tank must precede its afterburner tank.";
                break;
            }
        }

        internal CompProperties_AfterburnerTank WithTranslatedLabel()
        {
            var copy = (CompProperties_AfterburnerTank)MemberwiseClone();
            copy.fuelLabel = copy.fuelGizmoLabel = "DeltaV_ShuttleAfterburnerFuel".Translate();
            return copy;
        }
    }

    public class CompAfterburnerTank : CompRefuelable
    {
        internal int BurnTicksRemaining => Mathf.CeilToInt(Fuel / (Props.fuelConsumptionRate / GenDate.TicksPerDay));

        public override void Initialize(CompProperties props) =>
            base.Initialize(((CompProperties_AfterburnerTank)props).WithTranslatedLabel());

        public override void PostExposeData()
        {
            if (!Scribe.EnterNode("afterburnerTank")) return;
            try { base.PostExposeData(); }
            finally { Scribe.ExitNode(); }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                if (gizmo is Command command)
                {
                    command.defaultLabel = Props.FuelGizmoLabel + " · " + command.defaultLabel;
                    command.groupKey = 817432;
                    command.hotKey = null;
                }
                yield return gizmo;
            }
        }
    }
}

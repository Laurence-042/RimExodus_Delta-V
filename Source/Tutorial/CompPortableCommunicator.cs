using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal enum DeltaVCommsSource
    {
        ExternalConsole,
        InternalCommunicator
    }

    public sealed class CompProperties_PortableCommunicator : CompProperties
    {
        public CompProperties_PortableCommunicator() => compClass = typeof(CompPortableCommunicator);
    }

    public sealed class CompPortableCommunicator : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            if (parent.ParentHolder is not Pawn_ApparelTracker apparel
                || apparel.pawn?.IsColonistPlayerControlled != true) yield break;

            Pawn wearer = apparel.pawn;
            DeltaVTutorialService tutorial = Current.Game?.GetComponent<UniverseManager>()?.Tutorial;
            bool canTalk = wearer.health.capacities.CapableOf(PawnCapacityDefOf.Talking);
            var command = new Command_Action
            {
                defaultLabel = "DeltaV_PortableCommunicator_Call".Translate(),
                defaultDesc = "DeltaV_PortableCommunicator_CallDesc".Translate(),
                icon = parent.def.uiIcon,
                groupable = false,
                action = () => tutorial?.ContactMentors(wearer, parent)
            };
            if (!canTalk) command.Disable("DeltaV_PortableCommunicator_CannotTalk".Translate(wearer.Named("PAWN")));
            else if (tutorial?.CanContactMentors != true)
                command.Disable("DeltaV_PortableCommunicator_NoContact".Translate());
            yield return command;
        }
    }
}

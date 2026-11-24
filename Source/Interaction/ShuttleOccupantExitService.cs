using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class ShuttleOccupantExitService
    {
        internal static bool CanShow(CompTransporter transporter) => EligibleVehicle(transporter)
            && transporter.innerContainer.OfType<Pawn>().Any(pawn => CanExitFrom(transporter, pawn));

        internal static Command_Action CommandFor(CompTransporter transporter, Texture2D icon) => new Command_Action
        {
            defaultLabel = "DeltaV_ExitOwnPersonnelFromVehicle".Translate(),
            defaultDesc = "DeltaV_ExitOwnPersonnelFromVehicleDesc".Translate(),
            icon = icon,
            action = () => TryExit(transporter)
        };

        internal static bool CanExitFrom(CompTransporter transporter, Pawn pawn) => EligibleVehicle(transporter)
            && pawn?.Faction == Faction.OfPlayer && pawn.holdingOwner == transporter.innerContainer
            && transporter.innerContainer.Contains(pawn);

        private static bool EligibleVehicle(CompTransporter transporter)
        {
            Thing parent = transporter?.parent;
            Faction player = Faction.OfPlayer;
            return player != null && transporter?.innerContainer != null
                && parent?.Spawned == true && parent.Map?.Disposed == false
                && parent.TryGetComp<CompShuttle>() != null
                && parent.Faction?.HostileTo(player) != true;
        }

        private static void TryExit(CompTransporter transporter)
        {
            if (!CanShow(transporter)) return;
            Thing parent = transporter.parent;
            List<Pawn> passengers = transporter.innerContainer.OfType<Pawn>()
                .Where(pawn => CanExitFrom(transporter, pawn)).ToList();
            TransporterDropResult result = TransporterDropService.Request(
                transporter, passengers, parent.InteractionCell - parent.Position);
            if (result is TransporterDropResult.NotHandled or TransporterDropResult.Rejected) Reject();
        }

        private static void Reject() => Messages.Message(
            "DeltaV_ExitOwnPersonnelUnavailable".Translate(), MessageTypeDefOf.RejectInput, historical: false);
    }
}

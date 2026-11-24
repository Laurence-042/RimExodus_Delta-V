using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace DeltaV
{
    internal enum TransporterDropResult
    {
        NotHandled,
        Completed,
        Queued,
        Rejected
    }

    internal static class TransporterDropService
    {
        internal static TransporterDropResult Request(CompTransporter transporter, Thing item, int count,
            IntVec3 dropOffset)
        {
            Thing parent = transporter?.parent;
            Map map = parent?.Map;
            ThingOwner holder = transporter?.innerContainer;
            if (parent?.Spawned != true || map?.Disposed != false || holder == null || item == null
                || !SpaceScatterService.IsCargo(item))
                return TransporterDropResult.NotHandled;

            if (count <= 0 || count > item.stackCount || item.holdingOwner != holder || !holder.Contains(item))
                return TransporterDropResult.Rejected;

            IntVec3 dropCell = parent.Position + dropOffset;
            if (dropCell.InBounds(map))
            {
                if (!holder.TryDrop(item, dropCell, map, ThingPlaceMode.Near, count, out Thing _))
                    return TransporterDropResult.Rejected;

                NotifyRemoved(transporter, item);
                return TransporterDropResult.Completed;
            }

            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            bool confirm = item is Pawn pawn && !pawn.Dead;
            return universe?.Scatter.TryQueueTransporterEjection(
                    transporter, item, count, dropOffset, confirm) == true
                ? TransporterDropResult.Queued
                : TransporterDropResult.Rejected;
        }

        internal static TransporterDropResult Request(CompTransporter transporter,
            IReadOnlyList<Pawn> passengers, IntVec3 dropOffset)
        {
            Thing parent = transporter?.parent;
            Map map = parent?.Map;
            ThingOwner holder = transporter?.innerContainer;
            if (parent?.Spawned != true || map?.Disposed != false || holder == null || passengers == null)
                return TransporterDropResult.NotHandled;

            List<Pawn> snapshot = passengers.Where(pawn => pawn != null).Distinct().ToList();
            if (snapshot.Count == 0
                || snapshot.Any(pawn => !ShuttleOccupantExitService.CanExitFrom(transporter, pawn)))
                return TransporterDropResult.Rejected;

            IntVec3 dropCell = parent.Position + dropOffset;
            if (!dropCell.InBounds(map))
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                return universe?.Scatter.TryQueueTransporterEjection(
                        transporter, snapshot, dropOffset, confirm: true) == true
                    ? TransporterDropResult.Queued
                    : TransporterDropResult.Rejected;
            }

            bool released = false;
            foreach (Pawn pawn in snapshot)
            {
                if (!ShuttleOccupantExitService.CanExitFrom(transporter, pawn)) continue;
                if (!holder.TryDrop(pawn, dropCell, map, ThingPlaceMode.Near,
                        out Thing _, placedAction: null, nearPlaceValidator: null, playDropSound: false))
                    continue;

                released = true;
                NotifyRemoved(transporter, pawn);
            }
            return released ? TransporterDropResult.Completed : TransporterDropResult.Rejected;
        }

        internal static void NotifyRemoved(CompTransporter transporter, Thing thing)
        {
            if (thing is Pawn pawn && pawn.GetLord() is Lord lord
                && lord.LordJob is LordJob_LoadAndEnterTransporters)
                lord.Notify_PawnLost(pawn, PawnLostCondition.LeftVoluntarily);
            transporter.Notify_ThingRemoved(thing);
        }
    }
}

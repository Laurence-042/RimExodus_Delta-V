using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public partial class Building_SpaceTradeTerminal
    {
        internal bool MissionIssuer => Spawned && Current.Game.GetComponent<UniverseManager>().FrameOf(Map) != null
            && Faction != null && Faction != Faction.OfPlayer && !Faction.HostileTo(Faction.OfPlayer)
            && !ShipRegistry.AllShipsOnMap(Map).Any(s => s.Faction == Faction.OfPlayer && s.Contains(this))
            && MissionCargo != null;
        internal ISpaceCargoContainer MissionCargo => ValidContainers(true).FirstOrDefault();
        internal HashSet<IntVec3> MissionCells
        {
            get
            {
                var cells = new HashSet<IntVec3>();
                if (!Spawned) return cells;
                ShipBase station = ShipRegistry.ShipOf(this);
                if (station.IsValid) return new HashSet<IntVec3>(station.HullCells);
                SubstructureOwnership.CollectConnected(Map, Position, cells, null);
                // Shared-map docking must not make the player's ship a delivery destination.
                foreach (var ship in ShipRegistry.AllShipsOnMap(Map)) cells.ExceptWith(ship.HullCells);
                return cells;
            }
        }
        internal bool MissionInteraction(Pawn pawn) => Spawned && pawn?.Spawned == true && pawn.IsColonistPlayerControlled
            && pawn.Map == Map && pawn.CanReachImmediate(this, PathEndMode.Touch)
            && Current.Game.GetComponent<UniverseManager>().FrameOf(Map) != null;
        internal IEnumerable<Thing> MissionSilver() => TradeShips().SelectMany(s => TradeUtility.AllLaunchableThingsForTrade(s.Map, this))
            .Concat(MissionCargo?.Contents ?? System.Array.Empty<Thing>()).Where(t => t.def == ThingDefOf.Silver).Distinct();
        internal void OpenMissions(Pawn pawn)
        {
            if (MissionInteraction(pawn)) Find.WindowStack.Add(new Dialog_SpaceMissions(this, pawn));
        }
    }
    public sealed class JobDriver_UseMissionTerminal : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Do(() => (TargetThingA as Building_SpaceTradeTerminal)?.OpenMissions(pawn));
        }
    }
}

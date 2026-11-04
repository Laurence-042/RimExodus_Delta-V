using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DeltaV
{
    // Access is a ship capability; neither its buildings nor its foundations change owner.
    internal static class PublicFacility
    {
        internal static CompDeltaVPilotConsole ConsoleAt(Map map, IntVec3 cell)
        {
            if (map == null || Current.Game?.GetComponent<UniverseManager>()?.FrameOf(map) == null) return null;
            int owner = SubstructureOwnership.TryGet(map)?.OwnerAt(cell) ?? 0;
            CompDeltaVPilotConsole console = ShipRegistry.MainConsoleForOwner(map, owner);
            return console?.isPublicFacility == true && console.parent.Faction != null
                && console.parent.Faction != Faction.OfPlayer ? console : null;
        }

        internal static bool PlayerMayUse(Thing thing) => thing?.Spawned == true
            && ConsoleAt(thing.Map, thing.Position)?.Vessel.PlayerMayOperate == true;

        internal static void Penalize(Faction owner, Thing actor, GlobalTargetInfo target)
        {
            if (owner == null || owner == Faction.OfPlayer || actor?.Faction != Faction.OfPlayer) return;
            Faction.OfPlayer.TryAffectGoodwillWith(owner, -10, reason: HistoryEventDefOf.AttackedBuilding, lookTarget: target);
        }

        // Engineering tools change HP directly to preserve their native deconstruction yields.
        internal static void NotifyCut(Building building, Thing actor, int damage)
        {
            if (ConsoleAt(building.Map, building.Position) == null) return;
            building.Faction?.Notify_BuildingTookDamage(building,
                new DamageInfo(DamageDefOf.Cut, damage, instigator: actor));
        }
    }
}

using System.Collections.Generic;
using DeltaV;
using Verse;

internal static partial class Program
{
    static void PlaceFixtureBuilding(Building building, Map map, bool notify = true)
    {
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.listerThings.AllThings.Add(building);
        var defs = (Dictionary<ThingDef, List<Thing>>)typeof(ListerThings)
            .GetField("listsByDef", Private).GetValue(map.listerThings);
        if (!defs.TryGetValue(building.def, out var list)) defs[building.def] = list = new List<Thing>();
        list.Add(building);
        if (building.TryGetComp<CompSpaceShuttle>() != null)
        {
            var groups = (List<Thing>[])typeof(ListerThings).GetField("listsByGroup", Private).GetValue(map.listerThings);
            (groups[(int)ThingRequestGroup.Transporter] ??= new List<Thing>()).Add(building);
        }
        ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Add(building);
        map.thingGrid.Register(building);
        if (notify) ShipStructureChanges.Notify(map, building: building);
    }

    static void RemoveFixtureBuilding(Building building, Map map, System.Action afterDespawn = null)
    {
        map.thingGrid.Deregister(building);
        map.listerThings.AllThings.Remove(building);
        map.listerThings.ThingsOfDef(building.def).Remove(building);
        map.listerThings.ThingsInGroup(ThingRequestGroup.Transporter).Remove(building);
        ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Remove(building);
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)-1);
        afterDespawn?.Invoke();
        ShipStructureChanges.Notify(map, building: building);
    }
}

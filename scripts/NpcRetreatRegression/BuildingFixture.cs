using System;
using System.Collections.Generic;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void PlaceFixtureBuilding(Building building, Map map, bool notify = true)
    {
        building.def.thingClass ??= building.GetType();
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        // Native ListerThings.Add initializes the Unity DLC configuration while checking group predicates.
        map.listerThings.AllThings.Add(building);
        var defs = (Dictionary<ThingDef, List<Thing>>)typeof(ListerThings)
            .GetField("listsByDef", Private).GetValue(map.listerThings);
        if (!defs.TryGetValue(building.def, out var list)) defs[building.def] = list = new List<Thing>();
        list.Add(building);
        if (building.TryGetComp<CompTransporter>() != null || building.TryGetComp<CompSpaceShuttle>() != null)
        {
            var groups = (List<Thing>[])typeof(ListerThings).GetField("listsByGroup", Private).GetValue(map.listerThings);
            (groups[(int)ThingRequestGroup.Transporter] ??= new List<Thing>()).Add(building);
        }
        ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Add(building);
        map.thingGrid.Register(building);
        if (!notify) return;
        foreach (var weapon in CompSpaceWeapon.On(building)) FixtureUniverse.Weapons.RegisterWeapon(weapon);
        ShipStructureChanges.Notify(map, building: building);
    }

    static void MoveCombatFixture(Thing thing, IntVec3 position, Rot4? rotation = null)
    {
        Map map = thing.Map;
        CellRect previous = thing.OccupiedRect();
        bool edifice = map.edificeGrid?[thing.Position] == thing;
        if (edifice) map.edificeGrid.DeRegister((Building)thing);
        map.thingGrid.Deregister(thing);
        Set(typeof(Thing), thing, "positionInt", position);
        if (rotation.HasValue) thing.Rotation = rotation.Value;
        map.thingGrid.Register(thing);
        if (edifice) map.edificeGrid.Register((Building)thing);
        if (thing is Building building)
            ShipStructureChanges.Notify(map, building, null, previous);
    }

    static void RemoveFixtureBuilding(Building building, Map map, Action afterDespawn = null)
    {
        foreach (var weapon in CompSpaceWeapon.On(building)) FixtureUniverse.Weapons.UnregisterWeapon(weapon, map);
        if (map.edificeGrid?[building.Position] == building) map.edificeGrid.DeRegister(building);
        map.thingGrid.Deregister(building);
        map.listerThings.AllThings.Remove(building);
        map.listerThings.ThingsOfDef(building.def).Remove(building);
        map.listerThings.ThingsInGroup(ThingRequestGroup.Transporter).Remove(building);
        ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Remove(building);
        Set(typeof(Thing), building, "mapIndexOrState", (sbyte)-1);
        afterDespawn?.Invoke();
        ShipStructureChanges.Notify(map, building: building);
    }

    static UniverseManager FixtureUniverse => Current.Game.GetComponent<UniverseManager>();
    static readonly Action<Thing, Faction> PublishFixtureFaction = AccessTools.MethodDelegate<Action<Thing, Faction>>(
        AccessTools.Method(typeof(Patch_ShipFaction), "Postfix"));

    // The offline harness cannot run Unity's ModsConfig initialization; skip it on first use,
    // whichever regression entry reaches this fixture first.
    private static Harmony modsConfigGuard;
    private static void GuardModsConfig()
    {
        if (modsConfigGuard != null) return;
        modsConfigGuard = new Harmony("DeltaV.Regression.ModsConfigGuard");
        modsConfigGuard.Patch(typeof(ModsConfig).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
    }

    sealed class FixtureOdyssey : IDisposable
    {
        private readonly bool wasActive;
        public FixtureOdyssey()
        {
            GuardModsConfig();
            wasActive = ModsConfig.OdysseyActive;
            AccessTools.Field(typeof(ModsConfig), "odysseyActive").SetValue(null, true);
        }
        public void Dispose() => AccessTools.Field(typeof(ModsConfig), "odysseyActive").SetValue(null, wasActive);
    }

    static Building CreateFixtureBuilding(ThingDef def, int id, IntVec3 cell, Rot4 rotation, Faction faction = null)
    {
        var building = new Building { def = def, thingIDNumber = id, Position = cell, Rotation = rotation };
        building.SetFactionDirect(faction);
        building.InitializeComps();
        return building;
    }

    static void TransferFixtureBuilding(Building building, Map destination, Action afterDespawn = null)
    {
        using var batch = FixtureUniverse.ShipStructure.BeginChanges();
        RemoveFixtureBuilding(building, building.Map, afterDespawn);
        PlaceFixtureBuilding(building, destination);
    }

    static void SetFixtureHitPoints(Thing thing, int hp)
    {
        int before = thing.HitPoints;
        Map map = thing.Spawned ? thing.Map : null;
        if (map != null) map.events ??= new MapEvents(map);
        thing.HitPoints = hp;
        if (map != null) FixtureUniverse.NotifyProtectionHealth(map, thing, before);
    }

    static void SetFixtureFaction(Thing thing, Faction faction)
    {
        Faction before = thing.Faction;
        thing.SetFactionDirect(faction);
        PublishFixtureFaction(thing, before);
    }

    static void AddFixtureComponents(ThingWithComps thing, params ThingComp[] components)
    {
        foreach (var component in components)
        {
            component.parent = thing;
            thing.AllComps.Add(component);
        }
        if (thing is not Building building || !building.Spawned) return;
        foreach (var component in components)
            if (component is CompSpaceWeapon weapon) FixtureUniverse.Weapons.RegisterWeapon(weapon);
        ShipStructureChanges.Notify(building.Map, building, null, building.OccupiedRect());
    }

    static void RemoveFixtureComponent(Building building, ThingComp component)
    {
        if (!building.AllComps.Remove(component) || !building.Spawned) return;
        if (component is CompSpaceWeapon weapon) FixtureUniverse.Weapons.UnregisterWeapon(weapon, building.Map);
        ShipStructureChanges.Notify(building.Map, building, null, building.OccupiedRect());
    }

    static TerrainDef FixtureFoundation(string name)
    {
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.defName = name;
        foundation.isFoundation = true;
        foundation.tags = new List<string> { "Substructure" };
        return foundation;
    }

    // TerrainGrid.SetFoundation and RemoveFoundation run map-mesh effects in Unity.
    static void SetFixtureFoundation(Map map, IntVec3 cell, TerrainDef foundation, int owner)
    {
        int index = map.cellIndices.CellToIndex(cell);
        var before = map.terrainGrid.foundationGrid[index];
        using var batch = FixtureUniverse.ShipStructure.BeginChanges();
        map.terrainGrid.foundationGrid[index] = foundation;
        SubstructureOwnership.For(map).SetOwner(new[] { cell }, foundation != null ? owner : 0);
        FixtureUniverse.NotifyFoundationChanged(map, cell);
        FixtureUniverse.ShipStructure.NotifyCell(map, cell, ShipStructureAspect.Foundation, before, foundation);
        ShipStructureChanges.Notify(map, terrainCell: cell);
        FixtureUniverse.NotifySupportChanged(map, cell);
    }

    static void RetireFixtureMap(Map map)
    {
        if (FixtureUniverse.SpaceObjectOnMap(map) is { } container) FixtureUniverse.UnregisterObject(container);
        else FixtureUniverse.ForgetFoundationTopology(map);
        int index = Find.Maps.IndexOf(map);
        if (index < 0) return;
        for (int i = index + 1; i < Find.Maps.Count; i++)
            foreach (Thing thing in Find.Maps[i].listerThings.AllThings)
                if (thing.Spawned && thing.Map == Find.Maps[i]) thing.DecrementMapIndex();
        Find.Maps.RemoveAt(index);
    }

    static ThingDef TestBuildingDef(string name)
    {
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.defName = name; def.category = ThingCategory.Building; def.size = new IntVec2(1, 1);
        def.shortHash = ++testDefHash;
        def.comps = new List<CompProperties>();
        def.building = new BuildingProperties();
        return def;
    }

    static ushort testDefHash = 100;

    static Map TestMap(int id, int width = 5)
    {
        var map = (Map)Raw(typeof(Map));
        map.uniqueID = id;
        map.info = new MapInfo { Size = new IntVec3(width, 1, width), parent = (ShipPocketParent_DeltaV)Raw(typeof(ShipPocketParent_DeltaV)) };
        map.cellIndices = new CellIndices(map);
        map.events = new MapEvents(map);
        map.designationManager = new DesignationManager(map);
        map.components = new List<MapComponent> { new SubstructureOwnership(map) };
        map.terrainGrid = new TerrainGrid(map);
        // These generic fixtures model supported stations. Structure-loss tests replace this terrain.
        var ground = (TerrainDef)Raw(typeof(TerrainDef));
        ground.defName = "regressionGround";
        map.terrainGrid.topGrid[0] = ground;
        map.thingGrid = new ThingGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.spawnedThings = new ThingOwner<Thing>(map);
        map.floodFiller = new FloodFiller(map);
        Find.Maps.Add(map);
        return map;
    }

    static Building AddTestBuilding(Map map, Faction faction, int id, ThingDef def, ThingComp comp, Building body = null)
    {
        // These pre-existing fixtures specify a 2000-force model independently of vanilla range calibration.
        if (def.GetCompProperties<CompProperties_GravshipThruster>() != null
            && def.GetModExtension<DefModExtension_ThrusterPower>() == null)
            (def.modExtensions ??= new List<DefModExtension>()).Add(
                new DefModExtension_ThrusterPower { thrust = 2000f });
        body ??= (Building)Raw(typeof(Building));
        body.def = def; body.thingIDNumber = id;
        Set(typeof(Thing), body, "positionInt", new IntVec3(1, 0, 2));
        Set(typeof(Thing), body, "factionInt", faction);
        Set(typeof(ThingWithComps), body, "comps", new List<ThingComp> { comp });
        comp.parent = body;
        PlaceFixtureBuilding(body, map, notify: false);
        foreach (var weapon in CompSpaceWeapon.On(body))
            Current.Game?.GetComponent<UniverseManager>()?.Weapons.RegisterWeapon(weapon);
        SubstructureOwnership.TryGet(map)?.UpdateSpawnedBuilding(body);
        return body;
    }

    static CompDeltaVPilotConsole AddTestConsole(Map map, Faction faction, int id, int? buildingId = null)
    {
        var comp = new CompDeltaVPilotConsole { isMain = true, shipId = id };
        AddTestBuilding(map, faction, buildingId ?? id, ThingDefOf.PilotConsole, comp);
        return comp;
    }

    static CompSpaceShuttle AddTestHull(Map map, Faction faction, int id, CompSpaceShuttle comp = null)
    {
        comp ??= new CompSpaceShuttle { props = new CompProperties_SpaceShuttle() };
        AddTestBuilding(map, faction, id, TestBuildingDef("shuttle"), comp);
        return comp;
    }
}

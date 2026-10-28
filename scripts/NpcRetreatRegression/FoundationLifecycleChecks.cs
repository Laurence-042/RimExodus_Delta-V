using System.Collections.Generic;
using System.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static CompDeltaVPilotConsole RoundTripConsole(CompDeltaVPilotConsole console)
    {
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "foundation-console.xml");
        try
        {
            Scribe.saver.InitSaving(path, "console");
            console.PostExposeData();
            Scribe.saver.FinalizeSaving();
            var restored = new CompDeltaVPilotConsole();
            Scribe.loader.InitLoading(path);
            restored.PostExposeData();
            Scribe.loader.FinalizeLoading();
            return restored;
        }
        finally { Scribe.ForceStop(); }
    }

    static void RemoveConsoleFixture(CompDeltaVPilotConsole console, Map map)
    {
        map.thingGrid.Deregister(console.parent);
        map.listerThings.AllThings.Remove(console.parent);
        map.listerThings.ThingsOfDef(console.parent.def).Remove(console.parent);
        Set(typeof(Thing), console.parent, "mapIndexOrState", (sbyte)-1);
        // Real Comp callback queues reconciliation; Building postfix invalidates after it returns.
        console.PostDeSpawn(map);
        ShipStructureChanges.Notify(map);
    }

    static void PlaceConsoleFixture(CompDeltaVPilotConsole console, Map map)
    {
        Set(typeof(Thing), console.parent, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.listerThings.AllThings.Add(console.parent);
        var defs = (Dictionary<ThingDef, List<Thing>>)typeof(ListerThings)
            .GetField("listsByDef", Private).GetValue(map.listerThings);
        if (!defs.TryGetValue(console.parent.def, out var list))
            defs[console.parent.def] = list = new List<Thing>();
        list.Add(console.parent);
        map.thingGrid.Register(console.parent);
        ShipStructureChanges.Notify(map);
        Current.Game.GetComponent<UniverseManager>().Controls.For(console.Vessel);
    }

    static void CheckFoundationLifecycle(UniverseManager universe, Faction player, Faction enemy)
    {
        var sourceMap = TestMap(1600);
        var destinationMap = TestMap(1601);
        var source = new MapSpaceObject(1600, sourceMap, new SpacePose(Vector3.forward, 0));
        var destination = new MapSpaceObject(1601, destinationMap, source.pose);
        universe.objects.Add(source);
        universe.objects.Add(destination);
        Check(!source.Ships.Any() && !destination.Ships.Any(),
            "foundation lifecycle warms both containers before member changes");
        var primary = AddTestConsole(sourceMap, player, 1602);
        var hostConsole = AddTestConsole(sourceMap, enemy, 1603);
        var carried = AddTestHull(sourceMap, enemy, primary.shipId);
        var weapon = new CompSpaceWeapon { parent = carried.parent, props = new CompProperties_SpaceWeapon() };
        carried.parent.AllComps.Add(weapon);
        ShipStructureChanges.Notify(sourceMap);
        ShipBase original = primary.Vessel;
        DriveSubject identity = original.Identity, hullIdentity = carried.Vessel.Identity;
        var controller = universe.Controls.For(original);
        var hullController = universe.Controls.For(carried.Vessel);
        universe.Controls.For(hostConsole.Vessel);
        controller.Enter(PilotTarget.Of(original));
        Check(source.Ships.Count() == 3 && source.HasFaction(player) && source.HasFaction(enemy)
            && identity != hullIdentity && !source.InterceptionEligible,
            "mixed foundation and shuttle members retain typed identities and do not tag their carrier for PD");
        Check(original.State == ShipDerivedState.Docked && source.PrimaryDriver.key == 0,
            "two independent foundation ships sharing a map require separation before driving");
        Check(original.Transfer.console == primary.parent && original.Transfer.body == null
            && original.Transfer.includeTerrain && original.Transfer.ownerId == identity.key,
            "foundation transfer uses the existing console, ownership and terrain contract");
        Check(!original.Weapons.Any() && carried.Vessel.Weapons.Single() == weapon,
            "equal numeric foundation and hull IDs cannot alias weapon ownership");

        // Queue a real separation intent, then simulate an intervening whole-domain transfer.
        Call(universe.ShipTransitions, "QueueSeparation", universe, identity.key, false);
        primary.combat.fireControlMode = ShipFireControlMode.Manual;
        primary.combat.weaponGroupsMain = 16;
        RemoveConsoleFixture(primary, sourceMap);
        PlaceConsoleFixture(primary, destinationMap);
        UnspawnFixture(carried, sourceMap);
        SpawnFixture(carried, destinationMap);
        ShipBase relocated = ShipRegistry.ShipByIdentity(identity);
        Check(!original.IsValid && relocated.IsValid && relocated.Map == destinationMap
            && !ReferenceEquals(original, relocated),
            "foundation transfer invalidates the old Map snapshot and rebuilds the same identity");
        Check(controller.Active && controller.target.Vessel == relocated
            && ReferenceEquals(Controllers(universe)[identity], controller),
            "active foundation pilot follows the replacement snapshot through the shared binding entry");
        Call(universe, "RunFrameActions");
        Check(!universe.ShipTransitions.pendingSeparationOwnerIds.Contains(identity.key)
            && primary.parent.Map == destinationMap && universe.objects.Count == 2,
            "queued separation cannot follow a moved ship while its old source container still exists");
        Check(destination.PrimaryDriver == identity && !carried.Vessel.CanDriveIn(destination)
            && !destination.InterceptionEligible && source.Ships.Single().Identity == hostConsole.Vessel.Identity,
            "carried shuttle does not become a second driver or leave stale source membership");
        Check(ReferenceEquals(Controllers(universe)[hullIdentity], hullController)
            && destination.Weapons.Single() == weapon && !source.Weapons.Any(),
            "carried hull controller and weapons follow independently of the foundation owner");
        universe.Controls.ForgetMap(sourceMap);
        Check(Controllers(universe).ContainsKey(identity) && Controllers(universe).ContainsKey(hullIdentity)
            && !Controllers(universe).ContainsKey(hostConsole.Vessel.Identity),
            "old-map cleanup removes its resident controller while retaining both relocated identities");

        // Docking moves the same anchors back; persisted configuration must remain on those anchors.
        RemoveConsoleFixture(primary, destinationMap);
        PlaceConsoleFixture(primary, sourceMap);
        UnspawnFixture(carried, destinationMap);
        SpawnFixture(carried, sourceMap);
        Call(universe, "RunFrameActions");
        universe.Controls.ForgetMap(destinationMap);
        Check(controller.Active && controller.target.Map == sourceMap
            && primary.combat.fireControlMode == ShipFireControlMode.Manual
            && primary.combat.weaponGroupsMain == 16 && source.Weapons.Single() == weapon
            && !destination.HasTag(SpaceObjectTag.Ship),
            "docking follows current identities, preserves anchor combat state and clears source roles");

        var savedConsole = RoundTripConsole(primary);
        Check(savedConsole.shipId == identity.key && savedConsole.isMain
            && savedConsole.combat.fireControlMode == ShipFireControlMode.Manual
            && savedConsole.combat.weaponGroupsMain == 16
            && !ReferenceEquals(savedConsole.combat, primary.combat),
            "real Scribe roundtrip preserves console identity, main claim and independent combat state");

        var backup = AddTestConsole(sourceMap, player, 1604);
        backup.shipId = identity.key;
        backup.isMain = false;
        _ = backup.Vessel; // Warm an invalid adapter before succession.
        RemoveConsoleFixture(primary, sourceMap);
        primary.PostDestroy(DestroyMode.Vanish, sourceMap);
        Check(backup.isMain && ShipRegistry.ShipByIdentity(identity).Anchor == backup.parent
            && controller.Active && controller.target.Vessel.Anchor == backup.parent
            && ReferenceEquals(Controllers(universe)[identity], controller),
            "real main-console destruction and succession retain the controller and refresh its pilot snapshot");
        Call(universe, "RunFrameActions");
        Check(Controllers(universe).ContainsKey(identity) && Controllers(universe).ContainsKey(hullIdentity),
            "delayed old-console despawn reconciliation cannot remove the successor or equal-ID shuttle");
        RemoveConsoleFixture(backup, sourceMap);
        backup.PostDestroy(DestroyMode.Vanish, sourceMap);
        Call(universe, "RunFrameActions");
        Check(!ShipRegistry.ShipByIdentity(identity).IsValid && !Controllers(universe).ContainsKey(identity)
            && Controllers(universe).ContainsKey(hullIdentity) && source.Weapons.Single() == weapon,
            "loss of the last main console cleans only that foundation ship, preserving the carried shuttle");

        var restored = savedConsole;
        AddTestBuilding(destinationMap, player, 1605, ThingDefOf.PilotConsole, restored);
        ShipStructureChanges.Notify(destinationMap);
        universe.Controls.Rebuild();
        Check(ShipRegistry.ShipByIdentity(identity).Anchor == restored.parent
            && !ReferenceEquals(Controllers(universe)[identity], controller)
            && Controllers(universe).ContainsKey(hullIdentity)
            && !Controllers(universe)[identity].Active,
            "load-style rebuild restores both typed identities without reusing old controllers or manual sessions");
        ShipBase beforeDemotion = restored.Vessel;
        restored.MakeBackupConsole();
        Check(!restored.isMain && !beforeDemotion.IsValid
            && !ShipRegistry.AllShipsOnMap(destinationMap).Any()
            && !Controllers(universe).ContainsKey(identity),
            "voluntary last-primary demotion removes its valid ship and controller without choosing a successor");
        var noShipActions = new List<PilotPanelAction>();
        beforeDemotion.CollectPanelActions(noShipActions);
        Check(noShipActions.Count == 0,
            "an invalidated foundation ShipBase delegates no sidebar driving actions");
        var savedBackup = RoundTripConsole(restored);
        Check(!savedBackup.isMain && savedBackup.shipId == restored.shipId,
            "Scribe retains explicit backup state including unassigned identity");
        universe.Controls.Reset();
        universe.objects.Clear();
        Find.Maps.Clear();
        ShipRegistry.ClearCaches();
        ShipPhysics.ClearCache();
        SpaceCollision.ClearCaches();
    }
}

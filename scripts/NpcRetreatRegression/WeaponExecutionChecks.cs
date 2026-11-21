using System;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static int weaponAmmoReads, weaponEnergyReads, weaponTopologyReads, weaponPreparations, weaponAimCommands;
    static Thing ownershipProbeThing;
    static int weaponOwnershipReads;
    static void CountWeaponOwnership(Thing thing, bool requireWholeFootprint)
    { if (thing == ownershipProbeThing && requireWholeFootprint) weaponOwnershipReads++; }
    static float commandedWeaponAngle;
    static readonly HashSet<CompSpaceWeaponMagazine> loadedExecutionMagazines = new HashSet<CompSpaceWeaponMagazine>();
    static bool ExecutionMagazineLoaded(CompSpaceWeaponMagazine __instance, ref bool __result)
    {
        if (!loadedExecutionMagazines.Contains(__instance)) return true;
        __result = true; return false;
    }
    static void CountWeaponAmmo() { weaponAmmoReads++; }
    static void CountWeaponEnergy() { weaponEnergyReads++; }
    static void CountWeaponTopology() { weaponTopologyReads++; }
    static void CountWeaponPreparation() { weaponPreparations++; }
    static void CaptureWeaponAngle(float mapAngle) { commandedWeaponAngle = mapAngle; weaponAimCommands++; }
    static int powerUpdates, powerDiscoveries;
    static CompSpaceWeapon powerProbe;
    static void CountPowerUpdate(CompSpaceWeapon weapon) { if (weapon == powerProbe) powerUpdates++; }
    static void CountPowerDiscovery(ListerThings __instance)
    { if (__instance == powerProbe?.parent.Map.listerThings) powerDiscoveries++; }

    static void CheckWeaponPowerEvents(UniverseManager universe, Faction faction)
    {
        var map = TestMap(2990);
        var frame = new MapSpaceObject(2990, map, new SpacePose(Vector3.forward, 0f));
        universe.objects.Add(frame); Set(universe, "objectByMap", null); Set(universe, "objectById", null);
        var console = AddTestConsole(map, faction, 29901);
        var ship = new CombatHull(console.Ship);
        var gun = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon() };
        var body = AddTestBuilding(map, faction, 29902, TestBuildingDef("powerGun"), gun);
        ship.cells.Add(body.Position);
        var props = new CompProperties_Power { idlePowerDraw = 5f };
        Set(props, "basePowerConsumption", 220f);
        var power = new CompPowerTrader { parent = body, props = props };
        var mannable = new CompMannable { parent = body, props = new CompProperties_Mannable() };
        AddFixtureComponents(body, power, mannable);
        Set(power, "powerOnInt", true);
        universe.ShipStructure.BindIdentity(ship);
        powerProbe = gun;
        var patch = new Harmony("DeltaV.Regression.WeaponPowerEvents");
        patch.Patch(AccessTools.Method(typeof(SpaceWeaponService), "SyncPowerRequest"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPowerUpdate)));
        patch.Patch(AccessTools.PropertyGetter(typeof(ListerThings), nameof(ListerThings.AllThings)),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPowerDiscovery)));
        patch.Patch(AccessTools.Method(typeof(CompPowerTrader), "SetUpPowerVars"),
            postfix: new HarmonyMethod(typeof(Patch_SpaceShieldMode), nameof(Patch_SpaceShieldMode.SetUpPowerVars)));
        try
        {
            powerDiscoveries = 0;
            gun.SyncPowerRequest();
            Check(power.PowerOutput == -5 && !gun.Operational, "unarmed gun requests configured idle draw without firing");
            ship.Combat.weaponsArmed = true; universe.Weapons.SyncShipPowerRequests(ship);
            Check(power.PowerOutput == -220 && !gun.Operational,
                "active armed group requests wartime draw before the gunner arrives");
            var pawn = (Pawn)Raw(typeof(Pawn)); Set(typeof(Thing), pawn, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
            Set(mannable, "lastManPawn", pawn); Set(mannable, "lastManTick", Find.TickManager.TicksGame);
            Check(gun.Operational && power.PowerOutput == -220, "native gunner enables operation without changing the power tier");
            Set(mannable, "lastManTick", Find.TickManager.TicksGame - 2);
            Check(!gun.Operational && power.PowerOutput == -220, "native gunner expiry only disables operation");
            universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Main, 0);
            Check(power.PowerOutput == -5, "existing group toggle applies idle draw immediately");
            powerUpdates = 0; universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Main, 0);
            Check(powerUpdates == 0, "unchanged group mask performs no synchronization");
            universe.Weapons.QueuePower(map); universe.Weapons.QueuePower(map);
            universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Main, 1);
            universe.Weapons.TickPendingUpdates();
            Check(power.PowerOutput == -220 && powerUpdates == 1,
                "immediate group activation consumes duplicate pending structure events");
            gun.SetGroup(SpaceWeaponSalvoSet.Secondary, 2);
            universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Secondary, 0);
            Check(power.PowerOutput == -5, "regrouping follows the new set rather than the old active group");
            power.SetUpPowerVars();
            Check(power.PowerOutput == -5, "native research power reset cannot replace idle draw with wartime draw");
            props.idlePowerDraw = 9; gun.SyncPowerRequest();
            Check(power.PowerOutput == -9, "idle draw is configurable and never hardcoded to five watts");
            universe.Controls.SetWeaponGroupMask(ship, SpaceWeaponSalvoSet.Secondary, 2);
            Set(props, "basePowerConsumption", 330f); power.SetUpPowerVars();
            Check(power.PowerOutput == -330, "native power reset reads current wartime consumption");
            powerUpdates = 0;
            for (int i = 0; i < 10000; i++)
            {
                Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
                gun.CompTick(); universe.Weapons.TickPendingUpdates();
            }
            Check(powerUpdates == 0 && power.PowerOutput == -330, "ten thousand stable ticks never synchronize weapon power");
            Check(universe.Weapons.RegisteredWeapons(map).Contains(gun), "existing registry includes non-PD weapons");
            universe.ShipStructure.Changed(map);
            Check(ship.Weapons.Single() == gun && powerDiscoveries == 0,
                "cold ship weapon cache and power events reuse the index without map discovery");
            universe.Weapons.QueuePower(map); universe.Weapons.UnregisterWeapon(gun, map);
            universe.Weapons.TickPendingUpdates();
            Check(powerUpdates == 0 && !universe.Weapons.RegisteredWeapons(map).Contains(gun),
                "despawn removes both registered weapon and pending work");
        }
        finally { powerProbe = null; patch.UnpatchAll(patch.Id); universe.Weapons.ForgetMap(map); }
    }

    static void CheckWeaponExecution(UniverseManager universe, Faction faction)
    {
        CheckWeaponPowerEvents(universe, faction);
        Map map = TestMap(1995), alternateMap = TestMap(1996);
        var stamp = new PlayerInputStamp();
        int samples = 0;
        for (int tick = 0; tick < 12; tick++) if (stamp.BeginSample(10, map, 0, true)) samples++;
        Check(samples == 1, "twelve simulation ticks in one render frame share one input sample");
        Check(stamp.BeginSample(11, map, 0, true) && !stamp.BeginSample(11, map, 0, true),
            "new render frames sample once even without advancing game ticks");
        Check(stamp.BeginSample(11, alternateMap, 0, true), "map changes invalidate input within the same frame");
        Check(stamp.BeginSample(11, alternateMap, 1, true), "GUI changes invalidate input within the same frame");
        Check(stamp.BeginSample(11, alternateMap, 1, false), "focus loss invalidates input within the same frame");
        stamp.Clear();
        Check(stamp.BeginSample(11, alternateMap, 1, false), "session disposal clears input sampling state");

        var bullet = TestBuildingDef("executionBullet");
        bullet.comps.Add(new CompProperties_SpaceProjectile { speed = 100f, hasMap = false });
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon
            { projectileDef = bullet, range = 500f, fireArcDeg = 360f, intrinsicAimSkill = 10 }, safeFire = false };
        var traverse = new CompSpaceWeaponTraverse { props = new CompProperties_SpaceWeaponTraverse() };
        var building = AddTestBuilding(map, faction, 19950, TestBuildingDef("executionGun"), weapon);
        AddFixtureComponents(building, traverse);
        var console = AddTestConsole(map, faction, 19951);
        var weaponOwner = new CombatHull(console.Ship);
        weaponOwner.cells.Add(building.Position);
        weaponOwner.Combat.weaponsArmed = true;
        var own = new MapSpaceObject(1995, map, new SpacePose(Vector3.forward, 0f));
        universe.objects.Add(own); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        universe.ShipStructure.BindIdentity(weaponOwner);
        var harness = new Harmony("DeltaV.Regression.WeaponExecution");
        harness.Patch(AccessTools.Method(typeof(CompSpaceWeapon), "SelectMagazine"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountWeaponAmmo)));
        harness.Patch(AccessTools.Method(typeof(SpaceWeaponEnergy), "Evaluate"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountWeaponEnergy)));
        harness.Patch(AccessTools.Method(typeof(CompSpaceRailgun), "TraceConnections"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountWeaponTopology)));
        harness.Patch(AccessTools.Method(typeof(SpaceWeaponShot), "Prepare"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountWeaponPreparation)));
        harness.Patch(AccessTools.Method(typeof(CompSpaceWeaponTraverse), "AimAt"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureWeaponAngle)));
        harness.Patch(AccessTools.PropertyGetter(typeof(CompSpaceWeaponMagazine), "HasShot"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ExecutionMagazineLoaded)));
        try
        {
            weaponAmmoReads = weaponEnergyReads = weaponTopologyReads = weaponPreparations = 0;
            for (int i = 0; i < 120; i++)
            {
                Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
                universe.Weapons.ApplyControl(weapon, own, new WeaponControlCommand(new Vector2(8, 8), false));
            }
            Check(weaponAmmoReads == 0 && weaponEnergyReads == 0 && weaponTopologyReads == 0 && weaponPreparations == 0,
                "120 manual aim-only commands never query magazines, energy, connections or shot preparation");
            Check(traverse.CurrentAngle > .1f, "aim-only commands still advance the real turret every game tick");
            weapon.cooldownUntilTick = Find.TickManager.TicksGame + 100;
            universe.Weapons.ApplyControl(weapon, own, new WeaponControlCommand(new Vector2(8, 3), true));
            Check(weaponAmmoReads == 0 && weaponEnergyReads == 0 && weaponPreparations == 0,
                "manual fire during cooldown keeps pointing without preparing a shot");

            var victim = new SpacePose(OrbitSphere.Move(own.pose.position, new Vector2(80, 20)), 0f)
                { velocity = new Vector2(-5, 18) };
            own.pose.velocity = new Vector2(7, -2);
            WeaponAimInfo preview = SpaceWeaponService.AimInfo(weapon, own);
            Check(SpaceWeaponBallistics.TryAim(preview, own.pose, victim, out Vector3 lead),
                "shared weapon query produces a lead for a moving target");
            var fleeing = new SpacePose(OrbitSphere.Move(preview.Muzzle, new Vector2(60f, 0f)), 0f)
                { velocity = new Vector2(95f, 0f) };
            var shortRange = new WeaponAimInfo(preview.Muzzle, preview.MuzzleMap,
                bullet, 100f, 0f, 100f);
            Check(!SpaceWeaponBallistics.TryAim(shortRange, own.pose, fleeing, out Vector3 impossible)
                && impossible == fleeing.position,
                "an intercept beyond weapon range falls back to the current enemy position");
            var target = new ResolvedCombatTarget(null, null, victim);
            universe.Weapons.ApplyControl(weapon, own,
                new WeaponControlCommand(CombatTarget.At(victim.position), target, true, WeaponControlSource.Automatic));
            Vector2 delta = SpaceTransform.UniverseToMapPoint(own, map, lead) - preview.MuzzleMap;
            Check(Math.Abs(SpaceTransform.WrapAngle(commandedWeaponAngle - Mathf.Atan2(delta.y, delta.x))) < .00001f,
                "automatic cooldown pointing uses exactly the same lead as manual preview");
            Check(weaponTopologyReads == 1, "repeated ballistic queries reuse one structural connection model");
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll(); SpaceWeaponService.AimInfo(weapon, own);
            Check(weaponTopologyReads == 2, "a structural change invalidates weapon topology immediately");

            var magazine = new CompSpaceWeaponMagazine { parent = building,
                props = new CompProperties_SpaceWeaponMagazine { projectileDef = bullet } };
            AddFixtureComponents(building, magazine);
            weapon.cooldownUntilTick = 0;
            int oldPreparations = weaponPreparations;
            Check(!universe.Weapons.ApplyControl(weapon, own, new WeaponControlCommand(new Vector2(8, 3), true))
                && weaponPreparations == oldPreparations && weapon.cooldownUntilTick == 0,
                "missing ammunition rejects a triggered command without starting a firing transaction or cooldown");
            RemoveFixtureComponent(building, magazine);

            var mags = new List<CompSpaceWeaponMagazine>();
            for (int i = 0; i < 3; i++)
            {
                var mag = new CompSpaceWeaponMagazine { props = new CompProperties_SpaceWeaponMagazine { projectileDef = bullet } };
                var holder = AddTestBuilding(map, faction, 19960 + i, TestBuildingDef("mag" + i), mag);
                loadedExecutionMagazines.Add(mag); Set(mag, "priority", i == 2 ? 1 : 2); mags.Add(mag);
            }
            // Supply a deliberately unordered known connection set to isolate the selector's ordering contract.
            weapon.Layout.magazines.Add(mags[2]); weapon.Layout.magazines.Add(mags[1]); weapon.Layout.magazines.Add(mags[0]);
            Check(weapon.SelectMagazine() == mags[0], "magazine selection chooses priority then the smallest ID");
            weapon.NotifyMagazineConsumed(mags[0]);
            Check(weapon.SelectMagazine() == mags[1], "equal-priority magazines round-robin by ID");
            weapon.NotifyMagazineConsumed(mags[1]);
            Check(weapon.SelectMagazine() == mags[0], "round-robin wraps without choosing lower-priority magazines");
            universe.Weapons.ForgetMap(map);
            Check(AccessTools.Field(typeof(CompSpaceWeapon), "layout").GetValue(weapon) == null,
                "map removal releases the weapon connection model");
            CheckWeaponConnectionLayout(universe, faction);
            CheckWeaponOwnershipReuse(universe, faction);
        }
        finally
        {
            harness.UnpatchAll(harness.Id); loadedExecutionMagazines.Clear();
            universe.objects.Clear(); Find.Maps.Clear(); universe.Controls.Reset();
            ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        }
    }

    static void CheckWeaponConnectionLayout(UniverseManager universe, Faction faction)
    {
        ThingDef oldEngine = ThingDefOf.GravEngine, oldLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        DeltaVDefOf.DeltaV_Weapon_MissileLauncher ??= TestBuildingDef("layoutFixtureLauncher");
        ThingDefOf.GravEngine ??= TestBuildingDef("layoutFixtureEngine");
        Map map = TestMap(1997);
        using var odyssey = new FixtureOdyssey();
        universe.objects.Add(new MapSpaceObject(1997, map, new SpacePose(Vector3.forward, 0f)));
        Set(universe, "objectByMap", null); Set(universe, "objectById", null);
        var ownership = SubstructureOwnership.For(map);
        var deck = FixtureFoundation("weaponFixtureDeck");
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon
            { acceleratorStarts = new List<IntVec3> { new IntVec3(0, 0, 1) } } };
        var body = AddTestBuilding(map, faction, 19970, TestBuildingDef("layoutLoader"), weapon);
        var loaderPower = new CompPowerTrader { parent = body, props = new CompProperties_Power { idlePowerDraw = 5 } };
        Set(loaderPower.props, "basePowerConsumption", 220f); AddFixtureComponents(body, loaderPower);
        var console = AddTestConsole(map, faction, 19979); console.shipId = 7; console.combat.weaponsArmed = true;
        MoveCombatFixture(body, new IntVec3(1, 0, 1));
        var segments = new List<CompSpaceRailgun>();
        for (int i = 0; i < 2; i++)
        {
            var segment = new CompSpaceRailgun { props = new CompProperties_SpaceRailgun { kineticEnergyBonus = 216000f } };
            var part = AddTestBuilding(map, faction, 19971 + i, TestBuildingDef("layoutSegment" + i), segment);
            MoveCombatFixture(part, new IntVec3(1, 0, 2 + i)); segments.Add(segment);
            var power = new CompPowerTrader { parent = part, props = new CompProperties_Power { idlePowerDraw = 5 } };
            Set(power.props, "basePowerConsumption", 30f); AddFixtureComponents(part, power);
        }
        for (int z = 1; z <= 3; z++)
        {
            var cell = new IntVec3(1, 0, z);
            SetFixtureFoundation(map, cell, deck, 7);
        }
        try
        {
            SpaceWeaponLayout initial = weapon.Layout;
            Check(initial.acceleration.segments.Count == 2 && initial.acceleration.segments[1] == segments[1],
                "real connection tracing builds the ordered two-segment layout");
            Check(ReferenceEquals(initial, weapon.Layout), "unchanged structure reuses the same connection layout");
            weapon.SyncPowerRequest();
            Check(loaderPower.PowerOutput == -220 && segments.All(s => s.parent.TryGetComp<CompPowerTrader>().PowerOutput == -30),
                "loader and connected segments use their own configured wartime demand");
            ownership.SetOwner(new[] { segments[1].parent.Position }, 8);
            Check(weapon.Layout.acceleration.segments.Count == 1, "ownership reassignment breaks the chain at the foreign segment");
            weapon.SyncPowerRequest();
            Check(segments[1].parent.TryGetComp<CompPowerTrader>().PowerOutput == -5
                && segments[0].parent.TryGetComp<CompPowerTrader>().PowerOutput == -30,
                "layout replacement idles detached segments without dropping shared segments to idle");
            ownership.SetOwner(new[] { segments[1].parent.Position }, 7);
            Check(weapon.Layout.acceleration.segments.Count == 2, "ownership restoration reconnects the chain");
            RemoveFixtureBuilding((Building)segments[0].parent, map);
            Check(weapon.Layout.acceleration.segments.Count == 0, "removing a middle segment disconnects the rest");
            PlaceFixtureBuilding((Building)segments[0].parent, map);
            Check(weapon.Layout.acceleration.segments.Count == 2, "rebuilding the missing segment reconnects the original order");
            weapon.ClearRuntimeModel();
            Check(segments.All(s => s.Binding == null && s.parent.TryGetComp<CompPowerTrader>().PowerOutput == -5),
                "runtime release clears the sole segment bindings and restores idle demand");
            Check(!ReferenceEquals(initial, weapon.Layout) && weapon.Layout.acceleration.segments.Count == 2,
                "runtime reset reconstructs connections from the current map without saved cache data");
        }
        finally { ThingDefOf.GravEngine = oldEngine; DeltaVDefOf.DeltaV_Weapon_MissileLauncher = oldLauncher; }
    }

    static void CheckWeaponOwnershipReuse(UniverseManager universe, Faction faction)
    {
        Map map = TestMap(1998);
        using var odyssey = new FixtureOdyssey();
        var frame = new MapSpaceObject(1998, map, new SpacePose(Vector3.forward, 0f));
        universe.objects.Add(frame); Set(universe, "objectByMap", null); Set(universe, "objectById", null);
        var console = AddTestConsole(map, faction, 19980);
        console.combat.weaponsArmed = true;
        var deck = FixtureFoundation("weaponFixtureDeck");
        var ownership = SubstructureOwnership.For(map);
        for (int z = 0; z < 5; z++) for (int x = 0; x < 5; x++)
        {
            var cell = new IntVec3(x, 0, z);
            SetFixtureFoundation(map, cell, deck, console.shipId);
        }
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon() };
        var def = TestBuildingDef("ownershipGun"); def.size = new IntVec2(2, 1);
        var body = AddTestBuilding(map, faction, 19981, def, weapon);
        var traverse = new CompSpaceWeaponTraverse { parent = body, props = new CompProperties_SpaceWeaponTraverse() };
        AddFixtureComponents(body, traverse);
        var patch = new Harmony("DeltaV.Regression.WeaponOwnership");
        patch.Patch(AccessTools.Method(typeof(ShipRegistry), "ShipOf"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountWeaponOwnership)));
        ownershipProbeThing = body; weaponOwnershipReads = 0;
        universe.Weapons.RegisterWeapon(weapon);
        try
        {
            var fire = new ShipFireControl(universe, console.Vessel.Identity);
            var intent = new ShipVolleyIntent(new Vector2(4, 4), false, false);
            weaponAimCommands = weaponAmmoReads = weaponEnergyReads = weaponTopologyReads = 0;
            for (int i = 0; i < 120; i++)
            {
                Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1);
                fire.TickVolley(console.Vessel, frame, intent);
            }
            Check(weaponOwnershipReads == 1 && weaponAimCommands == 120,
                "120 real aim-only volleys validate the full footprint once and dispatch every tick");
            Check(weaponAmmoReads == 0 && weaponEnergyReads == 0 && weaponTopologyReads == 0,
                "cached ownership remains independent of ammo, energy and connection queries");
            console.combat.SetWeaponGroupMask(SpaceWeaponSalvoSet.Main, 0);
            fire.TickVolley(console.Vessel, frame, intent);
            Check(weaponAimCommands == 120 && !weapon.GroupActive, "group disable takes effect without structure changes");
            weapon.salvoSet = SpaceWeaponSalvoSet.Secondary; weapon.groupNumber = 2;
            console.combat.SetWeaponGroupMask(SpaceWeaponSalvoSet.Secondary, 2);
            fire.TickVolley(console.Vessel, frame, intent);
            Check(weaponAimCommands == 121 && weapon.GroupActive && weaponOwnershipReads == 1,
                "changing salvo set and group immediately uses the new mask without footprint validation");

            var edge = new IntVec3(2, 0, 2);
            ownership.Assign(edge, 999);
            Check(!weapon.OwnerShip.IsValid, "foreign ownership on the second footprint cell invalidates the whole weapon");
            int invalidReads = weaponOwnershipReads;
            for (int i = 0; i < 120; i++) _ = weapon.OwnerShip;
            Check(weaponOwnershipReads == invalidReads, "invalid full-footprint conclusions are cached too");
            ownership.Assign(edge, console.shipId);
            Check(weapon.OwnerShip.Identity == console.Vessel.Identity, "restored ownership re-enables the weapon immediately");
            SetFixtureFoundation(map, edge, null, console.shipId);
            Check(!weapon.OwnerShip.IsValid, "destroyed supporting terrain invalidates full-footprint ownership");
            SetFixtureFoundation(map, edge, deck, console.shipId);
            Check(weapon.OwnerShip.IsValid, "rebuilt support restores ownership");
            int prior = weaponOwnershipReads;
            MoveCombatFixture(body, new IntVec3(2, 0, 3));
            Check(weapon.OwnerShip.IsValid && weaponOwnershipReads == prior + 1, "moving a weapon invalidates its cached footprint");
            Set(typeof(Thing), body, "rotationInt", Rot4.East);
            Check(weapon.OwnerShip.IsValid && weaponOwnershipReads == prior + 2, "rotation invalidates its cached footprint");
            console.isMain = false;
            universe.ShipStructure.NotifyConsole(console);
            Check(!weapon.OwnerShip.IsValid, "expired main-console handles cannot keep a cached weapon active");
            console.isMain = true;
            universe.ShipStructure.NotifyConsole(console);
            universe.Controls.ReconcileOwner(console.shipId);
            Check(weapon.OwnerShip.IsValid, "main-console reconciliation invalidates cached unowned results");
            universe.Weapons.ForgetMap(map);
            Check(AccessTools.Field(typeof(CompSpaceWeapon), "cachedOwner").GetValue(weapon) == null,
                "map release drops cached ship and map references");
            Check(weapon.OwnerShip.IsValid, "runtime reset reconstructs current ownership");
        }
        finally { ownershipProbeThing = null; patch.UnpatchAll(patch.Id); }
    }

}

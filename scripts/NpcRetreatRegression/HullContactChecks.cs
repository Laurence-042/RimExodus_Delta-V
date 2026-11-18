using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

internal static partial class Program
{
    static bool HullContactPocketTile(ref PlanetTile __result)
    { __result = PlanetTile.Invalid; return false; }

    static Map hullFocusedMap;
    static readonly List<(SoundDef sound, SoundInfo info)> hullSounds = new();
    static bool HullFocusedMap(ref Map __result) { __result = hullFocusedMap; return false; }
    static bool HullDrawingMap(ref bool __result) { __result = true; return false; }
    static bool HullSoundEndpoint(SoundDef soundDef, SoundInfo info)
    { hullSounds.Add((soundDef, info)); return false; }
    // Replace only the Unity camera read; production sound routing and throttling still execute.
    static Camera HullCamera() => null;
    static Transform HullCameraTransform(Component component) => null;
    static Vector3 HullCameraPosition(Transform transform) => Vector3.zero;
    static IEnumerable<CodeInstruction> HullSoundCamera(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            string replacement = instruction.Calls(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Camera))) ? nameof(HullCamera)
                : instruction.Calls(AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform))) ? nameof(HullCameraTransform)
                : instruction.Calls(AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position))) ? nameof(HullCameraPosition) : null;
            if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Program), replacement); }
            yield return instruction;
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void HullPlayCombatSound(SpaceProjectileService service, SoundDef mapSound, SoundDef shellSound, SpaceObject owner,
        Vector2 local, Vector3 position, int spacing, float volume, bool combatSetting, float shellPitch)
        => throw new NotSupportedException("Production audio IL has not been bound.");
    static IEnumerable<CodeInstruction> HullAudioBoundary(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if (instruction.Calls(AccessTools.Method(typeof(SpaceProjectileService), "PlayCombatSound")))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Program), nameof(HullPlayCombatSound)); }
            yield return instruction;
        }
    }

    // Only Unity scene endpoints are replaced. Attached rounds, transforms, grid
    // sweeps, registry lookup and both TakeDamage calls execute production code.
    readonly record struct HullRoundView(object record, IntVec3 hullCell, SpaceObject container);
    sealed class HullRoundFixture : IDisposable
    {
        readonly UniverseManager universe;
        readonly Harmony patches = new("DeltaV.Regression.HullRounds");
        readonly EngagementFixturePatch geometry = new();
        readonly ThingDef oldRound = DeltaVDefOf.DeltaV_Projectile_HullContact;
        readonly DamageDef oldDamage = DamageDefOf.Bullet;
        readonly ThingDef oldConsole = ThingDefOf.PilotConsole;
        readonly ThingDef oldEngine = ThingDefOf.GravEngine;
        readonly ThingDef oldLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        readonly object oldFactions = AccessTools.Field(typeof(World), "factionManager").GetValue(Find.World);
        readonly bool oldDrag = DeltaVMod.CurrentSettings.passiveDrag;
        readonly bool oldSound = DeltaVMod.CurrentSettings.spaceImpactSound;
        readonly SoundDef nativeSound = new() { defName = "HullNativeImpact" };
        readonly List<SoundDef> sounds = new();
        readonly List<SpaceImpactEffectDef> effects = new();
        internal readonly List<SpaceObject> bodies = new();
        internal readonly List<Map> maps = new();
        int id = 52100;
        internal HullRoundFixture(UniverseManager universe, Faction faction)
        {
            this.universe = universe;
            patches.Patch(typeof(ModsConfig).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patches.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)),
                prefix: new HarmonyMethod(typeof(Program), nameof(HullContactPocketTile)));
            patches.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BattleNotFlammable)));
            foreach (var method in new[] {
                AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"),
                AccessTools.Method(typeof(Faction), "Notify_BuildingTookDamage"),
                AccessTools.Method(typeof(Building), "PostApplyDamage"),
                AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed") })
                patches.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patches.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap)), prefix: new HarmonyMethod(typeof(Program), nameof(HullFocusedMap)));
            patches.Patch(AccessTools.PropertyGetter(typeof(WorldRendererUtility), nameof(WorldRendererUtility.DrawingMap)), prefix: new HarmonyMethod(typeof(Program), nameof(HullDrawingMap)));
            patches.Patch(AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot)), prefix: new HarmonyMethod(typeof(Program), nameof(HullSoundEndpoint)));
            Harmony.ReversePatch(AccessTools.Method(typeof(SpaceProjectileService), "PlayCombatSound"),
                new HarmonyMethod(typeof(Program), nameof(HullPlayCombatSound)), AccessTools.Method(typeof(Program), nameof(HullSoundCamera)));
            patches.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx"), transpiler: new HarmonyMethod(typeof(Program), nameof(HullAudioBoundary)));
            patches.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"), prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)));
            patches.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly.GetType("DeltaV.Patch_TerrainGrid_RemoveFoundation_Ownership")).Patch();
            ThingDefOf.GravEngine = TestBuildingDef("HullRoundEngine");
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = TestBuildingDef("HullRoundLauncher");
            DamageDefOf.Bullet = new DamageDef { defName = "HullRoundDamage", workerClass = typeof(DamageWorker), harmsHealth = true,
                impactSoundType = new ImpactSoundTypeDef { defName = "HullRoundImpact" } };
            ThingDefOf.PilotConsole = TestBuildingDef("HullRoundConsole");
            ThingDefOf.PilotConsole.useHitPoints = ThingDefOf.PilotConsole.destroyable = true;
            var factions = (FactionManager)Raw(typeof(FactionManager));
            Set(factions, "ofPlayer", faction); Set(Find.World, "factionManager", factions);
            DeltaVMod.CurrentSettings.passiveDrag = false;
            var node = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml").Root.Elements("ThingDef")
                .Single(e => (string)e.Element("defName") == "DeltaV_Projectile_HullContact");
            var props = node.Element("comps").Elements().Single();
            var def = TestBuildingDef((string)node.Element("defName"));
            SoundDef Sound(string name)
            {
                Check(XDocument.Load("1.6/Defs/SoundDefs/DeltaV_SpaceCombatSounds.xml").Root.Elements()
                    .Any(e => (string)e.Element("defName") == name), "hull sound references an existing sound: " + name);
                var sound = new SoundDef { defName = name }; DefDatabase<SoundDef>.Add(sound); sounds.Add(sound); return sound;
            }
            def.comps.Add(new CompProperties_SpaceProjectile {
                hasMap = (bool)props.Element("hasMap"), collisionRadius = (float)props.Element("collisionRadius"),
                ignoresShields = (bool)props.Element("ignoresShields"),
                impactSoundMap = Sound((string)props.Element("impactSoundMap")),
                impactSoundShell = Sound((string)props.Element("impactSoundShell")) });
            DeltaVDefOf.DeltaV_Projectile_HullContact = def;
            foreach (var effect in XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
                .Where(e => e.Element("projectileDefs")?.Value == def.defName))
            {
                var part = effect.Element("defenderEffect");
                var rule = new SpaceImpactEffectDef { defName = (string)effect.Element("defName"),
                    projectileDefs = new List<ThingDef> { def }, targetsSubstructure = (bool?)effect.Element("targetsSubstructure") ?? false,
                    buildingHitChance = (float?)effect.Element("buildingHitChance") ?? -1,
                    substructureHitChance = (float?)effect.Element("substructureHitChance") ?? 0,
                    defenderEffect = new ImpactOutcomePart { kind = Enum.Parse<ImpactEffectKind>((string)part.Element("kind")),
                        damageDef = DamageDefOf.Bullet, damage = (float)part.Element("damage"), referenceSpeed = (float)part.Element("referenceSpeed"),
                        speedExponent = (float?)part.Element("speedExponent") ?? 0, kineticMassScaling = (bool?)part.Element("kineticMassScaling") ?? false, penetrates = (bool?)part.Element("penetrates") ?? false, incidenceMinFactor = (float)part.Element("incidenceMinFactor"),
                        armorPenetration = (float)part.Element("armorPenetration"), substructureBreakThreshold = (float)part.Element("substructureBreakThreshold") },
                    attackerEffect = new ImpactOutcomePart { kind = Enum.Parse<ImpactEffectKind>((string)effect.Element("attackerEffect").Element("kind")) } };
                effects.Add(rule); DefDatabase<SpaceImpactEffectDef>.Add(rule);
            }
            SpaceImpactEffects.ResetCache();
        }
        internal (SpaceObject body, BattleBuilding wall, ShipBase ship, ShipController control) Craft(
            Faction faction, float east, float north, Vector2 velocity, bool avoidance, int hp = 300, int size = 1, bool motion = false)
        {
            var map = TestMap(++id, 13);
            Array.Fill(map.terrainGrid.topGrid, map.terrainGrid.topGrid[0]);
            map.roofGrid = new RoofGrid(map); map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
            maps.Add(map);
            var def = TestBuildingDef("HullRoundCraft" + id);
            def.size = new IntVec2(size, size); def.useHitPoints = def.destroyable = true; def.building.isWall = true; def.fillPercent = 1;
            def.soundImpactDefault = nativeSound;
            var comp = new CompSpaceShuttle { props = new CompProperties_SpaceShuttle { canBoard = true } };
            var wall = PlaceHullFixtureBuilding(map, new IntVec3(6, 0, 6), def, hp, ++id, comp);
            Set(typeof(Thing), wall, "factionInt", faction);
            ShipBase ship = comp.Vessel;
            var pose = new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(east, north)), 0) { velocity = velocity };
            SpaceObject body = motion ? new ManeuverMotionObject(++id, map, pose) { member = ship } : new MapSpaceObject(++id, map, pose);
            universe.RegisterObject(body); bodies.Add(body);
            var control = universe.Controls.For(ship);
            control.SetCollisionAvoidanceEnabled(avoidance);
            return (body, wall, ship, control);
        }
        internal HullRoundView[] Rounds() =>
            ((System.Collections.IEnumerable)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(universe.Projectiles))
                .Cast<object>().Where(p => p.GetType().GetField("hullSource").GetValue(p) != null && !(bool)p.GetType().GetField("dead").GetValue(p))
                .Select(p => new HullRoundView(p, (IntVec3)p.GetType().GetField("hullCell").GetValue(p),
                    (SpaceObject)p.GetType().GetProperty("container").GetValue(p))).ToArray();
        internal void Prepare() { Set(Find.TickManager, "ticksGameInt", Find.TickManager.TicksGame + 1); universe.Projectiles.Tick(); }
        internal void Step()
        {
            Prepare();
            foreach (var body in universe.objects.ToArray()) body.AdvanceDrift(1f / 60f);
            universe.Projectiles.ResolveCollisions(); Flush();
        }
        internal void Flush() { var actions = Actions(universe).ToArray(); Actions(universe).Clear(); foreach (var action in actions) action(); }
        internal int Flashes => ((System.Collections.ICollection)AccessTools.Field(typeof(SpaceProjectileService), "impacts").GetValue(universe.Projectiles)).Count;
        internal int Particles => ((System.Collections.ICollection)AccessTools.Field(typeof(SpaceProjectileService), "particles").GetValue(universe.Projectiles)).Count;
        internal void Clear()
        {
            universe.Projectiles.ResetFlow(); Flush();
            foreach (var body in bodies) universe.UnregisterObject(body);
            foreach (var map in maps) { SpaceCollision.Forget(map); Find.Maps.Remove(map); }
            bodies.Clear(); maps.Clear(); ShipRegistry.ClearCaches();
            hullFocusedMap = null; hullSounds.Clear();
        }
        public void Dispose()
        {
            Clear();
            foreach (var effect in effects) DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading.Remove(effect);
            foreach (var sound in sounds) DefDatabase<SoundDef>.AllDefsListForReading.Remove(sound);
            SpaceImpactEffects.ResetCache();
            DeltaVDefOf.DeltaV_Projectile_HullContact = oldRound; DamageDefOf.Bullet = oldDamage;
            ThingDefOf.PilotConsole = oldConsole; ThingDefOf.GravEngine = oldEngine; DeltaVDefOf.DeltaV_Weapon_MissileLauncher = oldLauncher; Set(Find.World, "factionManager", oldFactions);
            DeltaVMod.CurrentSettings.passiveDrag = oldDrag;
            DeltaVMod.CurrentSettings.spaceImpactSound = oldSound;
            patches.UnpatchAll(patches.Id); geometry.Dispose();
        }
    }

    static void CheckHullContacts(UniverseManager universe, Faction faction)
    {
        using var f = new HullRoundFixture(universe, faction);
        foreach (bool leftOn in new[] { false, true })
        foreach (bool rightOn in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        {
            var a = f.Craft(faction, 0, 0, reverse ? Vector2.zero : new Vector2(300, 0), leftOn);
            var b = f.Craft(faction, 3, 0, reverse ? new Vector2(-300, 0) : Vector2.zero, rightOn);
            f.Step();
            bool hit = !leftOn || !rightOn;
            Check(hit ? a.wall.Destroyed && b.wall.Destroyed : a.wall.HitPoints == 300 && b.wall.HitPoints == 300,
                $"attached hull round: {leftOn}/{rightOn}, reverse={reverse}, symmetric damage={300-a.wall.HitPoints}/{300-b.wall.HitPoints}");
            Check(Mathf.Abs(a.body.pose.velocity.x + b.body.pose.velocity.x - (reverse ? -300 : 300)) < .02f
                && (!hit || Mathf.Abs(a.body.pose.velocity.x - (reverse ? 0 : 300)) > 1),
                "shared collision outcome transfers equal and opposite momentum to the actual carriers");
            f.Clear();
        }
        var weak = f.Craft(faction, 0, 0, new Vector2(300,0), false, hp: 40);
        var strong = f.Craft(faction, 3, 0, Vector2.zero, true);
        f.Step();
        Check(weak.wall.Destroyed && strong.wall.HitPoints == 260,
            "source hull destruction caps both sides to the same absorbed hit points");
        f.Prepare(); Check(f.Rounds().Length == 0, "destroyed source cannot replenish a collision projectile"); f.Clear();
        var slow = f.Craft(faction, 0,0,new Vector2(.4f,0),false);
        var scrape = f.Craft(faction,.99f,0,Vector2.zero,true);
        f.Step(); Check(slow.wall.HitPoints == 300 && scrape.wall.HitPoints == 300
            && Mathf.Abs(slow.body.pose.velocity.x-.2f)<.001f && Mathf.Abs(scrape.body.pose.velocity.x-.2f)<.001f,
            "sub-threshold contact transfers momentum without hull damage"); f.Clear();
        var deckTarget = f.Craft(faction,3,0,Vector2.zero,true);
        var bare = new IntVec3(6,0,6);
        RemoveFixtureBuilding(deckTarget.wall,deckTarget.body.map);
        var bareDeck = (TerrainDef)Raw(typeof(TerrainDef)); bareDeck.defName = "HullRoundBareDeck";
        bareDeck.isFoundation = true; bareDeck.tags = new List<string> { "Substructure" };
        deckTarget.body.map.terrainGrid.foundationGrid[deckTarget.body.map.cellIndices.CellToIndex(bare)] = bareDeck;
        SubstructureOwnership.BumpStructureVersion(deckTarget.body.map);
        var striker = f.Craft(faction,0,0,new Vector2(900,0),false);
        f.Step();
        Check(striker.wall.Destroyed && !SubstructureOwnership.HasFoundation(deckTarget.body.map,bare),
            "attached round resolves bare foundation through the existing foundation destruction chain"); f.Clear();
        CheckHullColumns(universe, faction, f);
        CheckContactMomentum(universe, faction, f);
        CheckHullScrapes(f, faction);
        var source = f.Craft(faction, 0, 0, new Vector2(12, 7), false, size: 3);
        source.body.pose.angularVelocity = .7f;
        f.Prepare();
        var rounds = f.Rounds();
        Check(rounds.Length == 8, "only actual exterior cells generate attached invisible projectiles");
        Check(universe.Projectiles.ActiveCount == 0, "hull rounds do not count as fired weapons");
        foreach (var body in universe.objects.ToArray()) body.AdvanceDrift(1f / 60f);
        universe.Projectiles.ResolveCollisions();
        Check(f.Rounds().All(p => {
            var local = new Vector2(p.hullCell.x + .5f, p.hullCell.z + .5f) - SpaceTransform.ShipLocalOrigin(source.body.map);
            var expected = SpaceTransform.RigidPointPose(source.body.pose, local, 0);
            return OrbitSphere.ArcCells(expected.position, p.container.pose.position) < .002f
                && (expected.velocity - p.container.pose.velocity).magnitude < .002f;
        }), "round position and point velocity follow actual translation and rotation without free flight");
        source.control.SetCollisionAvoidanceEnabled(true);
        Check(f.Rounds().Length == 0, "enabling avoidance immediately retires attached rounds");
        f.Flush();
        source.control.SetCollisionAvoidanceEnabled(false); f.Prepare();
        Check(f.Rounds().Length == 8, "disabling avoidance recreates the actual perimeter");
        source.body.projectileRoleOverride = true;
        universe.Projectiles.ResolveCollisions();
        Check(f.Rounds().Length == 0, "intrusion takeover immediately excludes hull rounds from the shared sweep");
        source.body.projectileRoleOverride = false; f.Flush(); f.Prepare();
        Check(f.Rounds().Length == 8, "ending intrusion returns hull maintenance to the existing avoidance setting");
        source.wall.Kill(); f.Prepare();
        Check(f.Rounds().Length == 0, "destroyed hull removes all attached rounds");
        f.Clear();

        // A real foundation control domain tests local structure edits and retained records.
        var deck = f.Craft(faction, 0, 0, Vector2.zero, true);
        RemoveFixtureBuilding(deck.wall, deck.body.map);
        var console = AddTestConsole(deck.body.map, faction, 52900);
        var foundation = (TerrainDef)Raw(typeof(TerrainDef)); foundation.defName = "HullRoundDeck"; foundation.isFoundation = true; foundation.tags = new List<string> { "Substructure" };
        var cells = new HashSet<IntVec3>();
        for (int x = 0; x < 3; x++) for (int z = 1; z < 4; z++) cells.Add(new IntVec3(x, 0, z));
        foreach (var cell in cells) deck.body.map.terrainGrid.foundationGrid[deck.body.map.cellIndices.CellToIndex(cell)] = foundation;
        var owners = new int[deck.body.map.cellIndices.NumGridCells];
        foreach (var cell in cells) owners[deck.body.map.cellIndices.CellToIndex(cell)] = console.shipId;
        Set(SubstructureOwnership.TryGet(deck.body.map), "ownerGrid", owners);
        foreach (var cell in cells) universe.NotifyFoundationChanged(deck.body.map, cell);
        SubstructureOwnership.BumpStructureVersion(deck.body.map);
        var pilot = (PlayerShipController)universe.Controls.For(console.Vessel);
        pilot.SetCollisionAvoidanceEnabled(false); f.Prepare();
        var before = f.Rounds().ToDictionary(p => p.hullCell);
        Check(before.Count == 8 && !before.ContainsKey(new IntVec3(1,0,2)), "foundation interior has no round");
        var breach = new IntVec3(1,0,3);
        deck.body.map.terrainGrid.foundationGrid[deck.body.map.cellIndices.CellToIndex(breach)] = null;
        universe.NotifyFoundationChanged(deck.body.map, breach);
        SubstructureOwnership.BumpStructureVersion(deck.body.map, CellRect.SingleCell(breach)); f.Prepare();
        var after = f.Rounds().ToDictionary(p => p.hullCell);
        Check(after.Count == 8 && !after.ContainsKey(breach) && after.ContainsKey(new IntVec3(1,0,2)),
            "local breach removes its round and exposes the neighboring interior cell");
        Check(before.Where(p => p.Key != breach).All(p => ReferenceEquals(p.Value.record, after[p.Key].record)),
            "unaffected perimeter rounds survive incremental damage updates");
        deck.body.map.terrainGrid.foundationGrid[deck.body.map.cellIndices.CellToIndex(breach)] = foundation;
        universe.NotifyFoundationChanged(deck.body.map, breach);
        SubstructureOwnership.BumpStructureVersion(deck.body.map, CellRect.SingleCell(breach)); f.Prepare();
        Check(f.Rounds().Length == 8 && f.Rounds().Any(p => p.hullCell == breach)
            && !f.Rounds().Any(p => p.hullCell == new IntVec3(1,0,2)), "repair restores exterior rounds and removes the re-covered interior round");
        foreach (int x in new[] { 3, 4 })
        {
            var cell = new IntVec3(x,0,3);
            int index = deck.body.map.cellIndices.CellToIndex(cell);
            deck.body.map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
            universe.NotifyFoundationChanged(deck.body.map,cell);
            SubstructureOwnership.BumpStructureVersion(deck.body.map,CellRect.SingleCell(cell));
        }
        f.Prepare(); Check(f.Rounds().Any(p => p.hullCell.x == 4), "newly connected structure adds exterior rounds");
        var neck = new IntVec3(2,0,3);
        deck.body.map.terrainGrid.foundationGrid[deck.body.map.cellIndices.CellToIndex(neck)] = null;
        universe.NotifyFoundationChanged(deck.body.map,neck);
        SubstructureOwnership.BumpStructureVersion(deck.body.map,CellRect.SingleCell(neck)); f.Prepare();
        Check(!f.Rounds().Any(p => p.hullCell.x >= 3), "disconnected fragments lose rounds belonging to the original control domain");
        CheckHullAvoidanceMotion(universe, faction, f);
    }

    static TerrainDef HullDeck(Map map, params IntVec3[] cells)
    {
        var def = (TerrainDef)Raw(typeof(TerrainDef)); def.defName = "HullBudgetDeck";
        def.isFoundation = true; def.tags = new List<string> { "Substructure" };
        foreach (var cell in cells)
        {
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = def;
            Current.Game.GetComponent<UniverseManager>().NotifyFoundationChanged(map,cell);
            SubstructureOwnership.BumpStructureVersion(map,CellRect.SingleCell(cell));
        }
        return def;
    }

    static void CheckHullColumns(UniverseManager universe, Faction faction, HullRoundFixture f)
    {
        var cell = new IntVec3(6,0,6);
        foreach (bool reverse in new[] { false,true })
        {
            var weak = f.Craft(faction,0,0,reverse ? Vector2.zero : new Vector2(600,0),!reverse,hp:100);
            var strong = f.Craft(faction,3,0,reverse ? new Vector2(-600,0) : Vector2.zero,reverse,hp:1000);
            HullDeck(weak.body.map,cell); HullDeck(strong.body.map,cell);
            f.Step();
            Check(weak.wall.Destroyed && !SubstructureOwnership.HasFoundation(weak.body.map,cell)
                && strong.wall.HitPoints == 600 && SubstructureOwnership.HasFoundation(strong.body.map,cell),
                $"wall plus foundation consumes 400 on each side without trading unequal walls, reverse={reverse}, strong={strong.wall.HitPoints}");
            f.Clear();
        }
        int broken = 0, survived = 0;
        Rand.PushState(93217);
        try
        {
            for (int trial = 0; trial < 48; trial++)
            {
                var weak = f.Craft(faction,0,0,new Vector2(600,0),false,hp:100);
                var strong = f.Craft(faction,3,0,Vector2.zero,true,hp:200);
                HullDeck(weak.body.map,cell); HullDeck(strong.body.map,cell);
                f.Step();
                Check(weak.wall.Destroyed && strong.wall.Destroyed && !SubstructureOwnership.HasFoundation(weak.body.map,cell),
                    "the weaker wall and deck are fully consumed while the stronger wall takes the same budget");
                if (SubstructureOwnership.HasFoundation(strong.body.map,cell)) survived++; else broken++;
                f.Clear();
            }
        }
        finally { Rand.PopState(); }
        Check(broken > 0 && survived > 0, $"only residual 200/300 damage rolls for the stronger deck: broken={broken}, survived={survived}");

        // A closed 3x3 real control domain: the center starts without an attached round.
        var attacker = f.Craft(faction,0,0,new Vector2(600,0),true);
        RemoveFixtureBuilding(attacker.wall,attacker.body.map);
        var main = new CompDeltaVPilotConsole {isMain=true,shipId=53500};
        var helm = PlaceHullFixtureBuilding(attacker.body.map,new IntVec3(5,0,5),ThingDefOf.PilotConsole,10000,53500,main);
        Set(typeof(Thing),helm,"factionInt",faction);
        var cells = (from x in Enumerable.Range(5,3) from z in Enumerable.Range(5,3) select new IntVec3(x,0,z)).ToArray();
        HullDeck(attacker.body.map,cells);
        var owners = new int[attacker.body.map.cellIndices.NumGridCells];
        foreach(var c in cells) owners[attacker.body.map.cellIndices.CellToIndex(c)] = main.shipId;
        Set(SubstructureOwnership.TryGet(attacker.body.map),"ownerGrid",owners);
        foreach(var c in cells) universe.NotifyFoundationChanged(attacker.body.map,c);
        SubstructureOwnership.BumpStructureVersion(attacker.body.map);
        var wall = TestBuildingDef("HullBudgetWall"); wall.useHitPoints=wall.destroyable=true; wall.building.isWall=true; wall.fillPercent=1;
        var front = PlaceHullFixtureBuilding(attacker.body.map,new IntVec3(7,0,6),wall,100,53501);
        var inside = PlaceHullFixtureBuilding(attacker.body.map,cell,wall,100,53502);
        universe.Controls.For(main.Vessel).SetCollisionAvoidanceEnabled(false);
        var target = f.Craft(faction,4,0,Vector2.zero,true,hp:10000);
        HullDeck(target.body.map,cell);
        f.Prepare(); int tick = Find.TickManager.TicksGame;
        Check(!f.Rounds().Any(p=>p.hullCell==cell && p.container != null), "interior collision round is absent before the breach");
        foreach(var body in universe.objects.ToArray()) body.AdvanceDrift(1f/60f);
        universe.Projectiles.ResolveCollisions();
        Check(front.Destroyed && inside.Destroyed && !SubstructureOwnership.HasFoundation(attacker.body.map,cell)
            && target.wall.HitPoints < 9600 && Find.TickManager.TicksGame==tick,
            $"same tick completes deck events, incremental connectivity and newly exposed round impact: front={front.Destroyed}, inside={inside.Destroyed}, target={target.wall.HitPoints}");
        f.Clear();
    }

    static void CheckContactMomentum(UniverseManager universe, Faction faction, HullRoundFixture f)
    {
        var a=f.Craft(faction,0,0,new Vector2(20,0),false,hp:10000);
        var b=f.Craft(faction,1.1f,0,Vector2.zero,true,hp:10000);
        f.Step();
        Check(Mathf.Abs(a.body.pose.velocity.x-10)<.02f && Mathf.Abs(b.body.pose.velocity.x-10)<.02f,
            $"intact equal-mass head-on contact stops relative closure: {a.body.pose.velocity.x}/{b.body.pose.velocity.x}");
        int hp = a.wall.HitPoints; float separation=OrbitSphere.ArcCells(a.body.pose.position,b.body.pose.position);
        for(int i=0;i<5;i++) f.Step();
        Check(a.wall.HitPoints==hp && separation>.5f && OrbitSphere.ArcCells(a.body.pose.position,b.body.pose.position)>=separation-.002f,
            "intact surfaces stop further penetration and repeated damage after closure stops");
        f.Clear();
        foreach(float retained in new[]{0f,.25f,1f})
        {
            a=f.Craft(faction,0,-2,new Vector2(20,3),true);
            b=f.Craft(faction,4,1,new Vector2(-2,3),true);
            var ma=new ShipPhysics.MassReport {mass=2,inertia=7}; var mb=new ShipPhysics.MassReport {mass=5,inertia=11};
            var point=OrbitSphere.Move(Vector3.forward,new Vector2(2,0));
            Vector2 Momentum() => SpaceTransform.TransportVelocity(a.body.pose,point)*ma.mass + SpaceTransform.TransportVelocity(b.body.pose,point)*mb.mass;
            float Energy()=> .5f*ma.mass*a.body.pose.velocity.sqrMagnitude + .5f*mb.mass*b.body.pose.velocity.sqrMagnitude
                + .5f*ma.inertia*a.body.pose.angularVelocity*a.body.pose.angularVelocity + .5f*mb.inertia*b.body.pose.angularVelocity*b.body.pose.angularVelocity;
            float AngularMomentum()
            {
                Vector2 ra=OrbitSphere.Offset(point,a.body.pose.position), rb=OrbitSphere.Offset(point,b.body.pose.position);
                Vector2 pa=SpaceTransform.TransportVelocity(a.body.pose,point)*ma.mass, pb=SpaceTransform.TransportVelocity(b.body.pose,point)*mb.mass;
                return ma.inertia*a.body.pose.angularVelocity + mb.inertia*b.body.pose.angularVelocity
                    + ra.x*pa.y-ra.y*pa.x + rb.x*pb.y-rb.y*pb.x;
            }
            var before=Momentum(); float energy=Energy(), angular=AngularMomentum();
            ShipPhysics.ApplyHullContactImpulse(a.body,ma,b.body,mb,point,Vector2.left,out float absorbed,retained);
            Check((Momentum()-before).magnitude<.01f && Mathf.Abs(AngularMomentum()-angular)<.02f
                && Mathf.Abs(Energy()-(energy-absorbed*(1-retained)))<.05f && Energy()<=energy+.02f
                && (retained==1 || Mathf.Abs(a.body.pose.angularVelocity)+Mathf.Abs(b.body.pose.angularVelocity)>.01f),
                $"existing off-center impulse conserves momentum without adding energy, retained={retained}");
            f.Clear();
        }
    }

    static void CheckHullScrapes(HullRoundFixture f, Faction faction)
    {
        foreach (float angle in new[] { 0f, .61f })
        foreach (int direction in new[] { -1, 1 })
        foreach (bool sourceOn in new[] { false, true })
        foreach (float closing in new[] { 0f, .4f })
        {
            var a = f.Craft(faction, 0, 0, Vector2.zero, sourceOn, hp:10000);
            var b = f.Craft(faction, 0, 0, Vector2.zero, !sourceOn, hp:10000);
            RemoveFixtureBuilding(b.wall,b.body.map);
            b.wall.def.size = new IntVec2(9,1);
            PlaceFixtureBuilding(b.wall,b.body.map);
            var offset = SpaceTransform.Rotate(new Vector2(-direction*2f, -1f),angle);
            a.body.pose = new SpacePose(OrbitSphere.Move(Vector3.forward,offset),angle)
                { velocity = SpaceTransform.Rotate(new Vector2(direction*12f,closing),angle) };
            b.body.pose.angle = angle;
            Vector2 tangent = SpaceTransform.Rotate(new Vector2(direction,0),angle);
            for (int i=0;i<15;i++) f.Step();
            Vector2 relative = SpaceTransform.TransportVelocity(a.body.pose,Vector3.forward)
                - SpaceTransform.TransportVelocity(b.body.pose,Vector3.forward);
            Check(Mathf.Abs(Vector2.Dot(relative,tangent)-12f)<.05f && a.wall.HitPoints==10000 && b.wall.HitPoints==10000,
                $"real multi-tick side scrape preserves tangent across grid seams: angle={angle}, direction={direction}, sourceOn={sourceOn}, closing={closing}, speed={Vector2.Dot(relative,tangent)}");
            Check(f.Flashes==0 && f.Particles==0 && (closing>0f || hullSounds.Count==0),
                "side scrape produces no damage flashes or smoke; tangent-only contact stays silent");
            f.Clear();
        }
        var slow = f.Craft(faction,0,0,new Vector2(.4f,0),false);
        var touching = f.Craft(faction,.99f,0,Vector2.zero,true);
        hullFocusedMap = touching.body.map;
        DeltaVMod.CurrentSettings.spaceImpactSound = true;
        f.Step(); int sounds = hullSounds.Count;
        Check(f.Flashes==0 && f.Particles==0 && sounds==1,
            "real low-speed impulse has one sound and no destructive flash");
        for(int i=0;i<12;i++) f.Step();
        Check(hullSounds.Count==sounds && f.Flashes==0,
            "replenished rounds do not repeat feedback on co-moving contact");
        slow.body.pose.velocity = new Vector2(-2,0);
        for(int i=0;i<5;i++) f.Step();
        Check(hullSounds.Count==sounds && f.Flashes==0,"separating contact creates no fresh feedback");
        f.Clear();
        var corner = f.Craft(faction,-.85f,-.85f,new Vector2(12,12),false,hp:10000);
        var cornerTarget = f.Craft(faction,0,0,Vector2.zero,true,hp:10000);
        f.Step();
        Check(Mathf.Abs(corner.body.pose.velocity.x-corner.body.pose.velocity.y)<.02f
            && Mathf.Abs(cornerTarget.body.pose.velocity.x-cornerTarget.body.pose.velocity.y)<.02f
            && corner.body.pose.velocity.x<11f,
            "actual corner hit uses the geometric diagonal normal rather than a velocity-axis normal");
        f.Clear();
        foreach(bool enabled in new[]{false,true})
        foreach(bool ownSource in new[]{false,true})
        foreach(bool focusSource in new[]{false,true})
        {
            var a=f.Craft(faction,0,0,new Vector2(20,0),!ownSource,hp:10000);
            var b=f.Craft(faction,1.1f,0,Vector2.zero,ownSource,hp:10000);
            hullFocusedMap = focusSource ? a.body.map : b.body.map;
            DeltaVMod.CurrentSettings.spaceImpactSound=enabled;
            f.Step();
            var natives=hullSounds.Where(s=>s.sound.defName=="HullNativeImpact").ToArray();
            var extra=hullSounds.Where(s=>s.sound.defName.StartsWith("DeltaV_ImpactHit")).ToArray();
            Check(a.wall.HitPoints<10000 && b.wall.HitPoints<10000 && natives.Length==2
                && natives.Any(s=>s.info.Maker.Map==a.body.map) && natives.Any(s=>s.info.Maker.Map==b.body.map),
                $"both TakeDamage sound paths survive the supplemental setting: enabled={enabled}, ownSource={ownSource}, focusSource={focusSource}");
            bool focusedDefender = ownSource != focusSource;
            Check(extra.Length==(enabled?1:0) && (!enabled || extra[0].sound.defName==
                (focusedDefender?"DeltaV_ImpactHitMap":"DeltaV_ImpactHitShell")),
                "actual hull impact uses the focused-map or shell sound route, and honors the supplemental switch");
            Check(f.Flashes>0 && f.Particles>0,"real damaging impact registers production flashes and particles");
            f.Clear();
        }
    }

    static BattleBuilding PlaceHullFixtureBuilding(Map map, IntVec3 cell, ThingDef def, int hp, int id, ThingComp comp = null)
    {
        var building = (BattleBuilding)Raw(typeof(BattleBuilding)); building.def = def; building.thingIDNumber = id; building.HitPoints = hp;
        Set(typeof(Thing), building, "positionInt", cell); comp ??= new EmptyComp(); comp.parent = building;
        Set(typeof(ThingWithComps), building, "comps", new List<ThingComp> { comp }); PlaceFixtureBuilding(building, map); return building;
    }
}

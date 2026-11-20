using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

internal static partial class Program
{
    // Only scene notifications and destruction bookkeeping are adapted. TakeDamage,
    // DamageWorker, fire control, shot preparation, collision and guidance stay live.
    sealed class BattleBuilding : Building
    {
        public override int HitPoints
        {
            get => (int)AccessTools.Field(typeof(Thing), "hitPointsInt").GetValue(this);
            set => Set(typeof(Thing), this, "hitPointsInt", value);
        }
        public override void Kill(DamageInfo? info = null, Hediff culprit = null)
        {
            var map = Map;
            map.thingGrid.Deregister(this);
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
            SubstructureOwnership.BumpStructureVersion();
        }
    }
    static bool BattleNotFlammable(ref bool __result) { __result = false; return false; }
    static bool BattleSceneOnly() => false;
    static bool BattleRoof(IntVec3 c, RoofDef def, Map ___map, RoofDef[] ___roofGrid)
    { ___roofGrid[___map.cellIndices.CellToIndex(c)] = def; return false; }

    static void CheckEngagementBattle(UniverseManager universe, Faction player, Faction enemy)
    {
        using var fixture = new EngagementFixturePatch();
        var patch = new Harmony("DeltaV.Regression.EngagementBattle");
        patch.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(typeof(CompRefuelable).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)),
            prefix: new HarmonyMethod(typeof(Program), nameof(HullContactPocketTile)));
        foreach (var method in new[] {
            AccessTools.Method(typeof(ImpactSoundUtility), "PlayImpactSound"),
            AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"),
            AccessTools.Method(typeof(Faction), "Notify_BuildingTookDamage"),
            AccessTools.Method(typeof(Building), "PostApplyDamage"),
            AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
            AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed"),
            AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx"),
            AccessTools.Method(typeof(SpaceProjectileService), "RegisterShotVisuals"),
            AccessTools.Method(typeof(SpaceProjectileService), "RecordBeamSegment") })
            patch.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleNotFlammable)));
        patch.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"),
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)));
        patch.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly.GetType("DeltaV.Patch_TerrainGrid_RemoveFoundation_Ownership")).Patch();
        try
        {
            Set(enemy, "relations", new List<FactionRelation> { new FactionRelation { other = player, kind = FactionRelationKind.Hostile } });
            Set(player, "relations", new List<FactionRelation> { new FactionRelation { other = enemy, kind = FactionRelationKind.Hostile } });
            var factions = (FactionManager)Raw(typeof(FactionManager));
            Set(factions, "ofPlayer", player); Set(Find.World, "factionManager", factions);
            DamageDefOf.Bullet = new DamageDef { defName = "BattleBullet", workerClass = typeof(DamageWorker), harmsHealth = true };
            var laser = TestBuildingDef("battleLaser");
            laser.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 120, speed = 60 });
            foreach (string name in new[] { "DeltaV_Impact_LaserVsStructure", "DeltaV_Impact_LaserVsSubstructure" })
            {
                var node = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
                    .Single(n => (string)n.Element("defName") == name);
                var part = node.Element("defenderEffect");
                DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "Battle_" + name,
                    projectileDefs = new List<ThingDef> { laser }, targetsSubstructure = (bool?)node.Element("targetsSubstructure") ?? false,
                    substructureHitChance = (float?)node.Element("substructureHitChance") ?? SpaceImpactEffectDef.DefaultSubstructureHitChance,
                    attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
                    defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = (float)part.Element("damage"),
                        damageDef = DamageDefOf.Bullet, incidenceMinFactor = (float?)part.Element("incidenceMinFactor") ?? .35f,
                        armorPenetration = (float?)part.Element("armorPenetration") ?? 10 } });
            }
            SpaceImpactEffects.ResetCache();
            CheckOrderedStructureContacts(universe, player);
            CheckChemicalAmmoSelection(universe, player);
            CheckSteelShellImpacts(universe, player, patch);
            var results = new List<object>();
            var outcomes = new List<(int seed, bool mirrored, bool automatic, int disabled, float loss, bool alive)>();
            foreach (int seed in new[] { 17, 29 })
            foreach (bool mirrored in new[] { false, true })
            foreach (bool automatic in new[] { false, true })
            {
                Rand.PushState(seed);
                try
                {
                    var outcome = RunEngagementBattle(universe, player, enemy, laser, seed, mirrored, automatic);
                    results.Add(outcome.report);
                    outcomes.Add((seed, mirrored, automatic, outcome.disabled, outcome.loss, outcome.alive));
                }
                finally { Rand.PopState(); }
            }
            foreach (var pair in outcomes.GroupBy(o => (o.seed, o.mirrored)))
            {
                var frontal = pair.Single(o => !o.automatic); var maneuver = pair.Single(o => o.automatic);
                Check(frontal.disabled > 0 && maneuver.disabled > 0 && maneuver.disabled < frontal.disabled
                    && maneuver.loss < frontal.loss && maneuver.alive,
                    $"weak-side maneuver disables real enemy weapons earlier with lower damage taken: seed={pair.Key.seed}, mirror={pair.Key.mirrored}");
            }
            CheckForecastFoundationBatch(universe);
            Directory.CreateDirectory("Source/obj/EngagementForecast");
            File.WriteAllText("Source/obj/EngagementForecast/battle.json", JsonSerializer.Serialize(new {
                scope = "Production fire control, shot transactions, laser collision, native building damage and MPC. Fixed target motion, physical thrust fixture, no rendering, map topology splitting, crews, native power-net ticking or lifecycle simulation.",
                results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { patch.UnpatchAll(patch.Id); }
    }

    static int steelBlasts;
    static IntVec3 steelBlastCenter;

    static void CheckChemicalAmmoSelection(UniverseManager universe, Faction faction)
    {
        var map = TestMap(159210, 9);
        map.edificeGrid = new EdificeGrid(map);
        var frame = new MapSpaceObject(159211, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(frame);
        var steel = TestBuildingDef("battleSteelChoice");
        var uranium = TestBuildingDef("battleUraniumChoice");
        foreach (var projectile in new[] { steel, uranium })
            projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 60 });
        var steelHead = TestBuildingDef("battleSteelHead");
        var uraniumHead = TestBuildingDef("battleUraniumHead");
        var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon
        {
            projectileDef = steel, armorPiercingProjectileDef = uranium,
            armorPiercingHeadDef = uraniumHead, armorPiercingUnitMass = 1.5f,
        } };
        var host = AddTestBuilding(map, faction, 159212, TestBuildingDef("battleAmmoHost"), weapon);
        map.edificeGrid.Register(host);
        var primary = new CompSpaceWeaponMagazine { parent = host, props = new CompProperties_SpaceWeaponMagazine() };
        CompGunSupply Stock(ThingWithComps owner, ThingDef head, float quantity)
        {
            var stock = new CompGunSupply { parent = owner, props = new CompProperties_GunSupply
                { kind = GunSupplyKind.Heads, fuelCapacity = 20 } };
            Set(stock, "selectedHead", head);
            Set(typeof(CompRefuelable), stock, "fuel", quantity);
            return stock;
        }
        Set(typeof(ThingWithComps), host, "comps", new List<ThingComp> { weapon, primary, Stock(host, steelHead, 20) });
        var link = new CompSpaceWeaponGenericAttachment { props = new CompProperties_SpaceWeaponAttachment() };
        var addon = AddTestBuilding(map, faction, 159213, TestBuildingDef("battleAmmoAddon"), link);
        var secondary = new CompSpaceWeaponMagazine { parent = addon, props = new CompProperties_SpaceWeaponMagazine
            { attachment = true } };
        var armorPiercingStock = Stock(addon, uraniumHead, 20);
        Set(typeof(ThingWithComps), addon, "comps", new List<ThingComp> { link, secondary, armorPiercingStock });
        Set(typeof(CompSpaceWeaponAttachment), link, "enabled", true);
        Set(secondary, "priority", 1);
        var layout = weapon.Layout;
        layout.magazines.Add(secondary);
        try
        {
            var withUranium = SpaceWeaponBallistics.Read(weapon, frame, new SpaceWeaponEnergy(), false);
            Check(withUranium.magazine == secondary && withUranium.aim.Projectile == uranium && withUranium.mass == 1.5f,
                "available auxiliary uranium stock selects matching projectile and mass for shot parameters");
            Set(typeof(CompRefuelable), armorPiercingStock, "fuel", 0f);
            var withSteel = SpaceWeaponBallistics.Read(weapon, frame, new SpaceWeaponEnergy(), false);
            Check(withSteel.magazine == primary && withSteel.aim.Projectile == steel && withSteel.mass == 1f,
                "empty auxiliary stock falls back to the steel projectile and mass together");
        }
        finally
        {
            universe.UnregisterObject(frame);
            Find.Maps.Remove(map);
            ShipRegistry.ClearCaches();
        }
    }

    static bool BattleSteelWalkable(IntVec3 c, Map map, ref bool __result)
    {
        if (map.uniqueID < 159220 || map.uniqueID >= 159280) return true;
        __result = c.InBounds(map) && c.GetEdifice(map)?.def.passability != Traversability.Impassable;
        return false;
    }

    static bool BattleFlatAngle(IntVec3 __instance, ref float __result)
    {
        __result = Mathf.Repeat(Mathf.Atan2(__instance.x, __instance.z) * Mathf.Rad2Deg, 360f);
        return false;
    }

    // Keep the production blast footprint, native per-cell damage and deck worker.
    // Replace the Unity explosion spawn and tick scheduling in this offline map.
    static bool BattleSteelBlast(IntVec3 center, Map map, float radius, DamageDef damType, Thing instigator,
        int damAmount, ThingDef projectile, bool damageFalloff, List<IntVec3> overrideCells)
    {
        if (damType.workerClass != typeof(DamageWorker_SpaceHighExplosive)) return true;
        steelBlasts++;
        steelBlastCenter = center;
        if (!damageFalloff || overrideCells is not { Count: > 0 })
            throw new InvalidOperationException("Steel blast must hand its footprint and falloff to the native explosion.");
        var explosion = (Explosion)Raw(typeof(Explosion));
        explosion.radius = radius;
        explosion.damAmount = damAmount;
        explosion.damType = damType;
        explosion.projectile = projectile;
        explosion.instigator = instigator;
        explosion.damageFalloff = damageFalloff;
        Set(typeof(Thing), explosion, "positionInt", center);
        Set(typeof(Thing), explosion, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        SpaceImpactEffects.OrderHighExplosiveCells(overrideCells, center);
        var damaged = new List<Thing>();
        for (int i = overrideCells.Count - 1; i >= 0; i--)
            damType.Worker.ExplosionAffectCell(explosion, overrideCells[i], damaged, null, false);
        return false;
    }

    static void CheckSteelShellImpacts(UniverseManager universe, Faction faction, Harmony patch)
    {
        var config = new System.Xml.XmlDocument();
        config.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        XmlInheritance.Clear();
        foreach (System.Xml.XmlNode node in config.SelectNodes("/Defs/DeltaV.SpaceImpactEffectDef"))
            XmlInheritance.TryRegister(node, null);
        XmlInheritance.Resolve();
        System.Xml.XmlNode Resolved(string name) => XmlInheritance.GetResolvedNodeFor(
            config.SelectSingleNode($"/Defs/DeltaV.SpaceImpactEffectDef[defName='{name}']"));
        var fullDeck = Resolved("DeltaV_Impact_SteelShellVsSubstructure");
        var tumbleDeck = Resolved("DeltaV_Impact_TumblingSteelShellVsSubstructure");
        Check(fullDeck.SelectSingleNode("substructureHitChance").InnerText == "0.03"
            && fullDeck.SelectNodes("projectileDefs/li").Count == 2
            && tumbleDeck.SelectSingleNode("projectileDefs/li").InnerText == "DeltaV_Projectile_SteelShellTumbling"
            && tumbleDeck.SelectSingleNode("defenderEffect/highExplosive") != null,
            "native XML inheritance gives intact and tumbling steel their intended deck profiles");
        var profile = DirectXmlToObject.ObjectFromXml<HighExplosiveProfile>(
            fullDeck.SelectSingleNode("defenderEffect/highExplosive"), false);
        XmlInheritance.Clear();
        var damage = new DamageDef { defName = "BattleSteelBlast", workerClass = typeof(DamageWorker_SpaceHighExplosive),
            harmsHealth = true, explosionSnowMeltAmount = 0f };
        var projectile = TestBuildingDef("battleSteelShell");
        projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 60, collisionRadius = 0 });
        var part = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, highExplosive = profile, damageDef = damage };
        var effect = new SpaceImpactEffectDef { defName = "BattleSteelShellImpact",
            projectileDefs = new List<ThingDef> { projectile }, defenderEffect = part,
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } };
        var deckEffect = new SpaceImpactEffectDef { defName = "BattleSteelDeckImpact",
            projectileDefs = effect.projectileDefs, defenderEffect = part, attackerEffect = effect.attackerEffect,
            targetsSubstructure = true, substructureHitChance = .03f };
        DefDatabase<SpaceImpactEffectDef>.Add(effect);
        DefDatabase<SpaceImpactEffectDef>.Add(deckEffect);
        SpaceImpactEffects.ResetCache();
        var explosionEntry = AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion));
        patch.Patch(explosionEntry, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSteelBlast)));
        var walkable = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable), new[] { typeof(IntVec3), typeof(Map) });
        patch.Patch(walkable, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSteelWalkable)));
        var angle = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.AngleFlat));
        patch.Patch(angle, prefix: new HarmonyMethod(typeof(Program), nameof(BattleFlatAngle)));
        int id = 159220;
        foreach (var (mass, speed, centerHp, radius, centerDamage, neighborDamage, openDeck) in new[]
        {
            (1f, 60f, 1000, .75f, 32, 0, false), (2f, 85f, 1000, 1.25f, 64, 23, false),
            (48f, 60f, 1000, 2.4f, 760, 507, false), (48f, 6f, 300, 2.4f, 760, 507, true)
        })
        {
            var map = TestMap(id++, 12);
            map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
            map.roofGrid = new RoofGrid(map);
            map.edificeGrid = new EdificeGrid(map);
            var vacuum = (TerrainDef)Raw(typeof(TerrainDef));
            vacuum.exposesToVacuum = true;
            Array.Fill(map.terrainGrid.topGrid, vacuum);
            var frame = new MapSpaceObject(id++, map, new SpacePose(Vector3.forward, 0));
            universe.objects.Add(frame);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            var wallDef = TestBuildingDef("blastWall");
            wallDef.building.isWall = true;
            wallDef.useHitPoints = wallDef.destroyable = true;
            wallDef.passability = Traversability.Impassable;
            wallDef.fillPercent = 1f;
            BattleBuilding Wall(IntVec3 cell, int hp)
            {
                var wall = (BattleBuilding)AddTestBuilding(map, faction, id++, wallDef, new CompPowerTransmitter(), new BattleBuilding());
                MoveCombatFixture(wall, cell);
                wall.HitPoints = hp;
                map.edificeGrid.Register(wall);
                return wall;
            }
            var center = new IntVec3(4, 0, 5);
            var wall = Wall(center, centerHp);
            var side = Wall(center + IntVec3.North, 1000);
            var farther = Wall(center + new IntVec3(4, 0, 0), 1000);
            var floor = (TerrainDef)Raw(typeof(TerrainDef));
            floor.isFoundation = true;
            floor.tags = new List<string> { "Substructure" };
            map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(center)] = floor;
            SubstructureOwnership.BumpStructureVersion(map);
            try
            {
                var from = new Vector2(2.5f, 5.5f);
                var forecast = new CombatDamageForecast(frame);
                var preview = forecast.Fire(from, Vector2.right, 7, projectile, mass, speed, coverSample: .37f);
                int launches = steelBlasts;
                var start = SpaceTransform.MapPointToUniverse(frame, map, from);
                var attacker = new SpaceObject(id++, new SpacePose(start, 0));
                attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(frame, map, from, Vector2.right) * speed;
                var shot = new SpaceCollision.SweptBody { attacker = attacker, requiredDefender = frame,
                    from = start, to = SpaceTransform.MapPointToUniverse(frame, map, from + Vector2.right * 7),
                    projectileDef = projectile, mass = mass, damageScale = .5f,
                    sweepRadius = SpaceProjectileService.FootprintRadius(projectile) };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
                Check(shot.dead && steelBlasts == launches + 1 && steelBlastCenter == center
                    && Math.Abs(shot.resolvedHitRadius - radius) < .001f && farther.HitPoints == 1000,
                    $"steel shell mass {mass} and speed {speed} detonates once at first contact and stops");
                int actualCenter = wall.Destroyed ? centerHp : centerHp - wall.HitPoints;
                int actualSide = 1000 - side.HitPoints;
                Check(actualCenter == Math.Min(centerDamage, centerHp) && Math.Abs(actualSide - neighborDamage) <= 1
                    && Math.Abs(preview.structure - actualCenter - actualSide) < 2,
                    "native blast and forecast agree on center and neighbor falloff");
                Check(SubstructureOwnership.HasFoundation(map, center) != openDeck
                    && (preview.foundation > 0f) == openDeck,
                    "the wall shields its deck only while it survives the cell blast");
            }
            finally
            {
                universe.objects.Remove(frame);
                Find.Maps.Remove(map);
                Set(universe, "objectById", null);
                Set(universe, "objectByMap", null);
            }
        }
        CheckUraniumPenetration(universe, faction);
        CompareAmmunitionRoles(universe, projectile);
        patch.Unpatch(explosionEntry, HarmonyPatchType.Prefix, patch.Id);
        patch.Unpatch(walkable, HarmonyPatchType.Prefix, patch.Id);
        patch.Unpatch(angle, HarmonyPatchType.Prefix, patch.Id);
        var remove = AccessTools.Method(typeof(DefDatabase<SpaceImpactEffectDef>), "Remove");
        remove.Invoke(null, new object[] { effect });
        remove.Invoke(null, new object[] { deckEffect });
        SpaceImpactEffects.ResetCache();
        SpaceCollision.ClearCaches();
        ShipRegistry.ClearCaches();
    }

    static void CheckUraniumPenetration(UniverseManager universe, Faction faction)
    {
        Map map = TestMap(159274, 12);
        map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
        var frame = new MapSpaceObject(159275, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(frame);
        var uranium = TestBuildingDef("battleUraniumPenetrator");
        uranium.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, collisionRadius = 0 });
        var settings = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
            .Single(n => (string)n.Element("defName") == "DeltaV_Impact_CannonVsStructure").Element("defenderEffect");
        var effect = new SpaceImpactEffectDef { defName = "BattleUraniumPenetratorEffect",
            projectileDefs = new List<ThingDef> { uranium }, attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = DamageDefOf.Bullet,
                damage = (float)settings.Element("damage"), kineticMassScaling = (bool)settings.Element("kineticMassScaling"),
                penetrates = (bool)settings.Element("penetrates"), incidenceMinFactor = (float)settings.Element("incidenceMinFactor") } };
        DefDatabase<SpaceImpactEffectDef>.Add(effect);
        SpaceImpactEffects.ResetCache();
        var def = TestBuildingDef("blastPenetrationWall");
        def.building.isWall = true;
        def.passability = Traversability.Impassable;
        def.useHitPoints = def.destroyable = true;
        var walls = new List<BattleBuilding>();
        for (int i = 0; i < 4; i++)
        {
            var wall = (BattleBuilding)AddTestBuilding(map, faction, 159290 + i, def, new CompPowerTransmitter(), new BattleBuilding());
            MoveCombatFixture(wall, new IntVec3(4 + i, 0, 5));
            wall.HitPoints = 300;
            walls.Add(wall);
        }
        var flank = (BattleBuilding)AddTestBuilding(map, faction, 159294, def, new CompPowerTransmitter(), new BattleBuilding());
        MoveCombatFixture(flank, new IntVec3(4, 0, 6));
        flank.HitPoints = 300;
        SubstructureOwnership.BumpStructureVersion(map);
        try
        {
            float speed = SpaceKinetics.LaunchSpeed(48, 72f);
            var from = new Vector2(2.5f, 5.5f);
            var forecast = new CombatDamageForecast(frame);
            var preview = forecast.Fire(from, Vector2.right, 8f, uranium, 72f, speed);
            var start = SpaceTransform.MapPointToUniverse(frame, map, from);
            var attacker = new SpaceObject(159276, new SpacePose(start, 0));
            attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(frame, map, from, Vector2.right) * speed;
            var shot = new SpaceCollision.SweptBody { attacker = attacker, requiredDefender = frame, from = start,
                to = SpaceTransform.MapPointToUniverse(frame, map, from + Vector2.right * 8),
                projectileDef = uranium, mass = 72f, damageScale = 1f,
                sweepRadius = SpaceProjectileService.FootprintRadius(uranium) };
            SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
            Check(walls.Take(3).All(w => w.Destroyed) && Math.Abs(walls[3].HitPoints - 48) <= 1 && flank.HitPoints == 300
                && Math.Abs(preview.structure - 1152) < 1f && forecast.RemainingHP(flank) == 300,
                "48/48 uranium and its forecast punch through three walls, damage the fourth, and leave the adjacent lane intact"
                    + $" (walls={string.Join(",", walls.Select(w => w.Destroyed ? 0 : w.HitPoints))}, flank={flank.HitPoints}, "
                    + $"forecast={preview.structure}/{forecast.RemainingHP(flank)}, contacts={preview.contacts})");
        }
        finally
        {
            universe.UnregisterObject(frame);
            Find.Maps.Remove(map);
            AccessTools.Method(typeof(DefDatabase<SpaceImpactEffectDef>), "Remove").Invoke(null, new object[] { effect });
            SpaceImpactEffects.ResetCache();
        }
    }

    static void CompareAmmunitionRoles(UniverseManager universe, ThingDef steel)
    {
        var uranium = TestBuildingDef("battleRoleUranium");
        uranium.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, speed = 60, collisionRadius = 0 });
        var penetration = new SpaceImpactEffectDef { defName = "BattleRoleUraniumDeck", targetsSubstructure = true,
            substructureHitChance = .03f, projectileDefs = new List<ThingDef> { uranium },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 24,
                kineticMassScaling = true, penetrates = true, incidenceMinFactor = .5f, damageDef = DamageDefOf.Bullet },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } };
        DefDatabase<SpaceImpactEffectDef>.Add(penetration);
        SpaceImpactEffects.ResetCache();
        var map = TestMap(159279, 32);
        map.roofGrid = new RoofGrid(map);
        map.edificeGrid = new EdificeGrid(map);
        var vacuum = (TerrainDef)Raw(typeof(TerrainDef));
        vacuum.exposesToVacuum = true;
        Array.Fill(map.terrainGrid.topGrid, vacuum);
        var deck = (TerrainDef)Raw(typeof(TerrainDef));
        deck.isFoundation = true;
        deck.tags = new List<string> { "Substructure" };
        var cells = new CellRect(9, 12, 12, 7).Cells.ToArray();
        var indices = cells.Select(map.cellIndices.CellToIndex).ToArray();
        var frame = new MapSpaceObject(159278, map, new SpacePose(Vector3.forward, 0));
        universe.RegisterObject(frame);
        var guns = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml").Root.Elements("ThingDef");
        var results = new Dictionary<(string name, bool moving, bool ap), (float contactRate, float deckPerMinute)>();
        const int samples = 195;
        try
        {
            foreach (var (name, defName, charge, heads) in new[] {
                ("small rapid", "DeltaV_Weapon_RapidMainSmall", 1, 1),
                ("small heavy", "DeltaV_Weapon_CannonTurret", 24, 24),
                ("medium rapid", "DeltaV_Weapon_RapidMainMedium", 4, 2),
                ("medium heavy", "DeltaV_Weapon_HeavyMainMedium", 48, 48),
                ("medium heavy light charge", "DeltaV_Weapon_HeavyMainMedium", 1, 96),
                ("medium heavy full charge", "DeltaV_Weapon_HeavyMainMedium", 96, 96) })
            {
                var tuningXml = guns.Single(n => (string)n.Element("defName") == defName).Element("comps").Elements("li")
                    .Single(n => (string)n.Attribute("Class") == "DeltaV.CompProperties_WeaponTuning");
                var node = new System.Xml.XmlDocument();
                node.LoadXml(tuningXml.ToString());
                var tuning = new CompWeaponTuning();
                tuning.Initialize(DirectXmlToObject.ObjectFromXml<CompProperties_WeaponTuning>(node.DocumentElement, false));
                tuning.charge = charge;
                tuning.mass = heads;
                int cycle = 0, scheduled = 0;
                bool locked = false;
                bool stable = !tuning.Settings.lockEveryShot
                    && tuning.ShotHeat <= tuning.Settings.coolingPerSecond * tuning.interval / 60f;
                for (; cycle < 100000; cycle++)
                {
                    if (tuning.RemainingTicks == 0)
                    {
                        if (cycle > 0 && (stable || locked && tuning.heat == 0)) break;
                        tuning.CommitShot();
                        scheduled++;
                        locked |= tuning.overheated;
                    }
                    tuning.AdvanceThermalTick();
                }
                Check(cycle < 100000, name + " reaches a full production heat cycle");

                foreach (bool ap in new[] { false, true })
                foreach (bool moving in new[] { false, true })
                {
                    int contacts = 0, destroyed = 0, widest = 0, deepest = 0;
                    float mass = heads * (ap ? 1.5f : 1f);
                    float speed = SpaceKinetics.LaunchSpeed(charge, mass);
                    float flight = 30f / speed;
                    for (int i = 0; i < samples; i++)
                    {
                        // Zero transverse velocity at launch. Sample lateral acceleration over a 30-cell flight.
                        float offset = moving ? .5f * ((i % 13 - 6f) * .55f) * flight * flight : 0f;
                        float lane = 15.5f - offset;
                        if (lane < 12 || lane >= 19) continue;
                        foreach (int index in indices) map.terrainGrid.foundationGrid[index] = deck;
                        SubstructureOwnership.BumpStructureVersion(map);
                        var from = new Vector2(2.5f, lane);
                        Vector3 start = SpaceTransform.MapPointToUniverse(frame, map, from);
                        var attacker = new SpaceObject(159277, new SpacePose(start, 0));
                        attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(frame, map, from, Vector2.right) * speed;
                        ThingDef projectile = ap ? uranium : steel;
                        var shot = new SpaceCollision.SweptBody { attacker = attacker, requiredDefender = frame,
                            from = start, to = SpaceTransform.MapPointToUniverse(frame, map, from + Vector2.right * 24),
                            projectileDef = projectile, mass = mass, damageScale = 1f,
                            sweepRadius = SpaceProjectileService.FootprintRadius(projectile) };
                        Rand.PushState(33101 + i);
                        try { SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot }); }
                        finally { Rand.PopState(); }
                        if (shot.resolvedHit) contacts++;
                        var holes = cells.Where((_, index) => map.terrainGrid.foundationGrid[indices[index]] == null).ToArray();
                        destroyed += holes.Length;
                        if (holes.Length > 0)
                        {
                            widest = Math.Max(widest, holes.Max(c => c.z) - holes.Min(c => c.z) + 1);
                            deepest = Math.Max(deepest, holes.Max(c => c.x) - holes.Min(c => c.x) + 1);
                        }
                        Call(universe, "RunFrameActions");
                    }
                    float rate = contacts / (float)samples;
                    float deckPerMinute = destroyed / (float)samples * scheduled * 3600f / cycle;
                    results[(name, moving, ap)] = (rate, deckPerMinute);
                    Console.WriteLine($"AMMO {name}, {(ap ? "uranium" : "steel")}, {(moving ? "maneuver" : "still")}: "
                        + $"flight={flight:F2}s contacts={contacts}/{samples} deck/min={deckPerMinute:F1} maxWidth={widest} maxDepth={deepest}");
                }
            }
            foreach (string size in new[] { "small", "medium" })
                Check(results[($"{size} rapid", false, false)].deckPerMinute > results[($"{size} heavy", false, false)].deckPerMinute,
                    size + " default rapid steel clears more bare deck over complete heat cycles");
            Check(results[("medium heavy light charge", true, false)].contactRate
                < results[("medium heavy full charge", true, false)].contactRate,
                "equally heavy low-charge steel loses contacts against a maneuvering target");
        }
        finally
        {
            universe.UnregisterObject(frame);
            Find.Maps.Remove(map);
            AccessTools.Method(typeof(DefDatabase<SpaceImpactEffectDef>), "Remove").Invoke(null, new object[] { penetration });
            SpaceImpactEffects.ResetCache();
        }
    }

    static void CheckOrderedStructureContacts(UniverseManager universe, Faction faction)
    {
        int id = 159060;
        foreach (var (sameCell, floorHit, expectFloor) in new[]
        {
            (true, 0f, true), (false, 1f, true), (false, 0f, false)
        })
        {
            Map map = TestMap(id++, 10);
            map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
            map.roofGrid = new RoofGrid(map);
            var vacuum = (TerrainDef)Raw(typeof(TerrainDef));
            vacuum.exposesToVacuum = true;
            Array.Fill(map.terrainGrid.topGrid, vacuum);
            var floor = (TerrainDef)Raw(typeof(TerrainDef));
            floor.isFoundation = true;
            floor.tags = new List<string> { "Substructure" };
            var floorCell = sameCell ? new IntVec3(4, 0, 5) : new IntVec3(4, 0, 3);
            var wallCell = sameCell ? floorCell : new IntVec3(5, 0, 5);
            int floorIndex = map.cellIndices.CellToIndex(floorCell);
            map.terrainGrid.foundationGrid[floorIndex] = floor;
            var frame = new MapSpaceObject(id++, map, new SpacePose(Vector3.forward, 0));
            universe.objects.Add(frame);
            Set(universe, "objectById", null);
            Set(universe, "objectByMap", null);
            var wallDef = TestBuildingDef("orderedContactWall");
            wallDef.building.isWall = true;
            wallDef.passability = Traversability.Impassable;
            wallDef.useHitPoints = wallDef.destroyable = true;
            var wall = (BattleBuilding)AddTestBuilding(map, faction, id++, wallDef, new CompPowerTransmitter(), new BattleBuilding());
            MoveCombatFixture(wall, wallCell);
            wall.HitPoints = sameCell ? 40 : 100;
            var round = TestBuildingDef("orderedContactProjectile");
            round.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, collisionRadius = 1.3f });
            var buildingEffect = new SpaceImpactEffectDef { defName = "OrderedBuilding" + id,
                projectileDefs = new List<ThingDef> { round }, buildingHitChance = 0f,
                defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 120,
                    damageDef = DamageDefOf.Bullet, incidenceMinFactor = 1, penetrates = sameCell } };
            var floorEffect = new SpaceImpactEffectDef { defName = "OrderedDeck" + id,
                projectileDefs = buildingEffect.projectileDefs, targetsSubstructure = true, substructureHitChance = floorHit,
                defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 120,
                    substructureBreakThreshold = 60, incidenceMinFactor = 1, penetrates = sameCell } };
            DefDatabase<SpaceImpactEffectDef>.Add(buildingEffect);
            DefDatabase<SpaceImpactEffectDef>.Add(floorEffect);
            SpaceImpactEffects.ResetCache();
            SubstructureOwnership.BumpStructureVersion(map);
            try
            {
                var forecast = new CombatDamageForecast(frame);
                Vector2 from = new Vector2(2.4f, 5.2f);
                var prediction = forecast.Fire(from, Vector2.right, 5, round, 1, 60, coverSample: .37f);
                var deckLoss = (HashSet<IntVec3>)AccessTools.Field(typeof(CombatDamageForecast), "brokenFoundation").GetValue(forecast);
                Check(deckLoss.Contains(floorCell) == expectFloor && map.terrainGrid.foundationGrid[floorIndex] == floor,
                    $"forecast resolves the ordered layers without changing the map (same cell: {sameCell}, deck hit: {floorHit})");
                Vector3 start = SpaceTransform.MapPointToUniverse(frame, map, from);
                var shot = new SpaceCollision.SweptBody
                {
                    attacker = new SpaceObject(id++, new SpacePose(start, 0)), requiredDefender = frame,
                    from = start, to = SpaceTransform.MapPointToUniverse(frame, map, from + Vector2.right * 5),
                    projectileDef = round, mass = 1, damageScale = 1, sweepRadius = 1.3f
                };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
                Check((map.terrainGrid.foundationGrid[floorIndex] == null) == expectFloor,
                    $"real sweep agrees with the forecast about the deck (same cell: {sameCell}, deck hit: {floorHit})");
                Check(wall.Destroyed == (sameCell || !expectFloor) && shot.resolvedHitCell == (expectFloor ? floorCell : wallCell),
                    "earlier bare deck is resolved before the rear wall; a missed deck lets the wall resolve next");
                if (sameCell)
                    Check(Math.Abs(shot.damageScale - 1f / 6f) < .001f && prediction.contacts == 2,
                        "building and confirmed same-cell deck each consume one layer of the penetration budget");
            }
            finally
            {
                var remove = AccessTools.Method(typeof(DefDatabase<SpaceImpactEffectDef>), "Remove");
                remove.Invoke(null, new object[] { buildingEffect });
                remove.Invoke(null, new object[] { floorEffect });
                SpaceImpactEffects.ResetCache();
                universe.objects.Remove(frame);
                Find.Maps.Remove(map);
                Set(universe, "objectById", null);
                Set(universe, "objectByMap", null);
                SpaceCollision.ClearCaches();
                ShipRegistry.ClearCaches();
            }
        }
    }

    static void CheckForecastFoundationBatch(UniverseManager universe)
    {
        var map = TestMap(159000, 9); map.roofGrid = new RoofGrid(map);
        var vacuum = (TerrainDef)Raw(typeof(TerrainDef)); vacuum.exposesToVacuum = true;
        Array.Fill(map.terrainGrid.topGrid, vacuum);
        var foundation = (TerrainDef)Raw(typeof(TerrainDef));
        foundation.isFoundation = true; foundation.tags = new List<string> { "Substructure" };
        var cell = new IntVec3(4, 0, 4); int index = map.cellIndices.CellToIndex(cell);
        map.terrainGrid.foundationGrid[index] = foundation;
        var frame = new MapSpaceObject(159001, map, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(frame); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        var projectile = TestBuildingDef("foundationProbabilityBeam");
        projectile.comps.Add(new CompProperties_SpaceProjectile { hasMap = false, laserRange = 10, speed = 60 });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "FoundationProbabilityImpact",
            projectileDefs = new List<ThingDef> { projectile }, targetsSubstructure = true, substructureHitChance = .5f,
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damage = 30,
                substructureBreakThreshold = 60, incidenceMinFactor = 1 } });
        SpaceImpactEffects.ResetCache(); SubstructureOwnership.BumpStructureVersion();
        var observation = new CombatDamageForecast.Observation(frame);
        var broken = AccessTools.Field(typeof(CombatDamageForecast), "brokenFoundation");
        int predicted = 0, actual = 0;
        const int samples = 2048;
        Rand.PushState(53441);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                map.terrainGrid.foundationGrid[index] = foundation;
                SubstructureOwnership.BumpStructureVersion();
                var forecast = new CombatDamageForecast(observation);
                forecast.Fire(new Vector2(2.5f, 4.5f), Vector2.right, 4, projectile, 1, 60,
                    coverSample: (i + .5f) / samples);
                if (((HashSet<IntVec3>)broken.GetValue(forecast)).Contains(cell)) predicted++;
                if (map.terrainGrid.foundationGrid[index] != foundation)
                    throw new Exception("forecast wrote the native foundation grid");
                Vector3 from = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(2.5f, 4.5f));
                var body = new SpaceCollision.SweptBody { attacker = new SpaceObject(159002, new SpacePose(from, 0)),
                    from = from, to = SpaceTransform.MapPointToUniverse(frame, map, new Vector2(6.5f, 4.5f)),
                    projectileDef = projectile, mass = 1, damageScale = 1,
                    sweepRadius = SpaceProjectileService.FootprintRadius(projectile) };
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body });
                if (map.terrainGrid.foundationGrid[index] == null) actual++;
                foreach (var action in Actions(universe)?.ToArray() ?? Array.Empty<Action>()) { Actions(universe).Remove(action); action(); }
            }
            Check(Math.Abs(predicted / (float)samples - .25f) < .04f
                && Math.Abs(predicted - actual) / (float)samples < .04f,
                $"independent floor hit/break forecast matches native destruction batch: predicted={predicted}/{samples}, actual={actual}/{samples}");
        }
        finally
        {
            Rand.PopState(); universe.objects.Remove(frame); Find.Maps.Remove(map);
            Set(universe, "objectById", null); Set(universe, "objectByMap", null);
            SpaceCollision.ClearCaches(); ShipRegistry.ClearCaches();
        }
    }

    static (object report, int disabled, float loss, bool alive) RunEngagementBattle(UniverseManager universe, Faction player, Faction enemy,
        ThingDef laser, int seed, bool mirrored, bool automatic)
    {
        int id = 140000;
        ThingDefOf.PilotConsole = TestBuildingDef("battleConsole");
        ThingDefOf.GravEngine = TestBuildingDef("battleUnusedEngine");
        DeltaVDefOf.DeltaV_Weapon_MissileLauncher = TestBuildingDef("battleUnusedLauncher");
        ThingDefOf.PilotConsole.useHitPoints = true; ThingDefOf.PilotConsole.fillPercent = 1;
        var own = new ManeuverMotionObject(id++, TestMap(id++, 21),
            new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(0, 100)), -Mathf.PI / 2));
        var target = new ManeuverMotionObject(id++, TestMap(id++, 21), new SpacePose(Vector3.forward, 0));
        var buildings = new List<(BattleBuilding thing, bool own, int hp)>();
        var guns = new List<CompSpaceWeapon>();
        BattleBuilding Add(Map map, Faction faction, IntVec3 cell, ThingDef def, int hp, ThingComp comp)
        {
            def.useHitPoints = true;
            var thing = (BattleBuilding)AddTestBuilding(map, faction, id++, def, comp, new BattleBuilding());
            MoveCombatFixture(thing, cell); thing.HitPoints = hp;
            buildings.Add((thing, map == own.map, hp)); return thing;
        }
        foreach (var item in new[] { (body: own, faction: player), (body: target, faction: enemy) })
        {
            item.body.faction = item.faction;
            item.body.map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher));
            item.body.map.roofGrid = new RoofGrid(item.body.map);
            var vacuum = (TerrainDef)Raw(typeof(TerrainDef)); vacuum.exposesToVacuum = true;
            Array.Fill(item.body.map.terrainGrid.topGrid, vacuum);
            item.body.map.lordManager = (LordManager)Raw(typeof(LordManager));
            item.body.map.lordManager.lords = new List<Lord>();
            var console = new CompDeltaVPilotConsole { isMain = true, shipId = id };
            Add(item.body.map, item.faction, new IntVec3(10, 0, 10), ThingDefOf.PilotConsole, 300, console);
            var hull = new CombatHull(console.Ship);
            hull.Combat.weaponsArmed = true;
            var foundation = (TerrainDef)Raw(typeof(TerrainDef));
            foundation.tags = new List<string> { "Substructure" }; foundation.isFoundation = true;
            var owners = new int[item.body.map.cellIndices.NumGridCells];
            Set(SubstructureOwnership.TryGet(item.body.map), "ownerGrid", owners);
            for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
            {
                var cell = new IntVec3(x, 0, z); hull.cells.Add(cell);
                int index = item.body.map.cellIndices.CellToIndex(cell);
                item.body.map.terrainGrid.foundationGrid[index] = foundation; owners[index] = console.shipId;
            }
            item.body.member = hull; universe.objects.Add(item.body);
            var gun = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon {
                projectileDef = laser, range = 120, intrinsicAimSkill = 20, topTurret = true,
                cooldownTicks = 90, impactScale = item.body == own ? 4 : .25f }, safeFire = false };
            var turret = Add(item.body.map, item.faction, new IntVec3(10, 0, 12), TestBuildingDef("battleGun" + id), 300, gun);
            turret.def.fillPercent = 1;
            var traverse = new CompSpaceWeaponTraverse { parent = turret,
                props = new CompProperties_SpaceWeaponTraverse() };
            turret.AllComps.Add(traverse); guns.Add(gun);
            universe.Controls.For(hull);
        }
        for (int x = 4; x <= 16; x++) for (int z = 4; z <= 16; z++)
        {
            bool thin = mirrored ? x == 4 : x == 16;
            bool thick = (mirrored ? x >= 14 : x <= 6) || z <= 6 || z >= 14;
            if (!thin && !thick) continue;
            var def = TestBuildingDef("battleArmor" + id); def.fillPercent = 1; def.passability = Traversability.Impassable;
            Add(target.map, enemy, new IntVec3(x, 0, z), def, thin ? 48 : 180, new EmptyComp());
        }
        SubstructureOwnership.BumpStructureVersion(); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        var strategy = universe.Controls.For(own.member).FireControl.Strategy(own.member);
        var rule = strategy.BuiltInManeuver; rule.tactic.distance = 100;
        if (!automatic) { rule.tactic.bearing = CombatBearing.World; rule.tactic.bearingDegrees = 90; }
        strategy.maneuverRules.Add(rule);
        var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
        Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
        Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
        var method = typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private);
        bool Sample(out SpacePose pose) { pose = ShipRegistry.ShipPose(target.member); return target.member.IsValid; }
        bool Plan(SpaceObject body, SpacePose other, float radius, DeltaV.DriveInfo drive, out CombatTrajectoryIntent intent)
        { object[] args = { body, other, radius, drive, null }; bool valid = (bool)method.Invoke(planner, args); intent = (CombatTrajectoryIntent)args[4]; return valid; }
        var subject = new DriveSubject(own.objectId, own.member.Identity.key);
        universe.Autopilot.TrackOrbit(subject, Sample, 10, Plan);
        int ticks = 0, ownShots = 0, enemyShots = 0, firstHit = -1, firstLoss = -1, enemyDisarmedTick = -1;
        int rangeTicks = 0, weakTicks = 0;
        var timeline = new List<object>();
        float Damage(bool ours) => buildings.Where(b => b.own == ours).Sum(b => b.hp - Math.Max(0, b.thing.HitPoints));
        for (; ticks < 10800; ticks++)
        {
            RecoveryTick(universe.Autopilot, ticks);
            foreach (var body in new[] { own, target })
            {
                var control = universe.Controls.For(body.member).FireControl;
                control.Decide(body.member, body);
                int before = universe.Projectiles.InFlightCount;
                control.TickAutoVolley(body.member, body, false);
                int launched = universe.Projectiles.InFlightCount - before;
                if (body == own) ownShots += launched; else enemyShots += launched;
            }
            universe.Projectiles.Tick(); universe.Projectiles.ResolveCollisions();
            foreach (var action in Actions(universe)?.ToArray() ?? Array.Empty<Action>()) { Actions(universe).Remove(action); action(); }
            if (firstHit < 0 && Damage(false) > 0) firstHit = ticks;
            if (firstLoss < 0 && buildings.Any(b => b.thing.Destroyed)) firstLoss = ticks;
            if (enemyDisarmedTick < 0 && guns[1].parent.Destroyed) enemyDisarmedTick = ticks;
            Vector2 offset = OrbitSphere.Offset(ShipRegistry.ShipPose(target.member).position, ShipRegistry.ShipPose(own.member).position);
            float phase = Mathf.Atan2(offset.y, offset.x);
            if (offset.magnitude >= 90 && offset.magnitude <= 110) rangeTicks++;
            if (Math.Abs(SpaceTransform.WrapAngle(phase - (mirrored ? Mathf.PI : 0))) <= Mathf.PI / 6) weakTicks++;
            if (ticks % 300 == 0)
            {
                var state = (CombatReposition)AccessTools.Field(typeof(ShipAutoManeuverControl), "reposition").GetValue(planner);
                var evaluation = (CombatEngagementEvaluation.Result)AccessTools.Field(typeof(ShipAutoManeuverControl), "engagementResult").GetValue(planner);
                timeline.Add(new { ticks, distance = offset.magnitude, bearing = phase * Mathf.Rad2Deg,
                    selectedBearing = state.phase * Mathf.Rad2Deg, state.selectedScore, forecastOut = evaluation?.outgoing,
                    forecastIn = evaluation?.incoming, ownShots, enemyShots, outgoing = Damage(false), incoming = Damage(true) });
            }
            if (!own.member.IsValid || !target.member.IsValid) { ticks++; break; }
        }
        float outgoing = Damage(false), incoming = Damage(true);
        Console.WriteLine($"BATTLE seed={seed} mirror={mirrored} auto={automatic} ticks={ticks} shots={ownShots}/{enemyShots} damage={outgoing}/{incoming} disarmed={enemyDisarmedTick} firstHit={firstHit} firstLoss={firstLoss} weak={weakTicks / (float)ticks:F3} range={rangeTicks / (float)ticks:F3}");
        Check(ownShots > 0 && outgoing > 0, "battle runs production launch and damage for " + seed + "/" + mirrored + "/" + automatic);
        Check(enemyShots > 0 && incoming > 0, "battle includes actual enemy fire and incoming damage");
        var result = new { seed, mirrored, automatic, ticks, ownShots, enemyShots, outgoing, incoming, firstHit, firstLoss, enemyDisarmedTick, rangeTicks, weakTicks, timeline,
            ownAlive = own.member.IsValid, targetAlive = target.member.IsValid,
            destroyedOwn = buildings.Count(b => b.own && b.thing.Destroyed), destroyedEnemy = buildings.Count(b => !b.own && b.thing.Destroyed) };
        universe.Autopilot.Cancel(subject); universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
        ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
        Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        return (result, enemyDisarmedTick, incoming, result.ownAlive);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Production collision, damage worker and impact Defs. Only Unity sound/materials,
// notifications and destruction bookkeeping are replaced by an in-memory map fixture.
internal static class Program
{
    static int checks, nextId = 7000;
    static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    static void Set(object obj, string field, object value) =>
        AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    static bool Skip() => false;
    static bool NotFlammable(ref bool __result) { __result = false; return false; }
    static bool ShieldActive(CompProjectileInterceptor __instance, ref bool __result)
    { __result = __instance.currentHitPoints > 0; return false; }
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(); Console.WriteLine($"PASS {checks} railgun checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    sealed class Target : Building
    {
        public bool invulnerable;
        public override int HitPoints
        {
            get => (int)AccessTools.Field(typeof(Thing), "hitPointsInt").GetValue(this);
            set => Set(this, "hitPointsInt", value);
        }
        public override void PreApplyDamage(ref DamageInfo info, out bool absorbed) => absorbed = invulnerable;
        public override void PostApplyDamage(DamageInfo info, float amount) { }
        public override void Kill(DamageInfo? info = null, Hediff culprit = null)
        { Map.thingGrid.Deregister(this); Set(this, "mapIndexOrState", (sbyte)-2); }
    }
    sealed class Hull : MapSpaceObject
    {
        public Hull(Map map) : base(nextId++, map, new SpacePose(Vector3.forward, 0)) { }
        public override IEnumerable<ShipBase> Ships => Enumerable.Empty<ShipBase>();
        public override IEnumerable<SpaceCollisionCell> CollisionCells =>
            map.listerThings.AllThings.Where(t => !t.Destroyed)
                .SelectMany(t => t.OccupiedRect().Select(c =>
                    new SpaceCollisionCell(c, SpaceCellKind.Building, t)));
    }
    static Map map;
    static Hull hull;
    static UniverseManager universe;
    static ThingDef rail, fragment, shieldDef;
    static DamageDef bullet;
    static readonly List<SpaceCollision.VisualContact> contacts = new();

    static ThingDef MakeDef(string name)
    {
        var def = (ThingDef)Raw(typeof(ThingDef));
        def.shortHash = (ushort)nextId++; def.thingClass = typeof(Building); def.defName = name; def.category = ThingCategory.Building; def.size = new IntVec2(1, 1);
        def.building = new BuildingProperties(); def.fillPercent = 1; def.useHitPoints = true;
        def.passability = Traversability.Impassable; def.comps = new List<CompProperties>();
        return def;
    }
    static Target Add(int x, int z, int hp, params ThingComp[] comps)
    {
        var def = MakeDef("fixture" + nextId++);
        var target = new Target { def = comps.Length > 0 ? shieldDef : def, thingIDNumber = nextId++ };
        Set(target, "positionInt", new IntVec3(x, 0, z));
        Set(target, "mapIndexOrState", (sbyte)0); Set(target, "hitPointsInt", hp);
        Set(target, "comps", comps.Length == 0 ? null : new List<ThingComp>(comps));
        foreach (var comp in comps) comp.parent = target;
        map.thingGrid.Register(target); map.listerThings.AllThings.Add(target);
        return target;
    }
    static CompProjectileInterceptor Shield(int hp, float radius = 12)
    {
        var comp = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        comp.props = new CompProperties_ProjectileInterceptor { radius = radius, hitPoints = 3000 };
        comp.currentHitPoints = hp; Add(40, 8, 500, comp);
        return comp;
    }
    static void Reset()
    {
        contacts.Clear();
        SpaceCollision.ClearCaches();
        map = (Map)Raw(typeof(Map));
        map.uniqueID = nextId++; map.info = new MapInfo { Size = new IntVec3(80, 1, 12) };
        map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher)); map.cellIndices = new CellIndices(map); map.components = new List<MapComponent>();
        map.thingGrid = new ThingGrid(map); map.terrainGrid = new TerrainGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Region);
        Find.Maps.Clear(); Find.Maps.Add(map);
        hull = new Hull(map); universe.objects.Clear(); universe.objects.Add(hull);
    }
    static SpaceCollision.SweptBody Fire(float scale, float speed = 180, ThingDef def = null,
        float from = 5, float to = 70, Action<SpaceCollision.SweptBody> configure = null)
    {
        Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(from, 5.5f));
        Vector3 end = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(to, 5.5f));
        var attacker = new SpaceObject(nextId++, new SpacePose(start, 0));
        attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(hull, map,
            new Vector2(from, 5.5f), Vector2.right) * speed;
        var body = new SpaceCollision.SweptBody { attacker = attacker, projectileDef = def ?? rail,
            from = start, to = end, damageScale = scale, sweepRadius = 0.01f,
            onVisualContact = contacts.Add };
        configure?.Invoke(body);
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body });
        return body;
    }
    static void Run()
    {
        using var logs = Log.LockMessages(); DeepProfiler.enabled = false;
        var harmony = new Harmony("DeltaV.RailgunRegression");
        harmony.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(typeof(CompProjectileInterceptor).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(CompProjectileInterceptor), "Active"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ShieldActive)));
        harmony.Patch(AccessTools.Method(typeof(ImpactSoundUtility), "PlayImpactSound"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"),
            prefix: new HarmonyMethod(typeof(Program), nameof(NotFlammable)));
        harmony.Patch(AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.CreateClassProcessor(typeof(Patch_TurretTop_SpaceWeaponRecoil)).Patch();
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(TurretTop), "DrawTurret")).Prefixes
            .Any(p => p.PatchMethod.DeclaringType == typeof(Patch_TurretTop_SpaceWeaponRecoil)),
            "visual recoil prefix binds to the real RimWorld turret draw signature");

        var game = (Game)Raw(typeof(Game)); Set(game, "maps", new List<Map>()); Current.Game = game;
        var world = (World)Raw(typeof(World)); Set(game, "worldInt", world);
        var layer = (PlanetLayer)Raw(typeof(PlanetLayer));
        var tiles = AccessTools.Field(typeof(PlanetLayer), "tileIDToNeighbors_offsets");
        object array = Activator.CreateInstance(tiles.FieldType); Set(array, "m_Length", 1); tiles.SetValue(layer, array);
        AccessTools.Field(typeof(OrbitSphere), "cachedWorld").SetValue(null, world);
        AccessTools.Field(typeof(OrbitSphere), "cachedLayer").SetValue(null, layer);
        ((Dictionary<PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "RadiusByLayer").GetValue(null))[layer] = 10000;
        ((Dictionary<PlanetLayer, float>)AccessTools.Field(typeof(OrbitSphere), "WorldPerCellByLayer").GetValue(null))[layer] = 1;
        game.tickManager = (TickManager)Raw(typeof(TickManager));
        universe = new UniverseManager(game); game.components = new List<GameComponent> { universe };
        bullet = new DamageDef { defName = "Bullet", workerClass = typeof(DamageWorker), harmsHealth = true };
        DamageDefOf.Bullet = bullet;
        rail = MakeDef("DeltaV_Projectile_RailgunSlug");
        rail.comps.Add(new CompProperties_SpaceProjectile { speed = 180, hasMap = false });
        fragment = MakeDef("DeltaV_Projectile_RailFragment");
        fragment.comps.Add(new CompProperties_SpaceProjectile { speed = 80, hasMap = false });
        shieldDef = MakeDef("DeltaV_ShieldGenerator");
        var defs = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        foreach (var node in defs.Root.Elements().Where(x =>
            x.Element("projectileDefs")?.Elements("li").Any(v => v.Value == rail.defName || v.Value == fragment.defName) == true))
        {
            var effect = new SpaceImpactEffectDef { defName = node.Element("defName").Value,
                projectileDefs = node.Element("projectileDefs").Elements("li").Where(v => v.Value == rail.defName || v.Value == fragment.defName)
                    .Select(v => v.Value == rail.defName ? rail : fragment).ToList(),
                targetsSubstructure = (bool?)node.Element("targetsSubstructure") ?? false,
                targetThingDefs = node.Element("targetThingDefs") == null ? null : new List<ThingDef> { shieldDef },
                attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
                defenderEffect = new ImpactOutcomePart() };
            foreach (var item in node.Element("defenderEffect").Elements())
            {
                var field = typeof(ImpactOutcomePart).GetField(item.Name.LocalName);
                if (field == null) throw new Exception(item.Name.ToString());
                object value = field.FieldType == typeof(DamageDef) ? bullet
                    : field.FieldType.IsEnum ? Enum.Parse(field.FieldType, item.Value)
                    : Convert.ChangeType(item.Value, field.FieldType, CultureInfo.InvariantCulture);
                field.SetValue(effect.defenderEffect, value);
            }
            DefDatabase<SpaceImpactEffectDef>.Add(effect);
        }
        SpaceImpactEffects.ResetCache();

        Reset(); var first = Shield(3000); var second = Shield(3000);
        var shot = Fire(24); // 7200 budget, co-located full shields.
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 0 && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 1200) < 2, "coincident full shields subtract 6000 once and retain 1200");
        Check(contacts.Count == 2 && contacts.All(c => c.shieldMode == SpaceShieldMode.Guard && c.shieldBroken)
            && contacts[0].damageScale > contacts[1].damageScale,
            "coincident shield breaks each retain their own visual outcome and incoming budget");
        Reset(); first = Shield(1); second = Shield(3000);
        shot = Fire(12);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 0 && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 599) < 2, "one-point shield cannot erase a 3600-budget round");
        Reset(); first = Shield(3000); second = Shield(3000);
        shot = Fire(12);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 2400 && shot.dead,
            "insufficient budget stops at the next shield");
        Check(contacts.Count == 2 && contacts[0].shieldBroken && !contacts[1].shieldBroken,
            "shield absorption does not display a false break");
        Reset(); first = Shield(3000); shot = Fire(10);
        Check(first.currentHitPoints == 0 && shot.dead, "exact shield depletion leaves no free residual");
        Reset(); var outer = Add(10, 5, 420); var inner = Add(55, 5, 420); var console = Add(65, 5, 300);
        shot = Fire(12);
        Check(outer.Destroyed && inner.Destroyed && console.Destroyed && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 2460) < 2,
            "empty compartment costs nothing; one shot crosses 45 cells and destroys deep console");
        Check(contacts.Count == 3 && contacts.All(c => c.destroyed && c.penetrated)
            && contacts[0].localPoint.x < contacts[1].localPoint.x
            && contacts[1].localPoint.x < contacts[2].localPoint.x
            && contacts[0].damageScale > contacts[2].damageScale,
            "every real penetration retains an ordered independent visual snapshot");
        Reset(); var armor = Add(10, 5, 5000); console = Add(55, 5, 300); shot = Fire(12);
        Check(!armor.Destroyed && armor.HitPoints == 1400 && console.HitPoints == 300 && shot.dead,
            "surviving armor stops the projectile without leaking damage behind it");
        Check(contacts.Count == 1 && !contacts[0].destroyed && !contacts[0].penetrated,
            "stopped round has one surface hit and no forward penetration jet");
        Reset(); armor = Add(10, 5, 50); armor.invulnerable = true; console = Add(55, 5, 300); shot = Fire(12);
        Check(armor.HitPoints == 50 && console.HitPoints == 300 && shot.dead,
            "absorbed damage cannot grant penetration");
        Check(contacts.Count == 1 && !contacts[0].destroyed && !contacts[0].penetrated,
            "invulnerable armor does not emit destruction or penetration feedback");
        Reset(); first = Shield(3000); console = Add(55, 5, 300); shot = Fire(12);
        Check(first.currentHitPoints == 0 && console.Destroyed && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 300) < 2, "shield and hull share a single remaining fraction");
        Reset(); outer = Add(10, 5, 1); console = Add(55, 5, 300); shot = Fire(1, 540, fragment);
        Check(outer.Destroyed && console.HitPoints == 300 && shot.dead,
            "even fast rail fragments stop at their first structure and cannot deep-penetrate");

        Reset(); first = Shield(0);
        var generator = (Target)first.parent;
        map.thingGrid.Deregister(generator); Set(generator, "positionInt", new IntVec3(40, 0, 5));
        map.thingGrid.Register(generator);
        shot = Fire(12);
        Check(generator.Destroyed && !shot.dead && Math.Abs(shot.damageScale * 300 - 3100) < 2,
            "empty shield generator takes physical hull damage instead of reusing shield absorption");

        // Exercise the production projectile service writeback across separate sweeps.
        Reset(); first = Shield(3000); console = Add(55, 5, 420);
        var service = new SpaceProjectileService(universe);
        Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(5, 5.5f));
        Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(hull, map, new Vector2(5, 5.5f), Vector2.right);
        OrbitSphere.Basis(start, out Vector3 east, out Vector3 north);
        Check(service.Launch(rail, null, start, east * tangent.x + north * tangent.y, null, null, default,
            new SpaceProjectileLaunchSpec(540, 0, 540, 4)), "real projectile service launches budget round");
        var records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
        object record = records[0];
        var container = (SpaceObject)AccessTools.Property(record.GetType(), "container").GetValue(record);
        container.pose.position = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(35, 5.5f));
        service.ResolveCollisions();
        float scale = (float)AccessTools.Field(record.GetType(), "damageScale").GetValue(record);
        Check(first.currentHitPoints == 0 && Math.Abs(scale * 900 - 600) < 2
            && Math.Abs(container.pose.velocity.magnitude - 540) < 0.01f,
            "shield loss persists in projectile record without double speed attenuation");
        Set(record, "prevPosition", container.pose.position);
        container.pose.position = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f));
        service.ResolveCollisions();
        scale = (float)AccessTools.Field(record.GetType(), "damageScale").GetValue(record);
        Check(console.Destroyed && Math.Abs(scale * 900 - 180) < 2,
            "next tick uses remaining 600 budget rather than restoring original 3600");

        // Real line geometry/payment and fragment creation, with zero-strength active defense.
        Reset();
        var battery = new CompPowerBattery(); Set(battery, "storedEnergy", 10f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        var power = new CompPowerTrader(); Set(power, "powerOnInt", true); power.transNet = net;
        var kinetic = new CompSpaceShield { props = new CompProperties_SpaceShield(),
            mode = SpaceShieldMode.Kinetic, projection = 10, length = 20 };
        var intercept = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        intercept.props = new CompProperties_ProjectileInterceptor { radius = 12 };
        var emitter = Add(40, 8, 500, intercept, kinetic, power);
        emitter.Rotation = Rot4.West;
        first = Shield(3000, 6); console = Add(55, 5, 420);
        bool shattered = false;
        service = new SpaceProjectileService(universe);
        var railProps = rail.GetCompProperties<CompProperties_SpaceProjectile>();
        railProps.shieldFragmentDef = fragment; railProps.shieldFragmentCount = 4;
        var attack = new SpaceObject(nextId++, new SpacePose(start, 0));
        attack.pose.velocity = tangent * 540;
        shot = new SpaceCollision.SweptBody { attacker = attack, projectileDef = rail, from = start,
            to = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f)), damageScale = 8,
            onVisualContact = contacts.Add,
            onShieldShatter = (hit, defender, direction, speed) =>
            {
                shattered = true;
                SpaceShieldProjectileBehavior.SpawnFragments(service, rail, null, defender, null, hit, direction, speed);
            } };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
        records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
        Check(shattered && shot.dead && first.currentHitPoints == 3000 && console.HitPoints == 420
            && Math.Abs(battery.StoredEnergy - 8) < 0.001f, "kinetic line shatters high-budget round for base pulse before inner guard");
        kinetic.GetSegment(out Vector2 lineStart, out Vector2 lineEnd, out _);
        Check(contacts.Count == 1 && contacts[0].shieldMode == SpaceShieldMode.Kinetic
            && (contacts[0].lineStart - lineStart).sqrMagnitude < 0.001f
            && (contacts[0].lineEnd - lineEnd).sqrMagnitude < 0.001f,
            "kinetic pulse uses the exact finite collision segment without an inner hull hit");
        Check(records.Count == 4 && records.Cast<object>().All(p =>
            (ThingDef)AccessTools.Field(p.GetType(), "def").GetValue(p) == fragment
            && (float)AccessTools.Field(p.GetType(), "damageScale").GetValue(p) == 1f),
            "real shatter creates four independent fragments without parent damage budget");
        contacts.Clear();
        SpaceCollision.ClearCaches();
        kinetic.mode = SpaceShieldMode.Electromagnetic;
        kinetic.electromagneticStrengthPercent = 100;
        var laser = MakeDef("fixtureLaser");
        laser.comps.Add(new CompProperties_SpaceProjectile { laserRange = 100, shieldLaserTurnEnergy = 1 });
        var segments = new List<(Vector3 from, Vector3 to)>();
        shot = new SpaceCollision.SweptBody { attacker = attack, projectileDef = laser, from = start,
            to = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f)),
            onVisualContact = contacts.Add, onShieldRedirect = (a, b) => segments.Add((a, b)) };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
        Check(contacts.Count == 1 && contacts[0].shieldMode == SpaceShieldMode.Electromagnetic
            && segments.Count == 1 && OrbitSphere.Offset(segments[0].to, contacts[0].position).magnitude < 0.01f
            && !contacts[0].destroyed && !contacts[0].penetrated && first.currentHitPoints == 3000,
            "electromagnetic response matches the true beam bend without a fake hull impact");
        var engineering = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_Engineering.xml");
        XElement couplingNode = engineering.Root.Elements("ThingDef")
            .Single(x => (string)x.Element("defName") == "DeltaV_Beam_InertiaCoupler").Element("comps").Element("li");
        var coupler = MakeDef("DeltaV_Beam_InertiaCoupler");
        coupler.comps.Add(new CompProperties_SpaceProjectile {
            laserRange = (float)couplingNode.Element("laserRange"), hasMap = false,
            ignoresShields = (bool)couplingNode.Element("ignoresShields"),
            continuousBeam = (bool)couplingNode.Element("continuousBeam"),
            visualStyle = CombatVisualStyle.Coupling });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef {
            defName = "fixtureCoupling", projectileDefs = new List<ThingDef> { coupler }, buildingHitChance = 1,
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.CoupleMotion },
            attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf } });
        SpaceImpactEffects.ResetCache();
        contacts.Clear();
        float energyBefore = battery.StoredEnergy;
        shot = Fire(1, def: coupler);
        Check(shot.resolvedHit && shot.resolvedHitCell == console.Position && !shot.didRedirect
            && !shot.resolvedHitShield && first.currentHitPoints == 3000 && battery.StoredEnergy == energyBefore
            && contacts.Count == 1 && !contacts[0].shieldMode.HasValue && console.HitPoints == 420,
            "coupling crosses electromagnetic line and guard shield without drain, bending or hull damage");
        kinetic.mode = SpaceShieldMode.Kinetic;
        SpaceCollision.ClearCaches(); contacts.Clear();
        shot = Fire(1, def: coupler);
        Check(shot.resolvedHitCell == console.Position && !shot.resolvedHitShield
            && first.currentHitPoints == 3000 && battery.StoredEnergy == energyBefore,
            "coupling also crosses kinetic line while preserving shields");
        XElement boardingNode = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_ShuttlesAndMoonPool.xml")
            .Descendants("ThingDef").Single(x => (string)x.Element("defName") == "DeltaV_ShuttleBoarding")
            .Element("comps").Elements("li").Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
        var boarding = MakeDef("DeltaV_ShuttleBoarding");
        boarding.comps.Add(new CompProperties_SpaceProjectile {
            guided = false,
            ignoresShields = (bool)boardingNode.Element("ignoresShields") });
        var boardingEffects = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml")
            .Root.Elements().Where(x => x.Element("projectileDefs")?.Elements("li")
                .Any(d => (string)d == boarding.defName) == true).ToList();
        Check(!boardingEffects.Any(x => (string)x.Element("defenderEffect")?.Element("kind") == "ShieldDrain"),
            "boarding has no shield-drain or shield-surface landing outcome");
        XElement boardingStructure = boardingEffects.Single(x => (string)x.Element("defName") == "DeltaV_Impact_BoardingVsStructure");
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef {
            defName = "fixtureBoarding", projectileDefs = new List<ThingDef> { boarding }, buildingHitChance = 1,
            attackerEffect = new ImpactOutcomePart { kind = Enum.Parse<ImpactEffectKind>(
                (string)boardingStructure.Element("attackerEffect").Element("kind")) } });
        SpaceImpactEffects.ResetCache();
        foreach (SpaceShieldMode mode in new[] { SpaceShieldMode.Kinetic, SpaceShieldMode.Electromagnetic })
        {
            kinetic.mode = mode;
            SpaceCollision.ClearCaches(); contacts.Clear();
            shot = Fire(1, speed: 12, def: boarding);
            Check(shot.dead && shot.resolvedHitCell == console.Position && !shot.resolvedHitShield
                && !shot.didRedirect && first.currentHitPoints == 3000 && battery.StoredEnergy == energyBefore
                && contacts.Count == 1 && !contacts[0].shieldMode.HasValue,
                "boarding crosses " + mode + " and guard shields, reaching the physical hull without shield costs");
        }
        for (int rotation = 0; rotation < 4; rotation++)
        {
            Reset();
            Add(55, 4, 420);
            void Nose(SpaceCollision.SweptBody body)
            {
                body.attackerBuilding = new Building { def = boarding, Rotation = new Rot4(rotation) };
                body.attacker.pose.angle = Mathf.PI * 0.5f - ShipOrientation.AngleFromRot4(new Rot4(rotation));
                body.sweepRadius = Mathf.Sqrt(34) * 0.5f;
                body.noseHalfWidth = 1.5f; body.noseForwardOffset = 2;
            }
            shot = Fire(1, speed: 12, def: boarding, configure: Nose);
            Check(!shot.resolvedHit, "side-slipping boarding hull does not ram with its side or rear " + rotation);
            var noseTarget = Add(55, 7, 420);
            SpaceCollision.ClearCaches();
            shot = Fire(1, speed: 12, def: boarding, configure: Nose);
            Check(shot.dead && shot.resolvedHitCell == noseTarget.Position,
                "side-slipping boarding nose collision follows real hull heading " + rotation);
        }
        Reset(); first = Shield(3000);
        generator = (Target)first.parent;
        map.thingGrid.Deregister(generator); Set(generator, "positionInt", new IntVec3(40, 0, 5));
        map.thingGrid.Register(generator);
        shot = Fire(1, def: coupler);
        Check(shot.resolvedHitCell == generator.Position && !shot.resolvedHitShield
            && first.currentHitPoints == 3000, "shield bypass still hits the physical generator");
        CheckContinuousBeam(coupler, engineering);
        CheckEnergy();
        CheckVisualAnchors();
    }

    static void CheckContinuousBeam(ThingDef coupler, XDocument defs)
    {
        Reset();
        var service = new SpaceProjectileService(universe);
        var emitter = Add(5, 5, 500);
        var beams = (IList)AccessTools.Field(typeof(SpaceProjectileService), "beams").GetValue(service);
        Vector2 point = new Vector2(5, 5.5f);
        bool Launch()
        {
            Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, point);
            Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(hull, map, point, Vector2.right);
            OrbitSphere.Basis(start, out var east, out var north);
            return service.Launch(coupler, hull, start, east * tangent.x + north * tangent.y,
                null, emitter, default, new SpaceProjectileLaunchSpec(2000, 0, 2000, 1));
        }
        int tick = Find.TickManager.TicksGame;
        Check(Launch(), "continuous beam enters the shared launch path");
        hull.pose.position = OrbitSphere.Move(hull.pose.position, new Vector2(12, 3));
        hull.pose.angle = 0.4f;
        service.ResolveCollisions();
        object beam = beams[0];
        bool Visible(int at) => (bool)AccessTools.Method(beam.GetType(), "VisibleAt").Invoke(beam, new object[] { at });
        Check(OrbitSphere.Offset((Vector3)AccessTools.Field(beam.GetType(), "from").GetValue(beam),
            SpaceTransform.MapPointToUniverse(hull, map, point)).magnitude < 0.01f,
            "continuous beam starts at the emitter after same-tick translation and rotation");
        Check(Visible(tick) && Visible(tick) && !Visible(tick + 1),
            "continuous beam stays lit while paused and is invisible after one missed tick");
        Set(Find.TickManager, "ticksGameInt", tick + 1); service.Tick();
        Check(Launch(), "held fire renews the next tick");
        service.ResolveCollisions();
        Check(beams.Count == 1 && ReferenceEquals(beams[0], beam) && Visible(tick + 1),
            "held fire updates one beam instead of stacking pulse afterimages");

        XElement wpNode = defs.Root.Elements("ThingDef").Single(x => (string)x.Element("defName") == "DeltaV_InertiaCoupler")
            .Element("comps").Element("li");
        var wp = new CompProperties_SpaceWeapon { projectileDef = coupler,
            shotEnergy = (float)wpNode.Element("shotEnergy"), cooldownTicks = (int)wpNode.Element("cooldownTicks") };
        var battery = new CompPowerBattery(); Set(battery, "storedEnergy", 3f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
        var weapon = new CompSpaceWeapon { props = wp };
        Add(6, 5, 500, power, weapon);
        var transaction = new SpaceWeaponShot(universe, weapon, hull, default) {
            projectileProps = coupler.GetCompProperties<CompProperties_SpaceProjectile>(), muzzleMap = point,
            muzzleUniverse = SpaceTransform.MapPointToUniverse(hull, map, point) };
        var solution = new SpaceWeaponAccelerationSolution(0, null, new List<CompSpaceRailgun>());
        for (int i = 0; i < 12; i++)
        {
            Check((bool)AccessTools.Method(typeof(SpaceWeaponShot), "PrepareEnergy").Invoke(transaction, new object[] { solution }),
                "continuous energy reservation " + i);
            AccessTools.Method(typeof(SpaceWeaponShot), "Commit").Invoke(transaction, null);
        }
        Check(battery.StoredEnergy == 0f
            && !(bool)AccessTools.Method(typeof(SpaceWeaponShot), "PrepareEnergy").Invoke(transaction, new object[] { solution }),
            "twelve ticks consume exactly the old pulse cost and an empty battery refuses the next tick");
        // Continue the already-lit emitter, avoiding Unity's native first-start audio in this fixture.
        weapon.parent = emitter;
        service.RegisterShotVisuals(transaction);
        Check(((IList)AccessTools.Field(typeof(SpaceProjectileService), "muzzleFlashes").GetValue(service)).Count == 0,
            "continuous illumination does not emit repeated muzzle flashes");
        Set(Find.TickManager, "ticksGameInt", tick + 2); service.Tick();
        Check(!Visible(tick + 2), "release or failed power payment leaves no visible beam");
        Set(Find.TickManager, "ticksGameInt", tick + 3); service.Tick();
        Check(beams.Count == 0, "stopped beam cache is retired");
        Check(Launch(), "continuous illumination can restart");
        service.ResolveCollisions();
        AccessTools.Method(typeof(SpaceProjectileService), "NotifyContainerRemoving").Invoke(service, new object[] { hull });
        Check(beams.Count == 0, "source removal clears continuous beams immediately");
        Set(Find.TickManager, "ticksGameInt", tick);
    }

    static void CheckVisualAnchors()
    {
        Reset();
        var service = new SpaceProjectileService(universe);
        Vector2 point = new Vector2(20, 5);
        hull.pose.velocity = new Vector2(5, 2);
        hull.pose.angularVelocity = 0.4f;
        Vector3 origin = SpaceTransform.MapPointToUniverse(hull, map, point);
        object anchor = AccessTools.Method(typeof(SpaceProjectileService), "Anchor")
            .Invoke(service, new object[] { hull, point, origin });
        Vector3 Position() => (Vector3)AccessTools.Method(typeof(SpaceProjectileService), "AnchorPosition")
            .Invoke(service, new[] { anchor });
        Vector2 velocity = (Vector2)AccessTools.Field(anchor.GetType(), "velocity").GetValue(anchor);
        Vector2 expectedVelocity = SpaceTransform.VelocityAtLocalPoint(hull.pose,
            SpaceTransform.MapToShipLocal(map, point), origin);
        Check((velocity - expectedVelocity).sqrMagnitude < 0.00001f
            && (velocity - hull.pose.velocity).sqrMagnitude > 0.01f,
            "detached particles inherit the struck rigid point's angular and linear velocity");
        hull.pose.position = OrbitSphere.Move(hull.pose.position, new Vector2(30, 12));
        hull.pose.angle = 1.2f;
        Check(OrbitSphere.Offset(Position(), SpaceTransform.MapPointToUniverse(hull, map, point)).magnitude < 0.01f,
            "attached flash follows a translated and rotated hull at the same local point");
        Vector3 paused = Position();
        Check(Position() == paused, "repeated render queries at one game tick do not advance effects");
        universe.objects.Clear();
        int before = Find.TickManager.TicksGame;
        Set(Find.TickManager, "ticksGameInt", before + 30);
        Check(OrbitSphere.Offset(Position(), OrbitSphere.Move(origin, velocity * 0.5f)).magnitude < 0.01f,
            "removed hull releases visual anchor into captured drift without map access");
        Set(Find.TickManager, "ticksGameInt", before);
    }

    static void CheckEnergy()
    {
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        XElement Comp(string name, string type) => xml.Root.Elements("ThingDef")
            .Single(x => (string)x.Element("defName") == name).Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_" + type);
        foreach (bool large in new[] { false, true })
        {
            Reset();
            string suffix = large ? "Large" : "";
            XElement loader = Comp("DeltaV_Weapon_RailgunLoader" + suffix, "SpaceWeapon");
            XElement accelerator = Comp("DeltaV_Weapon_RailgunAccelerator" + suffix, "SpaceRailgun");
            var wp = new CompProperties_SpaceWeapon { shotEnergy = (float)loader.Element("shotEnergy"),
                impactScale = (float)loader.Element("impactScale") };
            var rp = new CompProperties_SpaceRailgun { shotEnergy = (float)accelerator.Element("shotEnergy"),
                speedBonus = (float)accelerator.Element("speedBonus") };
            var battery = new CompPowerBattery();
            var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
            var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
            var weapon = new CompSpaceWeapon { props = wp }; Add(5, 5, 500, power, weapon);
            var segments = new List<CompSpaceRailgun>();
            for (int i = 0; i < 3; i++)
            {
                var segment = new CompSpaceRailgun { props = rp };
                var sp = new CompPowerTrader { transNet = net }; Set(sp, "powerOnInt", true);
                Add(6 + i, 5, 500, sp, segment); segments.Add(segment);
            }
            var solution = new SpaceWeaponAccelerationSolution(3 * rp.speedBonus, null, segments);
            var transaction = new SpaceWeaponShot(universe, weapon, hull, default);
            bool Plan(float available)
            {
                Set(battery, "storedEnergy", available);
                return (bool)AccessTools.Method(typeof(SpaceWeaponShot), "PrepareEnergy")
                    .Invoke(transaction, new object[] { solution });
            }
            Check(!Plan(wp.shotEnergy - 1) && battery.StoredEnergy == wp.shotEnergy - 1,
                suffix + " insufficient loader pulse refuses without spending");
            Check(Plan(wp.shotEnergy + rp.shotEnergy)
                && (float)AccessTools.Field(typeof(SpaceWeaponShot), "activeSpeedBonus").GetValue(transaction) == rp.speedBonus
                && battery.StoredEnergy == wp.shotEnergy + rp.shotEnergy,
                suffix + " shared battery is reserved once and cannot fund three segments with one pulse");
            // Leave the middle segment unfunded; a later segment on its own net still contributes.
            var separate = (PowerNet)Raw(typeof(PowerNet));
            var extraBattery = new CompPowerBattery(); Set(extraBattery, "storedEnergy", rp.shotEnergy);
            separate.batteryComps = new List<CompPowerBattery> { extraBattery };
            segments[2].parent.GetComp<CompPowerTrader>().transNet = separate;
            Check(Plan(wp.shotEnergy + rp.shotEnergy)
                && (float)AccessTools.Field(typeof(SpaceWeaponShot), "activeSpeedBonus").GetValue(transaction) == 2 * rp.speedBonus,
                suffix + " unfunded middle segment does not block later funded acceleration");
            int count = large ? 1 : 3;
            float velocity = 180 + count * rp.speedBonus;
            float budget = SpaceImpactEffects.ComputeDamage(
                SpaceImpactEffects.Resolve(rail, shieldDef, true).defenderEffect, velocity, 0) * wp.impactScale;
            Check(Math.Abs(budget - (large ? 4000 : 3600)) < 0.01f
                && wp.shotEnergy + count * rp.shotEnergy == (large ? 6000 : 5400),
                suffix + " real Defs meet declared shield-breaking budget and energy threshold");
            XElement cap = Comp("DeltaV_Building_RailgunCapacitor" + suffix, "SpaceWeaponCapacitor");
            Check((float)cap.Element("capacityWattDays") == 3 * rp.shotEnergy
                && (float)cap.Element("initialEnergyPercent") == 0
                && Math.Abs((float)cap.Element("capacityWattDays") * 60000 / (float)cap.Element("chargingWatts") - 216000) < 1,
                suffix + " capacitor stores three paid pulses, starts empty and recharges slowly");
        }
    }
}







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
        float from = 5, float to = 70)
    {
        Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(from, 5.5f));
        Vector3 end = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(to, 5.5f));
        var attacker = new SpaceObject(nextId++, new SpacePose(start, 0));
        attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(hull, map,
            new Vector2(from, 5.5f), Vector2.right) * speed;
        var body = new SpaceCollision.SweptBody { attacker = attacker, projectileDef = def ?? rail,
            from = start, to = end, damageScale = scale, sweepRadius = 0.01f };
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
        Reset(); first = Shield(1); second = Shield(3000);
        shot = Fire(12);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 0 && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 599) < 2, "one-point shield cannot erase a 3600-budget round");
        Reset(); first = Shield(3000); second = Shield(3000);
        shot = Fire(12);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 2400 && shot.dead,
            "insufficient budget stops at the next shield");
        Reset(); first = Shield(3000); shot = Fire(10);
        Check(first.currentHitPoints == 0 && shot.dead, "exact shield depletion leaves no free residual");
        Reset(); var outer = Add(10, 5, 420); var inner = Add(55, 5, 420); var console = Add(65, 5, 300);
        shot = Fire(12);
        Check(outer.Destroyed && inner.Destroyed && console.Destroyed && !shot.dead
            && Math.Abs(shot.damageScale * 300 - 2460) < 2,
            "empty compartment costs nothing; one shot crosses 45 cells and destroys deep console");
        Reset(); var armor = Add(10, 5, 5000); console = Add(55, 5, 300); shot = Fire(12);
        Check(!armor.Destroyed && armor.HitPoints == 1400 && console.HitPoints == 300 && shot.dead,
            "surviving armor stops the projectile without leaking damage behind it");
        Reset(); armor = Add(10, 5, 50); armor.invulnerable = true; console = Add(55, 5, 300); shot = Fire(12);
        Check(armor.HitPoints == 50 && console.HitPoints == 300 && shot.dead,
            "absorbed damage cannot grant penetration");
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
            onShieldShatter = (hit, defender, direction, speed) =>
            {
                shattered = true;
                SpaceShieldProjectileBehavior.SpawnFragments(service, rail, null, defender, null, hit, direction, speed);
            } };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
        records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
        Check(shattered && shot.dead && first.currentHitPoints == 3000 && console.HitPoints == 420
            && Math.Abs(battery.StoredEnergy - 8) < 0.001f, "kinetic line shatters high-budget round for base pulse before inner guard");
        Check(records.Count == 4 && records.Cast<object>().All(p =>
            (ThingDef)AccessTools.Field(p.GetType(), "def").GetValue(p) == fragment
            && (float)AccessTools.Field(p.GetType(), "damageScale").GetValue(p) == 1f),
            "real shatter creates four independent fragments without parent damage budget");
        CheckEnergy();
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







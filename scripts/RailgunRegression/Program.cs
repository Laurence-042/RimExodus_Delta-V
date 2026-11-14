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
internal static partial class Program
{
    static int checks, nextId = 7000;
    static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    static void Set(object obj, string field, object value) =>
        AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    static bool Skip() => false;
    static bool NotFlammable(ref bool __result) { __result = false; return false; }
    static bool ShieldActive(CompProjectileInterceptor __instance, ref bool __result)
    { __result = __instance.parent.TryGetComp<CompSpaceShield>()?.HasGuardCapacity ?? (__instance.currentHitPoints > 0); return false; }
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
        try { Run(args.Contains("--energy-only")); Console.WriteLine($"PASS {checks} railgun checks");
            if(args.Contains("--energy-impulse-audit")) RunEnergyImpulsePenetrationAudit();
            else if(args.Contains("--role-penetration-audit")) RunRolePenetrationAudit(); return 0; }
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
        public readonly List<ShipBase> members = new();
        public override IEnumerable<ShipBase> Ships => members;
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
    static void Reset(int height = 12)
    {
        contacts.Clear();
        SpaceCollision.ClearCaches();
        map = (Map)Raw(typeof(Map));
        map.uniqueID = nextId++; map.info = new MapInfo { Size = new IntVec3(80, 1, height) };
        map.damageWatcher = (DamageWatcher)Raw(typeof(DamageWatcher)); map.cellIndices = new CellIndices(map); map.components = new List<MapComponent>();
        map.thingGrid = new ThingGrid(map); map.terrainGrid = new TerrainGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Region);
        Find.Maps.Clear(); Find.Maps.Add(map);
        hull = new Hull(map); universe.objects.Clear(); universe.objects.Add(hull);
        Set(universe, "objectById", null); Set(universe, "objectByMap", null);
    }
    static SpaceCollision.SweptBody Fire(float budget, float speed = 180, ThingDef def = null,
        float from = 5, float to = 70, Action<SpaceCollision.SweptBody> configure = null, float lane = 5.5f,
        float? endLane = null)
    {
        Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(from, lane));
        Vector3 end = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(to, endLane ?? lane));
        var attacker = new SpaceObject(nextId++, new SpacePose(start, 0));
        attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(hull, map,
            new Vector2(from, lane), new Vector2(to - from, (endLane ?? lane) - lane).normalized) * speed;
        var body = new SpaceCollision.SweptBody { attacker = attacker, projectileDef = def ?? rail,
            from = start, to = end, damageScale = 1f,
            mass = budget / Math.Max(.001f, SpaceImpactEffects.ComputeDamage(SpaceImpactEffects.Resolve(def ?? rail, null).defenderEffect, speed, 0)),
            sweepRadius = 0.01f,
            onVisualContact = contacts.Add };
        configure?.Invoke(body);
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { body });
        return body;
    }
    static float RemainingBudget(SpaceCollision.SweptBody shot) =>
        SpaceImpactEffects.ComputeDamage(SpaceImpactEffects.Resolve(shot.projectileDef, null).defenderEffect,
            shot.attacker.pose.velocity.magnitude, 0f, 1f, shot.mass) * shot.damageScale;
    static void Run(bool energyOnly)
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
        if (Environment.GetCommandLineArgs().Contains("--engagement-baseline")
            || Environment.GetCommandLineArgs().Contains("--engagement-forecast"))
        { CheckEngagementProtectionBaseline(); return; }
        if (Environment.GetCommandLineArgs().Contains("--casemate-only")) { CheckCasemate(); return; }
        if (Environment.GetCommandLineArgs().Contains("--cargo-lifecycle-only")) { CheckCargoLifecycle(); return; }
        if (energyOnly) { CheckEnergy(); CheckBatteryIsolation(); CheckShipReactor(); CheckCargoTemperature(); CheckEquipmentIntegration(); return; }

        CheckGuardCenters();

        Reset(); var first = Shield(3000); var second = Shield(3000);
        var shot = Fire(7200); // 7200 budget, co-located full shields.
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 0 && !shot.dead
            && Math.Abs(RemainingBudget(shot) - 1200) < 2, "coincident full shields subtract 6000 once and retain 1200");
        Check(contacts.Count == 2 && contacts.All(c => c.shieldMode == SpaceShieldMode.Guard && c.shieldBroken)
            && contacts[0].damageScale > contacts[1].damageScale,
            "coincident shield breaks each retain their own visual outcome and incoming budget");
        Reset(); first = Shield(1); second = Shield(3000);
        shot = Fire(3600);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 0 && !shot.dead
            && Math.Abs(RemainingBudget(shot) - 599) < 2, "one-point shield cannot erase a 3600-budget round");
        Reset(); first = Shield(3000); second = Shield(3000);
        shot = Fire(3600);
        Check(first.currentHitPoints == 0 && second.currentHitPoints == 2400 && shot.dead,
            "insufficient budget stops at the next shield");
        Check(contacts.Count == 2 && contacts[0].shieldBroken && !contacts[1].shieldBroken,
            "shield absorption does not display a false break");
        Reset(); first = Shield(3000); shot = Fire(3000);
        Check(first.currentHitPoints == 0 && shot.dead, "exact shield depletion leaves no free residual");
        Reset(); var outer = Add(10, 5, 420); var inner = Add(55, 5, 420); var console = Add(65, 5, 300);
        shot = Fire(3600);
        Check(outer.Destroyed && inner.Destroyed && console.Destroyed && !shot.dead
            && Math.Abs(RemainingBudget(shot) - 2460) < 2,
            "empty compartment costs nothing; one shot crosses 45 cells and destroys deep console");
        Check(contacts.Count == 3 && contacts.All(c => c.destroyed && c.penetrated)
            && contacts[0].localPoint.x < contacts[1].localPoint.x
            && contacts[1].localPoint.x < contacts[2].localPoint.x
            && contacts[0].damageScale > contacts[2].damageScale,
            "every real penetration retains an ordered independent visual snapshot");
        Reset(); var armor = Add(10, 5, 5000); console = Add(55, 5, 300); shot = Fire(3600);
        Check(!armor.Destroyed && armor.HitPoints == 1400 && console.HitPoints == 300 && shot.dead,
            "surviving armor stops the projectile without leaking damage behind it: armor=" + armor.HitPoints + ", behind=" + console.HitPoints + ", dead=" + shot.dead);
        Check(contacts.Count == 1 && !contacts[0].destroyed && !contacts[0].penetrated,
            "stopped round has one surface hit and no forward penetration jet");
        Reset(); armor = Add(10, 5, 50); armor.invulnerable = true; console = Add(55, 5, 300); shot = Fire(3600);
        Check(armor.HitPoints == 50 && console.HitPoints == 300 && shot.dead,
            "absorbed damage cannot grant penetration");
        Check(contacts.Count == 1 && !contacts[0].destroyed && !contacts[0].penetrated,
            "invulnerable armor does not emit destruction or penetration feedback");
        Reset(); first = Shield(3000); console = Add(55, 5, 300); shot = Fire(3600);
        Check(first.currentHitPoints == 0 && console.Destroyed && !shot.dead
            && Math.Abs(RemainingBudget(shot) - 300) < 2, "shield and hull share a single remaining fraction");
        Reset(); outer = Add(10, 5, 1); console = Add(55, 5, 300); shot = Fire(300, 540, fragment);
        Check(outer.Destroyed && console.HitPoints == 300 && shot.dead,
            "even fast rail fragments stop at their first structure and cannot deep-penetrate");

        Reset(); first = Shield(0);
        var generator = (Target)first.parent;
        map.thingGrid.Deregister(generator); Set(generator, "positionInt", new IntVec3(40, 0, 5));
        map.thingGrid.Register(generator);
        shot = Fire(3600);
        Check(generator.Destroyed && !shot.dead && Math.Abs(RemainingBudget(shot) - 3100) < 2,
            "empty shield generator takes physical hull damage instead of reusing shield absorption");

        // Exercise the production projectile service writeback across separate sweeps.
        Reset(); first = Shield(3000); console = Add(55, 5, 420);
        var service = new SpaceProjectileService(universe);
        Vector3 start = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(5, 5.5f));
        Vector2 tangent = SpaceTransform.MapDirectionToUniverseTangent(hull, map, new Vector2(5, 5.5f), Vector2.right);
        OrbitSphere.Basis(start, out Vector3 east, out Vector3 north);
        Check(service.Launch(rail, null, start, east * tangent.x + north * tangent.y, null, null, default,
            new SpaceProjectileLaunchSpec(540, 0, 540, mass: 3600f / (8f * 81f))), "real projectile service launches budget round");
        var records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
        object record = records[0];
        var container = (SpaceObject)AccessTools.Property(record.GetType(), "container").GetValue(record);
        container.pose.position = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(35, 5.5f));
        service.ResolveCollisions();
        float scale = (float)AccessTools.Field(record.GetType(), "damageScale").GetValue(record);
        Check(first.currentHitPoints == 0 && Math.Abs(scale * 3600 - 600) < 2
            && Math.Abs(container.pose.velocity.magnitude - 540) < 0.01f,
            "shield loss persists in projectile record without double speed attenuation");
        Set(record, "prevPosition", container.pose.position);
        container.pose.position = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f));
        service.ResolveCollisions();
        scale = (float)AccessTools.Field(record.GetType(), "damageScale").GetValue(record);
        Check(console.Destroyed && Math.Abs(scale * 3600 - 180) < 2,
            "next tick uses remaining 600 budget rather than restoring original 3600");

        // Real line geometry/payment and long-rod fracture under a weak funded impulse.
        Reset();
        var battery = new CompPowerBattery(); Set(battery, "storedEnergy", 10f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        var power = new CompPowerTrader(); Set(power, "powerOnInt", true); power.transNet = net;
        var kinetic = new CompSpaceShield { props = new CompProperties_SpaceShield(),
            mode = SpaceShieldMode.Kinetic, kineticStrengthPercent = 10, projection = 10, length = 20, angleDeg = 45 };
        var intercept = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        intercept.props = new CompProperties_ProjectileInterceptor { radius = 12 };
        var emitter = Add(40, 8, 500, intercept, kinetic, power);
        emitter.Rotation = Rot4.West;
        first = Shield(3000, 6); console = Add(55, 5, 420);
        bool shattered = false;
        service = new SpaceProjectileService(universe);
        var railProps = rail.GetCompProperties<CompProperties_SpaceProjectile>();
        railProps.shieldFragmentDef = fragment; railProps.shieldFragmentCount = 4; railProps.shieldTumbleDef = fragment;
        railProps.shieldTumbleDeltaV = .15f; railProps.shieldBreakDeltaV = .5f;
        var attack = new SpaceObject(nextId++, new SpacePose(start, 0));
        attack.pose.velocity = tangent * 540;
        shot = new SpaceCollision.SweptBody { attacker = attack, projectileDef = rail, from = start,
            to = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f)), damageScale = 1, mass = 40,
            onVisualContact = contacts.Add,
            onShieldShatter = (hit, defender, velocity, normal, energy, count) =>
            {
                shattered = true;
                SpaceShieldProjectileBehavior.SpawnFragments(service, rail, null, defender, null, hit, velocity, normal, energy, 40f, 1f, fragmentCount: count);
            } };
        SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
        records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
        Check(shattered && shot.dead && first.currentHitPoints == 3000 && console.HitPoints == 420
            && Math.Abs(battery.StoredEnergy - 6.4f) < 0.001f, "kinetic line shatters high-budget round with paid impulse before inner guard");
        kinetic.GetSegment(out Vector2 lineStart, out Vector2 lineEnd, out _);
        Check(contacts.Count == 1 && contacts[0].shieldMode == SpaceShieldMode.Kinetic
            && (contacts[0].lineStart - lineStart).sqrMagnitude < 0.001f
            && (contacts[0].lineEnd - lineEnd).sqrMagnitude < 0.001f,
            "kinetic pulse uses the exact finite collision segment without an inner hull hit");
        Check(records.Count == 4 && records.Cast<object>().All(p =>
            (ThingDef)AccessTools.Field(p.GetType(), "def").GetValue(p) == fragment
            && (float)AccessTools.Field(p.GetType(), "damageScale").GetValue(p) == 1f
            && Math.Abs((float)AccessTools.Field(p.GetType(), "mass").GetValue(p) - 10f) < .001f),
            "real shatter creates four independent fragments sharing parent mass without copying its budget");
        // Keep the real payment, collision normal, scatter and launch chain under test.
        kinetic.angleDeg = 20;
        kinetic.kineticStrengthPercent = 25;
        railProps.shieldFragmentScatterDeg = 11;
        foreach (float incomingMass in new[] { 1f, 48f, 96f })
        foreach (float available in new[] { 1f, 2f, 3f, 100f })
        {
            contacts.Clear(); SpaceCollision.ClearCaches();
            Set(kinetic, "disabledUntilTick", 0); Set(battery, "storedEnergy", available);
            first.currentHitPoints = 3000;
            hull.pose.velocity = new Vector2(12, -7); hull.pose.angularVelocity = .1f;
            attack.pose.velocity = tangent * 60 + SpaceTransform.VelocityAtUniversePoint(hull.pose, start);
            service = new SpaceProjectileService(universe);
            Vector3 contactPoint = default;
            Vector2 incomingVelocity = default, normalAtHit = default;
            float fundedImpulse = -1;
            shot = new SpaceCollision.SweptBody { attacker = attack, projectileDef = rail, from = start,
                to = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f)), damageScale = .7f, mass = incomingMass,
                onVisualContact = contact => {
                    if (contact.shieldMode != SpaceShieldMode.Kinetic) return;
                    contactPoint = contact.position;
                    incomingVelocity = SpaceTransform.RelativeVelocityAtContact(attack.pose, hull.pose, contactPoint);
                    normalAtHit = SpaceTransform.MapDirectionToUniverseTangent(hull, map, contact.localPoint, contact.localNormal);
                },
                onShieldShatter = (hit, defender, velocity, normal, impulse, count) => {
                    fundedImpulse = impulse;
                    SpaceShieldProjectileBehavior.SpawnFragments(service, rail, null, defender, null,
                        hit, velocity, normal, impulse, incomingMass, 1f, damageScale: .7f, fragmentCount: count);
                } };
            SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { shot });
            var expected = EnergyImpulseModel.Apply(incomingMass, Vector2.Dot(incomingVelocity, normalAtHit),
                Vector2.Dot(incomingVelocity, new Vector2(-normalAtHit.y, normalAtHit.x)), true, 25, available);
            Check(Math.Abs(battery.StoredEnergy - (available - expected.Cost)) < .001f,
                $"production pulse matches independently calculated funded cost: mass={incomingMass}, available={available}");
            records = (IList)AccessTools.Field(typeof(SpaceProjectileService), "projectiles").GetValue(service);
            double lateral = expected.Impulse * Math.Abs(incomingVelocity.normalized.x*normalAtHit.y
                - incomingVelocity.normalized.y*normalAtHit.x) / incomingMass;
            int expectedCount = lateral > .5 ? 4 : lateral > .15 ? 1 : 0;
            Check(records.Count == expectedCount && (fundedImpulse >= 0) == (expectedCount > 0),
                "only funded impulse reaching the long-rod threshold produces fragments");
            for (int j = 0; j < records.Count; j++)
            {
                object piece = records[j];
                var pieceContainer = (SpaceObject)AccessTools.Property(piece.GetType(), "container").GetValue(piece);
                Vector2 after = SpaceTransform.TransportTangent(pieceContainer.pose.velocity, pieceContainer.pose.position, contactPoint)
                    - SpaceTransform.VelocityAtUniversePoint(hull.pose, contactPoint);
                Vector2 wanted = SpaceTransform.Rotate(incomingVelocity + normalAtHit * (fundedImpulse / incomingMass),
                    (records.Count == 1 ? 0 : 2f * j / (records.Count - 1) - 1) * 11 * Mathf.Deg2Rad);
                Check((after - wanted).magnitude < .01f, "fragment scatter follows paid parent impulse and inherits rotating shield point velocity");
                Check(Math.Abs((float)AccessTools.Field(piece.GetType(), "mass").GetValue(piece) - incomingMass / records.Count) < .001f
                    && Math.Abs((float)AccessTools.Field(piece.GetType(), "damageScale").GetValue(piece) - .7f) < .001f,
                    "fragment mass and remaining impact budget are separate and not duplicated");
                Check((int)AccessTools.Field(piece.GetType(), "spawnedAtTick").GetValue(piece) == Find.TickManager.TicksGame,
                    "newly fractured projectiles receive their fragment lifetime");
            }
        }
        hull.pose.velocity = Vector2.zero; hull.pose.angularVelocity = 0;
        attack.pose.velocity = tangent * 540;
        kinetic.angleDeg = 0;
        Set(kinetic, "disabledUntilTick", 0);
        Set(battery, "storedEnergy", 8f);
        contacts.Clear();
        SpaceCollision.ClearCaches();
        kinetic.mode = SpaceShieldMode.Electromagnetic;
        kinetic.electromagneticStrengthPercent = 100;
        var laser = MakeDef("fixtureLaser");
        laser.comps.Add(new CompProperties_SpaceProjectile { laserRange = 100, shieldLaserTurnEnergy = 1 });
        var segments = new List<(Vector3 from, Vector3 to)>();
        shot = new SpaceCollision.SweptBody { attacker = attack, projectileDef = laser, from = start,
            to = SpaceTransform.MapPointToUniverse(hull, map, new Vector2(70, 5.5f)),
            onVisualContact = contacts.Add, onShieldRedirect = (a, b, defender) => segments.Add((a, b)) };
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
        shot = Fire(300, def: coupler);
        Check(shot.resolvedHit && shot.resolvedHitCell == console.Position && !shot.didRedirect
            && !shot.resolvedHitShield && first.currentHitPoints == 3000 && battery.StoredEnergy == energyBefore
            && contacts.Count == 1 && !contacts[0].shieldMode.HasValue && console.HitPoints == 420,
            "coupling crosses electromagnetic line and guard shield without drain, bending or hull damage");
        kinetic.mode = SpaceShieldMode.Kinetic;
        SpaceCollision.ClearCaches(); contacts.Clear();
        shot = Fire(300, def: coupler);
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
            shot = Fire(300, speed: 12, def: boarding);
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
            shot = Fire(300, speed: 12, def: boarding, configure: Nose);
            Check(!shot.resolvedHit, "side-slipping boarding hull does not ram with its side or rear " + rotation);
            var noseTarget = Add(55, 7, 420);
            SpaceCollision.ClearCaches();
            shot = Fire(300, speed: 12, def: boarding, configure: Nose);
            Check(shot.dead && shot.resolvedHitCell == noseTarget.Position,
                "side-slipping boarding nose collision follows real hull heading " + rotation);
        }
        Reset(); first = Shield(3000);
        generator = (Target)first.parent;
        map.thingGrid.Deregister(generator); Set(generator, "positionInt", new IntVec3(40, 0, 5));
        map.thingGrid.Register(generator);
        shot = Fire(300, def: coupler);
        Check(shot.resolvedHitCell == generator.Position && !shot.resolvedHitShield
            && first.currentHitPoints == 3000, "shield bypass still hits the physical generator");
        CheckGuardOwnership();
        CheckDualGuard(harmony);
        CheckContinuousBeam(coupler, engineering);
        CheckEnergy();
        CheckBatteryIsolation();
        CheckShipReactor();
        CheckRailgunLengthBaseline();
        CheckImpulseSurvival();
        CheckLateralTransform();
        CheckVisualAnchors();
    }

    static void CheckGuardOwnership()
    {
        Reset();
        AccessTools.Field(typeof(ModsConfig), "odysseyActive").SetValue(null, true);
        var foundation = (TerrainDef)Raw(typeof(TerrainDef)); foundation.tags = new List<string> { "Substructure" };
        var ownership = new SubstructureOwnership(map); map.components.Add(ownership); map.floodFiller = new FloodFiller(map);
        void Own(int from, int to, int id) {
            for (int x = from; x <= to; x++) {
                var cell = new IntVec3(x, 0, 8); map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(cell)] = foundation;
                AccessTools.Method(typeof(SubstructureOwnership), "Assign").Invoke(ownership, new object[] { cell, id });
            }
        }
        Own(20, 50, 101); Own(60, 70, 202);
        var firstConsole = new CompDeltaVPilotConsole { isMain = true, shipId = 101 }; Add(20, 8, 100, firstConsole);
        var otherConsole = new CompDeltaVPilotConsole { isMain = true, shipId = 202 }; Add(60, 8, 100, otherConsole);
        hull.members.Add(firstConsole.Vessel); hull.members.Add(otherConsole.Vessel);
        var a = DualShield(); var b = DualShield(); var other = DualShield();
        void Move(CompSpaceShield shield, int x) {
            map.thingGrid.Deregister(shield.parent); Set(shield.parent, "positionInt", new IntVec3(x, 0, 8)); map.thingGrid.Register(shield.parent);
        }
        Move(a, 30); Move(other, 65);
        Set(map.listerThings, "use", ListerThingsUse.Global);
        var groups = (List<Thing>[])AccessTools.Field(typeof(ListerThings), "listsByGroup").GetValue(map.listerThings);
        groups[(int)ThingRequestGroup.ProjectileInterceptor] = new List<Thing> { a.parent, b.parent, other.parent };
        SubstructureOwnership.BumpStructureVersion();
        Check(firstConsole.Shields.Count == 2 && otherConsole.Shields.Count == 1 && a.SharedRegularCapacity == 2000,
            $"console cache uses actual owned connected domain, never the entire map: {firstConsole.Shields.Count}/{otherConsole.Shields.Count}, shared={a.SharedRegularCapacity}, owner={ownership.OwnerAt(a.parent.Position)}, valid={firstConsole.Ship.IsValid}, ship={ShipRegistry.ShipOf(a.parent,true).Identity}, domain={firstConsole.Vessel.HullCells.Count()}");
        a.ConsumeGuard(100);
        Check(a.RegularCapacity == 950 && b.RegularCapacity == 950 && other.RegularCapacity == 1000,
            "public hit path queries its own console list and leaves the other ship untouched");
        map.terrainGrid.foundationGrid[map.cellIndices.CellToIndex(new IntVec3(35, 0, 8))] = null;
        SubstructureOwnership.BumpStructureVersion();
        Check(firstConsole.Shields.Count == 1 && b.SharedRegularCapacity == 950,
            "broken foundation disconnects cached member; disconnected shield falls back locally");
        b.ConsumeGuard(100);
        Check(b.RegularCapacity == 850 && a.RegularCapacity == 950, "disconnected fragment cannot debit its former ship");
        Own(35, 35, 101);
        Check(firstConsole.Shields.Count == 2 && a.SharedRegularCapacity == 1800,
            "reconnecting foundation refreshes the same console membership cache");
        Own(35, 70, 202);
        Check(firstConsole.Shields.Count == 1 && otherConsole.Shields.Count == 2 && b.SharedRegularCapacity == 1850,
            "ownership transfer updates both cached pools without changing balances");
        firstConsole.isMain = false; SubstructureOwnership.BumpStructureVersion();
        Check(firstConsole.Shields.Count == 0 && a.SharedRegularCapacity == 950,
            "loss of main console clears its cache and preserves local fallback");
        var successor = new CompDeltaVPilotConsole { isMain = true, shipId = 101 }; Add(21, 8, 100, successor);
        hull.members[0] = successor.Vessel; SubstructureOwnership.BumpStructureVersion();
        Check(successor.Shields.Count == 1 && a.SharedRegularCapacity == 950,
            "replacement main console rebuilds membership from current ownership");
        Set(other.parent, "mapIndexOrState", (sbyte)-2); SubstructureOwnership.BumpStructureVersion();
        Check(otherConsole.Shields.Count == 1 && b.SharedRegularCapacity == 850,
            "destroyed generator disappears from cached membership");
    }

    sealed class GuardTestProjectile : Projectile
    {
        public override int DamageAmount => 125;
    }
    static CompSpaceShield DualShield(bool small = false)
    {
        var interceptor = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        interceptor.props = new CompProperties_ProjectileInterceptor { radius = 12, hitPoints = 0,
            interceptGroundProjectiles = true, interceptAirProjectiles = true, interceptNonHostileProjectiles = true };
        interceptor.currentHitPoints = -1;
        var shield = new CompSpaceShield { props = new CompProperties_SpaceShield {
            groundRadius = 12, recoveryDelayTicks = 600, guardRecoveryTicks = 2500, regularCapacity = small ? 0 : 1000, emergencyCapacity = small ? 1000 : 2000 } };
        var fuel = new CompRefuelable();
        fuel.Initialize(new CompProperties_Refuelable { fuelCapacity = small ? 1000 : 2000,
            initialFuelPercent = 1, consumeFuelOnlyWhenUsed = true });
        Add(40, 8, 500, interceptor, fuel, shield); shield.PostPostMake(); return shield;
    }
    static void CheckDualGuard(Harmony harmony)
    {
        Reset(); var shield = DualShield(); var second = DualShield();
        var shot = Fire(7200);
        Check(!shot.dead && Math.Abs(RemainingBudget(shot) - 1200) < 2 && shield.EmergencyCapacity == 0
            && second.EmergencyCapacity == 0 && contacts.Count == 2 && contacts.All(c => c.shieldBroken),
            "real sweep drains both dual-capacity domes and continues with 1200 budget");
        Check(shield.parent.GetComp<CompProjectileInterceptor>().currentHitPoints == -1,
            "dual capacity never writes a mirrored native HP balance");
        Reset(); shield = DualShield(true); shot = Fire(1440);
        Check(!shot.dead && Math.Abs(RemainingBudget(shot) - 440) < 2 && shield.EmergencyCapacity == 0,
            "small emergency dome participates in the real penetrating sweep");
        Reset(); shield = DualShield(); shot = Fire(3000);
        Check(shot.dead && shield.RegularCapacity < .02 && shield.EmergencyCapacity < .02,
            $"near-capacity curved sweep stops with only geometric tolerance remaining: dead={shot.dead}, regular={shield.RegularCapacity}, emergency={shield.EmergencyCapacity}, remaining={RemainingBudget(shot)}");
        Reset(); shield = DualShield(); shield.ConsumeGuard(2999); shot = Fire(1440);
        Check(!shot.dead && Math.Abs(RemainingBudget(shot) - 1439) < 2,
            "one remaining emergency point cannot erase a full rail shot");
        // Keep native geometry and hostile/outgoing checks, replacing only graphical effects.
        harmony.Patch(AccessTools.Method(typeof(CompProjectileInterceptor), "TriggerEffecter"),
            prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        harmony.CreateClassProcessor(typeof(Patch_SpaceShieldMode)).Patch();
        Reset(); shield = DualShield(); var native = shield.parent.GetComp<CompProjectileInterceptor>();
        var projectileDef = MakeDef("guardTestProjectile");
        projectileDef.projectile = new ProjectileProperties { damageDef = bullet, speed = 60 };
        var projectile = new GuardTestProjectile { def = projectileDef };
        Check(native.CheckIntercept(projectile, new Vector3(27, 0, 8.5f), new Vector3(30, 0, 8.5f))
            && shield.RegularCapacity == 875 && shield.EmergencyCapacity == 2000 && native.currentHitPoints == -1,
            "native CheckIntercept preserves geometry and debits regular capacity exactly once");
        Check(!native.CheckIntercept(projectile, new Vector3(10, 0, 8.5f), new Vector3(11, 0, 8.5f))
            && shield.RegularCapacity == 875, "native missed sweep does not debit capacity");
        shield.ConsumeGuard(875);
        Check(native.CheckIntercept(projectile, new Vector3(27, 0, 8.5f), new Vector3(30, 0, 8.5f))
            && shield.EmergencyCapacity == 1875 && shield.RecoveryWaitTicks == 600,
            "native emergency hit uses local reserve without resetting regular recovery");
        shield.ConsumeGuard(10000);
        Check(!native.CheckIntercept(projectile, new Vector3(27, 0, 8.5f), new Vector3(30, 0, 8.5f)),
            "empty custom dome rejects native interception despite the native unlimited sentinel");
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
        var battery = new CompPowerBattery(); Set(battery, "storedEnergy", wp.shotEnergy * 12f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
        var weapon = new CompSpaceWeapon { props = wp };
        Add(6, 5, 500, power, weapon);
        var transaction = new SpaceWeaponShot(universe, weapon, hull, default) {
            projectileProps = coupler.GetCompProperties<CompProperties_SpaceProjectile>(), muzzleMap = point,
            muzzleUniverse = SpaceTransform.MapPointToUniverse(hull, map, point) };
        var energy = new SpaceWeaponEnergy();
        for (int i = 0; i < 12; i++)
        {
            Check(energy.Evaluate(weapon),
                "continuous energy reservation " + i);
            energy.Commit();
        }
        Check(battery.StoredEnergy < 0.00001f
            && !energy.Evaluate(weapon),
            "twelve ticks consume the configured energy and the remaining charge cannot fund another tick");
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
        universe.objects.Clear(); Set(universe, "objectById", null); Set(universe, "objectByMap", null);
        int before = Find.TickManager.TicksGame;
        Set(Find.TickManager, "ticksGameInt", before + 30);
        Check(OrbitSphere.Offset(Position(), OrbitSphere.Move(origin, velocity * 0.5f)).magnitude < 0.01f,
            "removed hull releases visual anchor into captured drift without map access");
        Set(Find.TickManager, "ticksGameInt", before);
    }

    sealed class FailedLaunch : ThingComp, ISpaceWeaponLaunch
    {
        internal int calls;
        public bool TryLaunch(SpaceWeaponShot shot) { calls++; return false; }
    }
    static int storedEnergyReads;
    static float cargoOutdoor;
    static bool CargoOutdoor(ref float __result) { __result = cargoOutdoor; return false; }
    static void CheckCargoTemperature()
    {
        Reset();
        var patch = new Harmony("DeltaV.Regression.CargoTemperature");
        patch.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), "OutdoorTemp"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CargoOutdoor)));
        try
        {
            map.mapTemperature = new MapTemperature(map);
            var moduleDef = MakeDef("temperatureModule");
            var cargoDef = MakeDef("cargo"); cargoDef.size = new IntVec2(2, 3);
            cargoDef.modExtensions = new List<DefModExtension> { new DefModExtension_CargoAttachments {
                slots = new List<SpaceWeaponAttachmentSlot> { new SpaceWeaponAttachmentSlot { cell = IntVec3.Zero } },
                allowed = new List<ThingDef> { moduleDef } } };
            var cargo = (Building_SpaceCargoContainer)Raw(typeof(Building_SpaceCargoContainer));
            cargo.def = cargoDef; cargo.thingIDNumber = nextId++;
            var cargoControl = new CompSpaceCargo { parent = cargo };
            Set(cargo, "comps", new List<ThingComp> { cargoControl });
            Set(cargo, "positionInt", new IntVec3(20, 0, 5)); Set(cargo, "mapIndexOrState", (sbyte)0);
            map.thingGrid.Register(cargo);
            var attachment = new CompSpaceWeaponGenericAttachment { props = new CompProperties_SpaceWeaponAttachment() };
            Set(attachment, "enabled", true);
            var power = new CompPowerTrader { props = new CompProperties_Power() }; Set(power, "powerOnInt", true);
            var control = new CompCargoTemperature { props = new CompProperties_CargoTemperature(), targetTemperature = -10 };
            var module = Add(20, 5, 100, attachment, power, control); module.def = moduleDef;
            cargoOutdoor = 30; control.RefreshPower();
            Check(power.PowerOutput == -200 && cargoControl.CargoTemperature == -10,
                "cargo module uses ASF cooling from outdoor ambient and draws 200 W");
            cargoOutdoor = -80; control.RefreshPower();
            Check(power.PowerOutput == -175 && cargoControl.CargoTemperature == -10,
                "cargo module heats toward same setpoint at 175 W without simultaneous cooling");
            cargoOutdoor = -150; control.RefreshPower();
            Check(cargoControl.CargoTemperature == -50, "ASF thermal offset bounds extreme-cold heating to 100 degrees");
            cargoOutdoor = 150; control.RefreshPower();
            Check(cargoControl.CargoTemperature == 50, "ASF thermal offset bounds extreme-hot cooling to 100 degrees");
            cargoOutdoor = -10; control.RefreshPower();
            Check(power.PowerOutput == 0 && cargoControl.CargoTemperature == -10, "at target temperature module uses no power");
            cargoOutdoor = 30; Set(attachment, "enabled", false); control.RefreshPower();
            Check(power.PowerOutput == 0 && cargoControl.CargoTemperature == 30,
                "disabled module restores outdoor temperature and removes its load");
            Set(attachment, "enabled", true); Set(power, "powerOnInt", false); control.RefreshPower();
            Check(cargoControl.CargoTemperature == 30, "power failure immediately removes cargo temperature adjustment");
            map.thingGrid.Deregister(module); Set(module, "mapIndexOrState", (sbyte)-1);
            Check(cargoControl.CargoTemperature == 30, "removing module leaves passive outdoor-temperature cargo");
        }
        finally { patch.UnpatchAll(patch.Id); }
    }
    static void CountStoredEnergy() { storedEnergyReads++; }

    static void CheckRailgunLengthBaseline()
    {
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        XElement Comp(string name, string type) => xml.Root.Elements("ThingDef")
            .Single(x => (string)x.Element("defName") == name).Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_" + type);
        foreach (int guardCount in new[] { 3, 4 })
        {
            Reset();
            string suffix = "";
            var loader = Comp("DeltaV_Weapon_RailgunLoader" + suffix, "SpaceWeapon");
            var segment = Comp("DeltaV_Weapon_RailgunAccelerator" + suffix, "SpaceRailgun");
            int count = 9;
            float mass = 30;
            float speed = SpaceKinetics.Speed(mass, (float)loader.Element("launchKineticEnergy")
                + count * (float)segment.Element("kineticEnergyBonus"));
            float budget = SpaceImpactEffects.ComputeDamage(
                SpaceImpactEffects.Resolve(rail, shieldDef, true).defenderEffect, speed, 0, 1f, mass);
            var shields = new List<CompSpaceShield>();
            for (int i = 0; i < guardCount; i++)
            {
                var shield = DualShield();
                shield.props = new CompProperties_SpaceShield { groundRadius = 12 };
                shield.PostPostMake();
                Set(shield, "regularCapacity", 750d);
                shield.parent.GetComp<CompRefuelable>().Initialize(new CompProperties_Refuelable {
                    fuelCapacity = 750, initialFuelPercent = 1, consumeFuelOnlyWhenUsed = true });
                shields.Add(shield);
            }
            var armour = Add(60, 5, 700);
            var shot = Fire(budget, speed);
            if (guardCount == 3)
                Check(shot.dead && !armour.Destroyed && Math.Abs(armour.HitPoints - 200) <= 2
                    && shields.All(s => s.RegularCapacity < .02 && s.EmergencyCapacity < .02),
                    suffix + " 5000-budget production round drains three 750+750 domes but remaining armour stops it");
            else
                Check(shot.dead && armour.HitPoints == 700 && shields.Sum(s => s.RegularCapacity + s.EmergencyCapacity) > 999,
                    suffix + " fourth standard guard stops the same round before armour");
        }
    }

    static void CheckEnergy()
    {
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        var hosts = xml.Root.Elements("ThingDef").Where(d => d.Element("comps")?.Elements("li").Any(c =>
            (string)c.Attribute("Class") is "DeltaV.CompProperties_SpaceWeapon" or "DeltaV.CompProperties_SpaceRailgun") == true)
            .ToDictionary(d => (string)d.Element("defName"), d => MakeDef((string)d.Element("defName")));
        var recoveryXml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_EnergyRecovery.xml").Root
            .Elements("ThingDef").Single(d => (string)d.Element("defName") == "DeltaV_RailgunEnergyRecovery")
            .Element("comps").Elements("li").Single(c => (string)c.Attribute("Class") == "DeltaV.CompProperties_RailgunEnergyRecovery");
        var recoveryProps = new CompProperties_RailgunEnergyRecovery {
            allowedHostDefs = recoveryXml.Element("allowedHostDefs")?.Elements("li").Select(n => hosts[n.Value]).ToList()
                ?? new List<ThingDef>() };
        var recoveryAttachment = new CompRailgunEnergyRecovery { props = recoveryProps };
        foreach (var host in hosts)
            Check(recoveryAttachment.CompatibleWith(host.Value) == (host.Key == "DeltaV_Weapon_RailgunAccelerator"),
                "production recovery whitelist accepts only rail accelerators: " + host.Key);
        XElement Comp(string name, string type) => xml.Root.Elements("ThingDef")
            .Single(x => (string)x.Element("defName") == name).Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_" + type);
        {
            Reset();
            string suffix = "";
            rail.GetCompProperties<CompProperties_SpaceProjectile>().mass = 30;
            XElement loader = Comp("DeltaV_Weapon_RailgunLoader" + suffix, "SpaceWeapon");
            XElement accelerator = Comp("DeltaV_Weapon_RailgunAccelerator" + suffix, "SpaceRailgun");
            var wp = new CompProperties_SpaceWeapon { projectileDef = rail, intrinsicAimSkill = 10, shotEnergy = (float)loader.Element("shotEnergy"),
                launchKineticEnergy = (float)loader.Element("launchKineticEnergy"), impactScale = (float)loader.Element("impactScale") };
            var rp = new CompProperties_SpaceRailgun { shotEnergy = (float)accelerator.Element("shotEnergy"),
                kineticEnergyBonus = (float)accelerator.Element("kineticEnergyBonus") };
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
            // Energy fixture supplies a known topology; spatial connection discovery is covered separately.
            weapon.Layout.acceleration.segments.AddRange(segments);
            foreach (var segment in segments)
                weapon.Layout.segmentAttachments.Add(segment, new List<ThingWithComps>());
            var energy = new SpaceWeaponEnergy();
            bool Plan(float available)
            {
                Set(battery, "storedEnergy", available);
                return energy.Evaluate(weapon);
            }
            Check(!Plan(wp.shotEnergy - 1) && battery.StoredEnergy == wp.shotEnergy - 1,
                suffix + " insufficient loader pulse refuses without spending");
            Check(Plan(wp.shotEnergy + rp.shotEnergy)
                && energy.kineticEnergyBonus == rp.kineticEnergyBonus
                && battery.StoredEnergy == wp.shotEnergy + rp.shotEnergy,
                suffix + " shared battery is reserved once and cannot fund three segments with one pulse");
            float fullCost = wp.shotEnergy + 3 * rp.shotEnergy;
            var countNet = new Harmony("DeltaV.Regression.WeaponNetQuery");
            countNet.Patch(AccessTools.Method(typeof(PowerNet), "CurrentStoredEnergy"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountStoredEnergy)));
            try
            {
                storedEnergyReads = 0;
                Check(Plan(fullCost) && energy.active.Count == 3 && storedEnergyReads == 1,
                    suffix + " full shared-net chain sums stored energy once per query");
                var targetPose = new SpacePose(OrbitSphere.Move(hull.pose.position, new Vector2(100, 30)), 0)
                    { velocity = new Vector2(4, -8) };
                hull.pose.velocity = new Vector2(-2, 3);
                foreach (float charge in new[] { fullCost, wp.shotEnergy + rp.shotEnergy, wp.shotEnergy - 1 })
                {
                    Set(battery, "storedEnergy", charge);
                    var preview = SpaceWeaponService.AimInfo(weapon, hull);
                    var firing = SpaceWeaponBallistics.Read(weapon, hull, weapon.ExecutionEnergy, true).aim;
                    SpaceWeaponBallistics.TryAim(preview, hull.pose, targetPose, out var previewPoint);
                    SpaceWeaponBallistics.TryAim(firing, hull.pose, targetPose, out var firingPoint);
                    float massForPreview = 30;
                    int expectedStages = charge >= fullCost ? 3 : charge >= wp.shotEnergy + rp.shotEnergy ? 1 : 0;
                    float expectedSpeed = (float)Math.Sqrt(2 * (wp.launchKineticEnergy + expectedStages * rp.kineticEnergyBonus) / massForPreview);
                    Check(Math.Abs(preview.Speed - expectedSpeed) < .001f,
                        suffix + " production preview adds only paid stage kinetic energy");
                    rail.GetCompProperties<CompProperties_SpaceProjectile>().mass = massForPreview * 1.5f;
                    var uraniumPreview = SpaceWeaponService.AimInfo(weapon, hull);
                    Check(Math.Abs(uraniumPreview.Speed - preview.Speed / Math.Sqrt(1.5)) < .001f,
                        suffix + " heavier rail projectile gets the same energy and a lower speed");
                    rail.GetCompProperties<CompProperties_SpaceProjectile>().mass = massForPreview;
                    Check(preview.Speed == firing.Speed && preview.MuzzleMap == firing.MuzzleMap
                        && previewPoint == firingPoint && battery.StoredEnergy == charge,
                        suffix + " automatic and preview parameters agree without spending at charge " + charge);
                }
                Plan(fullCost); energy.Commit();
                Check(battery.StoredEnergy == 0f, suffix + " committing a full shared-net plan pays exactly once");
                Set(battery, "storedEnergy", fullCost);
                var failed = new FailedLaunch { parent = weapon.parent };
                weapon.parent.AllComps.Add(failed); weapon.safeFire = false; weapon.manualFireDeviationDeg = 180f;
                Check(!universe.Weapons.ApplyControl(weapon, hull, new WeaponControlCommand(new Vector2(20, 5), true))
                    && failed.calls == 1 && battery.StoredEnergy == fullCost && weapon.cooldownUntilTick == 0,
                    suffix + " actual failed launch consumes no energy and starts no cooldown");
                weapon.parent.AllComps.Remove(failed);
            }
            finally { countNet.UnpatchAll(countNet.Id); }
            // Leave the middle segment unfunded; a later segment on its own net still contributes.
            var separate = (PowerNet)Raw(typeof(PowerNet));
            var extraBattery = new CompPowerBattery(); Set(extraBattery, "storedEnergy", rp.shotEnergy);
            separate.batteryComps = new List<CompPowerBattery> { extraBattery };
            segments[2].parent.GetComp<CompPowerTrader>().transNet = separate;
            Check(Plan(wp.shotEnergy + rp.shotEnergy)
                && energy.kineticEnergyBonus == 2 * rp.kineticEnergyBonus,
                suffix + " unfunded middle segment does not block later funded acceleration");
            int count = 9;
            float velocity;
            XElement magazine = Comp("DeltaV_Weapon_RailgunLoader" + suffix, "SpaceWeaponMagazine");
            float mass = (float)magazine.Element("ammoPerShot") * ((float?)magazine.Element("unitMass") ?? 1f);
            velocity = SpaceKinetics.Speed(mass, (float)loader.Element("launchKineticEnergy") + count * rp.kineticEnergyBonus);
            float budget = SpaceImpactEffects.ComputeDamage(
                SpaceImpactEffects.Resolve(rail, shieldDef, true).defenderEffect, velocity, 0, 1f, mass) * wp.impactScale;
            Check(Math.Abs(budget - 5000) < 0.01f
                && wp.shotEnergy + count * rp.shotEnergy == 6750,
                suffix + " real Defs meet declared shield-breaking budget and energy threshold");
            XElement cap = Comp("DeltaV_Building_RailgunCapacitor" + suffix, "SwitchableBattery");
            Check((float)cap.Element("storedEnergyMax") == rp.shotEnergy
                && (float)cap.Element("efficiency") == 1
                && cap.Element("chargingWatts") == null && cap.Element("maxOutputWatts") == null,
                suffix + " capacitor stores one paid pulse and uses vanilla surplus charging at full efficiency");

            segments[2].parent.GetComp<CompPowerTrader>().transNet = net;
            var recovery = new CompRailgunEnergyRecovery { props = new CompProperties_RailgunEnergyRecovery() };
            var recovery2 = new CompRailgunEnergyRecovery { props = new CompProperties_RailgunEnergyRecovery() };
            Set(recovery, "enabled", true); Set(recovery2, "enabled", true);
            var module = Add(10, 5, 100, recovery);
            var module2 = Add(11, 5, 100, recovery2);
            var local = weapon.Layout.segmentAttachments[segments[0]];
            local.Add(module);
            Check(Plan(wp.shotEnergy + rp.shotEnergy * .75f) && energy.active.Count == 1,
                suffix + " one recovery makes its segment affordable at exactly 75 percent");
            energy.Commit();
            Check(battery.StoredEnergy == 0f, suffix + " recovery discount is committed once with full loader cost");
            local.Add(module2);
            Check(Plan(wp.shotEnergy + rp.shotEnergy * .5f) && energy.active.Count == 1,
                suffix + " two recovery modules stack additively to 50 percent");
            Check(Plan(wp.shotEnergy + rp.shotEnergy * .5f - 1) && energy.active.Count == 0,
                suffix + " recovery still requires the whole discounted pulse");
            Set(recovery2, "enabled", false);
            Check(Plan(wp.shotEnergy + rp.shotEnergy * .5f) && energy.active.Count == 0,
                suffix + " disabling recovery updates live affordability without rebuilding layout");
            local.Remove(module2);
            var reserve = new CompSwitchableBattery { props = new CompProperties_SwitchableBattery { storedEnergyMax = 3600 } };
            var capAttachment = new CompSpaceWeaponCapacitor { props = new CompProperties_SpaceWeaponCapacitor() };
            Set(capAttachment, "enabled", true); Set(reserve, "storedEnergy", rp.shotEnergy * .75f);
            local.Add(Add(12, 5, 100, reserve, capAttachment));
            Check(Plan(wp.shotEnergy) && energy.active.Count == 1,
                suffix + " capacitor plus recovery funds the reduced pulse from local reserve");
            energy.Commit();
            Check(battery.StoredEnergy == 0f && reserve.StoredEnergy == 0f,
                suffix + " mixed loadout deducts grid base and discounted capacitor pulse separately");
            // The same capacitor is now both a dedicated local source and a public grid battery.
            reserve.transNet = net; net.batteryComps.Insert(0, reserve);
            float localCost = rp.shotEnergy * .75f;
            Set(reserve, "storedEnergy", localCost);
            Check(Plan(wp.shotEnergy) && energy.active.Count == 1,
                "grid-first reservation cannot reuse the connected local capacitor for later segments");
            energy.Commit();
            Check(battery.StoredEnergy == 0 && reserve.StoredEnergy == 0,
                "connected capacitor pays its reserved local pulse before public grid withdrawal");
            Set(reserve, "storedEnergy", wp.shotEnergy);
            Check(Plan(0) && energy.active.Count == 0,
                "loader spending the entire connected capacitor leaves no phantom local pulse");
            energy.Commit();
            Check(reserve.StoredEnergy == 0, "grid-only draw consumes connected capacitor exactly once");
            local.Remove(reserve.parent);
            weapon.Layout.attachments.Add(reserve.parent);
            Set(reserve, "storedEnergy", wp.shotEnergy);
            Check(Plan(localCost) && energy.active.Count == 1,
                "local-first reservation subtracts from the shared net before downstream grid planning");
            energy.Commit();
            Check(battery.StoredEnergy == 0 && reserve.StoredEnergy == 0,
                "local-first mixed payment conserves both battery balances");
            weapon.Layout.attachments.Clear();
            net.batteryComps.Remove(reserve); reserve.transNet = null;
            local.Clear();
            Check(Plan(wp.shotEnergy + rp.shotEnergy * .75f) && energy.active.Count == 0,
                suffix + " removing recovery restores full segment price");
        }
    }
}







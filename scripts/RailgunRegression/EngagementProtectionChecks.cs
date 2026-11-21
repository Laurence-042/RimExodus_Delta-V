using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Fixture geometry only: production XML, shield interception, ray continuation and
    // native building damage establish the directional oracle independently of AI scores.
    static void CheckEngagementProtectionBaseline()
    {
        bool compareForecast = Environment.GetCommandLineArgs().Contains("--engagement-forecast");
        var projectiles = new Dictionary<string, ThingDef>();
        var xml = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml");
        foreach (string name in new[] { chemical.defName, "DeltaV_Projectile_Laser" })
        {
            var def = name == chemical.defName ? chemical : MakeDef(name);
            var props = def.GetCompProperties<CompProperties_SpaceProjectile>() ?? new CompProperties_SpaceProjectile();
            var node = xml.Root.Elements().Single(e => (string)e.Element("defName") == name)
                .Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
            foreach (var value in node.Elements())
            {
                var field = props.GetType().GetField(value.Name.LocalName);
                if (field == null || !(field.FieldType.IsPrimitive || field.FieldType.IsEnum)) continue;
                field.SetValue(props, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.Value)
                    : Convert.ChangeType(value.Value, field.FieldType, CultureInfo.InvariantCulture));
            }
            if (def != chemical) def.comps.Add(props);
            projectiles.Add(name, def);
        }
        var effects = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        foreach (var node in effects.Root.Elements().Where(e => e.Element("projectileDefs")?.Elements("li")
            .Any(v => v.Value == "DeltaV_Projectile_Laser") == true))
        {
            var effect = new SpaceImpactEffectDef { defName = node.Element("defName").Value,
                projectileDefs = node.Element("projectileDefs").Elements("li").Where(v => projectiles.ContainsKey(v.Value)).Select(v => projectiles[v.Value]).ToList(),
                targetsSubstructure = (bool?)node.Element("targetsSubstructure") ?? false,
                targetThingDefs = node.Element("targetThingDefs") == null ? null : new List<ThingDef> { shieldDef },
                attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf },
                defenderEffect = new ImpactOutcomePart() };
            foreach (var item in node.Element("defenderEffect").Elements())
            {
                var field = typeof(ImpactOutcomePart).GetField(item.Name.LocalName);
                field.SetValue(effect.defenderEffect, field.FieldType == typeof(DamageDef) ? bullet
                    : field.FieldType.IsEnum ? Enum.Parse(field.FieldType, item.Value)
                    : Convert.ChangeType(item.Value, field.FieldType, CultureInfo.InvariantCulture));
            }
            DefDatabase<SpaceImpactEffectDef>.Add(effect);
        }
        SpaceImpactEffects.ResetCache();
        var results = new List<object>();
        float Run(string weapon, string protection)
        {
            Reset();
            bool side = protection == "Armor";
            var armor = Enumerable.Range(0, side ? 3 : 1).Select(i => Add(35 + i, 5, side ? 180 : 48)).ToArray();
            var device = Add(55, 5, 300);
            CompPowerBattery bank = null;
            CompProjectileInterceptor guard = null;
            if (protection == "Kinetic" || protection == "Electromagnetic")
            {
                bank = new CompPowerBattery(); Set(bank, "storedEnergy", 100000f);
                var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { bank };
                var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
                var interceptor = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
                interceptor.props = new CompProperties_ProjectileInterceptor { radius = 12, hitPoints = 750 };
                var shield = new CompSpaceShield { props = new CompProperties_SpaceShield(),
                    mode = protection == "Kinetic" ? SpaceShieldMode.Kinetic : SpaceShieldMode.Electromagnetic,
                    angleDeg = 90, projection = 10, length = 18,
                    kineticStrengthPercent = 100, electromagneticStrengthPercent = 100 };
                Add(30, 8, 500, interceptor, power, shield);
            }
            else if (protection == "Guard") guard = Shield(750);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
            bool laser = weapon == "Laser";
            ThingDef projectile = projectiles[laser ? "DeltaV_Projectile_Laser" : chemical.defName];
            float speed = laser ? 60f : SpaceKinetics.LaunchSpeed(32, 1);
            int count = laser ? 10 : 1;
            CombatDamageForecast forecast = null;
            if (compareForecast)
            {
                forecast = new CombatDamageForecast(hull);
                float initialEnergy = bank?.StoredEnergy ?? 0;
                int initialGuard = guard?.currentHitPoints ?? 0;
                Rand.PushState(4213); float expectedRandom = Rand.Value; Rand.PopState();
                Rand.PushState(4213);
                try
                {
                    var prediction = forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65,
                        projectile, 1, speed, laser ? 4 : 1, laser ? 4 : 1);
                    Check(!prediction.exhausted, weapon + "/" + protection + " single shot remains within the contact budget");
                    Check(Rand.Value == expectedRandom && device.HitPoints == 300
                        && armor.All(a => a.HitPoints == (side ? 180 : 48))
                        && (bank?.StoredEnergy ?? 0) == initialEnergy && (guard?.currentHitPoints ?? 0) == initialGuard,
                        weapon + "/" + protection + " forecast preserves real HP, energy, guard capacity and random state");
                }
                finally { Rand.PopState(); }
            }
            for (int i = 0; i < count; i++)
            {
                Set(Find.TickManager, "ticksGameInt", 1000 + i * 12);
                // The first shot is also the oracle for the read-only estimate.
                Fire(laser ? 32 : 768, speed, projectile,
                    configure: body => { body.mass = 1; body.damageScale = laser ? 4 : 1; body.beamEnergyScale = laser ? 4 : 1; });
                if (i == 0 && forecast != null)
                {
                    // Compare the shot workspace with the damage just applied by production.
                    Check(Math.Abs(forecast.RemainingHP(device) - Math.Max(0, device.HitPoints)) < 1.01f,
                        weapon + "/" + protection + " forecast matches production device damage: predicted="
                        + forecast.RemainingHP(device) + " actual=" + device.HitPoints);
                    Check(armor.All(a => Math.Abs(forecast.RemainingHP(a) - Math.Max(0, a.HitPoints)) < 1.01f),
                        weapon + "/" + protection + " forecast matches production armor damage");
                }
            }
            float damage = 300 - Math.Max(0, device.HitPoints);
            results.Add(new { weapon, protection, deviceDamage = damage,
                armorDamage = armor.Sum(a => (side ? 180 : 48) - Math.Max(0, a.HitPoints)),
                pulseSpent = bank == null ? 0 : 100000 - bank.StoredEnergy,
                guardRemaining = guard?.currentHitPoints });
            return damage;
        }
        Rand.PushState(81108);
        try
        {
            float laserRear = Run("Laser", "Kinetic"), laserSide = Run("Laser", "Armor");
            float cannonRear = Run("Cannon", "Kinetic"), cannonSide = Run("Cannon", "Armor");
            Check(laserRear > laserSide && laserRear > 0, "production laser exploits thin armor behind a kinetic shield");
            Check(cannonSide > cannonRear && cannonSide > 0, "production penetrating shell prefers thick unshielded armor over a strong kinetic shield");
            Check(Run("Laser", "Electromagnetic") < laserRear, "active electromagnetic defense changes the laser weak-side result");
            Check(Run("Cannon", "Electromagnetic") > cannonRear, "electromagnetic defense does not inherit kinetic interception");
            Check(Run("Laser", "Guard") == 0 && Run("Cannon", "Guard") == 0, "guard capacity intercepts both weapon families before thin armor");
        }
        finally { Rand.PopState(); }
        string output = Environment.GetEnvironmentVariable("DELTAV_ENGAGEMENT_OUTPUT")
            ?? (compareForecast ? "Source/obj/EngagementForecast" : "Source/obj/EngagementBaseline-20261108");
        var path = Path.Combine(output, "protection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer.Serialize(new { baseline = compareForecast ? "working-tree" : "c81b2eb",
            scope = "Production collision and damage; fixed lanes, full pulse reserve, no AI, return-fire, thermal schedule or shield recovery. Not a complete ship battle.", results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("ENGAGEMENT_PROTECTION " + path);
        if (compareForecast)
        {
            CheckForecastPenetrationAndResources();
            CheckForecastGeometry(projectiles["DeltaV_Projectile_Laser"], projectiles[chemical.defName]);
            CheckPointContactsAfterDamage(projectiles["DeltaV_Projectile_Laser"], projectiles[chemical.defName]);
            CheckCompoundDamageRefresh(projectiles["DeltaV_Projectile_Laser"]);
            CheckForecastFootprints(projectiles["DeltaV_Projectile_Laser"], projectiles[chemical.defName]);
            CheckForecastIndependentCover(projectiles["DeltaV_Projectile_Laser"]);
            CheckForecastShortRays(output);
            CheckForecastRecursiveCover(output);
            CheckForecastHullEarlyOut(output);
        }
    }

    sealed class ShortRayHull : MapSpaceObject
    {
        public readonly List<SpaceCollisionCell> cells = new();
        internal ShortRayHull(Map map) : base(nextId++, map, new SpacePose(Vector3.forward, 0)) { }
        public override IEnumerable<SpaceCollisionCell> CollisionCells => cells;
    }

    static void CheckForecastShortRays(string output)
    {
        var rows = new List<object>();
        foreach (float chance in new[] { 0f, .01f, 1f })
        {
            Reset(70);
            var target = new ShortRayHull(map);
            for (int x = 5; x < 75; x++) for (int z = 5; z < 65; z++)
                target.cells.Add(new SpaceCollisionCell(new IntVec3(x, 0, z), SpaceCellKind.Substructure, null));
            var cover = Add(7, 7, 80); cover.def.fillPercent = .5f;
            var device = Add(9, 7, 80);
            foreach (Thing thing in new[] { cover, device })
                target.cells.Add(new SpaceCollisionCell(thing.Position, SpaceCellKind.Building, thing));
            var projectile = MakeDef("shortRay" + chance.ToString(CultureInfo.InvariantCulture));
            projectile.comps.Add(new CompProperties_SpaceProjectile { laserRange = 20 });
            var part = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet,
                damage = 100, penetrates = true, substructureBreakThreshold = 1 };
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = projectile.defName,
                projectileDefs = new List<ThingDef> { projectile }, targetsSubstructure = true,
                substructureHitChance = chance, defenderEffect = part });
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = projectile.defName + "Building",
                projectileDefs = new List<ThingDef> { projectile }, defenderEffect = part });
            SpaceImpactEffects.ResetCache();
            var forecast = new CombatDamageForecast(new CombatDamageForecast.Observation(target));
            CombatDamageEstimate Trace(int i) => forecast.Fire(new Vector2(5.25f, 7.5f), Vector2.right, 6f,
                projectile, 1, 60, coverSample: ((i & 7) + .5f) / 8);
            for (int i = 0; i < 24; i++)
            {
                if (i % 8 == 0) forecast.ResetShot();
                var estimate = Trace(i);
                rows.Add(new { chance, i, estimate.contacts, estimate.structure, estimate.guard, estimate.armorProgress,
                    estimate.returnedPotential, estimate.uncertainDamageUpper, estimate.uncertain, estimate.exhausted,
                    estimate.hitArmor, coverHp = forecast.RemainingHP(cover), deviceHp = forecast.RemainingHP(device) });
                Check(!estimate.exhausted && cover.HitPoints == 80 && device.HitPoints == 80,
                    $"cached cover leaves live state unchanged at floor chance {chance}, shot {i}");
            }
            Check(forecast.Query.foundationCells.Count >= 4198, "short ray traverses a small part of a large foundation");
            var watch = Stopwatch.StartNew();
            int contacts = 0;
            for (int i = 0; i < 4000; i++) { forecast.ResetShot(); contacts += Trace(i).contacts; }
            watch.Stop();
            Console.WriteLine($"SHORT_RAY chance={chance.ToString(CultureInfo.InvariantCulture)} contacts={contacts} ms={watch.Elapsed.TotalMilliseconds:F3}");
        }
        File.WriteAllText(Path.Combine(output, "short-ray-parity.json"), JsonSerializer.Serialize(rows));
    }

    static void CheckForecastRecursiveCover(string output)
    {
        Reset();
        var bank = new CompPowerBattery(); Set(bank, "storedEnergy", 10000f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { bank };
        var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
        var shield = new CompSpaceShield { props = new CompProperties_SpaceShield(), mode = SpaceShieldMode.Kinetic,
            angleDeg = 90, length = 18, projection = 10, kineticStrengthPercent = 100 };
        var interceptor = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        interceptor.props = new CompProperties_ProjectileInterceptor { radius = 12, hitPoints = 750 };
        Add(30, 8, 500, interceptor, power, shield);
        var front = Add(35, 5, 60); front.def.fillPercent = .5f;
        var rear = Add(40, 5, 60);
        var fragment = MakeDef("coverFragment");
        fragment.comps.Add(new CompProperties_SpaceProjectile { ignoresShields = true });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = fragment.defName,
            projectileDefs = new List<ThingDef> { fragment },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet,
                damage = 120f, penetrates = true } });
        var shell = MakeDef("coverFragmentParent");
        shell.comps.Add(new CompProperties_SpaceProjectile { shieldFragmentCount = 3, shieldFragmentDef = fragment,
            shieldBreakDeltaV = -1f, shieldFragmentScatterDeg = 0f });
        SpaceImpactEffects.ResetCache();
        var forecast = new CombatDamageForecast(hull);
        float sample = Enumerable.Range(1, 15).Select(i => i / 16f)
            .First(q => CombatDamageForecast.ContactSample(q, front.Position) < .5f);
        var rows = new List<object>();
        for (int i = 0; i < 2; i++)
        {
            forecast.ResetShot();
            var hit = forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, shell, 20, 120, coverSample: sample);
            float frontHp = forecast.RemainingHP(front), rearHp = forecast.RemainingHP(rear);
            rows.Add(new { hit.contacts, hit.structure, hit.uncertain, hit.exhausted, frontHp, rearHp });
            Check(hit.contacts == 3 && !hit.exhausted && frontHp == 0 && rearHp == 0
                && front.HitPoints == 60 && rear.HitPoints == 60 && bank.StoredEnergy == 10000f,
                "kinetic shield fragments share shot damage and preserve real HP and energy after reusing cover");
        }
        File.WriteAllText(Path.Combine(output, "recursive-cover-parity.json"), JsonSerializer.Serialize(rows));
    }

    sealed class EarlyOutScope : FoundationShip
    {
        public override IEnumerable<IntVec3> HullCells { get; }
        internal EarlyOutScope(Map map, params Thing[] buildings) : base(new Ship(0, map, null))
        {
            HullCells = buildings.SelectMany(building => building.OccupiedRect()).ToArray();
        }
        public override bool Contains(Thing thing) => thing.Map == Map && HullCells.Contains(thing.Position);
    }

    static void CheckForecastHullEarlyOut(string output)
    {
        Reset();
        var guard = Shield(750, radius: 8);
        guard.parent.def.fillPercent = 0;
        var wall = Add(31, 5, 300);
        var target = new ShortRayHull(map);
        target.cells.Add(new SpaceCollisionCell(new IntVec3(30, 0, 5), SpaceCellKind.Substructure, null));
        target.cells.AddRange(hull.CollisionCells);
        var projectile = MakeDef("earlyOutShield");
        projectile.comps.Add(new CompProperties_SpaceProjectile { laserRange = 60 });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "EarlyOutFloor",
            projectileDefs = new List<ThingDef> { projectile }, targetsSubstructure = true, substructureHitChance = 0,
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet, damage = 40 } });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "EarlyOutWall",
            projectileDefs = new List<ThingDef> { projectile },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = bullet, damage = 40 } });
        DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = "EarlyOutGuard",
            projectileDefs = new List<ThingDef> { projectile }, targetThingDefs = new List<ThingDef> { guard.parent.def },
            defenderEffect = new ImpactOutcomePart { kind = ImpactEffectKind.ShieldDrain, damage = 40 } });
        SpaceImpactEffects.ResetCache();
        var rows = new List<object>();
        void Verify(string scenario, ShipBase scope, bool hitsWall)
        {
            var forecast = new CombatDamageForecast(new CombatDamageForecast.Observation(target, scope));
            for (int i = 0; i < 2; i++)
            {
                forecast.ResetShot();
                var hit = forecast.Fire(new Vector2(30.25f, 5.5f), Vector2.right, 20, projectile, 1, 60);
                rows.Add(new { scenario, i, hit.contacts, hit.structure, hit.guard, hit.uncertain, hit.exhausted,
                    wallHp = forecast.RemainingHP(wall) });
                Check(hit.contacts == 1 && !hit.exhausted && wall.HitPoints == 300 && guard.currentHitPoints == 750
                    && (hitsWall ? hit.structure > 0 && hit.guard == 0 : hit.structure == 0 && hit.guard > 0),
                    $"early-out hull and guard ordering: {scenario}, contacts={hit.contacts}, structure={hit.structure}, guard={hit.guard}");
            }
        }
        Verify("solid wall ahead of guard", null, true);
        Verify("different ship on the same map", new EarlyOutScope(map, guard.parent), false);
        wall.def.fillPercent = 0;
        Verify("all cover passes before guard", null, false);
        File.WriteAllText(Path.Combine(output, "hull-early-out-parity.json"), JsonSerializer.Serialize(rows));
    }

    static void CheckForecastIndependentCover(ThingDef laser)
    {
        Reset();
        var first = Add(30, 5, 1000); first.def.fillPercent = .5f;
        var second = Add(40, 5, 1000); second.def.fillPercent = .5f;
        var last = Add(50, 5, 1000);
        var targets = new[] { first, second, last };
        var observation = new CombatDamageForecast.Observation(hull);
        var predicted = new double[3]; var actual = new double[3];
        const int samples = 2048;
        Rand.PushState(43911);
        try
        {
            for (int i = 0; i < samples; i++)
            {
                var forecast = new CombatDamageForecast(observation);
                forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, laser, 1, 60,
                    coverSample: (i + .5f) / samples);
                Fire(8, 60, laser);
                for (int j = 0; j < targets.Length; j++)
                {
                    predicted[j] += 1000 - forecast.RemainingHP(targets[j]);
                    actual[j] += 1000 - targets[j].HitPoints;
                    targets[j].HitPoints = 1000;
                }
            }
        }
        finally { Rand.PopState(); }
        for (int j = 0; j < targets.Length; j++)
        {
            double expected = j == 0 ? 4 : 2;
            Check(Math.Abs(predicted[j] / samples - expected) < .2
                && Math.Abs(predicted[j] / samples - actual[j] / samples) < .25,
                "independent layered cover matches fixed-seed native mean at depth " + j
                + $" predicted={predicted[j] / samples:F3} actual={actual[j] / samples:F3}");
        }
        double intercepted = 0, broken = 0;
        for (int i = 0; i < samples; i++)
        {
            float q = (i + .5f) / samples; var cell = new IntVec3(18, 0, 7);
            if (CombatDamageForecast.ContactSample(q, cell) >= .5f) continue;
            intercepted++;
            if (CombatDamageForecast.ContactSample(q, cell, 1) < .5f) broken++;
        }
        Check(Math.Abs(intercepted / samples - .5) < .04 && Math.Abs(broken / samples - .25) < .04,
            "floor interception and conditional destruction use independent deterministic dimensions");
    }

    static void CheckForecastFootprints(ThingDef laser, ThingDef cannon)
    {
        foreach (var projectile in new[] { laser, cannon, rail })
        {
            var originalSize = projectile.size;
            try
            {
                foreach (var footprint in new[] { new IntVec2(1, 1), new IntVec2(2, 2) })
                foreach (var rotation in new[] { Rot4.North, Rot4.East })
                foreach (float rise in new[] { 0f, 18f, 40f })
                {
                    projectile.size = footprint; Reset(70);
                    var walls = new List<Target>();
                    foreach (int x in new[] { 30, 40, 50 })
                    {
                        var wall = Add(x, (int)(5.5f + (x - 5) * rise / 65), 180);
                        map.thingGrid.Deregister(wall); wall.def.size = new IntVec2(3, 2);
                        Set(wall, "rotationInt", rotation); map.thingGrid.Register(wall); walls.Add(wall);
                    }
                    float speed = projectile == laser ? 60 : 180;
                    float mass = projectile == laser ? 1 : MassForBudget(projectile, speed, 1200);
                    var forecast = new CombatDamageForecast(hull);
                    var prediction = forecast.Fire(new Vector2(5, 5.5f), new Vector2(65, rise).normalized,
                        new Vector2(65, rise).magnitude, projectile, mass, speed, projectile == laser ? 4 : 1);
                    Fire(1200, speed, projectile, endLane: 5.5f + rise, configure: body =>
                    {
                        body.mass = mass; body.sweepRadius = SpaceProjectileService.FootprintRadius(projectile);
                        body.damageScale = projectile == laser ? 4 : 1;
                    });
                    Check(!prediction.exhausted && walls.Sum(w => 180 - Math.Max(0, w.HitPoints)) > 0
                        && walls.All(w => Math.Abs(forecast.RemainingHP(w) - Math.Max(0, w.HitPoints)) < 1.01),
                        $"multi-cell forecast matches native footprint: {projectile.defName}, projectile={footprint}, rotation={rotation}, slope={rise}");
                }
            }
            finally { projectile.size = originalSize; }
        }
    }

    sealed class CollisionFixtureMap : MapSpaceObject
    {
        public CollisionFixtureMap(Map map, Hull reference) : base(reference.objectId, map, reference.pose) { }
        public override IEnumerable<ShipBase> Ships => Enumerable.Empty<ShipBase>();
    }

    static int pointFullRebuilds, pointBlockRefreshes;
    static void CountPointFullRebuild() => pointFullRebuilds++;
    static void CountPointBlockRefresh() => pointBlockRefreshes++;

    sealed class FixtureExplosion : Explosion
    {
        internal void Advance() => Tick();
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) =>
            Set(this, "mapIndexOrState", (sbyte)-2);
    }

    sealed class FixtureBlastWorker : DamageWorker
    {
        // The fixture uses native scheduling and per-cell damage, with no heat or visual effects.
        public override void ExplosionStart(Explosion explosion, List<IntVec3> cellsToAffect) { }
    }

    static FixtureExplosion pendingExplosion;
    static List<IntVec3> blastCells;
    static List<IntVec3> compoundContacts;
    static readonly HashSet<(int x, int z)> refreshedBlocks = new();
    static bool recordingBlockCells;

    static void SetBlastFixtureOptions(ref bool doVisualEffects, ref bool doSoundEffects,
        ref List<IntVec3> overrideCells)
    {
        doVisualEffects = doSoundEffects = false;
        overrideCells = blastCells;
    }

    static void ReportFixtureBlastError(string text)
    {
        if (text.StartsWith("Explosion could not affect cell", StringComparison.Ordinal))
            throw new InvalidOperationException(text);
    }

    static bool FixtureFlatAngle(IntVec3 __instance, ref float __result)
    {
        float degrees = MathF.Atan2(__instance.x, __instance.z) * (180f / MathF.PI);
        __result = degrees < 0 ? degrees + 360f : degrees;
        return false;
    }

    static bool SpawnFixtureExplosion(ThingDef def, IntVec3 loc, Map map, ref Thing __result)
    {
        if (def != ThingDefOf.Explosion) return true;
        pendingExplosion = new FixtureExplosion { def = def };
        Set(pendingExplosion, "positionInt", loc);
        Set(pendingExplosion, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        Set(pendingExplosion, "cellsToAffect", new List<IntVec3>());
        Set(pendingExplosion, "damagedThings", new List<Thing>());
        Set(pendingExplosion, "addedCellsAffectedOnlyByDamage", new HashSet<IntVec3>());
        __result = pendingExplosion;
        return false;
    }

    static bool DespawnDamageFixture(Building __instance)
    {
        Map owner = __instance.Map;
        owner.thingGrid.Deregister(__instance);
        owner.listerThings.AllThings.Remove(__instance);
        Set(__instance, "mapIndexOrState", (sbyte)-1);
        return false;
    }

    static bool KillDamageFixture(Target __instance)
    {
        __instance.DeSpawn(DestroyMode.KillFinalize);
        Set(__instance, "mapIndexOrState", (sbyte)-2);
        return false;
    }

    static void BeginDamageBlockRefresh()
    {
        CountPointBlockRefresh();
        recordingBlockCells = true;
    }

    static void EndDamageBlockRefresh() => recordingBlockCells = false;

    static void RecordDamageBlockCell(IntVec3 cell)
    {
        if (recordingBlockCells) refreshedBlocks.Add((cell.x / 8, cell.z / 8));
    }

    static void RecordCompoundContact(SpaceCollision.ImpactEvent evt, ImpactOutcomePart part)
    {
        if (part?.kind == ImpactEffectKind.DamageMap) compoundContacts.Add(evt.hitCell);
    }

    static void CheckCompoundDamageRefresh(ThingDef laser)
    {
        var fixture = new Harmony("DeltaV.Regression.CompoundDamage");
        var previousExplosion = ThingDefOf.Explosion;
        int previousTick = Find.TickManager.TicksGame;
        var trace = new List<object>();
        bool baseline = Environment.GetEnvironmentVariable("DELTAV_EXPECT_DAMAGE_FULL") == "1";
        try
        {
            pointFullRebuilds = pointBlockRefreshes = 0;
            refreshedBlocks.Clear();
            ThingDefOf.Explosion = MakeDef("FixtureExplosion");
            fixture.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)),
                prefix: new HarmonyMethod(typeof(Program), nameof(SetBlastFixtureOptions)));
            fixture.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReportFixtureBlastError)));
            fixture.Patch(AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.AngleFlat)),
                prefix: new HarmonyMethod(typeof(Program), nameof(FixtureFlatAngle)));
            fixture.Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
                    new[] { typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(WipeMode) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(SpawnFixtureExplosion)));
            fixture.Patch(AccessTools.Method(typeof(RegionTraverser), nameof(RegionTraverser.BreadthFirstTraverse),
                    new[] { typeof(IntVec3), typeof(Map), typeof(RegionEntryPredicate), typeof(RegionProcessor),
                        typeof(int), typeof(RegionType) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
            fixture.CreateClassProcessor(typeof(Patch_ShipStructure_Despawn)).Patch();
            fixture.Patch(AccessTools.Method(typeof(Building), nameof(Building.DeSpawn)),
                prefix: new HarmonyMethod(typeof(Program), nameof(DespawnDamageFixture)) { priority = Priority.Last });
            fixture.Patch(AccessTools.Method(typeof(Target), nameof(Target.Kill)),
                prefix: new HarmonyMethod(typeof(Program), nameof(KillDamageFixture)));
            fixture.Patch(AccessTools.Method(typeof(SpaceCollision), "RebuildHull"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CountPointFullRebuild)));
            var surface = AccessTools.Inner(typeof(SpaceCollision), "Surface");
            fixture.Patch(AccessTools.Method(surface, "RefreshBlocks"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BeginDamageBlockRefresh)),
                postfix: new HarmonyMethod(typeof(Program), nameof(EndDamageBlockRefresh)));
            fixture.Patch(AccessTools.Method(surface, "SetCell"),
                prefix: new HarmonyMethod(typeof(Program), nameof(RecordDamageBlockCell)));
            fixture.Patch(AccessTools.Method(typeof(SpaceImpactEffects), nameof(SpaceImpactEffects.ApplyToDefender)),
                prefix: new HarmonyMethod(typeof(Program), nameof(RecordCompoundContact)));

            void Count(string stage, int builds, params (int x, int z)[] blocks)
            {
                int updates = blocks.Length == 0 ? 0 : 1;
                Console.WriteLine($"DAMAGE_REFRESH {stage} full={pointFullRebuilds} refresh={pointBlockRefreshes}"
                    + " blocks=" + string.Join(" ", refreshedBlocks.OrderBy(b => b.x).ThenBy(b => b.z)));
                Check(pointFullRebuilds == builds && pointBlockRefreshes == updates
                    && refreshedBlocks.SetEquals(blocks), stage + " publishes the expected collision changes");
                pointFullRebuilds = pointBlockRefreshes = 0;
                refreshedBlocks.Clear();
            }

            CollisionFixtureMap Start(int tick)
            {
                Reset(24);
                Set(Find.TickManager, "ticksGameInt", tick);
                var target = new CollisionFixtureMap(map, hull);
                universe.objects[0] = target;
                Array.Fill(map.terrainGrid.topGrid, (TerrainDef)Raw(typeof(TerrainDef)));
                compoundContacts = new List<IntVec3>();
                return target;
            }

            IntVec3 ReadRay(CollisionFixtureMap target, int lane)
            {
                var ray = new SpaceCollision.RayQuery(target);
                return ray.Next(new Vector2(5, lane + .5f), Vector2.right, 32, laser,
                    0, null, null, null, null, out var hit) ? hit.cell : IntVec3.Invalid;
            }

            var blastTarget = Start(100);
            var near = Add(15, 5, 15);
            var far = Add(18, 5, 15);
            var survives = Add(20, 5, 200);
            blastCells = new List<IntVec3> { near.Position, far.Position, survives.Position };
            Check(ReadRay(blastTarget, 5) == near.Position, "warm collision surface contains blast targets");
            Count("blast-cold", 1);
            var blastDamage = new DamageDef { defName = "FixtureBlastDamage",
                workerClass = typeof(FixtureBlastWorker), harmsHealth = true, explosionSnowMeltAmount = 0 };
            var blast = new ImpactOutcomePart { kind = ImpactEffectKind.DamageMap, damageDef = blastDamage,
                damage = 20, speedExponent = 0f, explosionRadius = 9f };
            SpaceImpactEffects.ApplyToDefender(new SpaceCollision.ImpactEvent { defender = blastTarget,
                projectileDef = laser, hitCell = new IntVec3(12, 0, 5), relativeSpeed = 60, damageScale = 1 }, blast);
            Check(pendingExplosion != null && near.HitPoints == 15 && far.HitPoints == 15,
                "actual explosion creation has not dealt damage");
            void BlastSample(string stage, IntVec3 expected)
            {
                IntVec3 cell = ReadRay(blastTarget, 5);
                Check(cell == expected, stage + " ray sees this tick's occupation");
                trace.Add(new { stage, tick = Find.TickManager.TicksGame, cell = new[] { cell.x, cell.z },
                    hp = new[] { near.HitPoints, far.HitPoints, survives.HitPoints },
                    destroyed = new[] { near.Destroyed, far.Destroyed, survives.Destroyed } });
            }
            void AdvanceBlast(int firstTick, int lastTick)
            {
                for (int tick = firstTick; tick <= lastTick; tick++)
                {
                    Set(Find.TickManager, "ticksGameInt", tick);
                    pendingExplosion.Advance();
                }
            }
            BlastSample("blast-created", near.Position);
            Count("blast-created", baseline ? 1 : 0);
            AdvanceBlast(100, 103);
            BlastSample("blast-before-damage", near.Position);
            Count("blast-before-damage", 0);
            AdvanceBlast(104, 104);
            Check(near.Destroyed && !far.Destroyed, "near blast destruction happens before far destruction");
            BlastSample("blast-near", far.Position);
            Count("blast-near", 0, (1, 0));
            AdvanceBlast(105, 108);
            Check(far.HitPoints == 15, "far blast target is undamaged through the preceding tick");
            BlastSample("blast-before-far-damage", far.Position);
            Count("blast-before-far-damage", 0);
            AdvanceBlast(109, 109);
            Check(far.Destroyed && survives.HitPoints == 200, "far blast destruction follows on its own tick");
            BlastSample("blast-far", survives.Position);
            Count("blast-far", 0, (2, 0));
            AdvanceBlast(110, 111);
            Check(survives.HitPoints == 200, "survivor is undamaged through the preceding tick");
            AdvanceBlast(112, 112);
            Check(survives.HitPoints == 180, "blast applies native damage to the surviving building");
            BlastSample("blast-survives", survives.Position);
            Count("blast-survives", 0);

            var layered = MakeDef("fixtureLayeredPenetration");
            layered.comps.Add(new CompProperties_SpaceProjectile { collisionRadius = 0, speed = 60 });
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef { defName = layered.defName,
                projectileDefs = new List<ThingDef> { layered }, attackerEffect = new ImpactOutcomePart {
                    kind = ImpactEffectKind.DestroySelf }, defenderEffect = new ImpactOutcomePart {
                    kind = ImpactEffectKind.DamageMap, damageDef = bullet, damage = 60, speedExponent = 0f,
                    penetrationLayers = new List<ImpactPenetrationLayer> {
                        new ImpactPenetrationLayer { damageFactor = 1 },
                        new ImpactPenetrationLayer { damageFactor = .5f, halfWidth = 1 } } } });
            SpaceImpactEffects.ResetCache();
            foreach (bool destroys in new[] { true, false })
            {
                string stage = destroys ? "penetration-destroys" : "penetration-survives";
                var target = Start(200);
                var wide = Add(16, 8, destroys ? 10 : 200);
                map.thingGrid.Deregister(wide);
                wide.def.size = new IntVec2(3, 2);
                map.thingGrid.Register(wide);
                var rect = wide.OccupiedRect();
                var side = Add(rect.minX + 1, rect.minZ - 1, 100);
                var rear = Add(24, rect.maxZ, 200);
                Check(ReadRay(target, rect.maxZ) == new IntVec3(rect.minX, 0, rect.maxZ),
                    "warm collision surface contains the opposite row of the wide building");
                Count(stage + "-cold", 1);

                SpaceCollision.SweptBody Shot(ThingDef projectile, int lane)
                {
                    var from = new Vector2(5, lane + .5f);
                    Vector3 start = SpaceTransform.MapPointToUniverse(target, map, from);
                    var attacker = new SpaceObject(nextId++, new SpacePose(start, 0));
                    attacker.pose.velocity = SpaceTransform.MapDirectionToUniverseTangent(target, map, from, Vector2.right) * 60;
                    return new SpaceCollision.SweptBody { attacker = attacker, projectileDef = projectile,
                        from = start, to = SpaceTransform.MapPointToUniverse(target, map, new Vector2(30, from.y)),
                        mass = 1, damageScale = 1, onVisualContact = contacts.Add };
                }
                var first = Shot(layered, rect.minZ);
                var second = Shot(laser, rect.maxZ);
                SpaceCollision.SweepAndResolve(universe, new List<SpaceCollision.SweptBody> { first, second });
                Check(Find.TickManager.TicksGame == 200 && compoundContacts.Count == 2 && contacts.Count == 2
                    && compoundContacts[0] == new IntVec3(rect.minX, 0, rect.minZ)
                    && compoundContacts[1] == (destroys ? rear.Position : new IntVec3(rect.minX, 0, rect.maxZ))
                    && first.dead && second.dead, stage + " resolves both real projectiles in one tick");
                Check(wide.Destroyed == destroys && (destroys ? rear.HitPoints < 200 && side.HitPoints < 100
                    : rear.HitPoints == 200 && side.HitPoints == 100), stage + " applies damage and stopping rules");
                trace.Add(new { stage, tick = Find.TickManager.TicksGame, hp = new[] { wide.HitPoints, side.HitPoints, rear.HitPoints },
                    destroyed = wide.Destroyed, stop = new[] { first.dead, second.dead },
                    cells = compoundContacts.Select(c => new[] { c.x, c.z }).ToArray(),
                    points = contacts.Select(c => new[] { BitConverter.SingleToInt32Bits(c.localPoint.x),
                        BitConverter.SingleToInt32Bits(c.localPoint.y) }).ToArray() });
                var expectedBlocks = rect.Select(c => (c.x / 8, c.z / 8)).Distinct().ToArray();
                Check(expectedBlocks.Length > 1, "destroyed fixture footprint crosses block boundaries");
                if (destroys && !baseline) Count(stage, 0, expectedBlocks);
                else Count(stage, destroys ? 1 : 0);
                Check(ReadRay(target, rect.maxZ) == (destroys ? rear.Position : new IntVec3(rect.minX, 0, rect.maxZ)),
                    stage + " reads the same occupation after the real sweep");
                Count(stage + "-repeat", 0);
            }
        }
        finally
        {
            fixture.UnpatchAll(fixture.Id);
            ThingDefOf.Explosion = previousExplosion;
            Set(Find.TickManager, "ticksGameInt", previousTick);
            pendingExplosion = null;
            blastCells = null;
            compoundContacts = null;
            recordingBlockCells = false;
        }
        Console.WriteLine("DAMAGE_PARITY " + JsonSerializer.Serialize(trace));
    }

    static void CheckPointContactsAfterDamage(ThingDef laser, ThingDef cannon)
    {
        var counter = new Harmony("DeltaV.Regression.PointCollision");
        counter.Patch(AccessTools.Method(typeof(SpaceCollision), "RebuildHull"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPointFullRebuild)));
        var surface = AccessTools.Inner(typeof(SpaceCollision), "Surface");
        counter.Patch(AccessTools.Method(surface, "RefreshBlocks"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CountPointBlockRefresh)));
        foreach (var projectile in new[] { laser, cannon })
        {
            Reset();
            var target = new CollisionFixtureMap(map, hull);
            universe.objects[0] = target;
            var front = Add(12, 5, 1);
            var rear = Add(20, 5, 400);
            int tick = Find.TickManager.TicksGame;
            float radius = SpaceProjectileService.FootprintRadius(projectile);
            float speed = projectile == laser ? 60f : 180f;
            float mass = MassForBudget(projectile, speed, 60);
            Check(radius == 0f, projectile.defName + " declares a zero-radius hull sweep");
            void Verify(float lane, float end, Thing expected)
            {
                var query = new SpaceCollision.RayQuery(target);
                bool hit = query.Next(new Vector2(5, lane), Vector2.right, end - 5, projectile, radius,
                    null, null, null, null, out var ray);
                Check(hit == (expected != null) && ray.building == expected,
                    projectile.defName + " forecast geometry returns the expected cell");
                var forecast = new CombatDamageForecast(target);
                forecast.Fire(new Vector2(5, lane), Vector2.right, end - 5, projectile, mass, speed);
                contacts.Clear();
                Fire(60, speed, projectile, to: end, lane: lane, configure: body => body.sweepRadius = radius);
                Check(contacts.Count == (expected == null ? 0 : 1)
                    && (expected == null || (contacts[0].localPoint - ray.point).sqrMagnitude < .0001f),
                    projectile.defName + " production contact agrees with the forecast ray");
                Check(Math.Abs(forecast.RemainingHP(front) - Math.Max(0, front.HitPoints)) < 1.01f
                    && Math.Abs(forecast.RemainingHP(rear) - Math.Max(0, rear.HitPoints)) < 1.01f,
                    projectile.defName + " predicted damage agrees with production");
            }

            pointFullRebuilds = pointBlockRefreshes = 0;
            Verify(6.125f, 26f, null);
            Verify(5.5f, 16f, front);
            Check(front.Destroyed, projectile.defName + " first shot destroys the leading building");
            contacts.Clear();
            Fire(60, speed, projectile, to: 26, configure: body => body.sweepRadius = radius);
            Check(Find.TickManager.TicksGame == tick && contacts.Count == 1
                && Math.Abs(contacts[0].localPoint.x - rear.Position.x) < .01f && rear.HitPoints < 400,
                projectile.defName + " later shot sees destruction in the same tick");
            Check(pointFullRebuilds == 1 && pointBlockRefreshes == 1,
                projectile.defName + " later sweep refreshes one block set itself");
        }
        counter.UnpatchAll(counter.Id);
    }

    static void CheckForecastGeometry(ThingDef laser, ThingDef cannon)
    {
        foreach (var projectile in new[] { laser, cannon, rail })
        foreach (float rise in new[] { 0f, 18f, 40f })
        foreach (int hp in new[] { 48, 180 })
        {
            Reset(70);
            var walls = new List<Target>();
            foreach (int x in new[] { 30, 40, 50 })
                for (int z = 1; z < 65; z++) walls.Add(Add(x, z, hp));
            var direction = new Vector2(65, rise).normalized;
            float speed = projectile == laser ? 60 : 180;
            float mass = projectile == laser ? 1 : MassForBudget(projectile, speed, 1200);
            var forecast = new CombatDamageForecast(hull);
            var result = forecast.Fire(new Vector2(5, 5.5f), direction, new Vector2(65, rise).magnitude,
                projectile, mass, speed, projectile == laser ? 4 : 1, projectile == laser ? 4 : 1);
            Fire(1200, speed, projectile, endLane: 5.5f + rise, configure: body =>
            {
                body.mass = mass; body.sweepRadius = SpaceProjectileService.FootprintRadius(projectile);
                body.damageScale = projectile == laser ? 4 : 1; body.beamEnergyScale = body.damageScale;
            });
            Check(!result.exhausted && walls.All(w => Math.Abs(forecast.RemainingHP(w) - Math.Max(0, w.HitPoints)) < 1.01),
                $"readonly and production geometry agree for {projectile.defName}, slope {rise}/65, armor HP {hp}");
        }
        Reset();
        var reduced = Add(35, 5, 300);
        var damageDef = SpaceImpactEffects.Resolve(rail, reduced.def).defenderEffect.damageDef ?? DamageDefOf.Bullet;
        reduced.def.damageMultipliers = new List<DamageMultiplier> { new DamageMultiplier { damageDef = damageDef, multiplier = .3f } };
        var reductionForecast = new CombatDamageForecast(hull);
        float roundMass = MassForBudget(rail, 180, 101);
        reductionForecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, rail, roundMass, 180);
        Fire(101);
        Check(Math.Abs(reductionForecast.RemainingHP(reduced) - reduced.HitPoints) < .001,
            "forecast applies Thing damage multipliers and their native integer rounding before worker factors");
        var immune = new Building { def = MakeDef("immuneForecast") };
        immune.def.building.canBeDamagedByAttacks = false; Set(immune, "hitPointsInt", 100);
        Check(SpaceImpactEffects.ExpectedBuildingDamage(immune, DamageDefOf.Bullet, 80, out _) == 0
            && immune.TakeDamage(new DamageInfo(DamageDefOf.Bullet, 80)).totalDamageDealt == 0,
            "native cannot-be-damaged building policy is a known zero-damage fact");
        Reset();
        var custom = Add(35, 5, 300); custom.invulnerable = true;
        var customForecast = new CombatDamageForecast(hull);
        var customResult = customForecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, rail, roundMass, 180);
        Fire(101);
        Check(customResult.uncertain && custom.HitPoints == 300 && customResult.structure >= 0,
            "custom damage interception is reported as uncertain instead of claiming exact destruction");
        Reset();
        var unbreakable = Add(30, 5, 0); unbreakable.def.useHitPoints = false;
        var protectedDevice = Add(45, 5, 300);
        var noHealth = new CombatDamageForecast(hull);
        noHealth.Fire(new Vector2(5, 5.5f), Vector2.right, 65, rail, roundMass, 180);
        Fire(101);
        Check(!unbreakable.Destroyed && protectedDevice.HitPoints == 300 && noHealth.RemainingHP(protectedDevice) == 300,
            "a non-HP building with zero numeric HP still blocks penetration until actually destroyed");
    }

    static void CheckForecastPenetrationAndResources()
    {
        foreach (float budget in new[] { 500f, 1500f, 5000f })
        {
            Reset(); var a = Shield(600); var b = Shield(600);
            var wall = Add(55, 5, 180); var device = Add(65, 5, 300);
            float mass = 2 * 225 * budget / (180 * 180);
            var forecast = new CombatDamageForecast(hull);
            var result = forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, rail, mass, 180);
            FireGuard(budget);
            Check(!result.exhausted && Math.Abs(forecast.RemainingHP(wall) - Math.Max(0, wall.HitPoints)) < 1
                && Math.Abs(forecast.RemainingHP(device) - Math.Max(0, device.HitPoints)) < 1,
                "forecast matches coincident guard depletion then armor penetration at budget " + budget);
        }
        Reset();
        var battery = new CompPowerBattery(); Set(battery, "storedEnergy", 10f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        CompSpaceWeapon Gun(int x)
        {
            var power = new CompPowerTrader { transNet = net }; Set(power, "powerOnInt", true);
            var weapon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { shotEnergy = 3, projectileDef = rail } };
            Add(x, 5, 500, power, weapon); return weapon;
        }
        var first = Gun(5);
        var query = new SpaceWeaponEnergy();
        Check(query.Evaluate(first) && battery.StoredEnergy == 10,
            "reading current shot energy does not debit the live bank");
        query.Commit();
        Check(battery.StoredEnergy == 7, "ordinary energy commits still deduct the actual bank once");
        var pulse = new CombatResourceLedger();
        Check(pulse.Draw(net, 4) == 4 && pulse.Grid(net) == 3 && battery.StoredEnergy == 7,
            "single-shot shield pulse debits only the local workspace");
        pulse.Reset();
        Check(pulse.Grid(net) == 7, "the next shot starts from the same real shield energy");
    }
}

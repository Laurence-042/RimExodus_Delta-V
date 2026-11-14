using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
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
        foreach (string name in new[] { "DeltaV_Projectile_Cannon", "DeltaV_Projectile_Laser" })
        {
            var def = MakeDef(name);
            var props = new CompProperties_SpaceProjectile();
            var node = xml.Root.Elements().Single(e => (string)e.Element("defName") == name)
                .Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
            foreach (var value in node.Elements())
            {
                var field = props.GetType().GetField(value.Name.LocalName);
                if (field == null || !(field.FieldType.IsPrimitive || field.FieldType.IsEnum)) continue;
                field.SetValue(props, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.Value)
                    : Convert.ChangeType(value.Value, field.FieldType, CultureInfo.InvariantCulture));
            }
            def.comps.Add(props); projectiles.Add(name, def);
        }
        var effects = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        foreach (var node in effects.Root.Elements().Where(e => e.Element("projectileDefs")?.Elements("li")
            .Any(v => projectiles.ContainsKey(v.Value)) == true))
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
            SubstructureOwnership.BumpStructureVersion();
            bool laser = weapon == "Laser";
            ThingDef projectile = projectiles[laser ? "DeltaV_Projectile_Laser" : "DeltaV_Projectile_Cannon"];
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
            CheckForecastGeometry(projectiles["DeltaV_Projectile_Laser"], projectiles["DeltaV_Projectile_Cannon"]);
            CheckForecastFootprints(projectiles["DeltaV_Projectile_Laser"], projectiles["DeltaV_Projectile_Cannon"]);
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
                    float mass = projectile == laser ? 1 : 1200 / SpaceImpactEffects.ComputeDamage(
                        SpaceImpactEffects.Resolve(projectile, null).defenderEffect, speed, 0);
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
            float mass = projectile == laser ? 1 : 1200 / SpaceImpactEffects.ComputeDamage(
                SpaceImpactEffects.Resolve(projectile, null).defenderEffect, speed, 0);
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
        float roundMass = 101 / SpaceImpactEffects.ComputeDamage(SpaceImpactEffects.Resolve(rail, null).defenderEffect, 180, 0);
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
            float mass = budget / SpaceImpactEffects.ComputeDamage(SpaceImpactEffects.Resolve(rail, null).defenderEffect, 180, 0);
            var forecast = new CombatDamageForecast(hull);
            var result = forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65, rail, mass, 180);
            Fire(budget);
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

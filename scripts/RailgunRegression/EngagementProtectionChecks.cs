using System;
using System.Collections.Generic;
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
                    for (int i = 0; i < count; i++)
                    {
                        forecast.seconds = i * .2f;
                        var prediction = forecast.Fire(new Vector2(5, 5.5f), Vector2.right, 65,
                            projectile, 1, speed, laser ? 4 : 1, laser ? 4 : 1);
                        Check(!prediction.exhausted, weapon + "/" + protection + " forecast remains within the contact budget");
                    }
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
                // One small laser at power four over two seconds; one 32-charge, 1-mass
                // cannon round. No synthetic multiplier replaces the live damage Def.
                Fire(laser ? 32 : 768, speed, projectile,
                    configure: body => { body.mass = 1; body.damageScale = laser ? 4 : 1; body.beamEnergyScale = laser ? 4 : 1; });
            }
            float damage = 300 - Math.Max(0, device.HitPoints);
            if (forecast != null)
            {
                // Ledger inputs refer to the pre-shot HP; inspect its remaining balances,
                // not DamageTo after the real world has also taken damage.
                Check(Math.Abs(forecast.RemainingHP(device) - Math.Max(0, device.HitPoints)) < 1.01f,
                    weapon + "/" + protection + " forecast matches production device damage: predicted="
                    + forecast.RemainingHP(device) + " actual=" + device.HitPoints);
                Check(armor.All(a => Math.Abs(forecast.RemainingHP(a) - Math.Max(0, a.HitPoints)) < 1.01f),
                    weapon + "/" + protection + " forecast matches production armor damage");
            }
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
        var path = compareForecast ? "Source/obj/EngagementForecast/protection.json" : "Source/obj/EngagementBaseline-20261108/protection.json";
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
        }
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
        var first = Gun(5); var second = Gun(6);
        var resources = new CombatResourceLedger();
        var reservation = new SpaceWeaponEnergy(resources);
        Check(reservation.Evaluate(first), "forecast reserves first weapon using the production energy allocator"); reservation.Commit();
        Check(reservation.Evaluate(second), "second weapon shares the forecast grid budget"); reservation.Commit();
        resources.Draw(net, 3); // A shield pulse competes with both weapons for the same bank.
        Check(!reservation.Evaluate(first) && resources.Grid(net) == 1 && battery.StoredEnergy == 10,
            "forecast guns and shields cannot overspend one bank or deduct live energy");
        var live = new SpaceWeaponEnergy();
        Check(live.Evaluate(first), "failed forecast never changes the real firing gate");
        live.Commit(); Check(battery.StoredEnergy == 7, "ordinary energy commits still deduct the actual bank once");
        var tuning = new CompWeaponTuning();
        tuning.Initialize(new CompProperties_WeaponTuning { chemical = true, maxCharge = 32, maxMass = 32,
            defaultCharge = 8, defaultMass = 8, lockEveryShot = true, heatPerShot = 5, coolingPerSecond = 10 });
        tuning.CommitShot();
        var copied = tuning.ForecastCopy();
        int remaining = tuning.RemainingTicks;
        for (int i = 0; i < remaining; i++) copied.AdvanceThermalTick();
        Check(copied.RemainingTicks == 0 && !copied.overheated && tuning.RemainingTicks == remaining && tuning.overheated,
            "forecast advances the production thermal cycle without cooling the real gun");
        Set(battery, "storedEnergy", 10f);
        var scheduleResources = new CombatResourceLedger();
        var schedule = new CombatWeaponForecast(new[] { first, second }, scheduleResources);
        int shots = 0;
        schedule.Run(2, (w, p, t) => true, (w, p, t) => shots++);
        Check(shots == 3 && battery.StoredEnergy == 10,
            "finite firing schedule spends a shared bank once without changing live weapons");
        var cold = new CompWeaponTuning();
        cold.Initialize(new CompProperties_WeaponTuning { defaultInterval = 60, minInterval = 60, maxInterval = 60,
            heatPerShot = 0, coolingPerSecond = 10 });
        cold.CommitShot(); cold.parent = first.parent; first.parent.AllComps.Add(cold);
        first.NotifyParametersChanged();
        var delayed = new CombatWeaponForecast(new[] { first }, new CombatResourceLedger());
        var times = new List<float>();
        delayed.Run(3, (w, p, t) => true, (w, p, t) => times.Add(t));
        Check(times.Count == 2 && Math.Abs(times[0] - 1) < .001 && Math.Abs(times[1] - 2) < .001
            && cold.RemainingTicks == 60 && battery.StoredEnergy == 10,
            "current cooldown delays finite-window fire rather than suppressing long-term output");
        var shieldCopy = new CompSpaceShield { props = new CompProperties_SpaceShield
            { regularCapacity = 100, guardRecoveryTicks = 60, recoveryDelayTicks = 30 } };
        shieldCopy.InitializeGuard();
        var guardCopy = shieldCopy.ForecastGuardCopy(); guardCopy.DebitRegular(100);
        for (int i = 0; i < 36; i++) guardCopy.AdvanceGuardTick(true);
        Check(Math.Abs(guardCopy.RegularCapacity - 10) < .001 && guardCopy.UsesRegular
            && shieldCopy.RegularCapacity == 100,
            "forecast guard uses production recovery delay and automatic rejoin threshold without mutation");
        CheckForecastNetworkFlow();
    }

    static void CheckForecastNetworkFlow()
    {
        foreach (var state in new (float a, float b, float flow)[] { (8f, 8f, -6f), (1f, 8f, -6f),
            (9f, 2f, 6f), (2f, 2f, 6f) })
        {
            Reset();
            var a = new CompPowerBattery { props = new CompProperties_Battery { storedEnergyMax = 10, efficiency = 1 } };
            var b = new CompPowerBattery { props = new CompProperties_Battery { storedEnergyMax = 10, efficiency = .5f } };
            Add(10, 5, 100, a); Add(12, 5, 100, b);
            Set(a, "storedEnergy", state.a); Set(b, "storedEnergy", state.b);
            var net = (PowerNet)Raw(typeof(PowerNet));
            net.batteryComps = new List<CompPowerBattery> { a, b };
            Set(net, "givingBats", new List<CompPowerBattery>());
            var load = new CompPowerTrader { transNet = net,
                powerOutputInt = state.flow / CompPower.WattsToWattDaysPerTick };
            Set(load, "powerOnInt", true); Add(15, 5, 100, load);
            net.powerComps = new List<CompPowerTrader> { load };
            var forecast = new CombatResourceLedger(); forecast.Observe(net);
            forecast.AdvanceTick();
            Check(a.StoredEnergy == state.a && b.StoredEnergy == state.b,
                "forecast network flow preserves live bank balances");
            Rand.PushState(991);
            try { HarmonyLib.AccessTools.Method(typeof(PowerNet), "ChangeStoredEnergy").Invoke(net, new object[] { state.flow }); }
            finally { Rand.PopState(); }
            foreach (var bank in net.batteryComps)
                bank.DrawPower(Math.Min(bank.StoredEnergy, 5 * CompPower.WattsToWattDaysPerTick));
            Check(Math.Abs(forecast.Stored(a) - a.StoredEnergy) < .00001f
                && Math.Abs(forecast.Stored(b) - b.StoredEnergy) < .00001f,
                "forecast continuous load and efficient charging match native per-bank redistribution");
        }
    }
}

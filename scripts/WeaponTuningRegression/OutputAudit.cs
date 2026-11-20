using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    // This audit supplies ideal firing opportunities, not a second combat simulation.
    // Damage, thermal locks and guard debits/recovery execute the production components.
    sealed class AuditCycle
    {
        public int Ticks;
        public List<int> Shots = new();
        public int? FirstLock;
        public int CountBefore(int ticks) => ticks / Ticks * Shots.Count + Shots.Count(t => t < ticks % Ticks);
        public int ShotAt(int index) => index / Shots.Count * Ticks + Shots[index % Shots.Count];
    }
    sealed class AuditRow
    {
        public string Weapon, Family, Size, Material;
        public int Charge, Heads, Interval, Power, CycleTicks, CycleShots, Shots10, Shots60, StockShots;
        public double Speed, LifeTravel, AimRange, Damage, BlastRadius, ShieldDamage, BurstDps, SustainedDps,
            Dps10, Dps60, StockDps60, SteelPerShot, UraniumPerShot, SteelPerSecond, UraniumPerSecond,
            EnergyPerShot, PulseWatts, IdleWatts, LastStockShotSeconds;
        public double? FirstLockSeconds;
        [System.Text.Json.Serialization.JsonIgnore] public AuditCycle Cycle;
        public string Key => $"{Weapon}:{Material}:{Charge}/{Heads}:{Interval}:{Power}";
    }
    static readonly Action<CompWeaponTuning> AuditThermalTick = typeof(CompWeaponTuning)
        .GetMethod("AdvanceThermalTick", BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action<CompWeaponTuning>>();
    static readonly Action<CompSpaceShield, bool> AuditGuardTick = typeof(CompSpaceShield)
        .GetMethod("AdvanceGuardTick", BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<Action<CompSpaceShield, bool>>();

    static AuditCycle MeasureCycle(CompWeaponTuning gun)
    {
        var cycle = new AuditCycle();
        bool stable = !gun.Settings.lockEveryShot && gun.ShotHeat <= gun.Settings.coolingPerSecond * gun.interval / 60d + 1e-8;
        for (int t = 0; t < 1000000; t++)
        {
            if (t > 0 && gun.RemainingTicks == 0 && (stable || cycle.FirstLock.HasValue && gun.heat == 0))
            { cycle.Ticks = t; return cycle; }
            if (gun.RemainingTicks == 0)
            {
                cycle.Shots.Add(t); gun.CommitShot();
                if (gun.overheated && !cycle.FirstLock.HasValue) cycle.FirstLock = t;
            }
            AuditThermalTick(gun);
        }
        throw new Exception("Thermal cycle failed to return to its initial ready state");
    }
    static XElement AuditComp(XElement def, string cls) => def.Element("comps")?.Elements("li")
        .SingleOrDefault(x => (string)x.Attribute("Class") == cls);
    static double AuditNumber(XElement node, string name, double fallback = 0) =>
        node?.Element(name) is XElement value ? double.Parse(value.Value, CultureInfo.InvariantCulture) : fallback;
    static ImpactOutcomePart AuditEffect(XDocument effects, string name)
    {
        var part = new ImpactOutcomePart();
        foreach (var node in effects.Root.Elements().Single(x => (string)x.Element("defName") == name).Element("defenderEffect").Elements())
        {
            if (node.Name == "highExplosive")
            {
                SimpleCurve Curve(string curveName)
                {
                    var curve = new SimpleCurve();
                    curve.SetPoints(node.Element(curveName).Element("points").Elements().Select(point =>
                    {
                        var values = point.Value.Trim('(', ')').Split(',');
                        return new CurvePoint(float.Parse(values[0], CultureInfo.InvariantCulture),
                            float.Parse(values[1], CultureInfo.InvariantCulture));
                    }));
                    return curve;
                }
                part.highExplosive = new HighExplosiveProfile
                    { damageByMass = Curve("damageByMass"), radiusByMass = Curve("radiusByMass") };
                continue;
            }
            FieldInfo field = typeof(ImpactOutcomePart).GetField(node.Name.LocalName);
            if (field != null && (field.FieldType.IsPrimitive || field.FieldType.IsEnum))
                field.SetValue(part, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, node.Value)
                    : Convert.ChangeType(node.Value, field.FieldType, CultureInfo.InvariantCulture));
        }
        return part;
    }

    static CompSpaceShield AuditStandardShield(XElement def)
    {
        var properties = new CompProperties_SpaceShield();
        foreach (var node in AuditComp(def, "DeltaV.CompProperties_SpaceShield").Elements())
        {
            FieldInfo field = typeof(CompProperties_SpaceShield).GetField(node.Name.LocalName);
            if (field != null && field.FieldType.IsPrimitive)
                field.SetValue(properties, Convert.ChangeType(node.Value, field.FieldType, CultureInfo.InvariantCulture));
        }
        float capacity = (float)AuditNumber(AuditComp(def, "CompProperties_Refuelable"), "fuelCapacity");
        var body = new Building { def = new ThingDef { comps = new() {
            new CompProperties_Refuelable { fuelCapacity = capacity, initialFuelPercent = 1,
                consumeFuelOnlyWhenUsed = true, targetFuelLevelConfigurable = true,
                initialConfigurableTargetFuelLevel = capacity }, properties
        } } };
        body.InitializeComps();
        var shield = body.GetComp<CompSpaceShield>();
        shield.PostPostMake(); SetSpawned(body, true); return shield;
    }

    static List<AuditRow> RunOutputAudit(string repo)
    {
        string defsPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        string effectsPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        string projectilePath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml");
        string suppliesPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_GunSupplies.xml");
        var defs = XDocument.Load(defsPath); var effects = XDocument.Load(effectsPath); var projectiles = XDocument.Load(projectilePath);
        var steelBlast = AuditEffect(effects, "DeltaV_Impact_SteelShellVsStructure");
        var supplies = XDocument.Load(suppliesPath);
        XElement Def(string name) => defs.Root.Elements().Single(x => (string)x.Element("defName") == name);
        double UnitCost(string product) {
            XElement recipe = supplies.Root.Elements("RecipeDef").Single(x => x.Element("products")?.Element(product) != null);
            return AuditNumber(recipe.Element("ingredients").Element("li"), "count") / AuditNumber(recipe.Element("products"), product);
        }
        double propellantCost = UnitCost("DeltaV_Propellant"), steelHeadCost = UnitCost("DeltaV_SteelHead"), uraniumHeadCost = UnitCost("DeltaV_UraniumHead");
        double uraniumMass = AuditNumber(AuditComp(Def("DeltaV_Building_UraniumMagazine"), "DeltaV.CompProperties_SpaceWeaponMagazine"), "unitMass");
        var rows = new List<AuditRow>();
        var names = new[] { "DeltaV_Weapon_LaserTurret", "DeltaV_Weapon_LaserMainMedium", "DeltaV_Weapon_RapidMainSmall",
            "DeltaV_Weapon_RapidMainMedium", "DeltaV_Weapon_CannonTurret", "DeltaV_Weapon_HeavyMainMedium" };
        foreach (string name in names)
        {
            XElement def = Def(name), weapon = AuditComp(def, "DeltaV.CompProperties_SpaceWeapon");
            var defaults = Gun(def); bool chemical = defaults.Settings.chemical, heavy = defaults.Settings.lockEveryShot;
            bool medium = name.EndsWith("Medium");
            var structure = AuditEffect(effects, chemical ? "DeltaV_Impact_CannonVsStructure" : medium ? "DeltaV_Impact_LaserHeavyVsStructure" : "DeltaV_Impact_LaserVsStructure");
            var shield = AuditEffect(effects, chemical ? "DeltaV_Impact_VsShield" : "DeltaV_Impact_LaserVsShield");
            XElement projectile = projectiles.Root.Elements().Single(x => (string)x.Element("defName") == (string)weapon.Element("projectileDef"));
            var projectileProps = AuditComp(projectile, "DeltaV.CompProperties_SpaceProjectile");
            double idle = 0;
            for (XElement ancestor = def; ancestor != null; ancestor = defs.Root.Elements().SingleOrDefault(x => (string)x.Attribute("Name") == (string)ancestor.Attribute("ParentName")))
            {
                XElement power = AuditComp(ancestor, "CompProperties_Power");
                if (power?.Element("basePowerConsumption") != null) { idle = AuditNumber(power, "basePowerConsumption"); break; }
            }
            var cycleCache = new Dictionary<(int, int), AuditCycle>();
            foreach (string material in chemical ? new[] { "Steel", "Uranium" } : new[] { "None" })
            foreach (int charge in heavy ? Enumerable.Range(1, defaults.Settings.maxCharge) : new[] { defaults.charge })
            foreach (int heads in heavy ? Enumerable.Range(1, defaults.Settings.maxMass) : new[] { defaults.mass })
            foreach (int interval in !heavy && chemical ? Enumerable.Range(defaults.Settings.minInterval, defaults.Settings.maxInterval - defaults.Settings.minInterval + 1) : new[] { defaults.interval })
            foreach (int power in chemical ? new[] { 1 } : Enumerable.Range(1, defaults.Settings.maxPower))
            {
                if (!cycleCache.TryGetValue((charge, interval), out AuditCycle cycle))
                {
                    var gun = Gun(def); gun.charge = charge; gun.interval = interval;
                    cycleCache[(charge, interval)] = cycle = MeasureCycle(gun);
                }
                double mass = heads * (material == "Uranium" ? uraniumMass : AuditNumber(AuditComp(def, "DeltaV.CompProperties_SpaceWeaponMagazine"), "unitMass", 1));
                float speed = chemical ? SpaceKinetics.LaunchSpeed(charge, (float)mass) : 60f;
                float scale = (float)AuditNumber(weapon, "impactScale", 1) * power;
                bool steel = material == "Steel";
                double damage = SpaceImpactEffects.ComputeDamage(steel ? steelBlast : structure, speed, 0, 1, (float)mass)
                    * (steel ? 1f : scale);
                double drain = SpaceImpactEffects.ComputeDamage(shield, speed, 0, 1, (float)mass) * scale;
                double rate = cycle.Shots.Count * 60d / cycle.Ticks;
                int stockShots = int.MaxValue;
                if (chemical)
                {
                    XElement Supply(string kind) => def.Element("comps").Elements("li").Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_GunSupply" && (string)x.Element("kind") == kind);
                    double headCapacity = material == "Uranium" ? AuditNumber(Def("DeltaV_Building_UraniumMagazine").Element("comps")?.Elements("li").Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_GunSupply"), "fuelCapacity") : AuditNumber(Supply("Heads"), "fuelCapacity");
                    stockShots = Math.Min((int)(headCapacity / heads), (int)(AuditNumber(Supply("Propellant"), "fuelCapacity") / charge));
                }
                var row = new AuditRow {
                    Weapon = name, Family = chemical ? heavy ? "Heavy" : "Rapid" : "Laser", Size = medium ? "Medium" : "Small", Material = material,
                    Charge = chemical ? charge : 0, Heads = chemical ? heads : 0, Interval = heavy ? cycle.Ticks : interval, Power = power,
                    CycleTicks = cycle.Ticks, CycleShots = cycle.Shots.Count, Cycle = cycle, Speed = chemical ? speed : 0,
                    LifeTravel = chemical ? speed * AuditNumber(projectileProps, "timeoutTicks") / 60d : AuditNumber(projectileProps, "laserRange"),
                    AimRange = AuditNumber(weapon, "range"), Damage = damage,
                    BlastRadius = steel ? steelBlast.highExplosive.Radius((float)mass) : 0d, ShieldDamage = drain,
                    BurstDps = damage * 60 / (heavy ? cycle.Ticks : interval), SustainedDps = damage * rate,
                    Shots10 = cycle.CountBefore(600), Shots60 = cycle.CountBefore(3600), StockShots = chemical ? stockShots : 0,
                    FirstLockSeconds = cycle.FirstLock / 60d,
                    SteelPerShot = chemical ? charge * propellantCost + (material == "Steel" ? heads * steelHeadCost : 0) : 0,
                    UraniumPerShot = material == "Uranium" ? heads * uraniumHeadCost : 0,
                    EnergyPerShot = AuditNumber(weapon, "shotEnergy") * power, IdleWatts = idle,
                    LastStockShotSeconds = chemical ? cycle.ShotAt(stockShots - 1) / 60d : 0
                };
                row.Dps10 = row.Shots10 * damage / 10; row.Dps60 = row.Shots60 * damage / 60;
                row.StockDps60 = Math.Min(stockShots, row.Shots60) * damage / 60;
                row.SteelPerSecond = row.SteelPerShot * rate; row.UraniumPerSecond = row.UraniumPerShot * rate;
                row.PulseWatts = row.EnergyPerShot * rate / (60d * CompPower.WattsToWattDaysPerTick);
                rows.Add(row);
            }
        }
        var representative = rows.Where(r => r.Material != "Uranium" && (r.Family == "Laser"
            || r.Family == "Rapid" && new[] { 4, 6, 10, 12, 20, 25, 30 }.Contains(r.Interval)
            || r.Family == "Heavy" && (r.Charge == r.Heads && new[] { 1, 8, 24, 32, 48, 96 }.Contains(r.Charge)
                || r.Heads == (r.Size == "Small" ? 32 : 96) && new[] { 1, 2, 4, 8, 16, 24, 48 }.Contains(r.Charge)))).ToList();
        var guardRows = new List<object>();
        string shieldPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_Shields.xml");
        var shieldDef = XDocument.Load(shieldPath).Root.Elements().Single(x => (string)x.Element("defName") == "DeltaV_ShieldGenerator");
        Game previous = Current.Game;
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        Current.Game.components = new List<GameComponent>();
        typeof(Game).GetField("maps", PrivateInstance).SetValue(Current.Game, new List<Map> { null });
        try
        {
            foreach (var row in representative)
            foreach (int count in new[] { 1, 3 })
            foreach (bool emergency in new[] { false, true })
            foreach (bool reload in new[] { false, true })
            {
                var pool = Enumerable.Range(0, count).Select(_ => AuditStandardShield(shieldDef)).ToArray();
                if (!emergency) foreach (var s in pool) s.parent.GetComp<CompRefuelable>().ConsumeFuel((float)s.EmergencyCapacity);
                int shotIndex = 0, nextShot = 0; double? regularEmpty = null, firstGap = null;
                double paid = 0;
                for (int t = 0; t < 36000; t++)
                {
                    if (t > 0) foreach (var s in pool) AuditGuardTick(s, true);
                    if (t != nextShot) continue;
                    if (!reload && row.Family != "Laser" && shotIndex >= row.StockShots) break;
                    paid += Hit(pool[0], row.ShieldDamage, pool);
                    if (!regularEmpty.HasValue && pool[0].RegularCapacity <= 1e-8) regularEmpty = t / 60d;
                    if (!pool[0].HasGuardCapacity) { firstGap = t / 60d; break; }
                    nextShot = row.Cycle.ShotAt(++shotIndex);
                }
                guardRows.Add(new { row.Key, Generators = count, FullEmergency = emergency, UnlimitedAmmo = reload,
                    RegularEmptySeconds = regularEmpty, FirstGapSeconds = firstGap, ShotsToGap = firstGap.HasValue ? shotIndex + 1 : (int?)null,
                    Absorbed = paid, RemainingRegular = pool.Sum(s => s.RegularCapacity) });
            }
        }
        finally { Current.Game = previous; }
        var optima = new List<object>();
        foreach (string size in new[] { "Small", "Medium" })
        foreach (double distance in new[] { 50d, 100d, 150d, 200d })
        {
            var eligible = rows.Where(r => r.Family == "Heavy" && r.Material == "Steel" && r.Size == size && r.LifeTravel > distance && r.AimRange >= distance).OrderByDescending(r => r.SustainedDps).ToList();
            if (eligible.Count > 0) optima.Add(new { Size = size, Distance = distance, Best = eligible[0] });
        }
        string output = Path.Combine(repo, "Source/obj/WeaponTuningRegression/output-audit.json");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var data = new { Assumptions = "60 ticks/s; cold and ready at t=0; [0,T) windows; stationary normal incidence; all shots hit; unlimited supply except StockDps60 and Guards with UnlimitedAmmo=false; lasers always have sufficient energy; no flight delay in guard timing; first local dome gap; shared regular but only front emergency; no silver replenishment; current standard 750+750, default 10% rejoin; powered recovery.",
            AssemblySha256 = Hash(typeof(CompWeaponTuning).Assembly.Location), DefsSha256 = Hash(defsPath), EffectsSha256 = Hash(effectsPath), ProjectileSha256 = Hash(projectilePath), SuppliesSha256 = Hash(suppliesPath), ShieldSha256 = Hash(shieldPath),
            Count = rows.Count, Rows = rows, Guards = guardRows, DistanceOptima = optima };
        File.WriteAllText(output, JsonSerializer.Serialize(data, new JsonSerializerOptions { IncludeFields = true, WriteIndented = false }));
        Console.WriteLine($"OUTPUT AUDIT: {rows.Count} configurations, {guardRows.Count} guard scenarios; {output}");
        return rows;
    }
}

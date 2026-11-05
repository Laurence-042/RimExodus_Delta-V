using System;
using System.Collections.Generic;
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
    sealed record ShieldCandidate(string Name, int Regular, int Emergency, int DelayTicks,
        int RecoveryTicks, int RejoinPercent, int RecoveryWatts, int SilverMultiplier = 15)
    {
        public double RecoveryPerSecond => Regular * 60d / RecoveryTicks;
        public double EmptyToRejoinSeconds => (DelayTicks + RecoveryTicks * RejoinPercent / 100d) / 60d;
    }
    sealed record Exposure(string Name, int OpenTicks, int ClosedTicks, int PhaseTicks = 0)
    {
        public bool Open(int tick) => ClosedTicks == 0 || (tick + PhaseTicks) % (OpenTicks + ClosedTicks) < OpenTicks;
    }
    sealed class DurationGun
    {
        public AuditRow Row;
        public CompWeaponTuning Tuning;
        public int FirstTick, Shots, CompletedThermalCycles;
        public double InitialEnergy;
    }
    sealed class DurationResult
    {
        public string Candidate, Loadout, Cadence, Exposure;
        public int Guns, Shields;
        public int LaserRechargeWatts;
        public bool UnlimitedSupply, FullEmergency, Powered, SplitTargets;
        public double? RegularEmptySeconds, FirstGapSeconds;
        public int CompletedBreaksBeforeGap, RegularRejoins, Shots;
        public int AllCoolingWindows, MinCompletedThermalCycles, MaxCompletedThermalCycles;
        public double TotalAllCoolingSeconds, LongestAllCoolingSeconds;
        public double Incoming, Absorbed, Regenerated, ChargeWattDays, ShotWattDays, Steel, Uranium;
        public double RegularRemaining, EmergencyRemaining;
        public List<double[]> Trace;
    }
    static readonly Func<CompSpaceShield, double, IReadOnlyList<CompSpaceShield>, double> DurationDebit =
        typeof(CompSpaceShield).GetMethod("ConsumeGuard", BindingFlags.Instance | BindingFlags.NonPublic)
        .CreateDelegate<Func<CompSpaceShield, double, IReadOnlyList<CompSpaceShield>, double>>();

    static CompSpaceShield DurationShield(ShieldCandidate candidate, bool emergency)
    {
        // New fixture props only: never mutate a loaded Def or production default.
        var body = new Building { def = new ThingDef { comps = new() {
            new CompProperties_Refuelable { fuelCapacity = candidate.Emergency, initialFuelPercent = emergency ? 1 : 0,
                consumeFuelOnlyWhenUsed = true, targetFuelLevelConfigurable = true,
                initialConfigurableTargetFuelLevel = candidate.Emergency },
            new CompProperties_SpaceShield { regularCapacity = candidate.Regular, emergencyCapacity = candidate.Emergency,
                recoveryDelayTicks = candidate.DelayTicks, guardRecoveryTicks = candidate.RecoveryTicks,
                recoveryPowerWatts = candidate.RecoveryWatts }
        } } };
        body.InitializeComps();
        typeof(CompProperties_Refuelable).GetField("fuelMultiplier", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(body.GetComp<CompRefuelable>().Props, (float)candidate.SilverMultiplier);
        var shield = body.GetComp<CompSpaceShield>();
        shield.PostPostMake(); shield.AutoRejoinPercent = candidate.RejoinPercent;
        SetSpawned(body, true);
        return shield;
    }

    static DurationResult SimulateDuration(ShieldCandidate candidate, string loadout, AuditRow[] weapons,
        int shields, bool staggered, Exposure exposure, Dictionary<string, XElement> defs,
        bool unlimited = true, bool emergency = true, bool powered = true, bool split = false, bool trace = false,
        int laserRechargeWatts = 0, Action<CompWeaponTuning, AuditRow> configure = null)
    {
        var result = new DurationResult { Candidate = candidate.Name, Loadout = loadout, Guns = weapons.Length,
            Shields = shields, Cadence = staggered ? "Staggered" : "Synchronized", Exposure = exposure.Name,
            UnlimitedSupply = unlimited, FullEmergency = emergency, Powered = powered, SplitTargets = split,
            LaserRechargeWatts = laserRechargeWatts,
            Trace = trace ? new List<double[]>() : null };
        var pool = Enumerable.Range(0, shields).Select(_ => DurationShield(candidate, emergency)).ToArray();
        DurationResult Finish()
        {
            result.RegularRemaining = pool.Sum(s => s.RegularCapacity);
            result.EmergencyRemaining = pool.Sum(s => s.EmergencyCapacity);
            double initial = shields * (candidate.Regular + (emergency ? candidate.Emergency : 0d));
            double residual = initial + result.Regenerated - result.Absorbed - result.RegularRemaining - result.EmergencyRemaining;
            if (Math.Abs(residual) > .05 + initial * 1e-6)
                throw new Exception($"Shield conservation failed: {candidate.Name}/{loadout}: {residual}");
            if (result.Absorbed > result.Incoming + .05)
                throw new Exception("Shield absorbed more than the incoming budget");
            return result;
        }
        var indices = new Dictionary<string, int>();
        var guns = weapons.Select(row => {
            var gun = Gun(defs[row.Weapon]);
            if (gun.Settings.chemical) { gun.charge = row.Charge; gun.mass = row.Heads; }
            gun.interval = row.Interval; gun.power = row.Power;
            configure?.Invoke(gun, row);
            int index = indices.GetValueOrDefault(row.Key); indices[row.Key] = index + 1;
            // Spread repeated weapons of each type over their complete thermal cycle.
            int phase = staggered ? row.CycleTicks * index / weapons.Count(r => r.Key == row.Key) : 0;
            return new DurationGun { Row = row, Tuning = gun, FirstTick = phase,
                InitialEnergy = row.Size == "Medium" ? 1200 : 600 };
        }).ToArray();
        bool previousOpen = exposure.Open(0);
        int coolingRun = 0;
        const int horizon = 600 * 60;
        for (int t = 0; t < horizon; t++)
        {
            bool open = exposure.Open(t);
            if (open && !previousOpen) result.CompletedBreaksBeforeGap++;
            previousOpen = open;
            if (t > 0)
            {
                foreach (var gun in guns)
                {
                    bool locked = gun.Tuning.overheated;
                    AuditThermalTick(gun.Tuning);
                    if (locked && !gun.Tuning.overheated) gun.CompletedThermalCycles++;
                }
                foreach (var shield in pool)
                {
                    double before = shield.RegularCapacity;
                    bool shared = shield.UsesRegular;
                    AuditGuardTick(shield, powered);
                    double recovered = shield.RegularCapacity - before;
                    result.Regenerated += recovered;
                    if (recovered > 0) result.ChargeWattDays += candidate.RecoveryWatts * CompPower.WattsToWattDaysPerTick;
                    if (!shared && shield.UsesRegular) result.RegularRejoins++;
                }
            }
            bool firedThisTick = false;
            for (int i = 0; i < guns.Length; i++)
            {
                var gun = guns[i]; var row = gun.Row;
                if (!open || t < gun.FirstTick || gun.Tuning.RemainingTicks > 0) continue;
                if (!unlimited)
                {
                    if (row.Family == "Laser")
                    {
                        // With recharge, this is an optimistic cumulative energy budget,
                        // not a simulation of capacitor selection, capacity or power grids.
                        double energy = gun.InitialEnergy + laserRechargeWatts * (double)CompPower.WattsToWattDaysPerTick * t;
                        if ((gun.Shots + 1) * row.EnergyPerShot > energy + 1e-6) continue;
                    }
                    else if (gun.Shots >= row.StockShots) continue;
                }
                gun.Tuning.CommitShot(); gun.Shots++; result.Shots++;
                firedThisTick = true;
                result.Incoming += row.ShieldDamage; result.ShotWattDays += row.EnergyPerShot;
                result.Steel += row.SteelPerShot; result.Uranium += row.UraniumPerShot;
                CompSpaceShield target = pool[split ? i % shields : 0];
                result.Absorbed += DurationDebit(target, row.ShieldDamage, pool);
                if (!result.RegularEmptySeconds.HasValue && !pool.Any(s => s.UsesRegular)) result.RegularEmptySeconds = t / 60d;
                if (!target.HasGuardCapacity && !result.FirstGapSeconds.HasValue) result.FirstGapSeconds = t / 60d;
            }
            if (!firedThisTick && guns.All(g => g.Tuning.overheated))
            {
                if (coolingRun == 0) result.AllCoolingWindows++;
                coolingRun++;
                result.TotalAllCoolingSeconds += 1d / 60;
                result.LongestAllCoolingSeconds = Math.Max(result.LongestAllCoolingSeconds, coolingRun / 60d);
            }
            else coolingRun = 0;
            result.MinCompletedThermalCycles = guns.Min(g => g.CompletedThermalCycles);
            result.MaxCompletedThermalCycles = guns.Max(g => g.CompletedThermalCycles);
            if (trace && (t % 60 == 0 || result.FirstGapSeconds.HasValue))
                result.Trace.Add(new[] { t / 60d, pool.Sum(s => s.RegularCapacity), pool[0].EmergencyCapacity,
                    result.Incoming, result.Regenerated, open ? 1d : 0d });
            // Finish all same-tick shots before recording the result: a salvo is not truncated.
            if (result.FirstGapSeconds.HasValue) return Finish();
        }
        return Finish();
    }

    static void RunShieldDurationAudit(string repo, List<AuditRow> rows)
    {
        var weaponsPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml");
        var defs = XDocument.Load(weaponsPath).Root.Elements().Where(d => d.Element("defName") != null)
            .ToDictionary(d => (string)d.Element("defName"));
        AuditRow Pick(string family, int charge = 0, int heads = 0, int interval = 0, int power = 1,
            string size = "Medium", string material = "Steel") => rows.Single(r => r.Family == family && r.Size == size
                && r.Material == (family == "Laser" ? "None" : material) && r.Power == power
                && (charge == 0 || r.Charge == charge) && (heads == 0 || r.Heads == heads) && (interval == 0 || r.Interval == interval));
        var heavy = Pick("Heavy", 48, 48);
        var rapid = Pick("Rapid", interval: 10);
        var laser = Pick("Laser");
        var loadouts = new Dictionary<string, AuditRow[]> {
            ["Heavy48"] = new[] { heavy }, ["Heavy96"] = new[] { Pick("Heavy", 96, 96) },
            ["Heavy8_96"] = new[] { Pick("Heavy", 8, 96) }, ["Heavy1_96"] = new[] { Pick("Heavy", 1, 96) },
            ["Heavy48U"] = new[] { Pick("Heavy", 48, 48, material: "Uranium") },
            ["RapidDefault"] = new[] { rapid }, ["RapidMax"] = new[] { Pick("Rapid", interval: 4) },
            ["RapidSteady"] = new[] { Pick("Rapid", interval: 25) },
            ["Laser1"] = new[] { laser }, ["Laser2"] = new[] { Pick("Laser", power: 2) },
            ["Laser4"] = new[] { Pick("Laser", power: 4) },
            ["Mixed"] = new[] { heavy, heavy, rapid, laser },
            ["MixedHot"] = new[] { heavy, heavy, rapid, Pick("Laser", power: 4) },
            ["SmallHeavy"] = new[] { Pick("Heavy", 24, 24, size: "Small") },
            ["SmallRapid"] = new[] { Pick("Rapid", interval: 12, size: "Small") }
        };
        var candidates = new[] {
            new ShieldCandidate("Current", 1000, 2000, 600, 2500, 100, 1000, 5),
            new ShieldCandidate("A18k", 18000, 6000, 900, 9000, 10, 3000),
            new ShieldCandidate("B30k", 30000, 6000, 900, 15000, 10, 3000),
            new ShieldCandidate("C42k", 42000, 6000, 900, 21000, 10, 3000),
            new ShieldCandidate("B30kFast", 30000, 6000, 900, 7500, 10, 6000),
            new ShieldCandidate("B30kNaive", 30000, 6000, 600, 2500, 100, 1000)
        };
        var constant = new Exposure("Continuous", 3600, 0);
        var exposures = new[] { constant, new Exposure("45on15off_p0", 2700, 900),
            new Exposure("45on15off_p15", 2700, 900, 900), new Exposure("45on15off_p30", 2700, 900, 1800),
            new Exposure("30on30off", 1800, 1800) };
        var results = new List<DurationResult>();
        var unique = new HashSet<string>();
        AuditRow[] Fit(string name, int count) => Enumerable.Range(0, count).Select(i => loadouts[name][i % loadouts[name].Length]).ToArray();
        void Run(ShieldCandidate c, string name, int gunCount, int shieldCount, bool stagger, Exposure exposure,
            bool unlimited = true, bool emergency = true, bool powered = true, bool split = false, int laserRechargeWatts = 0)
        {
            // Mixed is exactly 2 heavy + 1 rapid + 1 laser per four-weapon block.
            if (loadouts[name].Length > 1 && gunCount % 4 != 0) return;
            string key = $"{c.Name}:{name}:{gunCount}:{shieldCount}:{stagger}:{exposure.Name}:{unlimited}:{emergency}:{powered}:{split}:{laserRechargeWatts}";
            if (!unique.Add(key)) return;
            bool trace = gunCount == 4 && shieldCount == 2 && !stagger && unlimited && emergency && powered && !split
                && new[] { "Current", "B30k" }.Contains(c.Name) && new[] { "Heavy48", "RapidDefault", "Mixed", "Laser4" }.Contains(name)
                && new[] { "Continuous", "45on15off_p0" }.Contains(exposure.Name);
            results.Add(SimulateDuration(c, name, Fit(name, gunCount), shieldCount, stagger, exposure, defs,
                unlimited, emergency, powered, split, trace, laserRechargeWatts));
        }
        var recovery = new List<object>();
        Game previous = Current.Game;
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(Game).GetField("maps", PrivateInstance).SetValue(Current.Game, new List<Map> { null });
        try
        {
            foreach (var candidate in candidates)
            foreach (string name in new[] { "Heavy48", "Heavy96", "RapidDefault", "Laser1", "Laser4", "Mixed", "MixedHot" })
            foreach (bool stagger in new[] { false, true })
            foreach (var exposure in exposures.Take(2)) Run(candidate, name, 4, 2, stagger, exposure);

            var proposed = candidates.Single(c => c.Name == "B30k");
            foreach (string name in loadouts.Keys)
            foreach (var scale in new[] { (2, 1), (4, 2), (8, 4), (4, 1), (8, 2) })
            foreach (bool stagger in new[] { false, true })
            {
                foreach (var exposure in exposures) Run(proposed, name, scale.Item1, scale.Item2, stagger, exposure);
                Run(candidates[0], name, scale.Item1, scale.Item2, stagger, constant);
                Run(proposed, name, scale.Item1, scale.Item2, stagger, constant, unlimited: false);
                Run(proposed, name, scale.Item1, scale.Item2, stagger, constant, emergency: false);
                Run(proposed, name, scale.Item1, scale.Item2, stagger, constant, powered: false);
                Run(proposed, name, scale.Item1, scale.Item2, stagger, constant, split: true);
                if (name.StartsWith("Laser"))
                    foreach (int watts in new[] { 3600, 10000 })
                        Run(proposed, name, scale.Item1, scale.Item2, stagger, constant, unlimited: false, laserRechargeWatts: watts);
            }
            foreach (var candidate in candidates)
            {
                var shield = DurationShield(candidate, false);
                DurationDebit(shield, candidate.Regular, new[] { shield });
                int rejoin = -1, full = -1;
                for (int t = 1; t <= 36000; t++)
                {
                    AuditGuardTick(shield, true);
                    if (rejoin < 0 && shield.UsesRegular) rejoin = t;
                    if (shield.RegularCapacity == candidate.Regular) { full = t; break; }
                }
                var fuel = shield.parent.GetComp<CompRefuelable>();
                fuel.Refuel(candidate.Emergency / (float)candidate.SilverMultiplier);
                if (Math.Abs(fuel.Fuel - candidate.Emergency) > .001) throw new Exception("Candidate silver conversion failed");
                recovery.Add(new { candidate.Name, RejoinSeconds = rejoin / 60d, FullSeconds = full / 60d,
                    SilverForFullEmergency = candidate.Emergency / (double)candidate.SilverMultiplier });
            }
        }
        finally { Current.Game = previous; }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string path = Path.Combine(repo, "Source/obj/WeaponTuningRegression/shield-duration-audit.json");
        var output = new { HorizonSeconds = 600, Candidates = candidates, Exposures = exposures, Recovery = recovery,
            Loadouts = loadouts.ToDictionary(p => p.Key, p => p.Value.Select(r => r.Key).ToArray()),
            AssemblySha256 = Hash(typeof(CompSpaceShield).Assembly.Location), WeaponsSha256 = Hash(weaponsPath),
            Notes = "Production thermal and guard components; fixture-only proposed props. Static normal hits with no flight delay. First shield gap, not ship death. Ideal no-line-of-fire windows, not actual MPC or dodge. All shots hit while exposed. Shared regular, one local emergency unless SplitTargets. Full initial stocks; UnlimitedSupply=false stops at own ammo or 600 Wd per laser slot. Optional LaserRechargeWatts gives an optimistic cumulative initial-energy-plus-input budget, NOT an actual capacitor/grid simulation. Same-tick salvo completes before stop. Null gap means no gap within 600s, not infinite survival.",
            Results = results };
        File.WriteAllText(path, JsonSerializer.Serialize(output, new JsonSerializerOptions { IncludeFields = true }));
        Console.WriteLine($"SHIELD DURATION AUDIT: {results.Count} scenarios; {path}");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    // Proposed numbers live only in fresh fixture props. New low-speed damage is
    // an explicit analytical proposal, NOT a claim that production already uses it.
    static void ThermalProposal(CompWeaponTuning gun, AuditRow row)
    {
        bool medium = row.Size == "Medium";
        if (row.Family == "Heavy") gun.Settings.heatPerShot = 8;
        if (row.Family == "Rapid")
        {
            gun.Settings.coolingPerSecond = medium ? 12 : 3;
            gun.Settings.heatCapacity = medium ? 240 : 60;
        }
        if (row.Family == "Laser")
        {
            gun.Settings.heatPerShot = (medium ? 2 : 1) * row.Power;
            gun.Settings.coolingPerSecond = medium ? 4 : 2;
            gun.Settings.heatCapacity = medium ? 80 : 40;
        }
    }

    static void RunThermalBalanceAudit(string repo, List<AuditRow> current)
    {
        var defs = XDocument.Load(Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml"))
            .Root.Elements().Where(d => d.Element("defName") != null).ToDictionary(d => (string)d.Element("defName"));
        AuditRow Pick(string family, int charge = 0, int heads = 0, int power = 1,
            string size = "Medium", string material = "Steel") => current.First(r => r.Family == family && r.Size == size
            && r.Material == (family == "Laser" ? "None" : material) && r.Power == power
            && (charge == 0 || r.Charge == charge) && (heads == 0 || r.Heads == heads));
        AuditRow Proposed(AuditRow old, int interval = 0)
        {
            var row = JsonSerializer.Deserialize<AuditRow>(JsonSerializer.Serialize(old,
                new JsonSerializerOptions { IncludeFields = true }), new JsonSerializerOptions { IncludeFields = true });
            if (interval > 0) row.Interval = interval;
            var gun = Gun(defs[row.Weapon]); gun.charge = row.Charge; gun.mass = row.Heads;
            gun.interval = row.Interval; gun.power = row.Power;
            ThermalProposal(gun, row);
            row.Cycle = MeasureCycle(gun); row.CycleTicks = row.Cycle.Ticks; row.CycleShots = row.Cycle.Shots.Count;
            if (row.Family == "Heavy") row.Interval = row.CycleTicks;
            row.FirstLockSeconds = row.Cycle.FirstLock / 60d;
            double factor = .25 * (row.Family == "Laser" ? 1 : Math.Min(1, row.Speed / 60));
            row.Damage *= factor; row.ShieldDamage *= factor;
            row.SustainedDps = row.Damage * row.CycleShots * 60 / row.CycleTicks;
            row.EnergyPerShot *= row.Family == "Laser" ? .5 : 1;
            row.PulseWatts = row.EnergyPerShot * row.CycleShots * 60 / row.CycleTicks / (60d * CompPower.WattsToWattDaysPerTick);
            return row;
        }
        var h = Proposed(Pick("Heavy", 48, 48));
        var r = Proposed(Pick("Rapid"), 20);
        var l = Proposed(Pick("Laser"));
        var loadouts = new Dictionary<string, AuditRow[]> {
            ["Heavy48"] = new[] { h }, ["Heavy96"] = new[] { Proposed(Pick("Heavy",96,96)) },
            ["Heavy1_96"] = new[] { Proposed(Pick("Heavy",1,96)) },
            ["Heavy8_96"] = new[] { Proposed(Pick("Heavy",8,96)) },
            ["Heavy48U"] = new[] { Proposed(Pick("Heavy",48,48,material:"Uranium")) },
            ["RapidDefault"] = new[] { r }, ["RapidFast"] = new[] { Proposed(Pick("Rapid"),8) },
            ["RapidSlow"] = new[] { Proposed(Pick("Rapid"),60) },
            ["Laser1"] = new[] { l }, ["Laser2"] = new[] { Proposed(Pick("Laser",power:2)) },
            ["Laser4"] = new[] { Proposed(Pick("Laser",power:4)) },
            ["Mixed"] = new[] { h,h,r,l },
            ["MixedHot"] = new[] { h,h,r,Proposed(Pick("Laser",power:4)) },
            ["SmallHeavy"] = new[] { Proposed(Pick("Heavy",24,24,size:"Small")) },
            ["SmallRapid"] = new[] { Proposed(Pick("Rapid",size:"Small"),20) },
            ["SmallLaser"] = new[] { Proposed(Pick("Laser",size:"Small")) }
        };
        var candidates = new[] {
            new ShieldCandidate("S600",600,300,360,6000,10,1000,5),
            new ShieldCandidate("S800",800,400,360,6000,10,1000,5),
            new ShieldCandidate("S1000",1000,500,360,7500,10,1000,5),
            new ShieldCandidate("S800Fast",800,400,360,4000,10,1000,5)
        };
        var results = new List<DurationResult>();
        var native = new List<object>();
        Game previous = Current.Game;
        Current.Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(Game).GetField("maps",PrivateInstance).SetValue(Current.Game,new List<Map>{null});
        try
        {
            foreach(var c in candidates)
            foreach(var pair in loadouts)
            foreach(var scale in new[] {(2,1),(4,2),(8,4),(4,1),(2,2),(4,4),(8,8)})
            foreach(bool stagger in new[] {false,true})
            {
                if(pair.Value.Length>1 && scale.Item1%4!=0) continue;
                var guns = Enumerable.Range(0,scale.Item1).Select(i=>pair.Value[i%pair.Value.Length]).ToArray();
                results.Add(SimulateDuration(c,pair.Key,guns,scale.Item2,stagger,new Exposure("Continuous",1,0),defs,
                    configure:ThermalProposal));
                if(c.Name=="S800" && scale==(4,2))
                    results.Add(SimulateDuration(c,pair.Key,guns,scale.Item2,stagger,new Exposure("Continuous",1,0),defs,
                        emergency:false,configure:ThermalProposal));
            }
            // Native burst schedule: 24 x 15, 5 ticks apart, 634-tick full cycle.
            // Damage/AI not simulated; actual Delta-V guard debit/recovery is used.
            foreach(var c in candidates)
            foreach(int attackers in new[] {1,4})
            {
                var s=DurationShield(c,true); double? gap=null;
                for(int t=0;t<36000;t++)
                {
                    if(t>0) AuditGuardTick(s,true);
                    int phase=t%634;
                    if(phase<=115 && phase%5==0) DurationDebit(s,15*attackers,new[]{s});
                    if(!s.HasGuardCapacity) {gap=t/60d;break;}
                }
                native.Add(new {Candidate=c.Name,Attackers=attackers,FirstGapSeconds=gap});
            }
        }
        finally {Current.Game=previous;}
        var cycles=loadouts.Where(p=>p.Value.Length==1).Select(p=>new {
            Name=p.Key,p.Value[0].Key,p.Value[0].Damage,p.Value[0].CycleShots,
            BurstDamage=p.Value[0].Damage*p.Value[0].CycleShots,
            p.Value[0].FirstLockSeconds,CycleSeconds=p.Value[0].CycleTicks/60d,
            CoolSeconds=p.Value[0].CycleTicks/60d-p.Value[0].FirstLockSeconds,
            p.Value[0].SustainedDps,p.Value[0].EnergyPerShot,p.Value[0].PulseWatts});
        string path=Path.Combine(repo,"Source/obj/WeaponTuningRegression/thermal-balance-audit.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new {
            Assumptions="Proposed fixture parameters only; damage analytically transformed from production reference; actual production thermal and guard methods; unlimited ammunition/energy; no silver refill; all hits normal incidence stationary; continuous opportunities; first-hit time origin; front emergency only; no hull/flight/maneuver simulation; stagger is initial phase only, not forced synchronization.",
            Candidates=candidates,Cycles=cycles,Results=results,Native=native
        },new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
        Console.WriteLine($"THERMAL BALANCE AUDIT: {results.Count} ship scenarios; {path}");
    }
}

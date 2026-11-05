using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using Verse;

internal static partial class Program
{
    static void EnergyProposal(CompWeaponTuning gun,AuditRow row)
    {
        RoleProposal(gun,row);
        if(row.Family=="Rapid"&&row.Size=="Medium")
        {gun.Settings.coolingPerSecond=85;gun.Settings.heatCapacity=340;}
    }
    static void RunEnergyImpulseAudit(string repo,List<AuditRow> current)
    {
        var json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
        var defs=XDocument.Load(Path.Combine(repo,"1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml")).Root.Elements()
            .Where(x=>x.Element("defName")!=null).ToDictionary(x=>(string)x.Element("defName"));
        var proposed=new List<AuditRow>();var cycles=new Dictionary<(string,int,int),AuditCycle>();
        foreach(var old in current)
        {
            var row=JsonSerializer.Deserialize<AuditRow>(JsonSerializer.Serialize(old,json),json);
            var key=(row.Weapon,row.Charge,row.Interval);
            if(!cycles.TryGetValue(key,out var cycle))
            {
                var gun=Gun(defs[row.Weapon]);gun.charge=row.Charge;gun.mass=row.Heads;gun.interval=row.Interval;gun.power=row.Power;
                EnergyProposal(gun,row);cycles[key]=cycle=MeasureCycle(gun);
            }
            row.Cycle=cycle;row.CycleTicks=cycle.Ticks;row.CycleShots=cycle.Shots.Count;row.FirstLockSeconds=cycle.FirstLock/60d;
            if(row.Family=="Heavy")row.Interval=cycle.Ticks;
            bool laser=row.Family=="Laser";
            double m=row.Heads*(row.Material=="Uranium"?1.5:1),size=row.Size=="Medium"?2:1;
            row.Damage=laser?8*size*row.Power:EnergyImpulseModel.Budget(EnergyImpulseModel.Energy(m,row.Speed));
            row.ShieldDamage=laser?size*row.Power:row.Damage/8;
            double rate=cycle.Shots.Count*60d/cycle.Ticks;
            row.SustainedDps=row.Damage*rate;row.BurstDps=row.Damage*60/row.Interval;
            row.Shots10=cycle.CountBefore(600);row.Shots60=cycle.CountBefore(3600);
            row.Dps10=row.Shots10*row.Damage/10;row.Dps60=row.Shots60*row.Damage/60;
            row.StockDps60=laser?0:Math.Min(row.StockShots,row.Shots60)*row.Damage/60;
            row.SteelPerSecond=row.SteelPerShot*rate;row.UraniumPerSecond=row.UraniumPerShot*rate;
            row.LastStockShotSeconds=laser?0:cycle.ShotAt(row.StockShots-1)/60d;row.PulseWatts=row.EnergyPerShot*rate*1000;
            proposed.Add(row);
        }
        // New-law properties: no material/mass bonus at the same launch charge;
        // current production LaunchSpeed is used, hence tolerate float roundoff.
        foreach(var group in proposed.Where(r=>r.Family=="Heavy").GroupBy(r=>(r.Size,r.Charge)))
            if(group.Max(r=>r.Damage)-group.Min(r=>r.Damage)>.002)throw new Exception("Energy varied with mass at fixed charge");
        long scan=0;double largestExcess=0;
        foreach(var r in proposed.Where(r=>r.Family=="Heavy"))
        foreach(int s in new[]{0,5,10,25,50,75,100})foreach(int angle in new[]{0,30,60})
        {
            double m=r.Heads*(r.Material=="Uranium"?1.5:1),a=angle*Math.PI/180;
            var p=EnergyImpulseModel.Apply(m,-r.Speed*Math.Cos(a),r.Speed*Math.Sin(a),false,s);
            if(Math.Abs(m*(p.Normal+r.Speed*Math.Cos(a))-p.Impulse)>1e-6||Math.Abs(p.Tangent-r.Speed*Math.Sin(a))>1e-9)
                throw new Exception("Impulse/tangential invariant failed");
            scan++;largestExcess=Math.Max(largestExcess,p.Damage/r.Damage);
        }
        var selected=new Dictionary<string,AuditRow>();
        foreach(string size in new[]{"Small","Medium"})
        {
            int d=size=="Small"?24:48,max=size=="Small"?32:96;
            foreach(var pair in new[]{(1,max),(8,max),(d,8),(d,d),(d,max),(max,max)})
            foreach(string material in new[]{"Steel","Uranium"})
                selected[$"{size}_Heavy_{pair.Item1}_{pair.Item2}_{material}"]=proposed.First(r=>r.Size==size&&r.Family=="Heavy"&&r.Charge==pair.Item1&&r.Heads==pair.Item2&&r.Material==material);
            foreach(int i in size=="Small"?new[]{6,8,12,30}:new[]{4,6,10,15,30})
                selected[$"{size}_Rapid_{i}"]=proposed.First(r=>r.Size==size&&r.Family=="Rapid"&&r.Interval==i&&r.Material=="Steel");
            foreach(int p in new[]{1,2,3,4})
                selected[$"{size}_Laser_{p}"]=proposed.First(r=>r.Size==size&&r.Family=="Laser"&&r.Power==p);
        }
        var rails=new List<object>();
        foreach(bool large in new[]{false,true})foreach(int stages in new[]{0,1,2,3})foreach(string material in new[]{"Steel","Uranium"})
        {
            double m=(large?40:20)*(material=="Uranium"?1.5:1),e=EnergyImpulseModel.RailEnergy(large,stages);
            rails.Add(new{Name=$"Rail{(large?"Large":"Small")}_{stages}_{material}",Mass=m,Stages=stages,
                Speed=Math.Sqrt(2*e/m),Damage=EnergyImpulseModel.Budget(e,true),CycleTicks=large?2400:1800,
                GridPulse=(large?3600:1800)*(1+stages)});
        }
        var line=new List<object>();
        foreach(var pair in selected.Where(p=>p.Value.Family!="Laser"))
        foreach(int strength in new[]{0,5,10,25,50,60,75,100})
        {
            var r=pair.Value;double m=r.Heads*(r.Material=="Uranium"?1.5:1);
            var p=EnergyImpulseModel.Apply(m,-r.Speed,0,false,strength);
            line.Add(new{Name=pair.Key,Mass=m,Strength=strength,Response=p,RemainingRatio=p.Damage/r.Damage});
        }
        var c=new ShieldCandidate("EnergyImpulse",750,750,180,7500,10,1000,5);
        var guards=new List<DurationResult>();var native=new List<object>();
        Game previous=Current.Game;Current.Game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(Game).GetField("maps",PrivateInstance).SetValue(Current.Game,new List<Map>{null});
        try
        {
            foreach(var pair in selected)foreach(var count in new[]{(1,1),(2,1),(4,2),(8,2),(8,4)})foreach(bool stagger in new[]{false,true})
                guards.Add(SimulateDuration(c,pair.Key,Enumerable.Repeat(pair.Value,count.Item1).ToArray(),count.Item2,stagger,new Exposure("Continuous",1,0),defs,configure:EnergyProposal));
            foreach(bool powered in new[]{false,true})foreach(int attackers in new[]{1,4})
            {
                var shield=DurationShield(c,true);double? gap=null;
                for(int t=0;t<36000;t++){if(t>0)AuditGuardTick(shield,powered);int phase=t%634;
                    if(phase<=115&&phase%5==0)DurationDebit(shield,15*attackers,new[]{shield});if(!shield.HasGuardCapacity){gap=t/60d;break;}}
                native.Add(new{Powered=powered,Attackers=attackers,FirstGap=gap});
            }
        }finally{Current.Game=previous;}
        var boundaries=new List<object>();
        foreach(var mass in new[]{1d,2,20,48,96,144})foreach(bool railShape in new[]{false,true})foreach(int strength in new[]{0,10,100})foreach(double available in new[]{0d,1,2,3,10,10000})
        {
            var p=EnergyImpulseModel.Apply(mass,-60,15,railShape,strength,available);
            if(p.Cost>available+1e-7||p.Broken&&p.Impulse/mass+1e-9<(railShape?.5:4.5))throw new Exception("Unfunded effect");
            boundaries.Add(new{Mass=mass,Rail=railShape,Strength=strength,Available=available,Response=p});
        }
        foreach(bool railShape in new[]{false,true})
        {
            double mass=railShape?20:24;
            var parent=EnergyImpulseModel.Apply(mass,-60,15,railShape,100);
            var child=EnergyImpulseModel.Apply(mass/parent.Fragments,parent.Normal,parent.Tangent,railShape,0,fragmented:true);
            if(!parent.Broken||child.Broken||Math.Abs(parent.Damage-child.Damage*parent.Fragments)>1e-8)
                throw new Exception("Fragment conversion was duplicated/lost on a second shield");
        }
        File.WriteAllText(Path.Combine(repo,"Source/obj/WeaponTuningRegression/energy-impulse-audit.json"),JsonSerializer.Serialize(new{
            Assumptions="V4 proposal, NOT production kinetic behavior. Production launch speed, thermal and guard methods; analytical energy/impulse law in shared offline model; stationary normal incidence unless angle explicitly scanned; all old C/M combinations retained. V3 sustained DPS anchors, medium rapid cooling compensation 85/340. Full supply; no new laser heat.",
            Candidate=c,Rows=proposed,Loadouts=selected.Select(p=>new{Name=p.Key,Row=p.Value,Shots=p.Value.Cycle.Shots}),
            Rails=rails,Line=line,Guard=guards,Native=native,Boundaries=boundaries,FullGridLineCases=scan,MaxReflectedBudgetRatio=largestExcess},json));
        Console.WriteLine($"ENERGY IMPULSE: {proposed.Count} configs; {scan} full-grid line cases; {guards.Count} guard cases; {boundaries.Count} funding checks");
    }
}

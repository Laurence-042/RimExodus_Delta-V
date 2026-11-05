using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using Verse;
using UnityEngine;

internal static partial class Program
{
    // V3: existing momentum / penetration model. All changes are fixture values.
    static void RoleProposal(CompWeaponTuning gun, AuditRow row)
    {
        bool medium = row.Size == "Medium";
        if (row.Family == "Heavy") gun.Settings.coolingPerSecond = medium ? 24 : 16;
        if (row.Family == "Rapid")
        {
            gun.Settings.coolingPerSecond = medium ? 120 : 40;
            gun.Settings.heatCapacity = medium ? 480 : 160;
        }
        // Laser heat stays exactly zero. No added cooldown or exposure gates.
    }

    static void RunRoleBalanceAudit(string repo, List<AuditRow> current)
    {
        var json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        var defs = XDocument.Load(Path.Combine(repo,"1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml"))
            .Root.Elements().Where(x=>x.Element("defName")!=null).ToDictionary(x=>(string)x.Element("defName"));
        var effects = XDocument.Load(Path.Combine(repo,"1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml"));
        var proposed = new List<AuditRow>();
        var cycles = new Dictionary<(string,int,int),AuditCycle>();
        foreach(var old in current)
        {
            var row = JsonSerializer.Deserialize<AuditRow>(JsonSerializer.Serialize(old,json),json);
            var key = (row.Weapon,row.Charge,row.Interval);
            if (!cycles.TryGetValue(key,out var cycle))
            {
                var gun=Gun(defs[row.Weapon]); gun.charge=row.Charge; gun.mass=row.Heads;
                gun.interval=row.Interval; gun.power=row.Power; RoleProposal(gun,row);
                cycles[key]=cycle=MeasureCycle(gun);
            }
            row.Cycle=cycle; row.CycleTicks=cycle.Ticks; row.CycleShots=cycle.Shots.Count;
            row.FirstLockSeconds=cycle.FirstLock/60d;
            if(row.Family=="Heavy") row.Interval=cycle.Ticks;
            bool laser=row.Family=="Laser";
            var structure=AuditEffect(effects,laser ? (row.Size=="Medium"?"DeltaV_Impact_LaserHeavyVsStructure":"DeltaV_Impact_LaserVsStructure") : "DeltaV_Impact_CannonVsStructure");
            var shield=AuditEffect(effects,laser?"DeltaV_Impact_LaserVsShield":"DeltaV_Impact_VsShield");
            if(laser) {structure.damage=8;shield.damage=1;} else shield.damage=3;
            float mass=laser?1:row.Heads*(row.Material=="Uranium"?1.5f:1);
            float scale=laser?(row.Size=="Medium"?2:1)*row.Power:1;
            row.Damage=SpaceImpactEffects.ComputeDamage(structure,(float)row.Speed,0,1,mass)*scale;
            row.ShieldDamage=SpaceImpactEffects.ComputeDamage(shield,(float)row.Speed,0,1,mass)*scale;
            row.SustainedDps=row.Damage*row.CycleShots*60/row.CycleTicks;
            row.BurstDps=row.Damage*60/row.Interval;
            row.Shots10=cycle.CountBefore(600);row.Shots60=cycle.CountBefore(3600);
            row.Dps10=row.Shots10*row.Damage/10;row.Dps60=row.Shots60*row.Damage/60;
            row.StockDps60=Math.Min(row.StockShots,row.Shots60)*row.Damage/60;
            if(laser) row.StockDps60=0;
            double rate=row.CycleShots*60d/row.CycleTicks;
            row.SteelPerSecond=row.SteelPerShot*rate;row.UraniumPerSecond=row.UraniumPerShot*rate;
            row.PulseWatts=row.EnergyPerShot*rate*1000;
            row.LastStockShotSeconds=laser?0:cycle.ShotAt(row.StockShots-1)/60d;
            proposed.Add(row);
        }
        AuditRow Pick(string f,int c=0,int m=0,int interval=0,int p=1,string size="Medium",string material="Steel") =>
            proposed.First(r=>r.Family==f&&r.Size==size&&r.Material==(f=="Laser"?"None":material)
                &&(c==0||r.Charge==c)&&(m==0||r.Heads==m)&&(interval==0||r.Interval==interval)&&r.Power==p);
        var loadouts=new Dictionary<string,AuditRow[]>();
        foreach(string size in new[]{"Small","Medium"})
        {
            bool medium=size=="Medium";int d=medium?48:24,max=medium?96:32;
            foreach(var pair in new[]{(1,max),(8,max),(d,d),(max,max),(max,8)})
            foreach(string material in new[]{"Steel","Uranium"})
                loadouts[$"{size}_Heavy_{pair.Item1}_{pair.Item2}_{material}"]=new[]{Pick("Heavy",pair.Item1,pair.Item2,size:size,material:material)};
            foreach(int i in (medium?new[]{4,6,10,15,30}:new[]{6,8,12,30}))
                loadouts[$"{size}_Rapid_{i}"]=new[]{Pick("Rapid",interval:i,size:size)};
            foreach(int p in new[]{1,2,3,4})loadouts[$"{size}_Laser_{p}"]=new[]{Pick("Laser",p:p,size:size)};
            loadouts[$"{size}_Mixed"]=new[]{Pick("Heavy",d,d,size:size),Pick("Heavy",d,d,size:size),Pick("Rapid",interval:6,size:size),Pick("Laser",p:3,size:size)};
        }
        var candidate=new ShieldCandidate("V3",600,300,180,6000,10,1000,5);
        var results=new List<DurationResult>();var native=new List<object>();
        Game previous=Current.Game;Current.Game=(Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(Game).GetField("maps",PrivateInstance).SetValue(Current.Game,new List<Map>{null});
        try
        {
            foreach(var pair in loadouts)
            foreach(var count in new[]{(1,1),(2,1),(4,2),(8,2),(8,4)})
            foreach(bool stagger in new[]{false,true})
            {
                if(pair.Value.Length>1&&count.Item1%4!=0)continue;
                var guns=Enumerable.Range(0,count.Item1).Select(i=>pair.Value[i%pair.Value.Length]).ToArray();
                results.Add(SimulateDuration(candidate,pair.Key,guns,count.Item2,stagger,new Exposure("Continuous",1,0),defs,configure:RoleProposal));
            }
            foreach(bool powered in new[]{false,true})foreach(int attackers in new[]{1,4})
            {
                var s=DurationShield(candidate,true);double? gap=null;
                for(int t=0;t<36000;t++)
                {if(t>0)AuditGuardTick(s,powered);int phase=t%634;
                    if(phase<=115&&phase%5==0)DurationDebit(s,15*attackers,new[]{s});
                    if(!s.HasGuardCapacity){gap=t/60d;break;}}
                native.Add(new{Powered=powered,Attackers=attackers,FirstGapSeconds=gap});
            }
        }finally{Current.Game=previous;}
        var kinetic=new List<object>();
        foreach(var pair in loadouts.Where(p=>p.Value.Length==1&&p.Value[0].Family!="Laser"))
        foreach(float strength in new[]{0f,10f,50f,100f})
        {
            var r=pair.Value[0];float m=r.Heads*(r.Material=="Uranium"?1.5f:1);
            // Historical V3 response; retained only to interpret its archived snapshot.
            float signedEnergy=-(float)SpaceKinetics.Energy(m,(float)r.Speed)+28800*strength/100;
            var velocity=new Vector2(Math.Sign(signedEnergy)*SpaceKinetics.Speed(m,Math.Abs(signedEnergy)),0);
            // Collinear analytical bound, not a claim that random fragments all hit.
            float fragmentBudget=SpaceImpactEffects.ComputeDamage(new ImpactOutcomePart{damage=12,kineticMassScaling=true},velocity.magnitude,0,1,m);
            kinetic.Add(new{Name=pair.Key,Strength=strength,NormalSpeed=velocity.x,TotalFragmentBudget=fragmentBudget,RatioToIntact=fragmentBudget/r.Damage,PulseWd=2+(28800*strength/100)/1800});
        }
        var export=loadouts.Where(p=>p.Value.Length==1).Select(p=>new{Name=p.Key,Row=p.Value[0],Shots=p.Value[0].Cycle.Shots});
        var reference=loadouts.Where(p=>p.Value.Length==1).Select(p=>{
            var r=p.Value[0];var old=current.First(x=>x.Weapon==r.Weapon&&x.Material==r.Material&&x.Charge==r.Charge&&x.Heads==r.Heads&&x.Power==r.Power&&(r.Family=="Heavy"||x.Interval==r.Interval));
            return new{Name=p.Key,Row=old,Shots=old.Cycle.Shots};});
        var electromagnetic=new List<object>();
        foreach(int size in new[]{1,2})foreach(int power in new[]{1,2,3,4})
        foreach(int angle in new[]{0,30,60})foreach(int strength in new[]{0,50,100})
        {
            float radians=angle*Mathf.Deg2Rad;
            var plan=SpaceShieldRefraction.Calculate(new Vector2(-Mathf.Cos(radians),Mathf.Sin(radians)),Vector2.right,strength);
            var dir=plan.Direction(1);
            electromagnetic.Add(new{Size=size==1?"Small":"Medium",Power=power,Incidence=angle,Strength=strength,
                AddedDegrees=plan.addedDeg,OutgoingX=dir.x,OutgoingY=dir.y,PulseWd=.2*size*power*plan.addedDeg/180,LaunchWd=size*power});
        }
        var admissible=proposed.Where(r=>r.Family=="Heavy"&&r.Heads<=r.Charge).GroupBy(r=>r.Size)
            .Select(g=>new{Size=g.Key,Configurations=g.Count(),MaxDps=g.Max(r=>r.SustainedDps)});
        File.WriteAllText(Path.Combine(repo,"Source/obj/WeaponTuningRegression/role-balance-audit.json"),JsonSerializer.Serialize(new{
            Assumptions="V3 candidate only; production damage, thermal and guard functions; full old tuning grid retained; infinite supply, normal incidence, stationary targets; first shield gap is NOT combat duration; no artificial ceasefire; collinear kinetic comparison is an upper bound without fragment spread or return hit probability.",
            Candidate=candidate,Rows=proposed,Loadouts=export,CurrentLoadouts=reference,Results=results,Native=native,Kinetic=kinetic,
            Electromagnetic=electromagnetic,OptionalChargeAtLeastHeads=admissible},json));
        Console.WriteLine($"ROLE AUDIT: {proposed.Count} configurations; {results.Count} guard scenarios");
    }
}

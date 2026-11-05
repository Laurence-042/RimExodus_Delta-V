using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    sealed class EnergyShot
    {
        public string Name,Family;public double Mass,Speed,Damage,Drain,EnergyCost,Range;
        public int Cycle;public int[] Shots;public bool Rail=>Family=="Rail";public bool Laser=>Family=="Laser";
    }
    sealed class EnergyHullResult
    {
        public string Name,Defense,Pattern,Cadence;public int Guns,Lanes,Layers,ArmorHp,Strength,GuardCount;
        public double InitialPulseReserve,PulseSpent,ReserveLeft,Distance,ReflectedPotential,AttackerPulseEnergy;
        public int Launched,Expired,Arrivals,Reflected,Broken,PassedIntact,DevicesLost,ArmorDestroyed,GuardRejoins;
        public double? FirstLineFailure,FirstDeviceDamage,FirstDeviceKill,HalfDevicesLost;
        public double DeviceDamage,ArmorDamage;
    }
    static ThingDef energyIntact,energyStopped;
    static void RunEnergyImpulsePenetrationAudit()
    {
        Rand.PushState(170519);
        try { RunEnergyImpulsePenetrationCore(); }
        finally { Rand.PopState(); }
    }
    static void RunEnergyImpulsePenetrationCore()
    {
        var json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
        using var source=JsonDocument.Parse(File.ReadAllText("Source/obj/WeaponTuningRegression/energy-impulse-audit.json"));
        energyIntact=MakeDef("EnergyAuditIntact");energyStopped=MakeDef("EnergyAuditStopped");
        foreach(var pair in new[]{(energyIntact,true),(energyStopped,false)})
        {
            pair.Item1.comps.Add(new CompProperties_SpaceProjectile{speed=60,hasMap=false});
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef{defName=pair.Item1.defName,
                projectileDefs=new List<ThingDef>{pair.Item1},defenderEffect=new ImpactOutcomePart{
                    kind=ImpactEffectKind.DamageMap,damageDef=bullet,damage=24,kineticMassScaling=true,
                    penetrates=pair.Item2,incidenceMinFactor=1},attackerEffect=new ImpactOutcomePart{kind=ImpactEffectKind.DestroySelf}});
        }
        SpaceImpactEffects.ResetCache();
        var singles=new Dictionary<string,EnergyShot>();
        foreach(var e in source.RootElement.GetProperty("Loadouts").EnumerateArray())
        {
            var r=e.GetProperty("Row");string family=r.GetProperty("Family").GetString();
            var gun=new EnergyShot{Name=e.GetProperty("Name").GetString(),Family=family,
                Mass=r.GetProperty("Heads").GetInt32()*(r.GetProperty("Material").GetString()=="Uranium"?1.5:1),
                Damage=r.GetProperty("Damage").GetDouble(),Drain=r.GetProperty("ShieldDamage").GetDouble(),
                Speed=r.GetProperty("Speed").GetDouble(),Range=r.GetProperty("LifeTravel").GetDouble(),
                Cycle=r.GetProperty("CycleTicks").GetInt32(),Shots=e.GetProperty("Shots").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
                EnergyCost=r.GetProperty("EnergyPerShot").GetDouble()};
            singles[gun.Name]=gun;
        }
        foreach(var r in source.RootElement.GetProperty("Rails").EnumerateArray())
        {
            var gun=new EnergyShot{Name=r.GetProperty("Name").GetString(),Family="Rail",Mass=r.GetProperty("Mass").GetDouble(),
                Damage=r.GetProperty("Damage").GetDouble(),Drain=r.GetProperty("Damage").GetDouble(),
                Speed=r.GetProperty("Speed").GetDouble(),Cycle=r.GetProperty("CycleTicks").GetInt32(),Shots=new[]{0},EnergyCost=r.GetProperty("GridPulse").GetDouble()};
            gun.Range=gun.Speed*10;singles[gun.Name]=gun;
        }
        var depth=new List<object>();
        foreach(var gun in singles.Values)foreach(int strength in new[]{0,10,50,100})
        {
            Reset();var walls=Enumerable.Range(0,3).Select(i=>Add(10+3*i,5,840)).ToArray();var device=Add(55,5,300);
            var p=gun.Laser?null:EnergyImpulseModel.Apply(gun.Mass,-gun.Speed,0,gun.Rail,strength);
            if(p==null||!p.Reflected)
            {
                int count=p?.Fragments??1;double damage=p?.Damage??gun.Damage;
                for(int i=0;i<count;i++)Fire((float)(damage/count),60,p?.Broken==true||gun.Laser?energyStopped:energyIntact);
            }
            depth.Add(new{gun.Name,Strength=strength,Response=p,DestroyedLayers=walls.Count(x=>x.Destroyed),DeviceDamage=300-Math.Max(0,device.HitPoints)});
        }
        var names=new[]{"Small_Heavy_24_24_Steel","Small_Rapid_6","Small_Laser_3",
            "Medium_Heavy_1_96_Steel","Medium_Heavy_8_96_Steel","Medium_Heavy_48_8_Steel","Medium_Heavy_48_48_Steel",
            "Medium_Heavy_48_96_Steel","Medium_Heavy_48_96_Uranium","Medium_Heavy_96_96_Steel",
            "Medium_Rapid_4","Medium_Rapid_6","Medium_Rapid_10","Medium_Rapid_30",
            "Medium_Laser_1","Medium_Laser_3","Medium_Laser_4","RailSmall_2_Steel","RailLarge_1_Steel"};
        var loadouts=names.ToDictionary(n=>n,n=>new[]{singles[n]});
        loadouts["Medium_Mixed"]=new[]{singles["Medium_Heavy_48_48_Steel"],singles["Medium_Heavy_48_48_Steel"],singles["Medium_Rapid_6"],singles["Medium_Laser_3"]};
        loadouts["Medium_HeavyMixed"]=new[]{singles["Medium_Heavy_48_96_Steel"],singles["Medium_Heavy_48_96_Steel"],singles["Medium_Rapid_6"],singles["Medium_Laser_3"]};
        loadouts["Small_Mixed"]=new[]{singles["Small_Heavy_24_24_Steel"],singles["Small_Heavy_24_24_Steel"],singles["Small_Rapid_6"],singles["Small_Laser_3"]};
        var results=new List<EnergyHullResult>();
        foreach(var pair in loadouts)foreach(int guns in new[]{1,4,8})
        foreach(var defense in new[]{("GG",0),("KG",10),("KG",50),("KG",100),("EG",100)})
        foreach(string pattern in new[]{"Focus","Sweep8"})
        {
            if(pair.Value.Length>1&&guns%4!=0)continue;
            var battery=Enumerable.Range(0,guns).Select(i=>pair.Value[i%pair.Value.Length]).ToArray();
            results.Add(EnergyHullRun(pair.Key,battery,defense.Item1,defense.Item2,8400,8,3,840,pattern,false,100));
        }
        foreach(string name in new[]{"Small_Mixed","Medium_Mixed","Medium_HeavyMixed"})
        foreach(int strength in new[]{10,50,100})foreach(double bank in new[]{12000d,1000000000})
            results.Add(EnergyHullRun(name,loadouts[name],"KG",strength,bank,8,3,840,"Sweep8",false,100));
        foreach(string name in new[]{"Medium_Mixed","Medium_HeavyMixed"})foreach(string defense in new[]{"GG","KG","EG"})
        {
            results.Add(EnergyHullRun(name,loadouts[name],defense,100,8400,8,3,840,"Sweep8",true,100));
            foreach(int layers in new[]{0,1})results.Add(EnergyHullRun(name,loadouts[name],defense,100,8400,8,layers,300,"Sweep8",false,100));
        }
        File.WriteAllText("Source/obj/WeaponTuningRegression/energy-impulse-penetration.json",JsonSerializer.Serialize(new{
            Assumptions="PROPOSED energy/impulse/fragmentation in shared offline model; production penetration, guard state and native building damage. Eight independent armor/device lanes, NOT a complete two-sided ship battle. Defenses always two devices: GG=2 guards; KG=1 kinetic+1 guard; EG=1 EM+1 guard. Pulse reserve only, no grid routing/generation/idle/recovery draw simulated. Weapon supply unlimited; energy spent recorded, not a firing gate. Constant 100-cell flight delay and projectile lifetime, no movement/aiming/return hits/fragment scatter/structural breakup/repair. Reflected packets leave target; recorded return budget is potential, not actual friendly damage. Fragments are collinear, a concentrated-hit stress case. First line failure is first underfunded interception, not shield HP depletion. Half-device destruction is not ship defeat. 1e9 pulse reserve is a practically-unlimited diagnostic.",
            Depth=depth,Results=results,RailEnergyTransactions=EnergyStageTransactions()},json));
        Console.WriteLine($"ENERGY PENETRATION: {depth.Count} burst cases; {results.Count} battery scenarios");
    }

    static EnergyHullResult EnergyHullRun(string name,EnergyShot[] guns,string defense,int strength,double reserve,int lanes,int layers,int hp,string pattern,bool stagger,double distance)
    {
        Rand.PushState(170519);
        try { return EnergyHullRunCore(name,guns,defense,strength,reserve,lanes,layers,hp,pattern,stagger,distance); }
        finally { Rand.PopState(); }
    }
    static EnergyHullResult EnergyHullRunCore(string name,EnergyShot[] guns,string defense,int strength,double reserve,int lanes,int layers,int hp,string pattern,bool stagger,double distance)
    {
        Reset(lanes+4);int guardCount=defense=="GG"?2:1;
        var result=new EnergyHullResult{Name=name,Defense=defense,Pattern=pattern,Cadence=stagger?"Staggered":"Synchronized",
            Guns=guns.Length,Lanes=lanes,Layers=layers,ArmorHp=hp,Strength=strength,GuardCount=guardCount,InitialPulseReserve=reserve,Distance=distance};
        var walls=new List<Target>();var devices=new List<Target>();
        for(int lane=0;lane<lanes;lane++){for(int l=0;l<layers;l++)walls.Add(Add(10+3*l,2+lane,hp));devices.Add(Add(55,2+lane,300));}
        var shields=new List<CompSpaceShield>();
        for(int i=0;i<guardCount;i++)
        {
            var s=new CompSpaceShield{props=new CompProperties_SpaceShield{regularCapacity=750,emergencyCapacity=750,recoveryDelayTicks=180,guardRecoveryTicks=7500,recoveryPowerWatts=1000}};
            var fuel=new CompRefuelable();fuel.Initialize(new CompProperties_Refuelable{fuelCapacity=750,initialFuelPercent=1,consumeFuelOnlyWhenUsed=true});
            Add(75,i,500,fuel,s);s.PostPostMake();s.AutoRejoinPercent=10;shields.Add(s);
        }
        var phase=new int[guns.Length];var index=new int[guns.Length];var events=new SortedDictionary<int,Queue<(int Gun,int Shot,int Lane)>>();
        for(int i=0;i<guns.Length;i++)phase[i]=stagger?guns[i].Cycle*Enumerable.Range(0,i).Count(k=>guns[k].Name==guns[i].Name)/guns.Count(x=>x.Name==guns[i].Name):0;
        int disabledUntil=0,disableTicks=new CompProperties_SpaceShield().pulseFailureTicks;
        void DamagePacket(EnergyShot gun,double damage,int lane,bool stopped)
        {
            double scale=1,drain=gun.Laser?gun.Drain:damage*(gun.Rail?1:1d/8);
            foreach(var shield in shields)
            {
                if(!shield.HasGuardCapacity||scale<=0)continue;
                double input=drain*scale,paid=RoleDebit(shield,input,shields);
                scale=gun.Laser?0:scale*RoleRemaining((float)input,(float)paid);
            }
            if(scale>1e-7){contacts.Clear();Fire((float)(damage*scale),60,stopped?energyStopped:energyIntact,lane:2+lane+.5f);}
        }
        for(int t=0;t<=36000;t++)
        {
            if(t>0)foreach(var s in shields){bool active=s.UsesRegular;RoleGuardTick(s,true);if(!active&&s.UsesRegular)result.GuardRejoins++;}
            for(int g=0;g<guns.Length;g++)
            {
                var gun=guns[g];int n=index[g],next=phase[g]+n/gun.Shots.Length*gun.Cycle+gun.Shots[n%gun.Shots.Length];
                if(t!=next)continue;
                index[g]++;result.Launched++;result.AttackerPulseEnergy+=gun.EnergyCost;
                if(!gun.Laser&&distance>gun.Range){result.Expired++;continue;}
                int lane=pattern=="Focus"?devices.FindIndex(x=>!x.Destroyed):(n*3+g)%lanes;
                if(lane>=0)
                {
                    int due=t+(gun.Laser?0:(int)Math.Ceiling(distance/gun.Speed*60));
                    if(!events.TryGetValue(due,out var queue))events[due]=queue=new Queue<(int,int,int)>();
                    queue.Enqueue((g,n,lane));
                }
            }
            while(events.Count>0&&events.First().Key<=t)
            {
                var pending=events.First();var hit=pending.Value.Dequeue();if(pending.Value.Count==0)events.Remove(pending.Key);
                var gun=guns[hit.Gun];result.Arrivals++;
                if(defense=="KG"&&!gun.Laser&&t>=disabledUntil)
                {
                    var p=EnergyImpulseModel.Apply(gun.Mass,-gun.Speed,0,gun.Rail,strength,reserve);
                    reserve=Math.Max(0,reserve-p.Cost);result.PulseSpent+=p.Cost;
                    if(p.Underfunded){result.FirstLineFailure??=t/60d;disabledUntil=t+disableTicks;}
                    if(p.Broken)result.Broken++;else result.PassedIntact++;
                    if(p.Reflected){result.Reflected++;result.ReflectedPotential+=p.Damage;continue;}
                    for(int f=0;f<p.Fragments;f++)DamagePacket(gun,p.Damage/p.Fragments,hit.Lane,p.Broken);
                }
                else if(defense=="EG"&&gun.Laser&&t>=disabledUntil)
                {
                    double cost=gun.EnergyCost*.2,paid=Math.Min(cost,reserve);reserve-=paid;result.PulseSpent+=paid;
                    if(paid+1e-8>=cost){result.Reflected++;result.ReflectedPotential+=gun.Damage;continue;}
                    result.FirstLineFailure??=t/60d;disabledUntil=t+disableTicks;
                    // One partially funded optical event is conservatively counted
                    // as hitting the target; actual deflected geometry is not modelled.
                    DamagePacket(gun,gun.Damage,hit.Lane,true);
                }
                else DamagePacket(gun,gun.Damage,hit.Lane,gun.Laser);
            }
            int lost=devices.Count(x=>x.Destroyed);double damage=devices.Sum(x=>300-Math.Max(0,x.HitPoints));
            if(damage>0)result.FirstDeviceDamage??=t/60d;if(lost>0)result.FirstDeviceKill??=t/60d;
            if(lost>=(lanes+1)/2){result.HalfDevicesLost=t/60d;break;}
        }
        result.DevicesLost=devices.Count(x=>x.Destroyed);result.DeviceDamage=devices.Sum(x=>300-Math.Max(0,x.HitPoints));
        result.ArmorDestroyed=walls.Count(x=>x.Destroyed);result.ArmorDamage=walls.Sum(x=>hp-Math.Max(0,x.HitPoints));result.ReserveLeft=reserve;
        if(Math.Abs(result.InitialPulseReserve-result.PulseSpent-reserve)>.002)throw new Exception("Pulse conservation failed");
        return result;
    }

    static List<object> EnergyStageTransactions()
    {
        var results=new List<object>();
        foreach(bool large in new[]{false,true})foreach(int stages in new[]{1,2,3})
        foreach(int bank in new[]{8400,12000})foreach(string loadout in new[]{"Bare","TwoCapacitorsPerStage","TwoRecoveryPerStage"})
        {
            Reset();float factor=large?2:1,pulse=1800*factor;
            var battery=new CompPowerBattery();Set(battery,"storedEnergy",bank*factor);
            var net=(PowerNet)Raw(typeof(PowerNet));net.batteryComps=new List<CompPowerBattery>{battery};
            CompPowerTrader Power(){var p=new CompPowerTrader{transNet=net};Set(p,"powerOnInt",true);return p;}
            var gun=new CompSpaceWeapon{props=new CompProperties_SpaceWeapon{projectileDef=rail,shotEnergy=pulse}};Add(5,5,500,Power(),gun);
            var reserves=new List<CompSwitchableBattery>();
            for(int s=0;s<stages;s++)
            {
                var stage=new CompSpaceRailgun{props=new CompProperties_SpaceRailgun{shotEnergy=pulse,kineticEnergyBonus=216000}};
                Add(6+s,5,500,Power(),stage);gun.Layout.acceleration.segments.Add(stage);
                var attachments=new List<ThingWithComps>();gun.Layout.segmentAttachments.Add(stage,attachments);
                for(int i=0;i<2;i++)
                {
                    if(loadout=="TwoRecoveryPerStage")
                    {var recovery=new CompRailgunEnergyRecovery{props=new CompProperties_RailgunEnergyRecovery()};Set(recovery,"enabled",true);attachments.Add(Add(12+s*2+i,5,100,recovery));}
                    if(loadout=="TwoCapacitorsPerStage")
                    {
                        var reserve=new CompSwitchableBattery{props=new CompProperties_SwitchableBattery{capacityWattDays=pulse}};Set(reserve,"storedEnergy",pulse);reserves.Add(reserve);
                        var cap=new CompSpaceWeaponCapacitor{props=new CompProperties_SpaceWeaponCapacitor()};Set(cap,"enabled",true);attachments.Add(Add(12+s*2+i,5,100,reserve,cap));
                    }
                }
            }
            var energy=new SpaceWeaponEnergy();var shots=new List<object>();
            for(int n=1;n<=12;n++)
            {
                float before=battery.StoredEnergy;if(!energy.Evaluate(gun))break;
                int paid=energy.active.Count;energy.Commit();
                shots.Add(new{Number=n,PaidStages=paid,Full=paid==stages,GridSpent=before-battery.StoredEnergy,
                    GridRemaining=battery.StoredEnergy,CapRemaining=reserves.Sum(x=>x.StoredEnergy),
                    Damage=EnergyImpulseModel.Budget(EnergyImpulseModel.RailEnergy(large,paid),true)});
            }
            results.Add(new{Large=large,Stages=stages,Bank=bank*factor,Loadout=loadout,Shots=shots});
        }
        return results;
    }
}

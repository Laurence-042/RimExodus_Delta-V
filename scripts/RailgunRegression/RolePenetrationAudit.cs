using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    sealed class RoleShot
    {
        public string Name,Family; public double Damage,Drain,Speed; public int Cycle;
        public int[] Shots; public ThingDef Projectile;
    }
    sealed class RoleHullResult
    {
        public string Name,Pattern,Cadence; public int Guns,Shields,Lanes,Layers,ArmorHp,Shots;
        public double? FirstDeviceDamage,FirstDeviceKill,HalfDevicesLost;
        public int DevicesLost,ArmorDestroyed; public double DeviceDamage,ArmorDamage;
    }
    static readonly Action<CompSpaceShield,bool> RoleGuardTick=typeof(CompSpaceShield).GetMethod("AdvanceGuardTick",BindingFlags.Instance|BindingFlags.NonPublic)
        .CreateDelegate<Action<CompSpaceShield,bool>>();
    static readonly Func<CompSpaceShield,double,IReadOnlyList<CompSpaceShield>,double> RoleDebit=typeof(CompSpaceShield)
        .GetMethod("ConsumeGuard",BindingFlags.Instance|BindingFlags.NonPublic)
        .CreateDelegate<Func<CompSpaceShield,double,IReadOnlyList<CompSpaceShield>,double>>();
    static readonly Func<float,float,float> RoleRemaining=typeof(SpaceImpactEffects)
        .GetMethod("RemainingFraction",BindingFlags.Static|BindingFlags.NonPublic).CreateDelegate<Func<float,float,float>>();

    static void RunRolePenetrationAudit()
    {
        var json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
        using var source=JsonDocument.Parse(File.ReadAllText("Source/obj/WeaponTuningRegression/role-balance-audit.json"));
        // Isolated projectile definitions only; the production SweepAndResolve and
        // native building damage worker retain their real penetration / rounding.
        var intact=MakeDef("RoleAuditIntact");var beam=MakeDef("RoleAuditBeam");
        foreach(var pair in new[]{(intact,true),(beam,false)})
        {
            pair.Item1.comps.Add(new CompProperties_SpaceProjectile{speed=60,hasMap=false});
            DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef{
                defName=pair.Item1.defName,projectileDefs=new List<ThingDef>{pair.Item1},
                defenderEffect=new ImpactOutcomePart{kind=ImpactEffectKind.DamageMap,damageDef=bullet,
                    damage=24,kineticMassScaling=true,penetrates=pair.Item2,incidenceMinFactor=1},
                attackerEffect=new ImpactOutcomePart{kind=ImpactEffectKind.DestroySelf}});
        }
        SpaceImpactEffects.ResetCache();
        var singles=new Dictionary<string,RoleShot>();
        foreach(string collection in new[]{"Loadouts","CurrentLoadouts"})
        foreach(var e in source.RootElement.GetProperty(collection).EnumerateArray())
        {
            var r=e.GetProperty("Row"); string family=r.GetProperty("Family").GetString();
            var shot=new RoleShot{Name=(collection=="CurrentLoadouts"?"Current_":"")+e.GetProperty("Name").GetString(),Family=family,
                Damage=r.GetProperty("Damage").GetDouble(),Drain=r.GetProperty("ShieldDamage").GetDouble(),
                Speed=family=="Laser"?60:r.GetProperty("Speed").GetDouble(),Cycle=r.GetProperty("CycleTicks").GetInt32(),
                Shots=e.GetProperty("Shots").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
                Projectile=family=="Laser"?beam:intact};
            singles[shot.Name]=shot;
        }
        singles["RailSmall1"]=new RoleShot{Name="RailSmall1",Family="Rail",Damage=2400,Drain=2400,Speed=300,Cycle=1800,Shots=new[]{0},Projectile=intact};
        var depth=new List<object>();
        foreach(var shot in singles.Values.Where(x=>!x.Name.StartsWith("Current_")))
        foreach(int hp in new[]{300,840})
        {
            Reset();var layers=Enumerable.Range(0,6).Select(i=>Add(10+i*3,5,hp)).ToArray();var device=Add(55,5,300);
            Fire((float)shot.Damage,(float)shot.Speed,shot.Projectile);
            depth.Add(new{shot.Name,ArmorHp=hp,DestroyedLayers=layers.Count(x=>x.Destroyed),
                ArmorDamage=layers.Sum(x=>hp-Math.Max(0,x.HitPoints)),DeviceDamage=300-Math.Max(0,device.HitPoints)});
        }
        var chosen=new[]{"Small_Heavy_24_24_Steel","Small_Rapid_6","Small_Laser_3",
            "Medium_Heavy_48_48_Steel","Medium_Heavy_96_96_Steel","Medium_Heavy_1_96_Steel",
            "Medium_Heavy_8_96_Steel","Medium_Rapid_4","Medium_Rapid_6","Medium_Rapid_15","Medium_Rapid_30",
            "Medium_Laser_1","Medium_Laser_2","Medium_Laser_3","Medium_Laser_4"};
        var loadouts=chosen.ToDictionary(n=>n,n=>new[]{singles[n]});
        loadouts["Medium_Mixed"]=new[]{singles["Medium_Heavy_48_48_Steel"],singles["Medium_Heavy_48_48_Steel"],singles["Medium_Rapid_6"],singles["Medium_Laser_3"]};
        var results=new List<RoleHullResult>();
        foreach(var pair in loadouts)
        foreach(int guns in new[]{1,2,4,8})
        foreach(var armor in new[]{(0,300),(1,300),(3,840)})
        foreach(string pattern in new[]{"Focus","Sweep8"})
        {
            if(pair.Value.Length>1&&guns%4!=0)continue;
            var battery=Enumerable.Range(0,guns).Select(i=>pair.Value[i%pair.Value.Length]).ToArray();
            results.Add(RoleHullRun(pair.Key,battery,2,8,armor.Item1,armor.Item2,pattern,false));
        }
        foreach(string name in new[]{"Medium_Mixed","Medium_Rapid_6","Medium_Heavy_48_48_Steel","Medium_Laser_3"})
        {
            var battery=Enumerable.Range(0,4).Select(i=>loadouts[name][i%loadouts[name].Length]).ToArray();
            results.Add(RoleHullRun(name,battery,2,8,3,840,"Sweep8",true));
            results.Add(RoleHullRun(name,battery,0,8,3,840,"Sweep8",false));
            results.Add(RoleHullRun(name,battery,0,8,3,840,"Focus",false));
        }
        foreach(int lanes in new[]{16,24,32})
            results.Add(RoleHullRun("Medium_Mixed",loadouts["Medium_Mixed"],2,lanes,3,840,$"Sweep{lanes}",false));
        var oldMixed=new[]{singles["Current_Medium_Heavy_48_48_Steel"],singles["Current_Medium_Heavy_48_48_Steel"],singles["Current_Medium_Rapid_6"],singles["Current_Medium_Laser_3"]};
        foreach(string pattern in new[]{"Focus","Sweep8"})
            results.Add(RoleHullRun("Current_Medium_Mixed",oldMixed,2,8,3,840,pattern,false));
        File.WriteAllText("Source/obj/WeaponTuningRegression/role-penetration-audit.json",JsonSerializer.Serialize(new{
            Assumptions="Production 1.6 collision + native building damage; production guard state/debit with explicitly supplied same-ship membership; firing schedules from production 1.7 thermal audit. Numeric ray targets, NOT complete ships or a combat AI simulation. Eight independent lanes, each 0/1/3 layers and 300 HP device; first hit t=0, no flight delay; full supply; no ammunition gates, no target retaliation, repairs, device cover, power loss or foundation. Both guard surfaces cover every lane in sequence; fixed 2 guards for all gun counts. Focus targets next living device lane; Sweep8 cycles all lanes including open holes, representing spatial dispersion only, not actual maneuver. Half devices lost is a benchmark endpoint, NOT ship defeat.",
            Depth=depth,Results=results,Energy=RoleEnergyAudit()},json));
        Console.WriteLine($"ROLE PENETRATION: {depth.Count} single-shot and {results.Count} battery scenarios");
    }

    static RoleHullResult RoleHullRun(string name,RoleShot[] guns,int shieldCount,int lanes,int layers,int hp,string pattern,bool stagger)
    {
        Reset(lanes+4);var result=new RoleHullResult{Name=name,Guns=guns.Length,Shields=shieldCount,Lanes=lanes,Layers=layers,ArmorHp=hp,Pattern=pattern,Cadence=stagger?"Staggered":"Synchronized"};
        var armor=new List<Target>();var devices=new List<Target>();
        for(int lane=0;lane<lanes;lane++)
        {for(int l=0;l<layers;l++)armor.Add(Add(10+l*3,2+lane,hp));devices.Add(Add(55,2+lane,300));}
        var shields=new List<CompSpaceShield>();
        for(int i=0;i<shieldCount;i++)
        {
            bool current=name.StartsWith("Current_");
            var s=new CompSpaceShield{props=new CompProperties_SpaceShield{regularCapacity=current?1000:600,emergencyCapacity=current?2000:300,recoveryDelayTicks=current?600:180,guardRecoveryTicks=current?2500:6000,recoveryPowerWatts=1000}};
            var fuel=new CompRefuelable();fuel.Initialize(new CompProperties_Refuelable{fuelCapacity=current?2000:300,initialFuelPercent=1,consumeFuelOnlyWhenUsed=true});
            // Outside firing rays; surfaces are supplied explicitly above, not fake
            // geometry or ownership inferred by a parallel collision implementation.
            Add(75,i,500,fuel,s);s.PostPostMake();s.AutoRejoinPercent=current?100:10;shields.Add(s);
        }
        var phase=new int[guns.Length];var index=new int[guns.Length];
        for(int i=0;i<guns.Length;i++)phase[i]=stagger?guns[i].Cycle*Enumerable.Range(0,i).Count(k=>guns[k].Name==guns[i].Name)/guns.Count(x=>x.Name==guns[i].Name):0;
        for(int t=0;t<=36000;t++)
        {
            if(t>0)foreach(var s in shields)RoleGuardTick(s,true);
            for(int g=0;g<guns.Length;g++)
            {
                var gun=guns[g];int n=index[g];int next=phase[g]+n/gun.Shots.Length*gun.Cycle+gun.Shots[n%gun.Shots.Length];
                if(next!=t)continue;
                index[g]++;result.Shots++;
                int lane=pattern=="Focus"?devices.FindIndex(x=>!x.Destroyed):(n*3+g)%lanes;
                if(lane<0)continue;
                double scale=1;
                foreach(var s in shields)
                {
                    if(!s.HasGuardCapacity||scale<=0)continue;
                    double incoming=gun.Drain*scale,paid=RoleDebit(s,incoming,shields);
                    scale=gun.Family=="Laser"?0:scale*RoleRemaining((float)incoming,(float)paid);
                }
                if(scale>1e-7)
                {contacts.Clear();Fire((float)(gun.Damage*scale),(float)gun.Speed,gun.Projectile,lane:2+lane+.5f);}
            }
            int lost=devices.Count(x=>x.Destroyed);double damage=devices.Sum(x=>300-Math.Max(0,x.HitPoints));
            if(damage>0&&!result.FirstDeviceDamage.HasValue)result.FirstDeviceDamage=t/60d;
            if(lost>0&&!result.FirstDeviceKill.HasValue)result.FirstDeviceKill=t/60d;
            if(lost>=(lanes+1)/2)
            {result.HalfDevicesLost=t/60d;break;}
        }
        result.DevicesLost=devices.Count(x=>x.Destroyed);result.DeviceDamage=devices.Sum(x=>300-Math.Max(0,x.HitPoints));
        result.ArmorDestroyed=armor.Count(x=>x.Destroyed);result.ArmorDamage=armor.Sum(x=>hp-Math.Max(0,x.HitPoints));
        return result;
    }

    static List<object> RoleEnergyAudit()
    {
        var results=new List<object>();
        foreach(bool large in new[]{false,true})foreach(int bank in new[]{8400,12000})
        foreach(string loadout in new[]{"Bare","TwoCapacitors","TwoRecovery"})
        {
            Reset();float factor=large?2:1,pulse=1800*factor;
            var battery=new CompPowerBattery();Set(battery,"storedEnergy",bank*factor);
            var net=(PowerNet)Raw(typeof(PowerNet));net.batteryComps=new List<CompPowerBattery>{battery};
            CompPowerTrader Power(){var p=new CompPowerTrader{transNet=net};Set(p,"powerOnInt",true);return p;}
            var gun=new CompSpaceWeapon{props=new CompProperties_SpaceWeapon{projectileDef=rail,shotEnergy=pulse}};
            Add(5,5,500,Power(),gun);
            var segment=new CompSpaceRailgun{props=new CompProperties_SpaceRailgun{shotEnergy=pulse,kineticEnergyBonus=216000}};
            Add(6,5,500,Power(),segment);gun.Layout.acceleration.segments.Add(segment);
            var attachments=new List<ThingWithComps>();gun.Layout.segmentAttachments.Add(segment,attachments);
            var reserves=new List<CompSwitchableBattery>();
            for(int i=0;i<2;i++)
            {
                if(loadout=="TwoRecovery")
                {
                    var recovery=new CompRailgunEnergyRecovery{props=new CompProperties_RailgunEnergyRecovery()};Set(recovery,"enabled",true);
                    attachments.Add(Add(10+i,5,100,recovery));
                }
                else if(loadout=="TwoCapacitors")
                {
                    var reserve=new CompSwitchableBattery{props=new CompProperties_SwitchableBattery{capacityWattDays=pulse}};
                    Set(reserve,"storedEnergy",pulse);reserves.Add(reserve);
                    var cap=new CompSpaceWeaponCapacitor{props=new CompProperties_SpaceWeaponCapacitor()};Set(cap,"enabled",true);
                    attachments.Add(Add(10+i,5,100,reserve,cap));
                }
            }
            var energy=new SpaceWeaponEnergy();var shots=new List<object>();
            for(int n=1;n<=10;n++)
            {
                float before=battery.StoredEnergy;
                if(!energy.Evaluate(gun))break;
                int active=energy.active.Count;energy.Commit();
                shots.Add(new{Number=n,Accelerated=active==1,GridSpent=before-battery.StoredEnergy,GridRemaining=battery.StoredEnergy,CapRemaining=reserves.Sum(x=>x.StoredEnergy)});
            }
            results.Add(new{Size=large?"Large":"Small",Bank=bank*factor,Loadout=loadout,Shots=shots});
        }
        return results;
    }
}

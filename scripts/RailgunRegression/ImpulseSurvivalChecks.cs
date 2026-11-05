using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckImpulseSurvival()
    {
        foreach (var test in new[] { (96f,100,100f,3), (48f,10,100f,3), (20f,0,100f,3),
            (20f,100,1f,3), (1f,100,100f,1) })
        {
            Reset();
            var projectile = MakeDef("ImpulseSurvivor" + nextId++);
            projectile.comps.Add(new CompProperties_SpaceProjectile { speed=60, hasMap=false,
                shieldFragmentCount=test.Item4, shieldFragmentDef=fragment });
            var battery=new CompPowerBattery(); Set(battery,"storedEnergy",test.Item3);
            var net=(PowerNet)Raw(typeof(PowerNet)); net.batteryComps=new List<CompPowerBattery>{battery};
            var power=new CompPowerTrader{transNet=net}; Set(power,"powerOnInt",true);
            var shield=new CompSpaceShield{props=new CompProperties_SpaceShield(),mode=SpaceShieldMode.Kinetic,
                kineticStrengthPercent=test.Item2,projection=10,length=20};
            var interceptor=(CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
            interceptor.props=new CompProperties_ProjectileInterceptor{radius=12};
            Add(40,8,500,interceptor,shield,power).Rotation=Rot4.West;
            var service=new SpaceProjectileService(universe);
            var start=SpaceTransform.MapPointToUniverse(hull,map,new Vector2(5,5.5f));
            Vector2 tangent=SpaceTransform.MapDirectionToUniverseTangent(hull,map,new Vector2(5,5.5f),Vector2.right);
            OrbitSphere.Basis(start,out var east,out var north);
            Check(service.Launch(projectile,null,start,east*tangent.x+north*tangent.y,null,null,default,
                new SpaceProjectileLaunchSpec(60,0,60,.7f,1,test.Item1)),"launch surviving round through production service");
            var records=(IList)AccessTools.Field(typeof(SpaceProjectileService),"projectiles").GetValue(service);
            object record=records[0];
            var container=(SpaceObject)AccessTools.Property(record.GetType(),"container").GetValue(record);
            object spawned=AccessTools.Field(record.GetType(),"spawnedAtTick").GetValue(record);
            container.pose.position=SpaceTransform.MapPointToUniverse(hull,map,new Vector2(40,5.5f));
            service.ResolveCollisions();
            var expected=EnergyImpulseModel.Apply(test.Item1,-60,0,false,test.Item2,test.Item3);
            Check(records.Count==1&&ReferenceEquals(records[0],record)
                && Equals(spawned,AccessTools.Field(record.GetType(),"spawnedAtTick").GetValue(record)),
                "intact heavy round and existing fragment retain identity and lifetime");
            Check(Math.Abs(container.pose.velocity.magnitude-Math.Abs(expected.Normal))<.01f
                && Math.Abs(battery.StoredEnergy-(test.Item3-expected.Cost))<.001f,
                "service writes funded impulse speed back without restoring incoming speed");
            Check((float)AccessTools.Field(record.GetType(),"mass").GetValue(record)==test.Item1
                && Math.Abs((float)AccessTools.Field(record.GetType(),"damageScale").GetValue(record)-.7f)<.001f,
                "survival preserves physical mass and preexisting remaining impact fraction");
            Check((bool)AccessTools.Field(record.GetType(),"reflectedByShield").GetValue(record)==expected.Reflected,
                "only reflected survivors lose the launcher's friendly exclusion");
            Set(record,"prevPosition",container.pose.position);
            container.pose.position=OrbitSphere.Move(container.pose.position,container.pose.velocity/60f);
            service.ResolveCollisions();
            Check(records.Count==1&&ReferenceEquals(records[0],record)
                && Math.Abs(container.pose.velocity.magnitude-Math.Abs(expected.Normal))<.01f,
                "surviving impulse response persists across the next tick without another pulse");
        }
    }
    static void CheckLateralTransform()
    {
        foreach (var test in new[] { (1f,0f,100,0,false), (1f,5f,10,1,false), (1f,45f,10,3,false),
            (96f,45f,100,1,false), (96f,5f,100,0,false),
            (20f,0f,100,0,true), (20f,5f,10,1,true), (20f,30f,10,4,true) })
        {
            Reset();
            var tumble = MakeDef("Tumble"+nextId++);
            tumble.comps.Add(new CompProperties_SpaceProjectile { hasMap=false, shieldTumbling=true,
                shieldFragmentCount=test.Item5?4:3, shieldFragmentDef=fragment,
                shieldTumbleDeltaV=test.Item5?.15f:1.5f, shieldBreakDeltaV=test.Item5?.5f:18f, timeoutTicks=300 });
            var projectile = MakeDef("Lateral"+nextId++);
            projectile.comps.Add(new CompProperties_SpaceProjectile { speed=60, hasMap=false,
                shieldFragmentCount=test.Item5?4:3, shieldFragmentDef=fragment, shieldTumbleDef=tumble,
                shieldTumbleDeltaV=test.Item5?.15f:1.5f, shieldBreakDeltaV=test.Item5?.5f:18f, timeoutTicks=900 });
            var battery=new CompPowerBattery(); Set(battery,"storedEnergy",100f);
            var net=(PowerNet)Raw(typeof(PowerNet)); net.batteryComps=new List<CompPowerBattery>{battery};
            var power=new CompPowerTrader{transNet=net}; Set(power,"powerOnInt",true);
            var shield=new CompSpaceShield{props=new CompProperties_SpaceShield(),mode=SpaceShieldMode.Kinetic,
                kineticStrengthPercent=test.Item3,projection=10,length=32,angleDeg=test.Item2};
            var interceptor=(CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
            interceptor.props=new CompProperties_ProjectileInterceptor{radius=12};
            Add(40,8,500,interceptor,shield,power).Rotation=Rot4.West;
            var service=new SpaceProjectileService(universe);
            var start=SpaceTransform.MapPointToUniverse(hull,map,new Vector2(5,5.5f));
            var tangent=SpaceTransform.MapDirectionToUniverseTangent(hull,map,new Vector2(5,5.5f),Vector2.right);
            OrbitSphere.Basis(start,out var east,out var north);
            service.Launch(projectile,null,start,east*tangent.x+north*tangent.y,null,null,default,
                new SpaceProjectileLaunchSpec(60,0,60,.7f,1,test.Item1));
            var records=(IList)AccessTools.Field(typeof(SpaceProjectileService),"projectiles").GetValue(service);
            object original=records[0];
            Set(original,"spawnedAtTick",Find.TickManager.TicksGame-890);
            var container=(SpaceObject)AccessTools.Property(original.GetType(),"container").GetValue(original);
            container.pose.position=SpaceTransform.MapPointToUniverse(hull,map,new Vector2(40,5.5f));
            service.ResolveCollisions();
            var alive=records.Cast<object>().Where(r=>!(bool)AccessTools.Field(r.GetType(),"dead").GetValue(r)).ToList();
            Check(alive.Count==Math.Max(1,test.Item4), $"service lateral stages: mass={test.Item1}, angle={test.Item2}, strength={test.Item3}");
            Check(alive.All(r=>(ThingDef)AccessTools.Field(r.GetType(),"def").GetValue(r)
                ==(test.Item4==0?projectile:test.Item4==1?tumble:fragment)), "stage chooses intact, full-mass tumble or torn-fragment Def");
            Check(Math.Abs(alive.Sum(r=>(float)AccessTools.Field(r.GetType(),"mass").GetValue(r))-test.Item1)<.001f,
                "all transformation stages conserve total physical mass");
            foreach(var r in alive)
            {
                var props=(CompProperties_SpaceProjectile)AccessTools.Field(r.GetType(),"props").GetValue(r);
                int born=(int)AccessTools.Field(r.GetType(),"spawnedAtTick").GetValue(r);
                Check(props.timeoutTicks-(Find.TickManager.TicksGame-born)==10,
                    "tumbling and tearing cannot extend an almost expired projectile lifetime");
                Check(Math.Abs((float)AccessTools.Field(r.GetType(),"damageScale").GetValue(r)-.7f)<.001f,
                    "shape conversion does not duplicate or repeatedly reduce remaining damage scale");
            }
            if(test.Item4==1)
            {
                object rolling=alive[0];
                var rollingContainer=(SpaceObject)AccessTools.Property(rolling.GetType(),"container").GetValue(rolling);
                Set(rolling,"prevPosition",start);
                rollingContainer.pose.position=SpaceTransform.MapPointToUniverse(hull,map,new Vector2(40,5.5f));
                rollingContainer.pose.velocity=SpaceTransform.TransportTangent(tangent*60,start,rollingContainer.pose.position);
                shield.angleDeg=45; shield.kineticStrengthPercent=100;
                Set(shield,"disabledUntilTick",0); Set(battery,"storedEnergy",100f); SpaceCollision.ClearCaches();
                service.ResolveCollisions();
                alive=records.Cast<object>().Where(r=>!(bool)AccessTools.Field(r.GetType(),"dead").GetValue(r)).ToList();
                int count=test.Item1==96?1:test.Item5?4:3;
                Check(alive.Count==count && (count!=1 || ReferenceEquals(alive[0],rolling)),
                    "a second real shield encounter tears a light tumbling body but preserves a heavy tumbling body");
                Check(Math.Abs(alive.Sum(r=>(float)AccessTools.Field(r.GetType(),"mass").GetValue(r))-test.Item1)<.001f
                    && alive.All(r=>Math.Abs((float)AccessTools.Field(r.GetType(),"damageScale").GetValue(r)-.7f)<.001f),
                    "second encounter preserves full mass and remaining budget without repeated attenuation");
            }
        }
    }

}

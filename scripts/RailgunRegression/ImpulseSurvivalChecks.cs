using System;
using System.Collections;
using System.Collections.Generic;
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
}

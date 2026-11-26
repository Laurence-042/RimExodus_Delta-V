using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Only scene destruction bookkeeping is adapted; TakeDamage and DamageWorker stay live.
    sealed class CollisionEntity : Building
    {
        internal int destroys;
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        { if (!Destroyed) { destroys++; Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2); } }
        public override void Kill(DamageInfo? info = null, Hediff culprit = null) => Destroy(DestroyMode.KillFinalize);
    }
    static readonly List<(IntVec3 cell, Map map, float radius, Thing instigator, int amount, float ap, ThingDef projectile)> collisionExplosions = new();
    static bool collisionFailVisual;
    static void CollisionVisualBoundary()
    { if (collisionFailVisual) throw new InvalidOperationException("collision fixture visual endpoint"); }
    // Observe the native Unity scene endpoint, including production damage/attribution inputs.
    static bool CollisionExplosion(IntVec3 center, Map map, float radius, Thing instigator,
        int damAmount, float armorPenetration, ThingDef projectile)
    { collisionExplosions.Add((center,map,radius,instigator,damAmount,armorPenetration,projectile)); return false; }
    static object CollisionView(SpaceObject owner) => AccessTools.Method(
        typeof(SpaceCollision).GetNestedType("TargetView", System.Reflection.BindingFlags.NonPublic), "Of").Invoke(null, new object[] { owner });
    static bool CollisionValid(object view) => (bool)AccessTools.Property(view.GetType(), "Valid").GetValue(view);
    static IEnumerable<MethodBase> CollisionCalls(MethodBase method) => PatchProcessor.GetOriginalInstructions(method)
        .Where(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt)
        .Select(i => i.operand).OfType<MethodBase>();
    static void CheckCollisionRouting()
    {
        var target = typeof(SpaceCollision).GetNestedType("TargetView", BindingFlags.NonPublic);
        var query = typeof(SpaceCollision).GetNestedType("RayQuery", BindingFlags.NonPublic);
        bool Calls(Type type, string method, Type receiver, string entry) =>
            CollisionCalls(AccessTools.Method(type, method)).Any(m => m.DeclaringType == receiver && m.Name == entry);
        Check(AccessTools.Method(typeof(SpaceCollision), "TryMissileFootprint") == null
            && AccessTools.Method(typeof(AbstractMissile), "ApplyDamage") == null
            && AccessTools.Property(typeof(AbstractMissile), "HalfFootprint") == null,
            "legacy missile geometry and damage entry points have been removed from the production DLL");
        Check(Calls(typeof(ShipFireControl), "EstimateFanHits", query, "NextLocal")
            && !PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(ShipFireControl), "EstimateFanHits"))
                .Any(i => i.opcode == OpCodes.Castclass && Equals(i.operand, typeof(AbstractMissile))),
            "point-defense fan prediction uses the unified local query without a missile cast");
        Check(Calls(query, "Next", query, "NextLocal") && Calls(query, "NextLocal", target, "TryRay"),
            "both public ray coordinate conventions enter the structure adapter");
        Check(Calls(typeof(SpaceCollision), "OriginBoundsRadiusOf", target, "get_OriginBoundsRadius"),
            "origin-centered bounds enter the same map/entity adapter");
        Check(Calls(typeof(SpaceProjectileService), "Tick", target, "ApplyResolvedDamage"),
            "deferred damage execution re-enters the adapter instead of a payload-specific method");
        var effectTypes = new[] { typeof(SpaceImpactEffects) }.Concat(typeof(SpaceImpactEffects).GetNestedTypes(Private | BindingFlags.Public));
        var column = effectTypes.SelectMany(t => t.GetMethods(Private | BindingFlags.Public))
            .Single(m => m.Name.Contains("<ApplyHullContact>g__ApplyDamage"));
        Check(CollisionCalls(column).Any(m => m.DeclaringType == target && m.Name == "ApplyDamage")
            && !CollisionCalls(column).Any(m => m.Name == "ApplyStructureDamage")
            && Calls(typeof(SpaceCollision), "ResolveHullContact", typeof(SpaceCollision), "ResolveContact"),
            "the contact layer enters the common outcome once and uses the map/entity damage adapter");
        Check(Calls(typeof(SpaceImpactEffects), "ApplyStructureDamage", target, "ApplyResolvedDamage"),
            "native map direct damage and entity direct damage share the resolved-damage endpoint");
    }
    static object CollisionFlight(UniverseManager universe, SpaceObject owner, ThingDef def, Vector3 from, Thing launcher = null)
    {
        var type = typeof(SpaceProjectileService).GetNestedType("Projectile", System.Reflection.BindingFlags.NonPublic);
        var flight = Activator.CreateInstance(type, true);
        type.GetProperty("container").SetValue(flight, owner);
        foreach (var pair in new (string name, object value)[] {
            ("def",def), ("props",def.GetCompProperties<CompProperties_SpaceProjectile>()),
            ("prevPosition",from), ("launcher",launcher),
            ("timeoutTicks",def.GetCompProperties<CompProperties_SpaceProjectile>().timeoutTicks),
            ("spawnedAtTick",Find.TickManager.TicksGame) })
            type.GetField(pair.name).SetValue(flight,pair.value);
        ProjectileRecords(universe.Projectiles).Add(flight); return flight;
    }
    static void CheckCollisionViews(UniverseManager universe, Faction faction, HullRoundFixture f, ThingDef missileDef)
    {
        var craft = f.Craft(faction,0,0,Vector2.zero,true);
        var entity = new CollisionEntity { def=missileDef, HitPoints=25 };
        var missile = universe.CapturePayload(entity,new SpacePose(OrbitSphere.Move(Vector3.forward,new Vector2(13,19)),.7f),null);
        var original = craft.body.pose;
        try
        {
            foreach(var owner in new[] { craft.body,missile })
            {
                var view=CollisionView(owner); var local=new Vector2(.2f,-.3f);
                Check(CollisionValid(view),"both collision representations expose a live structure view");
                var backend=(Vector2)Call(view,"ToBackend",local);
                Check(((Vector2)Call(view,"ToLocal",backend)-local).magnitude<1e-5f,"backend origin offsets round-trip without a fake map");
                var world=(Vector3)Call(view,"ToUniverse",local);
                Check(((Vector2)Call(view,"FromUniverse",world)-local).magnitude<.003f,"rigid-local points round-trip on the real sphere");
                var normal=new Vector2(.6f,.8f); var tangent=(Vector2)Call(view,"DirectionToUniverse",local,normal);
                Check(Vector2.Dot(normal,(Vector2)Call(view,"DirectionFromUniverse",world,tangent))>.999f,"contact normals preserve direction at the hit point");
                var queryType=typeof(SpaceCollision).GetNestedType("RayQuery",System.Reflection.BindingFlags.NonPublic);
                var query=Activator.CreateInstance(queryType,Private,null,new object[]{owner,null,true},null);
                var args=new object[]{Call(view,"ToBackend",new Vector2(-3,0)),Vector2.right,6f,missileDef,0f,
                    null,null,null,null,null,false,null,null,false};
                bool found=(bool)Call(query,"Next",args);
                Check(found && AccessTools.Field(args[9].GetType(),"building").GetValue(args[9])==(owner==missile?(object)entity:craft.wall),
                    "shared ray query returns the real map or entity structure");
                Vector2 backendHit=(Vector2)AccessTools.Field(args[9].GetType(),"point").GetValue(args[9]);
                args[0]=new Vector2(-3,0);
                Check((bool)Call(query,"NextLocal",args)
                    && ((Vector2)AccessTools.Field(args[9].GetType(),"point").GetValue(args[9])
                        -(Vector2)Call(view,"ToLocal",backendHit)).magnitude<1e-5f,
                    "native and rigid-local ray entry points produce the same physical hit");
                float originRadius=(float)AccessTools.Method(typeof(SpaceCollision),"OriginBoundsRadiusOf").Invoke(null,new object[]{owner});
                Check(originRadius>=SpaceCollision.BoundsRadiusOf(owner) && originRadius>0f,
                    "map and entity origin bounds enclose their real hittable structure");
                if(owner==missile) Check(Mathf.Abs(originRadius-missileDef.Size.ToVector2().magnitude*.5f)<1e-5f,
                    "an entity uses its actual footprint radius instead of zero navigation bounds");
                int version=owner.RepresentationVersion;
                AccessTools.Property(typeof(SpaceObject),nameof(SpaceObject.RepresentationVersion)).SetValue(owner,version+1);
                Check(!CollisionValid(view),"representation replacement invalidates a captured view");
                Check(!CollisionValid(Call(view,"AtPose",owner.pose)),"changing a contact pose cannot resurrect a stale representation");
                AccessTools.Property(typeof(SpaceObject),nameof(SpaceObject.RepresentationVersion)).SetValue(owner,version);
            }
            Check(!CollisionValid(CollisionView(new SpaceObject(59000,original){baseTag=SpaceObjectTag.Projectile})),"ordinary ballistic records do not acquire hittable entities");
            var part=new ImpactOutcomePart {kind=ImpactEffectKind.DamageMap,damageDef=DamageDefOf.Bullet};
            var target=CollisionView(missile);
            Call(target,"ApplyDamage",IntVec3.Invalid,entity,part,7f,1f,DamageDefOf.Bullet,craft.wall,missileDef,Vector2.zero);
            Check(entity.HitPoints==18,"entity adapter applies real TakeDamage to its owned building");
            Call(CollisionView(craft.body),"ApplyDamage",new IntVec3(6,0,6),craft.wall,part,7f,1f,DamageDefOf.Bullet,entity,missileDef,Vector2.zero);
            Check(craft.wall.HitPoints==293,"map adapter reaches the existing direct building damage primitive");
            entity.Destroy(); Check(!CollisionValid(target),"entity destruction immediately invalidates existing views");
            Check(craft.body.pose.position==original.position && craft.body.pose.angle==original.angle,"structure reads do not change universe pose");
        }
        finally {f.Clear();universe.UnregisterObject(missile);}
    }

    static void CheckCollisionScheduling(UniverseManager universe, Faction faction,
        HullRoundFixture f, ThingDef bulletDef, ThingDef missileDef)
    {
        const int count = 64;
        var target = f.Craft(faction, 100, 0, Vector2.zero, true);
        var shots = new List<SpaceObject>();
        var flights = new List<object>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var pose = new SpacePose(OrbitSphere.Move(Vector3.forward,
                    new Vector2(-i * .05f, 20 + i * .1f)), 0f) { velocity = new Vector2(-60f, 0f) };
                var shot = new SpaceObject(60000 + i, pose) { baseTag = SpaceObjectTag.Projectile };
                universe.RegisterObject(shot); shots.Add(shot);
                flights.Add(CollisionFlight(universe, shot, bulletDef, pose.position));
            }

            f.Prepare();
            foreach (SpaceObject shot in shots) shot.AdvanceDrift(1f / 60f);
            universe.Projectiles.ResolveCollisions();
            int firstExact = flights.Count(record =>
                (bool)record.GetType().GetField("collisionCheckedThisTick").GetValue(record));
            Check(firstExact == count,
                $"new projectile certificates begin with one exact collision round: {firstExact}/{count}");

            f.Prepare();
            foreach (SpaceObject shot in shots) shot.AdvanceDrift(1f / 60f);
            universe.Projectiles.ResolveCollisions();
            Check(flights.All(record => !(bool)record.GetType().GetField("collisionCheckedThisTick").GetValue(record))
                && ((ICollection)AccessTools.Field(typeof(SpaceProjectileService), "bodyScratch")
                    .GetValue(universe.Projectiles)).Count == 0,
                "far missed projectiles retain passive hit bodies while skipping active exact sweeps");

            // Add a new legal defender after certificates were issued. It was absent from
            // their proof, so every surviving shot must return to the exact path immediately.
            f.Craft(faction, 300, 0, Vector2.zero, true);
            f.Prepare();
            foreach (SpaceObject shot in shots) shot.AdvanceDrift(1f / 60f);
            universe.Projectiles.ResolveCollisions();
            Check(flights.All(record => (bool)record.GetType().GetField("collisionCheckedThisTick").GetValue(record)),
                "a newly eligible collision target invalidates existing miss certificates");
        }
        finally { f.Clear(); }

        // A target may cross the whole reserved gap in one tick. Actual target displacement
        // consumes the certificate before resolution, so the production moving-target sweep
        // still observes the contact in that same tick.
        target = f.Craft(faction, 100, 0, Vector2.zero, true);
        var crossingPose = new SpacePose(OrbitSphere.Move(Vector3.forward, Vector2.zero), 0f)
            { velocity = new Vector2(-60f, 0f) };
        var crossingShot = new SpaceObject(60100, crossingPose) { baseTag = SpaceObjectTag.Projectile };
        universe.RegisterObject(crossingShot);
        object crossingFlight = CollisionFlight(universe, crossingShot, bulletDef, crossingPose.position);
        try
        {
            f.Prepare(); crossingShot.AdvanceDrift(1f / 60f); universe.Projectiles.ResolveCollisions();
            f.Prepare(); crossingShot.AdvanceDrift(1f / 60f); universe.Projectiles.ResolveCollisions();
            Check(!(bool)crossingFlight.GetType().GetField("collisionCheckedThisTick").GetValue(crossingFlight),
                "a distant miss enters the conservative skip path");

            target.body.pose.velocity = new Vector2(-6150f, 0f);
            f.Prepare();
            crossingShot.AdvanceDrift(1f / 60f);
            target.body.AdvanceDrift(1f / 60f);
            universe.Projectiles.ResolveCollisions();
            Check((bool)crossingFlight.GetType().GetField("collisionCheckedThisTick").GetValue(crossingFlight)
                && (bool)crossingFlight.GetType().GetField("dead").GetValue(crossingFlight)
                && target.wall.HitPoints == 290,
                "same-tick target crossing exhausts clearance and resolves without a missed hit");
        }
        finally { f.Clear(); }

        target = f.Craft(faction, 100, 0, Vector2.zero, true);
        var passiveEntity = new CollisionEntity { def = missileDef, HitPoints = 25 };
        var passiveCarrier = universe.CapturePayload(passiveEntity,
            new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(0, 30)), 0f)
                { velocity = new Vector2(-60f, 0f) }, null);
        object passiveFlight = CollisionFlight(universe, passiveCarrier, missileDef,
            passiveCarrier.pose.position);
        try
        {
            f.Prepare(); passiveCarrier.AdvanceDrift(1f / 60f); universe.Projectiles.ResolveCollisions();
            f.Prepare(); passiveCarrier.AdvanceDrift(1f / 60f); universe.Projectiles.ResolveCollisions();
            Check(!(bool)passiveFlight.GetType().GetField("collisionCheckedThisTick").GetValue(passiveFlight),
                "a distant interceptable missile can skip its own active sweep");

            Vector3 from = OrbitSphere.Move(passiveCarrier.pose.position, new Vector2(-3f, 0f));
            var interceptor = new SpaceObject(60101,
                new SpacePose(OrbitSphere.Move(passiveCarrier.pose.position, new Vector2(3f, 0f)), 0f))
                { baseTag = SpaceObjectTag.Projectile };
            universe.RegisterObject(interceptor);
            object interceptorFlight = CollisionFlight(universe, interceptor, bulletDef, from);
            interceptorFlight.GetType().GetField("reflectedByShield").SetValue(interceptorFlight, true);
            universe.Projectiles.ResolveCollisions();
            Check(!(bool)passiveFlight.GetType().GetField("collisionCheckedThisTick").GetValue(passiveFlight)
                && passiveEntity.HitPoints == 15,
                "a missile that skips its active sweep remains a live defender for an exact projectile");
        }
        finally { f.Clear(); }
    }
    static void CheckHullMaplessContact(UniverseManager universe,Faction faction,HullRoundFixture f)
    {
        CheckCollisionRouting();
        var missileDef=TestBuildingDef("HullMaplessMissile");
        missileDef.comps.Add(new CompProperties_SpaceProjectile {guided=true,hasMap=false,timeoutTicks=600});
        missileDef.useHitPoints=missileDef.destroyable=true;missileDef.fillPercent=1;
        var bulletDef=TestBuildingDef("HullMaplessOrdinary");
        bulletDef.comps.Add(new CompProperties_SpaceProjectile {hasMap=false,timeoutTicks=600});
        var effect=new SpaceImpactEffectDef {
            defName="HullMaplessDirect",projectileDefs=new List<ThingDef>{missileDef,bulletDef},buildingHitChance=1,
            defenderEffect=new ImpactOutcomePart {kind=ImpactEffectKind.DamageMap,damageDef=DamageDefOf.Bullet,damage=10,incidenceMinFactor=1,armorPenetration=1},
            attackerEffect=new ImpactOutcomePart {kind=ImpactEffectKind.DestroySelf}};
        var native=XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
            .Single(e=>(string)e.Element("defName")=="DeltaV_Impact_MissileVsStructure").Element("defenderEffect");
        var patch=new Harmony("DeltaV.Regression.CollisionTarget");
        patch.Patch(AccessTools.Method(typeof(GenExplosion),nameof(GenExplosion.DoExplosion)),prefix:new HarmonyMethod(typeof(Program),nameof(CollisionExplosion)));
        patch.Patch(AccessTools.Method(typeof(SpaceProjectileService),"RegisterImpactFx"),prefix:new HarmonyMethod(typeof(Program),nameof(CollisionVisualBoundary)));
        DefDatabase<SpaceImpactEffectDef>.Add(effect);SpaceImpactEffects.ResetCache();
        try
        {
            // The pre-fix production DLL fails the first scenario in MapDirectionToUniverseTangent(null).
            foreach(bool first in new[]{false,true})
            foreach(int scenario in new[]{0,1,2,3,4,5,6})
            {
                bool explosive=scenario==3;
                effect.defenderEffect.damage=explosive?(float)native.Element("damage"):scenario==5?500:10;
                effect.defenderEffect.armorPenetration=explosive?(float)native.Element("armorPenetration"):1;
                effect.defenderEffect.explosionRadius=explosive?(float)native.Element("explosionRadius"):0;
                var craft=f.Craft(faction,0,0,new Vector2(300,0),false,size:scenario==2?3:1);
                if(scenario==2){craft.body.pose.angle=.35f;craft.body.pose.angularVelocity=.5f;}
                f.Prepare();
                var entity=new CollisionEntity {def=missileDef,HitPoints=25};
                var pose=new SpacePose(OrbitSphere.Move(Vector3.forward,new Vector2(3,0)),0){velocity=scenario==1?new Vector2(-60,0):Vector2.zero};
                var missile=universe.CapturePayload(entity,pose,null);
                var flight=CollisionFlight(universe,missile,missileDef,pose.position,craft.wall);
                if(first){ProjectileRecords(universe.Projectiles).Remove(flight);ProjectileRecords(universe.Projectiles).Insert(0,flight);}
                var bystander=f.Craft(faction,20,20,Vector2.zero,true);
                var bullet=new SpaceObject(59001,new SpacePose(SpaceTransform.ShipLocalToUniverse(bystander.body,new Vector2(3,0)),0)){baseTag=SpaceObjectTag.Projectile};
                universe.RegisterObject(bullet);
                CollisionFlight(universe,bullet,bulletDef,SpaceTransform.ShipLocalToUniverse(bystander.body,new Vector2(-3,0)));
                collisionExplosions.Clear();
                try
                {
                    craft.body.AdvanceDrift(1f/60f);missile.AdvanceDrift(1f/60f);
                    if(scenario==4)entity.TakeDamage(new DamageInfo(DamageDefOf.Bullet,100,1));
                    collisionFailVisual=scenario==6;
                    try
                    {
                        universe.Projectiles.ResolveCollisions();
                        Check(!collisionFailVisual,"normal visual boundary completes without a fixture exception");
                    }
                    catch(InvalidOperationException exception) when(collisionFailVisual && exception.Message=="collision fixture visual endpoint")
                    { Check(true,"unexpected visual endpoint failure releases the current collision index"); }
                    finally {collisionFailVisual=false;}
                    Check(true,$"hull/entity contact without null-map conversion: order={first}, scenario={scenario}");
                    if(scenario!=4)
                    {
                        Check((bool)flight.GetType().GetField("dead").GetValue(flight),"the real missile consumes the contact outcome");
                        var body=(SpaceCollision.SweptBody)flight.GetType().GetField("body").GetValue(flight);
                        Check(body.resolvedHit && body.resolvedHitDefender==craft.body && craft.wall.OccupiedRect().Contains(body.resolvedHitCell),"contact identifies the actual carrier and its structure");
                        if(explosive)
                        {
                            Check(collisionExplosions.Count==2,"one native explosion per real projectile, without duplicate hull detonations");
                            var hit=collisionExplosions.Single(e=>e.map==craft.body.map);
                            Check(hit.cell==new IntVec3(6,0,6) && hit.amount==(int)native.Element("damage") && hit.ap==(float)native.Element("armorPenetration")
                                && hit.radius==(float)native.Element("explosionRadius") && hit.instigator==craft.wall && hit.projectile==missileDef,
                                "warhead explosion preserves real Def parameters and attribution");
                        }
                        else if(scenario==5) Check(craft.wall.Destroyed && f.Registered <= 4,
                            "warhead destruction invalidates the hull source in the same tick");
                        else Check(craft.wall.HitPoints==290,"one real missile hit, without additional hull crush damage");
                    }
                    else Check(craft.wall.HitPoints==300,"destroyed missile cannot damage the approaching hull");
                    Check(scenario==5?bystander.wall.Destroyed:bystander.wall.HitPoints==(explosive?300:290),
                        "later ordinary projectile work continues after hull/entity contact");
                    f.Flush();
                    Check(entity.destroys==1 && universe.ObjectById(missile.objectId)==null,"payload retirement occurs once at the frame boundary");
                    Check(((IDictionary)AccessTools.Field(typeof(SpaceProjectileService),"collisionBodies").GetValue(universe.Projectiles)).Count==0,"current-sweep index releases all references");
                }
                finally {f.Clear();universe.UnregisterObject(missile);universe.UnregisterObject(bullet);}
            }
            effect.defenderEffect.explosionRadius=0;
            effect.defenderEffect.damage=500;
            effect.defenderEffect.penetrates=true;
            var firstWall=f.Craft(faction,0,0,Vector2.zero,true,hp:10);
            var middleWall=f.Craft(faction,1.2f,0,Vector2.zero,true,hp:10);
            var lastWall=f.Craft(faction,4,0,Vector2.zero,true,hp:10);
            f.Craft(faction,0,100,Vector2.zero,false);
            f.Prepare();
            var penetrating=new SpaceObject(59002,new SpacePose(OrbitSphere.Move(Vector3.forward,new Vector2(8,0)),0)
                {velocity=new Vector2(1680,0)}) {baseTag=SpaceObjectTag.Projectile};
            universe.RegisterObject(penetrating);
            CollisionFlight(universe,penetrating,bulletDef,OrbitSphere.Move(Vector3.forward,new Vector2(-20,0)));
            try
            {
                universe.Projectiles.ResolveCollisions();
                Check(firstWall.wall.Destroyed && middleWall.wall.Destroyed && lastWall.wall.Destroyed,
                    "the projectile stage preserves every adjacent target after projectile penetration");
            }
            finally {f.Clear();universe.UnregisterObject(penetrating);}
            effect.defenderEffect.penetrates=false;
            var approaching = f.Craft(faction, 0, 0, new Vector2(300,0), false);
            var removed = f.Craft(faction, 3, 0, Vector2.zero, true, hp:10);
            f.Prepare();
            var openingShot = new SpaceObject(59003, new SpacePose(OrbitSphere.Move(Vector3.forward, new Vector2(3,4)), 0)
                {velocity=new Vector2(0,300)}) {baseTag=SpaceObjectTag.Projectile};
            universe.RegisterObject(openingShot);
            CollisionFlight(universe, openingShot, bulletDef, OrbitSphere.Move(Vector3.forward, new Vector2(3,-1)));
            try
            {
                approaching.body.AdvanceDrift(1f/60f);
                universe.Projectiles.ResolveCollisions();
                Check(removed.wall.Destroyed && approaching.wall.HitPoints == 300 && f.Requests == 0,
                    $"projectile-stage destruction removes the face before hull detection: target={removed.wall.HitPoints}, source={approaching.wall.HitPoints}, requests={f.Requests}");
            }
            finally { f.Clear(); universe.UnregisterObject(openingShot); }
            effect.defenderEffect.damage = 10;
            var touching = f.Craft(faction, 0f, 0f, new Vector2(20f, 0f), false, hp: 10000);
            var opposite = f.Craft(faction, 1.1f, 0f, Vector2.zero, true, hp: 10000);
            var stationary = f.Craft(faction, 0f, 40f, Vector2.zero, true);
            f.Prepare();
            Vector3 missileStart = SpaceTransform.ShipLocalToUniverse(stationary.body, new Vector2(-.25f, 0f));
            var sameTimeMissile = new CollisionEntity { def = missileDef, HitPoints = 25 };
            var sameTimeCarrier = universe.CapturePayload(sameTimeMissile,
                new SpacePose(missileStart, 0f) {velocity = new Vector2(20f, 0f)}, null);
            CollisionFlight(universe, sameTimeCarrier, missileDef, missileStart);
            try
            {
                touching.body.AdvanceDrift(1f / 60f);
                sameTimeCarrier.AdvanceDrift(1f / 60f);
                universe.Projectiles.ResolveCollisions();
                f.Flush();
                Check(f.Requests == 1 && opposite.wall.HitPoints < 10000 && stationary.wall.HitPoints == 290
                    && sameTimeMissile.destroys == 1,
                    $"hull and mapless missile settle with one effect each: hull={f.Requests}, wall={opposite.wall.HitPoints}, missile={sameTimeMissile.destroys}");
            }
            finally { f.Clear(); universe.UnregisterObject(sameTimeCarrier); }
            CheckCollisionViews(universe,faction,f,missileDef);
            CheckPointDefenseFanAdapters(universe,faction,f,missileDef);
            CheckCollisionScheduling(universe,faction,f,bulletDef,missileDef);
        }
        finally {collisionFailVisual=false;patch.UnpatchAll(patch.Id);collisionExplosions.Clear();DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading.Remove(effect);SpaceImpactEffects.ResetCache();}
    }
}

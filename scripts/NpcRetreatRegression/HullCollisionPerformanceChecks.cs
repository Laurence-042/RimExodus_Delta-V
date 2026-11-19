using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void HullBenchSound(SpaceProjectileService service,SoundDef mapSound,SoundDef shellSound,SpaceObject owner,
        Vector2 local,Vector3 position,int spacing,float volume,bool combatSetting,float shellPitch) { }
    static IEnumerable<CodeInstruction> HullBenchAudio(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(AccessTools.Method(typeof(SpaceProjectileService),"PlayCombatSound")))
            {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(Program),nameof(HullBenchSound));}
            yield return instruction;
        }
    }
    static long hullBenchDamageCalls, hullBenchSweeps, hullBenchContacts, hullBenchRays;
    static long hullBenchImpulses, hullBenchBatches, hullBenchQueries, hullBenchDeferred;
    static int hullBenchStops;
    static double hullBenchLostTicks;
    static void HullBenchRay() => hullBenchRays++;
    static void HullBenchDeferred(SpaceCollision.ImpactEvent evt)
    {
        if (evt.hullBatch?.continues == true) hullBenchDeferred++;
    }
    static void HullBenchRecordedImpulse() => hullBenchImpulses++;
    static void HullBenchBatch() => hullBenchBatches++;
    static void HullBenchQuery() => hullBenchQueries++;
    static void HullBenchStop(float __0)
    {
        hullBenchStops++;
        hullBenchLostTicks += 1 - __0;
    }


    sealed class BenchHullWall : Building
    {
        public override int HitPoints
        {
            get => (int)AccessTools.Field(typeof(Thing), "hitPointsInt").GetValue(this);
            set => Set(typeof(Thing), this, "hitPointsInt", value);
        }

        public override void Kill(DamageInfo? info = null, Hediff culprit = null)
        {
            Map map = Map;
            CellRect footprint = this.OccupiedRect();
            map.thingGrid.Deregister(this);
            Set(typeof(Thing), this, "mapIndexOrState", (sbyte)-2);
            SubstructureOwnership.BumpStructureVersion(map, footprint);
        }
    }
    static void HullBenchSweep() => hullBenchSweeps++;
    static void HullBenchDamage() => hullBenchDamageCalls++;
    static bool HullBenchStat(Thing thing, StatDef stat, ref float __result)
    { if (stat != StatDefOf.Mass) return true; __result = 1f; return false; }
    static bool HullBenchTile(ref PlanetTile __result) { __result = PlanetTile.Invalid; return false; }

    sealed class HullPerformanceFixture : IDisposable
    {
        internal readonly UniverseManager universe;
        readonly Harmony patch = new("DeltaV.Regression.HullPerformance");
        readonly EngagementFixturePatch geometry = new();
        internal readonly List<Map> maps = new();
        internal readonly List<SpaceObject> frames = new();
        internal readonly List<Building> walls = new();
        int id = 61000;
        readonly List<SpaceImpactEffectDef> effects = new();
        readonly DamageDef previousDamage = DamageDefOf.Bullet;
        readonly StatDef previousMass = StatDefOf.Mass;
        readonly ThingDef previousConsole = ThingDefOf.PilotConsole;
        readonly ThingDef previousEngine = ThingDefOf.GravEngine;
        readonly ThingDef previousLauncher = DeltaVDefOf.DeltaV_Weapon_MissileLauncher;
        readonly object previousFactions = AccessTools.Field(typeof(World), "factionManager").GetValue(Find.World);
        readonly bool previousPerf = DeltaVMod.CurrentSettings.logPerformance;
        readonly bool previousDrag = DeltaVMod.CurrentSettings.passiveDrag;
        readonly ThingDef previousHull = DeltaVDefOf.DeltaV_Projectile_HullContact;
        internal HullPerformanceFixture(UniverseManager universe, Faction faction, bool suppressVisual = false)
        {
            this.universe = universe;
            patch.Patch(typeof(ModsConfig).TypeInitializer, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            patch.Patch(AccessTools.PropertyGetter(typeof(MapInfo), nameof(MapInfo.Tile)), prefix: new HarmonyMethod(typeof(Program), nameof(HullBenchTile)));
            patch.Patch(AccessTools.PropertyGetter(typeof(Thing), "FlammableNow"), prefix: new HarmonyMethod(typeof(Program), nameof(BattleNotFlammable)));
            patch.Patch(AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue)), prefix: new HarmonyMethod(typeof(Program), nameof(HullBenchStat)));
            foreach (var endpoint in new[] {
                AccessTools.Method(typeof(DamageWatcher), "Notify_DamageTaken"), AccessTools.Method(typeof(Faction), "Notify_BuildingTookDamage"),
                AccessTools.Method(typeof(Building), "PostApplyDamage"), AccessTools.Method(typeof(TerrainGrid), "DoTerrainChangedEffects"),
                AccessTools.Method(typeof(ImpactSoundUtility), "PlayImpactSound"), AccessTools.Method(typeof(SpaceProjectileService), "NotifyFoundationDestroyed") })
                patch.Patch(endpoint, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            var impactFx = AccessTools.Method(typeof(SpaceProjectileService), "RegisterImpactFx");
            if (suppressVisual)
                patch.Patch(impactFx, prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
            else
                patch.Patch(impactFx, transpiler: new HarmonyMethod(typeof(Program), nameof(HullBenchAudio)));
            if (Environment.GetEnvironmentVariable("DELTAV_HULL_COUNT") == "1")
            {
                // Detailed probe time belongs only to the independent count lane.
                DeltaVMod.CurrentSettings.logPerformance = true;
                patch.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchDamage)));
                var geometryType = typeof(SpaceCollision);
                patch.Patch(AccessTools.Method(geometryType, "TryHullGridSegment"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchRay)));
                patch.Patch(AccessTools.Method(typeof(DeltaVPerformanceProbe), "RecordHullCrushBatch"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchBatch)));
                patch.Patch(AccessTools.Method(typeof(DeltaVPerformanceProbe), "RecordHullImpulse"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchRecordedImpulse)));
                patch.Patch(AccessTools.Method(typeof(SpaceProjectileService), "StopHullMotion"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchStop)));
                patch.Patch(AccessTools.Method(typeof(SpaceImpactEffects), "ApplyHullContact"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchDeferred)));
                patch.Patch(AccessTools.Method(typeof(SpaceCollision), "TryFindFirstContact"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchQuery)));
                patch.Patch(AccessTools.Method(geometryType, "Sweep"),
                    postfix: new HarmonyMethod(typeof(Program), nameof(HullBenchSweep)));
            }
            patch.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"), prefix: new HarmonyMethod(typeof(Program), nameof(BattleRoof)));
            patch.CreateClassProcessor(typeof(SpaceImpactEffects).Assembly.GetType("DeltaV.Patch_TerrainGrid_RemoveFoundation_Ownership")).Patch();
            DamageDefOf.Bullet = new DamageDef {defName="HullBenchmarkDamage",workerClass=typeof(DamageWorker),harmsHealth=true};
            StatDefOf.Mass = new StatDef {defName="HullBenchmarkMass"};
            ThingDefOf.PilotConsole = TestBuildingDef("HullBenchmarkConsole");
            ThingDefOf.PilotConsole.useHitPoints=ThingDefOf.PilotConsole.destroyable=true;
            ThingDefOf.GravEngine=TestBuildingDef("HullBenchmarkEngine");
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher=TestBuildingDef("HullBenchmarkLauncher");
            var manager=(FactionManager)Raw(typeof(FactionManager)); Set(manager,"ofPlayer",faction); Set(Find.World,"factionManager",manager);
            DeltaVMod.CurrentSettings.passiveDrag=false;
            var def=TestBuildingDef("DeltaV_Projectile_HullContact");
            def.comps.Add(new CompProperties_SpaceProjectile {hasMap=false,ignoresShields=true,collisionRadius=.5f});
            DeltaVDefOf.DeltaV_Projectile_HullContact=def;
            foreach(var node in XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml").Root.Elements()
                .Where(e=>e.Element("projectileDefs")?.Value==def.defName))
            {
                var p=node.Element("defenderEffect");
                var effect=new SpaceImpactEffectDef {defName=(string)node.Element("defName"),projectileDefs=new(){def},
                    targetsSubstructure=(bool?)node.Element("targetsSubstructure")??false,
                    buildingHitChance=(float?)node.Element("buildingHitChance")??-1,substructureHitChance=(float?)node.Element("substructureHitChance")??0,
                    defenderEffect=new ImpactOutcomePart {kind=Enum.Parse<ImpactEffectKind>((string)p.Element("kind")),damageDef=DamageDefOf.Bullet,
                        damage=(float)p.Element("damage"),referenceSpeed=(float)p.Element("referenceSpeed"),
                        kineticMassScaling=(bool?)p.Element("kineticMassScaling")??false,penetrates=(bool?)p.Element("penetrates")??false,
                        incidenceMinFactor=(float)p.Element("incidenceMinFactor"),armorPenetration=(float)p.Element("armorPenetration"),
                        substructureBreakThreshold=(float)p.Element("substructureBreakThreshold")},
                    attackerEffect=new ImpactOutcomePart {kind=ImpactEffectKind.DestroySelf}};
                DefDatabase<SpaceImpactEffectDef>.Add(effect);effects.Add(effect);
            }
            SpaceImpactEffects.ResetCache();
        }
        internal void Fleet(Faction faction,int count,string shape,string mode)
        {
            (int w,int h)=shape switch {"large"=>(41,29),"concave"=>(23,23),_=>(7,7)};
            for(int n=0;n<count;n++)
            {
                var map=TestMap(++id,Math.Max(w,h)+6);maps.Add(map);
                Array.Fill(map.terrainGrid.topGrid,map.terrainGrid.topGrid[0]);
                map.roofGrid=new RoofGrid(map);map.damageWatcher=(DamageWatcher)Raw(typeof(DamageWatcher));
                var console=new CompDeltaVPilotConsole {isMain=true,shipId=++id};
                var helm=PlaceHullFixtureBuilding(map,new IntVec3(w/2+3,0,h/2+3),ThingDefOf.PilotConsole,100000,id,console);
                Set(typeof(Thing),helm,"factionInt",faction);
                var terrain=(TerrainDef)Raw(typeof(TerrainDef));terrain.defName="HullBenchmarkDeck";terrain.isFoundation=true;terrain.tags=new(){"Substructure"};
                var owners=new int[map.cellIndices.NumGridCells];Set(SubstructureOwnership.TryGet(map),"ownerGrid",owners);
                var wallDef=TestBuildingDef("HullBenchmarkWall"+n);wallDef.useHitPoints=wallDef.destroyable=true;wallDef.building.isWall=true;wallDef.fillPercent=1;
                for(int x=0;x<w;x++)for(int z=0;z<h;z++)
                {
                    if(shape=="concave" && x>w/2 && z>h/3 && z<h*2/3)continue;
                    var cell=new IntVec3(x+3,0,z+3);int index=map.cellIndices.CellToIndex(cell);
                    map.terrainGrid.foundationGrid[index]=terrain;owners[index]=console.shipId;
                    if(cell==console.parent.Position)continue;
                    if(x==0||x==w-1||z==0||z==h-1)
                    {
                        var building = (BenchHullWall)Raw(typeof(BenchHullWall));
                        building.def = wallDef;
                        building.thingIDNumber = ++id;
                        building.HitPoints = mode == "breach" ? 20 : 100000;
                        Set(typeof(Thing), building, "positionInt", cell);
                        var comp = new EmptyComp {parent = building};
                        Set(typeof(ThingWithComps), building, "comps", new List<ThingComp> {comp});
                        PlaceFixtureBuilding(building, map);
                        Set(typeof(Thing),building,"factionInt",faction);walls.Add(building);
                    }
                }
                SubstructureOwnership.BumpStructureVersion(map);
                float east=(n%2)*(w+500f), north=(n/2)*(h+500f);
                Vector2 velocity=Vector2.zero;float angular=0;
                if(mode=="far-drift")velocity=new Vector2(20,3);
                if(mode=="head-on"||mode=="breach"||mode=="mixed")
                {east=n%2*(w+.1f);velocity=new Vector2(n%2==0?30:-30,0);}
                if(mode=="breach")velocity*=10;
                if(mode=="multi"){east=(n%3)*(w+.15f);north=(n/3)*(h+500f);velocity=new Vector2(n%3==0?120:0,0);}
                if(mode=="scrape"){east=(n%2)*2f;north+=(n%2)*(h-.005f);velocity=new Vector2(n%2==0?12:0,n%2==0?.05f:0);}
                if(mode=="spin"){east=n%2*(w+1f);angular=n%2==0?.7f:-.5f;}
                var pose=new SpacePose(OrbitSphere.Move(Vector3.forward,new Vector2(east,north)),0){velocity=velocity,angularVelocity=angular};
                var frame=new MapSpaceObject(++id,map,pose);universe.RegisterObject(frame);frames.Add(frame);
                universe.Controls.For(console.Vessel).SetCollisionAvoidanceEnabled(n%2==1);
            }
            if(mode=="mixed") AddMixedProjectiles(faction,w);
        }
        private sealed class BenchMissile : Building
        {
            public override void Destroy(DestroyMode mode=DestroyMode.Vanish)
            { Set(typeof(Thing),this,"mapIndexOrState",(sbyte)-2); }
            public override void Kill(DamageInfo? info=null,Hediff culprit=null) => Destroy(DestroyMode.KillFinalize);
        }
        private void AddMixedProjectiles(Faction faction,int width)
        {
            var bullet=TestBuildingDef("HullBenchBullet");bullet.comps.Add(new CompProperties_SpaceProjectile {hasMap=false,collisionRadius=0});
            var missile=TestBuildingDef("HullBenchMissile");missile.useHitPoints=missile.destroyable=true;
            missile.comps.Add(new CompProperties_SpaceProjectile {hasMap=false,guided=true,collisionRadius=0});
            var pd=TestBuildingDef("HullBenchPointDefense");pd.comps.Add(new CompProperties_SpaceProjectile {hasMap=false,laserRange=width*4,collisionRadius=0,guaranteedHitMissiles=true});
            var rule=new SpaceImpactEffectDef {defName="HullBenchMixedDamage",projectileDefs=new(){bullet,missile,pd},buildingHitChance=1,
                defenderEffect=new ImpactOutcomePart {kind=ImpactEffectKind.DamageMap,damageDef=DamageDefOf.Bullet,damage=10,armorPenetration=1,incidenceMinFactor=1},
                attackerEffect=new ImpactOutcomePart {kind=ImpactEffectKind.DestroySelf}};
            effects.Add(rule);DefDatabase<SpaceImpactEffectDef>.Add(rule);SpaceImpactEffects.ResetCache();
            for(int i=0;i<frames.Count;i+=2)
            {
                var target=frames[i+1];
                Vector3 from=SpaceTransform.ShipLocalToUniverse(target,new Vector2(-width-2,0));
                var round=new SpaceObject(universe.AllocateOwnerId(),new SpacePose(from,0){velocity=new Vector2(900,0)}){baseTag=SpaceObjectTag.Projectile};
                universe.RegisterObject(round);var flight=AddProjectileRecord(universe,round);
                Set(flight,"def",bullet);Set(flight,"props",bullet.GetCompProperties<CompProperties_SpaceProjectile>());Set(flight,"prevPosition",from);
                var entity=new BenchMissile {def=missile};Set(typeof(Thing),entity,"hitPointsInt",25);
                var missileOwner=universe.CapturePayload(entity,new SpacePose(SpaceTransform.ShipLocalToUniverse(target,new Vector2(-width/2f-1,0)),0){velocity=new Vector2(100,0)},null);
                var guided=AddProjectileRecord(universe,missileOwner);Set(guided,"def",missile);Set(guided,"props",missile.GetCompProperties<CompProperties_SpaceProjectile>());Set(guided,"prevPosition",missileOwner.pose.position);
                var rayOwner=new SpaceObject(universe.AllocateOwnerId(),new SpacePose(from,0)){baseTag=SpaceObjectTag.Projectile,faction=faction};universe.RegisterObject(rayOwner);
                var beam=AddProjectileRecord(universe,rayOwner);Set(beam,"def",pd);Set(beam,"props",pd.GetCompProperties<CompProperties_SpaceProjectile>());Set(beam,"laser",true);
                Set(beam,"laserFrom",from);Set(beam,"laserTo",missileOwner.pose.position);
            }
        }
        internal void Prepare()
        {
            Set(Find.TickManager,"ticksGameInt",Find.TickManager.TicksGame+1);
            universe.Projectiles.Tick();
        }
        internal void Step()
        {
            Prepare();
            for(int i=0;i<universe.objects.Count;i++)universe.objects[i].AdvanceDrift(1f/60f);
            universe.Projectiles.ResolveCollisions();
            var actions=Actions(universe);if(actions!=null){for(int i=0;i<actions.Count;i++)actions[i]();actions.Clear();}
        }
        internal void ClearFleet()
        {
            universe.Projectiles.ResetFlow();
            var actions = Actions(universe);
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++) actions[i]();
                actions.Clear();
            }
            foreach (var frame in frames) universe.UnregisterObject(frame);
            foreach (var map in maps) { SpaceCollision.Forget(map); Find.Maps.Remove(map); }
            frames.Clear();
            maps.Clear();
            walls.Clear();
            id = 61000;
            ShipRegistry.ClearCaches();
            ShipPhysics.ClearCache();
        }

        public void Dispose()
        {
            ClearFleet();
            foreach(var effect in effects)DefDatabase<SpaceImpactEffectDef>.AllDefsListForReading.Remove(effect);
            SpaceImpactEffects.ResetCache();ShipRegistry.ClearCaches();ShipPhysics.ClearCache();
            DamageDefOf.Bullet = previousDamage;
            StatDefOf.Mass = previousMass;
            ThingDefOf.PilotConsole = previousConsole;
            ThingDefOf.GravEngine = previousEngine;
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = previousLauncher;
            Set(Find.World, "factionManager", previousFactions);
            DeltaVMod.CurrentSettings.logPerformance = previousPerf;
            DeltaVMod.CurrentSettings.passiveDrag = previousDrag;
            DeltaVDefOf.DeltaV_Projectile_HullContact = previousHull;
            patch.UnpatchAll(patch.Id);geometry.Dispose();
        }
    }
    static double HullQuantile(double[] values,double p)
    {
        double at=(values.Length-1)*p;int left=(int)Math.Floor(at);int right=(int)Math.Ceiling(at);
        return values[left]+(values[right]-values[left])*(at-left);
    }
    static void RunHullCollisionPerformance(UniverseManager universe,Faction faction)
    {
        string only=Environment.GetEnvironmentVariable("DELTAV_HULL_FILTER");
        int samples=int.TryParse(Environment.GetEnvironmentVariable("DELTAV_HULL_SAMPLES"),out int parsed)?parsed:24;
        foreach(string warm in new[]{"far-drift","head-on","scrape","spin","breach","mixed"})
        {
            using var fixture=new HullPerformanceFixture(universe,faction);fixture.Fleet(faction,2,"small",warm);
            for(int i=0;i<8;i++)fixture.Step();
        }
        foreach (int count in new[] {2, 8, 24})
        foreach (var (shape, mode) in new[] {("large", "head-on"), ("large", "breach"), ("concave", "multi"), ("large", "mixed")})
        {
            string key = $"{count}/{shape}/{mode}";
            if (!string.IsNullOrEmpty(only) && !key.Contains(only)) continue;
            Rand.PushState(20261119);
            using var fixture=new HullPerformanceFixture(universe,faction);
            try { fixture.Fleet(faction,count,shape,mode); }
            catch(Exception ex) { Console.Error.WriteLine("Fleet initialization: "+ex); throw; }
            Console.WriteLine("HULL_CASE_BEGIN "+key);
            hullBenchDamageCalls = hullBenchSweeps = hullBenchContacts = hullBenchRays = hullBenchDeferred = 0;
            hullBenchImpulses = hullBenchBatches = hullBenchQueries = 0;
            hullBenchStops = 0;
            hullBenchLostTicks = 0;
            bool counting = Environment.GetEnvironmentVariable("DELTAV_HULL_COUNT") == "1";
            int initialFoundations = FoundationCount();
            int FoundationCount() => fixture.maps.Sum(m => m.terrainGrid.foundationGrid.Count(t => t != null));
            var foundationLossByTick = counting ? new int[samples] : null;
            var wallLossByTick = counting ? new int[samples] : null;
            var travelByTick = counting ? new double[samples] : null;
            var deferredByTick = counting ? new long[samples] : null;
            var origins = counting ? new Vector3[fixture.frames.Count] : null;
            int remainingFoundations = initialFoundations;
            int destroyedWalls = 0;
            long allocated=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();fixture.Prepare();foreach(var frame in fixture.frames)ShipPhysics.GetReport(frame);watch.Stop();
            double cold=watch.Elapsed.TotalMilliseconds;long coldBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            int objects=universe.objects.Count;
            long initialRequests = universe.Projectiles.HullRequests;
            var times=new double[samples];var bytes=new long[samples];var damage=new long[samples];
            int g0=GC.CollectionCount(0),g1=GC.CollectionCount(1),g2=GC.CollectionCount(2);
            for(int tick=0;tick<samples;tick++)
            {
                long beforeDeferred = hullBenchDeferred;
                if (counting)
                    for (int i = 0; i < origins.Length; i++) origins[i] = fixture.frames[i].pose.position;
                allocated=GC.GetAllocatedBytesForCurrentThread();long before=hullBenchDamageCalls;
                watch.Restart();fixture.Step();watch.Stop();
                times[tick]=watch.Elapsed.TotalMilliseconds;bytes[tick]=GC.GetAllocatedBytesForCurrentThread()-allocated;
                damage[tick]=hullBenchDamageCalls-before;
                if (!counting) continue;
                int decks = FoundationCount(), walls = fixture.walls.Count(w => w.Destroyed);
                foundationLossByTick[tick] = remainingFoundations - decks;
                wallLossByTick[tick] = walls - destroyedWalls;
                remainingFoundations = decks;
                destroyedWalls = walls;
                deferredByTick[tick] = hullBenchDeferred - beforeDeferred;
                for (int i = 0; i < origins.Length; i++)
                    travelByTick[tick] += OrbitSphere.ArcCells(origins[i], fixture.frames[i].pose.position);
            }
            var sorted=times.OrderBy(v=>v).ToArray();
            long lost=fixture.walls.Sum(w=>(long)((mode=="breach"?20:100000)-Math.Max(0,w.HitPoints)));
            hullBenchContacts = universe.Projectiles.HullRequests - initialRequests;
            Console.WriteLine("HULL_RESULT "+JsonSerializer.Serialize(new {key,count,shape,mode,samples,coldMs=cold,coldBytes,objects,
                firstTickMs=times[0],episodeMs=cold+times.Sum(),meanMs=times.Average(),afterFirstMs=times.Skip(1).Average(),p95Ms=HullQuantile(sorted,.95),p99Ms=HullQuantile(sorted,.99),maxMs=sorted[^1],
                allocatedBytes=bytes.Sum(),gc0=GC.CollectionCount(0)-g0,gc1=GC.CollectionCount(1)-g1,gc2=GC.CollectionCount(2)-g2,
                damageCalls=damage.Sum(),sweeps=counting?hullBenchSweeps:0,contacts=counting?hullBenchContacts:0,
                gridRays=hullBenchRays, impulseContacts=hullBenchImpulses, crushBatches=hullBenchBatches,
                projectileQueries=hullBenchQueries, deferredLayers=hullBenchDeferred,
                travel=counting ? (double?)travelByTick.Sum() : null,
                foundationLossByTick,wallLossByTick,travelByTick,deferredByTick,
                foundations = initialFoundations - FoundationCount(),
                walls = fixture.walls.Count(w => w.Destroyed),
                stops = counting ? (int?)hullBenchStops : null,
                progress = counting ? (double?)(samples - hullBenchLostTicks) : null,
                lostHp=lost,times,bytes,
                final=fixture.frames.Select(f=>new[]{f.pose.velocity.x,f.pose.velocity.y,f.pose.angularVelocity}).ToArray()}));
            Rand.PopState();
        }


    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class MothershipEngagementFixture
    {
        readonly record struct Mount(string Def, IntVec3 Cell, Rot4 Facing, int Hp, int Index);
        readonly Dictionary<string, XElement> definitions = new(StringComparer.Ordinal);
        readonly Dictionary<string, ThingDef> things = new(StringComparer.Ordinal);
        readonly Dictionary<string, DamageDef> damage = new(StringComparer.Ordinal);
        readonly Mount[] mounts;
        readonly IntVec3[] foundations;
        readonly IntVec3 translation;
        readonly int width;
        readonly UniverseManager universe;
        readonly Faction player, enemy;

        internal MothershipEngagementFixture(UniverseManager universe, Faction player, Faction enemy, string game)
        {
            this.universe = universe; this.player = player; this.enemy = enemy;
            var xml = XDocument.Load("Common/Prefabs/Special/MotherShip.xml");
            var root = xml.Root;
            foundations = root.Element("foundations").Elements().Select(e => IntVec3.FromString((string)e.Element("pos"))).ToArray();
            mounts = root.Element("thingStates").Elements().Select((e,i) => new Mount(
                (string)e.Element("def"), IntVec3.FromString((string)e.Element("pos")),
                ParseHelper.FromString<Rot4>((string)e.Element("rotation")),
                (int)e.Element("hitPoints"), i))
                .Where(e => e.Def.StartsWith("DeltaV_Weapon_", StringComparison.Ordinal)).ToArray();
            Check(foundations.Length == 2000 && foundations.Distinct().Count() == foundations.Length,
                "Mothership has 2,000 unique foundation locations");
            Check(mounts.Count(x => x.Def == "DeltaV_Weapon_RailgunAccelerator") == 10,
                "Mothership has ten physical railgun segments");
            var cells = foundations.Concat(mounts.Select(x => x.Cell)).ToArray();
            translation = new IntVec3(6 - cells.Min(x => x.x), 0, 6 - cells.Min(x => x.z));
            width = Math.Max(cells.Max(x => x.x) + translation.x, cells.Max(x => x.z) + translation.z) + 8;
            LoadDefinitions(game);
            ThingDefOf.PilotConsole = TestBuildingDef("MothershipFixtureConsole");
            ThingDefOf.GravEngine = TestBuildingDef("MothershipFixtureEngine");
            DeltaVDefOf.DeltaV_Weapon_MissileLauncher = Def("DeltaV_Weapon_MissileLauncher");
            ThingDefOf.Wall = TestBuildingDef("Wall");
            DamageDefOf.Bullet = Damage("Bullet");
            var factions = (FactionManager)Raw(typeof(FactionManager));
            Set(factions,"ofPlayer",player); Set(Find.World,"factionManager",factions);
            Set(player,"relations",new List<FactionRelation>{new() { other=enemy, kind=FactionRelationKind.Hostile }});
            Set(enemy,"relations",new List<FactionRelation>{new() { other=player, kind=FactionRelationKind.Hostile }});
            foreach (var entry in definitions.Values.Where(e => e.Name.LocalName == "DeltaV.SpaceImpactEffectDef"))
                DefDatabase<SpaceImpactEffectDef>.Add((SpaceImpactEffectDef)Populate(new SpaceImpactEffectDef(), entry));
            SpaceImpactEffects.ResetCache();
        }

        void LoadDefinitions(string game)
        {
            var paths = Directory.GetFiles("1.6/Defs","*.xml",SearchOption.AllDirectories)
                .Concat(new[]{"Core","Odyssey"}.SelectMany(d => Directory.GetFiles(Path.Combine(game,"Data",d,"Defs"),
                    "*.xml",SearchOption.AllDirectories)));
            var nodes = new List<XmlNode>();
            XmlInheritance.Clear();
            foreach (var path in paths.OrderBy(p=>p,StringComparer.Ordinal))
            {
                var doc = new XmlDocument();
                doc.Load(path);
                if (doc.DocumentElement?.Name != "Defs") continue;
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element) continue;
                    if (node.Name != "ThingDef" && node.Name != "DamageDef"
                        && node.Name != "DeltaV.SpaceImpactEffectDef") continue;
                    if (node.Attributes["MayRequire"] is XmlAttribute requires)
                    {
                        if (requires.Value.Split(',').Any(x=>!new[]{"ludeon.rimworld","ludeon.rimworld.odyssey"}
                            .Contains(x.Trim(),StringComparer.OrdinalIgnoreCase))) continue;
                        node.Attributes.Remove(requires);
                    }
                    XmlInheritance.TryRegister(node,null);
                    if (node["defName"] != null) nodes.Add(node);
                }
            }
            XmlInheritance.Resolve();
            foreach (XmlNode node in nodes)
            {
                var name = node["defName"].InnerText;
                if (!name.StartsWith("DeltaV_",StringComparison.Ordinal) && node.Name != "DamageDef")
                    continue;
                definitions[name] = XElement.Parse(XmlInheritance.GetResolvedNodeFor(node).OuterXml);
            }
            XmlInheritance.Clear();
        }

#if MOTHERSHIP_OFFLINE
        internal void CheckSharedDamageCases()
        {
            var table = new MothershipDamageSurface();
            ThingDef Projectile(string name, bool penetrates)
            {
                var projectile = TestBuildingDef(name);
                projectile.comps.Add(new CompProperties_SpaceProjectile());
                DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef
                {
                    defName=name+"Impact", projectileDefs=new List<ThingDef>{projectile}, buildingHitChance=1,
                    defenderEffect=new ImpactOutcomePart
                    {
                        kind=ImpactEffectKind.DamageMap, damage=60, damageDef=DamageDefOf.Bullet,
                        penetrates=penetrates, incidenceMinFactor=1
                    }
                });
                return projectile;
            }
            var first = Projectile("MothershipExtensionFirst", false);
            var clone = Projectile("MothershipExtensionSameRule", false);
            var piercing = Projectile("MothershipExtensionPiercing", true);
            SpaceImpactEffects.ResetCache();

            MothershipDamageSurface.Curve Curve(ThingDef projectile)
            {
                var part=SpaceImpactEffects.Resolve(projectile,null).defenderEffect;
                return table.Get(MothershipDamageSurface.Mechanism.Resolve(part,1),part.damage);
            }
            Check(ReferenceEquals(Curve(first),Curve(clone)) && !ReferenceEquals(Curve(first),Curve(piercing)),
                "extension Defs share a damage curve by resolved rule, with penetration distinguished");

            var callbacks=new MothershipForecastTable.DurabilityIndex();
            var firstPath=new MothershipForecastTable.Path();
            var secondPath=new MothershipForecastTable.Path();
            var evenPath=new MothershipForecastTable.Path();
            var thinLayer=new object();
            var thickLayer=new object();
            var shieldLayer=new object();
            callbacks.Register(thinLayer,1,firstPath);
            callbacks.Register(thickLayer,1999,firstPath);
            callbacks.Register(thickLayer,1999,firstPath);
            callbacks.Register(thickLayer,1999,secondPath);
            callbacks.Register(new object(),1000,evenPath);
            callbacks.Register(new object(),1000,evenPath);
            var unevenResponse=MothershipDamageSurface.AtAverage(20,2400,0,firstPath.TotalHp,firstPath.Layers);
            var evenResponse=MothershipDamageSurface.AtAverage(20,2400,0,evenPath.TotalHp,evenPath.Layers);
            Check(firstPath.TotalHp==2000 && firstPath.Layers==2 && unevenResponse==evenResponse &&
                unevenResponse==(2000f,240f),
                "one plus 1999 HP and two 1000 HP layers read the same multi-shot surface entry");
            Check(callbacks.Notify(thickLayer,1500)==2 && firstPath.TotalHp==1501 &&
                secondPath.TotalHp==1500 && firstPath.Layers==2 && secondPath.Layers==1,
                "wall damage updates both paths and keeps layer counts");
            Check(callbacks.Notify(thickLayer,0)==2 && callbacks.Notify(thickLayer,0)==0 &&
                firstPath.TotalHp==1 && firstPath.Layers==1 && secondPath.TotalHp==0 && secondPath.Layers==0,
                "wall destruction removes its remaining HP and one layer exactly once");
            callbacks.Register(shieldLayer,0,firstPath);
            callbacks.Register(shieldLayer,0,secondPath);
            Check(callbacks.Notify(shieldLayer,40)==2 && callbacks.Notify(shieldLayer,40)==0 &&
                firstPath.TotalHp==41 && firstPath.Layers==2 && secondPath.TotalHp==40 && secondPath.Layers==1,
                "recovering a registered empty shield restores its layer on each path");
            callbacks.Notify(shieldLayer,10);
            Check(firstPath.TotalHp==11 && secondPath.TotalHp==10 && firstPath.Layers==2,
                "debiting a shield adjusts HP without removing the active layer");
            callbacks.Notify(shieldLayer,0);
            callbacks.Notify(shieldLayer,25);
            Check(firstPath.TotalHp==26 && firstPath.Layers==2 && secondPath.TotalHp==25 && secondPath.Layers==1,
                "depleting and recharging a shield updates each registered path");

            var map = TestMap(99100,32);
            var defender = new ManeuverMotionObject(99100,map,new SpacePose(Vector3.forward,0)) { faction=enemy };
            var bridge=AddTestConsole(map,enemy,99109);
            MoveCombatFixture(bridge.parent,new IntVec3(20,0,5));
            var hull=new CombatHull(bridge.Ship);
            defender.member=hull;
            universe.objects.Add(defender);
            Building Wall(int x, int z, int hp, int id)
            {
                var wall=AddTestBuilding(map,enemy,id,TestBuildingDef("sharedDamageWall"+id),new EmptyComp());
                MoveCombatFixture(wall,new IntVec3(x,0,z));
                SetStaticFixtureHealth(wall,hp);
                hull.cells.Add(wall.Position);
                return (Building)wall;
            }
            var equal=new[]{Wall(12,9,50,99101),Wall(14,9,50,99102)};
            var mixed=new[]{Wall(12,11,30,99103),Wall(14,11,70,99104)};
            var thin=new[]{Wall(12,15,10,99105),Wall(14,15,10,99106),Wall(16,15,10,99107),Wall(18,15,10,99108)};
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
            var reference = new CombatDamageForecast(defender);
            void Compare(string name, ThingDef projectile, int count, Building[] walls)
            {
                reference.ResetShot();
                var initial=walls.Select(w=>(float)w.HitPoints).ToArray();
                var hp=(float[])initial.Clone();
                int firstBreach=0;
                for (int shot=1;shot<=count;shot++)
                {
                    reference.Fire(new Vector2(2.5f,walls[0].Position.z+.5f),Vector2.right,24,projectile,1,60);
                    float budget=60;
                    bool penetrates=SpaceImpactEffects.Resolve(projectile,null).defenderEffect.penetrates;
                    for (int i=0;i<hp.Length && budget>0;i++)
                    {
                        if (hp[i]==0) continue;
                        var response=MothershipDamageSurface.Evaluate(penetrates,budget,1,hp[i],1);
                        hp[i]=response.RemainingWall;
                        budget=response.Through;
                    }
                    if (firstBreach==0 && hp[0]==0) firstBreach=shot;
                    Check(walls.Select(reference.RemainingHP).SequenceEqual(hp),
                        $"{name} exact wall HP and broken layers after arrival {shot}");
                }
                if (initial.All(x=>x==initial[0]))
                {
                    var response=Curve(projectile).At(count,initial[0],initial.Length);
                    Check(response.Broken==hp.Count(x=>x==0) &&
                        response.RemainingWall==hp.FirstOrDefault(x=>x>0) &&
                        (firstBreach==0 || response.FirstBreach==firstBreach),
                        $"{name} three-dimensional response matches repeated native arrivals");
                }
            }
            Compare("equal walls",first,2,equal);
            Compare("mixed walls",clone,2,mixed);
            Compare("thick low-HP layers slow cadence",first,2,thin);
            Compare("thick low-HP layers fast cadence",first,4,thin);
            Compare("extension piercing",piercing,2,mixed);
            var emitterMap=TestMap(99130,32);
            var emitters=new[]{99131,99132}.Select(id=>
            {
                var weapon=new CompSpaceWeapon { props=new CompProperties_SpaceWeapon { cooldownTicks=20 } };
                AddTestBuilding(emitterMap,player,id,TestBuildingDef("sharedDamageEmitter"+id),weapon);
                return weapon;
            }).ToArray();
            var shots=emitters.Reverse().Select((weapon,index)=>
            {
                var from=new Vector2(2.5f,11.5f);
                var projectile=index==0 ? clone : first;
                var aim=new WeaponAimInfo(default,from,projectile,60,0,24);
                return new CombatEngagementEvaluation.OfflineShot(true,reference,mixed[0],new Vector2(12.5f,11.5f),
                    from,Vector2.right,24,60,0,new SpaceWeaponParameters(null,aim,default,cycleTicks:20),weapon);
            }).ToArray();
            var ordered=MothershipSharedDamageReference.Schedule(shots,20);
            Check(ordered.Length==2 && ordered.All(x=>x.Tick==0) && ordered[0].Slot==1 &&
                MothershipSharedDamageReference.Schedule(shots,21).Length==4,
                "same-tick weapons use stable order and the end of the time window is exclusive");
            Check(MothershipWeaponOutputTable.Covers(4, 8, -1, Vector2.right, new Vector2(4, 0), out _) &&
                MothershipWeaponOutputTable.Covers(4, 8, -1, Vector2.right, new Vector2(8, 0), out _) &&
                !MothershipWeaponOutputTable.Covers(4, 8, -1, Vector2.right, new Vector2(3.99f, 0), out _) &&
                !MothershipWeaponOutputTable.Covers(4, 8, -1, Vector2.right, new Vector2(8.01f, 0), out _),
                "simple gate applies the true minimum and maximum range before looking up distance bins");
            Check(MothershipWeaponOutputTable.Covers(0, 10, 0, Vector2.right, Vector2.up, out _) &&
                !MothershipWeaponOutputTable.Covers(0, 10, 0, Vector2.right, new Vector2(-.01f, 1), out _) &&
                MothershipWeaponOutputTable.Covers(0, 10, 1, Vector2.right, Vector2.right, out _) &&
                !MothershipWeaponOutputTable.Covers(0, 10, 1, Vector2.right, Vector2.up, out _),
                "simple gate includes the arc edge and rejects sideways fixed-axis fire");
            Check(MothershipWeaponOutputTable.Sample(0, 20, 20, 20, 1, true, 60, 60, false).Arrivals == 0 &&
                MothershipWeaponOutputTable.Sample(0, 21, 20, 20, 1, true, 60, 60, false).Arrivals == 1 &&
                MothershipWeaponOutputTable.Sample(1, 6, 0, 20, 1, false, 60, 60, false).Arrivals == 0 &&
                MothershipWeaponOutputTable.Sample(1, 7, 0, 20, 1, false, 60, 60, false).Arrivals == 1,
                "cooldown and nominal flight time use the exclusive damage-window boundary");
            var staticOutput = MothershipWeaponOutputTable.Sample(1, 47, 0, 20, 1, false, 60, 60, true);
            Check(staticOutput.Arrivals == 3 && staticOutput.Budget == 180 && staticOutput.Penetrating == 180,
                "stationary output at the bin center precomputes both the volley budget and penetrating share");
            var volley=MothershipSharedDamageReference.Run(shots,20,null);
            Check(volley[1].Damage==30 && volley[0].Damage==60 &&
                reference.RemainingHP(mixed[0])==0 && reference.RemainingHP(mixed[1])==10,
                "two weapons share the previous shot's wall damage within the same tick");

            var guardPatch=new HarmonyLib.Harmony("DeltaV.Regression.SharedDamageGuard");
            guardPatch.Patch(typeof(CompProjectileInterceptor).TypeInitializer,
                prefix:new HarmonyLib.HarmonyMethod(typeof(Program),nameof(BattleSceneOnly)));
            try
            {
                AddStaticFleetShield(map,enemy,99120,32);
                var projector=map.listerThings.AllThings.OfType<Building>().Single(x=>x.thingIDNumber==99120);
                hull.cells.Add(projector.Position);
                foreach (var projectile in new[]{first,clone,piercing})
                    DefDatabase<SpaceImpactEffectDef>.Add(new SpaceImpactEffectDef
                    {
                        defName=projectile.defName+"Shield",projectileDefs=new List<ThingDef>{projectile},
                        targetThingDefs=new List<ThingDef>{projector.def},
                        defenderEffect=new ImpactOutcomePart { kind=ImpactEffectKind.ShieldDrain,damage=30,incidenceMinFactor=1 }
                    });
                SpaceImpactEffects.ResetCache();
                SpaceCollision.ClearCaches();
                Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
                var shared=new CombatDamageForecast(defender);
                float[] paid=new float[3];
                var shield = projector.GetComp<CompSpaceShield>();
                float remaining=(float)shield.RegularCapacity;
                var guardPaths = Enumerable.Range(0,3).Select(_=>new MothershipForecastTable.Path()).ToArray();
                foreach (var path in guardPaths) callbacks.Register(shield,remaining,path);
                foreach (int i in new[]{0,1,2})
                {
                    var from=new Vector2(2.5f,23.5f+i);
                    var projectile=i==0 ? first : clone;
                    paid[i]=shared.Fire(from,Vector2.right,27,projectile,1,60).guard;
                    remaining-=paid[i];
                    callbacks.Notify(shield,remaining);
                    Check(guardPaths.All(path=>path.TotalHp==remaining && path.Layers==(remaining>0?1:0)),
                        $"shared guard debit updates all registered paths after impact {i+1}");
                }
                Check(paid.SequenceEqual(new[]{30f,20f,0f}),
                    $"distinct incoming paths and extension rounds share guard balance ({string.Join(",",paid)})");
                callbacks.Notify(shield,25);
                Check(guardPaths.All(path=>path.TotalHp==25 && path.Layers==1),
                    "one recharge notification restores the shield layer on every registered path");
            }
            finally { guardPatch.UnpatchAll(guardPatch.Id); }
        }
#endif

        object Populate(object obj, XElement data)
        {
            foreach (var child in data.Elements())
            {
                var field = obj.GetType().GetField(child.Name.LocalName, BindingFlags.Public|BindingFlags.Instance);
                if (field == null) continue;
                if (TryValue(field.FieldType, child, out var value)) field.SetValue(obj,value);
            }
            return obj;
        }

        bool TryValue(Type type, XElement node, out object value)
        {
            value = null;
            string text = node.Value.Trim();
            if (type == typeof(string)) value = text;
            else if (type == typeof(bool)) value = bool.Parse(text);
            else if (type == typeof(int)) value = int.Parse(text,CultureInfo.InvariantCulture);
            else if (type == typeof(float)) value = float.Parse(text,CultureInfo.InvariantCulture);
            else if (type.IsEnum) value = Enum.Parse(type,text.Replace("|",","));
            else if (type == typeof(Vector2)) value = ParseHelper.FromString<Vector2>(text);
            else if (type == typeof(IntVec3)) value = IntVec3.FromString(text);
            else if (type == typeof(ThingDef)) value = Def(text);
            else if (type == typeof(DamageDef)) value = Damage(text);
            else if (type == typeof(ImpactOutcomePart) || type == typeof(ImpactPenetrationLayer))
                value = Populate(Activator.CreateInstance(type),node);
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type item = type.GetGenericArguments()[0];
                if (item != typeof(ThingDef) && item != typeof(IntVec3) && item != typeof(ImpactPenetrationLayer))
                    return false;
                var list = (IList)Activator.CreateInstance(type);
                foreach (var part in node.Elements())
                    if (TryValue(item, part, out var entry)) list.Add(entry);
                value = list;
            }
            else return false;
            return true;
        }

        DamageDef Damage(string name)
        {
            if (damage.TryGetValue(name,out var found)) return found;
            var def = new DamageDef { defName=name };
            damage.Add(name,def);
            if (definitions.TryGetValue(name,out var node)) Populate(def,node);
            return def;
        }

        ThingDef Def(string name)
        {
            if (things.TryGetValue(name,out var found)) return found;
            if (!definitions.TryGetValue(name,out var data))
            {
                var external = TestBuildingDef(name);
                things.Add(name, external);
                return external;
            }
            var def = TestBuildingDef(name);
            things.Add(name,def);
            if (data.Element("size") is XElement size) def.size=ParseHelper.FromString<IntVec2>(size.Value);
            def.rotatable = (bool?)data.Element("rotatable") ?? true;
            def.useHitPoints = (bool?)data.Element("useHitPoints") ?? true;
            def.fillPercent = (float?)data.Element("fillPercent") ?? 0f;
            foreach (var comp in data.Element("comps")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                CompProperties props=(string)comp.Attribute("Class") switch
                {
                    "DeltaV.CompProperties_SpaceWeapon" => new CompProperties_SpaceWeapon(),
                    "DeltaV.CompProperties_SpaceProjectile" => new CompProperties_SpaceProjectile(),
                    "DeltaV.CompProperties_SpaceRailgun" => new CompProperties_SpaceRailgun(),
                    "DeltaV.CompProperties_SpaceWeaponTraverse" => new CompProperties_SpaceWeaponTraverse(),
                    "DeltaV.CompProperties_SpaceWeaponMagazine" => new CompProperties_SpaceWeaponMagazine(),
                    _ => null
                };
                if (props != null) def.comps.Add((CompProperties)Populate(props,comp));
            }
            return def;
        }

        internal (ManeuverMotionObject Own,ManeuverMotionObject Enemy) Build(int scenario)
        {
            universe.Controls.Reset(); universe.objects.Clear(); Find.Maps.Clear();
            ShipRegistry.ClearCaches(); ShipPhysics.ClearCache(); SpaceCollision.ClearCaches();
            Set(universe,"objectById",null); Set(universe,"objectByMap",null);
            var settings = new[] { (new Vector2(137,0), 0f, Mathf.PI),
                (new Vector2(103,81), .17f, -.67f), (new Vector2(190,-18), -.04f, 2.95f),
                (new Vector2(105,-93), -.55f, 2.6f) };
            var state = settings[scenario];
            var own = Ship(100000,player,new SpacePose(OrbitSphere.Move(Vector3.forward,state.Item1),state.Item2),false);
            var foe = Ship(200000,enemy,new SpacePose(Vector3.forward,state.Item3),scenario==3);
            Current.Game.GetComponent<UniverseManager>().ShipStructure.InvalidateAll();
            Set(universe,"objectById",null); Set(universe,"objectByMap",null);
            universe.Controls.For(own.member); universe.Controls.For(foe.member);
            return (own,foe);
        }

        internal (ManeuverMotionObject Own, ManeuverMotionObject Enemy) BuildRailgunFlight(bool mirrored)
        {
            Set(Find.TickManager, "ticksGameInt", 0);
            var pair = Build(0);
            var bodies = new[] { pair.Own, pair.Enemy };
            for (int i = 0; i < bodies.Length; i++)
            {
                var body = bodies[i];
                var other = bodies[1 - i];
                var rail = body.member.Weapons.Single(weapon => weapon.parent.def.defName == "DeltaV_Weapon_RailgunLoader");
                foreach (var weapon in body.member.Weapons)
                    if (weapon != rail) weapon.aimMode = SpaceWeaponAimMode.PointDefense;
                var firing = ShipRegistry.ShipPose(body.member);
                var target = ShipRegistry.ShipPose(other.member);
                var line = OrbitSphere.Offset(firing.position, target.position);
                float mount = ShipOrientation.AngleFromRot4(rail.parent.Rotation);
                var pose = body.pose;
                pose.angle = SpaceTransform.WrapAngle(Mathf.Atan2(line.y, line.x) - mount
                    + (mirrored ? 1f : -1f) * (i == 0 ? 24f : -24f) * Mathf.Deg2Rad);
                body.pose = pose;
                var offset = OrbitSphere.Offset(target.position, firing.position);
                var strategy = universe.Controls.For(body.member).FireControl.Strategy(body.member);
                var rule = strategy.BuiltInManeuver;
                rule.tactic.behavior = CombatTactic.HoldRange;
                rule.tactic.facing = CombatFacing.Automatic;
                rule.tactic.distance = offset.magnitude;
                rule.tactic.bearing = CombatBearing.World;
                rule.tactic.bearingDegrees = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                strategy.maneuverRules.Insert(0, rule);
                rail.safeFire = rail.WeaponProps.safeFireByDefault;
            }
            return pair;
        }

        ManeuverMotionObject Ship(int id,Faction faction,SpacePose pose,bool damaged)
        {
            var map=TestMap(id,width);
            map.info.isPocketMap = true;
            // A headless pocket has no WorldObject renderer or occupied world tile.
            map.info.parent = null;
            // The existing MPC fixture supplies deterministic drive facts to both ships.
            var frame=new ManeuverMotionObject(id,map,pose,productionDrive:true) { faction=faction };
            map.terrainGrid.topGrid[0]=null;
            var console=AddTestConsole(map,faction,id+1);
            MoveCombatFixture(console.parent,new IntVec3(width/2,0,width/2));
            SetStaticFixtureHealth(console.parent,150);
            console.Vessel.Combat.weaponsArmed=true;
            var foundation = FixtureFoundation("engagementMothershipDeck");
            using (universe.ShipStructure.BeginChanges())
                foreach (var cell in foundations)
                    if (!damaged || cell.x >= 10 || cell.z <= 30)
                        SetFixtureFoundation(map, cell + translation, foundation, console.shipId);
            var battery=new CompPowerBattery(); Set(battery,"storedEnergy",1_000_000f);
            var net=(PowerNet)Raw(typeof(PowerNet)); net.batteryComps=new List<CompPowerBattery>{battery};
            foreach (var mount in mounts)
            {
                if (damaged && mount.Cell.x<10 && mount.Cell.z>30) continue;
                var def=Def(mount.Def);
                var place = CreateFixtureBuilding(def, id + 100 + mount.Index, mount.Cell + translation, mount.Facing, faction);
                SetStaticFixtureHealth(place,mount.Hp);
                var power=new CompPowerTrader { transNet=net }; Set(power,"powerOnInt",true);
                AddFixtureComponents(place, power);
                // These engagement samples provide the input projectile directly.
                if (place.TryGetComp<CompSpaceWeaponMagazine>() is { } magazine) RemoveFixtureComponent(place, magazine);
                PlaceFixtureBuilding(place, map);
                if (place.Position!=mount.Cell+translation || !place.Rotation.Equals(mount.Facing))
                    throw new InvalidOperationException($"Fixture changed a prefab mount: {mount.Def} at {mount.Cell}.");
            }
            universe.objects.Add(frame);
            frame.member=frame.Ships.Single();
            return frame;
        }
    }
}

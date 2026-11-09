using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text.Json;
using DeltaV;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

internal static partial class Program
{
    // Only the native static UI icons/materials require a Unity renderer.
    static bool SkipFuelGraphics() => false;
    static bool RangeOwner(ref int __result) { __result=1; return false; }
    static void RangePowerNet(List<Building> transmitters, string path)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(path));
        var isolated=json.RootElement.EnumerateArray().Where(r=>r.GetProperty("name").GetString()=="Battery"
            && r.GetProperty("stateOverrides").TryGetProperty("storedPower",out var power) && power.GetString()=="0")
            .Select(r=>new IntVec3(r.GetProperty("root")[0].GetInt32()+20,0,r.GetProperty("root")[1].GetInt32()+20)).ToHashSet();
        Check(isolated.Count==5,"five empty penetration targets");
        foreach(var b in transmitters.Where(b=>isolated.Contains(b.Position)))
            Check(PowerNetMaker.NewPowerNetStartingFrom(b).transmitters.Count==1,"test battery cannot recharge from live grid");
        var live=PowerNetMaker.NewPowerNetStartingFrom(transmitters.First(b=>!isolated.Contains(b.Position)));
        Check(live.transmitters.Count==transmitters.Count-isolated.Count,"native target working grid remains connected");
    }
    static void RangeLayout(List<Building> buildings, string recordsPath)
    {
        string repo=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(recordsPath),"../../.."));
        var defs=new XmlDocument(); defs.Load(Path.Combine(repo,"1.6","Defs","SpaceCombat","DeltaV_SpaceWeapons.xml"));
        XmlNode Def(string name) => defs.SelectSingleNode("/Defs/ThingDef[defName='"+name+"']");
        var attachments=buildings.Where(b=>b.def.defName=="DeltaV_Building_AutoAimPluginII").ToList();
        foreach(var turret in buildings.Where(b=>b.def.defName.EndsWith("MainMedium")))
        {
            var slot=IntVec3.FromString(Def(turret.def.defName).SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/attachmentSlots/li/cell").InnerText);
            Check(attachments.Count(a=>a.Position==SpaceWeaponGeometry.RotatedCell(turret,slot)
                && a.Rotation==turret.Rotation)==1,"native rotated attachment slot contains exactly one advanced auto-aim");
        }
        var loader=buildings.Single(b=>b.def.defName=="DeltaV_Weapon_RailgunLoader");
        var segments=buildings.Where(b=>b.def.defName=="DeltaV_Weapon_RailgunAccelerator").ToList();
        foreach(var definition in new[] {loader.def,segments[0].def})
        for(int rotation=0;rotation<4;rotation++)
        {
            var part=Raw<Building>(); part.def=definition;
            Set(part,"mapIndexOrState",(sbyte)-1);
            Set(part,"positionInt",new IntVec3(50,0,50)); Set(part,"rotationInt",new Rot4(rotation));
            var footprint=part.OccupiedRect();
            Check(footprint.Count()==3 && SpaceWeaponGeometry.FootprintCenter(part)==new Vector2(50.5f,50.5f),
                "three-cell rail stays centered in every native rotation");
            foreach(XmlNode slot in Def(definition.defName).SelectNodes("comps/li/attachmentSlots/li/cell"))
                Check(footprint.Contains(SpaceWeaponGeometry.RotatedCell(part,IntVec3.FromString(slot.InnerText))),
                    "rotated rail attachment stays inside its host");
            if(definition==loader.def)
                Check(!footprint.Contains(part.InteractionCell)
                    && part.InteractionCell==SpaceWeaponGeometry.RotatedCell(part,new IntVec3(-1,0,-1)),
                    "native operator cell follows the left terminal behind the loader");
        }
        var segmentNode=Def(segments[0].def.defName).SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']");
        int id=900;
        foreach(var b in segments)
        {
            b.thingIDNumber=id++;
            var segment=new CompSpaceRailgun { parent=b,props=new CompProperties_SpaceRailgun {
                nextRootOffset=IntVec3.FromString(segmentNode["nextRootOffset"].InnerText),
                outputOffset=IntVec3.FromString(segmentNode["outputOffset"].InnerText),
                kineticEnergyBonus=float.Parse(segmentNode["kineticEnergyBonus"].InnerText,System.Globalization.CultureInfo.InvariantCulture) } };
            Set(b,"comps",new List<ThingComp> {segment});
        }
        var starts=Def(loader.def.defName).SelectNodes("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/acceleratorStarts/li");
        var weapon=new CompSpaceWeapon { parent=loader,props=new CompProperties_SpaceWeapon {
            acceleratorStarts=starts.Cast<XmlNode>().Select(n=>IntVec3.FromString(n.InnerText)).ToList(),
            allowedAcceleratorDefs=segments.Select(b=>b.def).ToList() } };
        // Only ownership registration is substituted; trace and muzzle geometry are production code.
        var harmony=new Harmony("DeltaV.TutorialRegression.RangeLayout");
        var owner=AccessTools.Method(typeof(SpaceWeaponGeometry),nameof(SpaceWeaponGeometry.OwnerIdOf));
        harmony.Patch(owner,prefix:new HarmonyMethod(typeof(Program),nameof(RangeOwner)));
        try
        {
            var chain=CompSpaceRailgun.TraceConnections(weapon);
            Check(chain.segments.Count==9 && chain.kineticEnergyBonus==1012500f,"production trace reaches nine accelerators");
            var cells=segments.Append(loader).SelectMany(b=>b.OccupiedRect()).Distinct().ToList();
            Check(cells.Count==30 && cells.Max(c=>c.x)-cells.Min(c=>c.x)+1==10
                && cells.Max(c=>c.z)-cells.Min(c=>c.z)+1==3,"native rail footprint is ten cells long and three wide");
            var last=chain.segments.Last().parent.OccupiedRect();
            var muzzleCell=new IntVec3(Mathf.FloorToInt(chain.muzzleMap.Value.x),0,Mathf.FloorToInt(chain.muzzleMap.Value.y));
            Check(chain.muzzleMap.HasValue && chain.muzzleMap.Value.x>=last.maxX+1
                && !buildings.Any(b=>b.def.passability==Traversability.Impassable
                    && b.OccupiedRect().Contains(muzzleCell)),
                "production rail muzzle exits unobstructed beyond the final segment");
        }
        finally {harmony.Unpatch(owner,HarmonyPatchType.Prefix,harmony.Id);}
    }
    sealed class RangeWeaponSave : IExposable
    {
        internal CompSpaceWeapon weapon;
        internal CompGunSupply supply;
        public RangeWeaponSave(Building building)
        {
            weapon = new CompSpaceWeapon { parent=building, props=new CompProperties_SpaceWeapon { range=160f, fireArcDeg=360f } };
            supply = new CompGunSupply { parent=building,
                props=new CompProperties_GunSupply { kind=GunSupplyKind.Heads, fuelCapacity=1200, showAllowAutoRefuelToggle=true } };
        }
        public void ExposeData() { weapon.PostExposeData(); supply.PostExposeData(); }
    }
    sealed class TerminalSave : IExposable
    {
        internal CompTutorialShieldTerminal terminal = new CompTutorialShieldTerminal();
        public void ExposeData() => terminal.PostExposeData();
    }

    sealed class RangeShieldSave : IExposable
    {
        internal CompSpaceShield shield;
        public RangeShieldSave(Building parent) { shield=new CompSpaceShield {parent=parent,props=new CompProperties_SpaceShield()}; }
        public void ExposeData() => shield.PostExposeData();
    }

    static void RangeLaserSightlines(Game game, XmlDocument platform, XmlDocument target, List<CompSpaceShield> shields)
    {
        var layer=Raw<PlanetLayer>();
        var tiles=AccessTools.Field(typeof(PlanetLayer),"tileIDToNeighbors_offsets");
        object array=Activator.CreateInstance(tiles.FieldType);Set(array,"m_Length",1);tiles.SetValue(layer,array);
        AccessTools.Field(typeof(OrbitSphere),"cachedWorld").SetValue(null,game.World);
        AccessTools.Field(typeof(OrbitSphere),"cachedLayer").SetValue(null,layer);
        ((Dictionary<PlanetLayer,float>)AccessTools.Field(typeof(OrbitSphere),"RadiusByLayer").GetValue(null))[layer]=100000;
        ((Dictionary<PlanetLayer,float>)AccessTools.Field(typeof(OrbitSphere),"WorldPerCellByLayer").GetValue(null))[layer]=1;
        Map MapFor(XmlDocument doc)
        {
            var map=Raw<Map>();map.info=new MapInfo {Size=ShipPocketMapFactory.CompactSize(IntVec2.FromString(doc.SelectSingleNode("/MapPrefab/prefab/size").InnerText))};return map;
        }
        var source=new MapSpaceObject(8001,MapFor(platform),new SpacePose(Vector3.forward,0));
        var destination=new MapSpaceObject(8002,MapFor(target),SpaceTransform.RigidPointPose(source.pose,new Vector2(120,0),0));
        foreach(string name in new[]{"DeltaV_Weapon_RapidMainMedium","DeltaV_Weapon_HeavyMainMedium","DeltaV_Weapon_LaserMainMedium"})
        foreach(XmlNode entry in platform.SelectNodes("/MapPrefab/thingStates/li[def='"+name+"']"))
        {
        var body=Raw<Building>();body.def=Raw<ThingDef>();body.def.size=IntVec2.FromString(SourceChain(name).Select(n=>n["size"]).First(n=>n!=null).InnerText);
        Set(body,"positionInt",IntVec3.FromString(entry["pos"].InnerText)+new IntVec3(12,0,12));Set(body,"rotationInt",Rot4.East);
        var weaponProperties=sourceDefs[name].SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']");
        var weapon=new CompSpaceWeapon {parent=body,props=new CompProperties_SpaceWeapon {
            turretMuzzleDistance=float.Parse(weaponProperties["turretMuzzleDistance"].InnerText,System.Globalization.CultureInfo.InvariantCulture)}};
        weapon.coverage.arc=float.Parse(entry.SelectSingleNode("state/saveable/combatCoverage/arc").InnerText,System.Globalization.CultureInfo.InvariantCulture);
        var traverse=new CompSpaceWeaponTraverse {parent=body,props=new CompProperties_SpaceWeaponTraverse()};
        Set(body,"comps",new List<ThingComp> {weapon,traverse});
        foreach(var shield in shields)
        {
            shield.GetSegment(out var a,out var b,out _);
            foreach(float fraction in new[]{0.05f,0.5f,0.95f})
            {
            Vector2 point=Vector2.Lerp(a,b,fraction)+new Vector2(12,12);
            Vector2 aim=SpaceTransform.UniverseToMapPoint(source,source.map,SpaceTransform.MapPointToUniverse(destination,destination.map,point));
            Vector2 direction=aim-SpaceWeaponGeometry.FootprintCenter(body);
            Set(traverse,"currentAngle",Mathf.Atan2(direction.y,direction.x));
            Vector2 muzzle=SpaceWeaponGeometry.MuzzleMapPoint(weapon);direction=aim-muzzle;
            Check(weapon.CoverageError(Mathf.Atan2(direction.y,direction.x))==0 && direction.magnitude<150,
                "both turret banks can reach shield centres and ends within restricted firing sector");
            }
        }
        }
    }

    sealed class RangeEntranceSave : IExposable
    {
        internal Building_HackableDoor door;
        internal ThingWithComps terminal;
        public void ExposeData()
        {
            Scribe_Deep.Look(ref door,"door");
            Scribe_Deep.Look(ref terminal,"terminal");
        }
    }

    static void RangeEntrance(XmlDocument platform, Harmony harmony, string directory)
    {
        var content=AccessTools.Method(typeof(ContentFinder<Texture2D>),"Get");
        harmony.Patch(content,prefix:new HarmonyMethod(typeof(Program),nameof(SkipFuelGraphics)));
        try
        {
            var definitions=new XmlDocument(); definitions.Load(Path.Combine(directory,"../../1.6/Defs/Tutorial/DeltaV_TutorialRange.xml"));
            var doorConfig=definitions.SelectSingleNode("/Defs/ThingDef[defName='DeltaV_RangeBlastDoor']");
            Check(doorConfig.SelectSingleNode("comps/li[@Class='CompProperties_Hackable']/onlyRemotelyHackable").InnerText=="true",
                "safety door cannot be hacked locally to skip the briefing");
            var entries=new List<RimExodus.Maps.MapPrefabThingEntry>();
            foreach(string name in new[]{"DeltaV_RangeBlastDoor","DeltaV_RangeTerminal_Introduction"})
            {
                var def=Raw<ThingDef>(); def.defName=name; def.category=ThingCategory.Building;
                def.size=IntVec2.One;def.building=new BuildingProperties();def.comps=new List<CompProperties>();
                if(name=="DeltaV_RangeBlastDoor")
                {
                    def.thingClass=typeof(Building_HackableDoor);
                    def.comps.Add(new CompProperties_Hackable { defence=1800,onlyRemotelyHackable=true });
                }
                else {def.thingClass=typeof(Building);def.comps.Add(new CompProperties_TutorialRangeBriefing());}
                DefDatabase<ThingDef>.Add(def);
                entries.Add(new RimExodus.Maps.MapPrefabThingEntry {def=def,
                    stateXml=platform.SelectSingleNode("/MapPrefab/thingStates/li[def='"+name+"']/state/saveable").OuterXml});
            }
            var things=RimExodus.Maps.MapPrefabThingState.Instantiate(entries,"range entrance",null);
            var door=(Building_HackableDoor)things[0]; var terminal=(ThingWithComps)things[1];
            var ally=new Faction {def=new FactionDef(),loadID=937};Set(door,"factionInt",ally);
            Find.FactionManager.AllFactionsListForReading.Add(ally);
            terminal.GetComp<CompTutorialRangeBriefing>().entranceDoor=door;
            Check(door.Locked && !door.PawnCanOpen(null),"native blast door starts locked");
            Check(!door.Hackable.CanHackNow(null).Accepted,"native remote-only hack gate rejects local hacking");
            foreach(bool unlocked in new[]{false,true})
            {
                if(unlocked) CompTutorialRangeBriefing.UnlockEntrance(door);
                Check(door.Locked!=unlocked && door.Faction==ally,"native remote unlock preserves allied owner");
                var document=new XmlDocument();document.LoadXml(Scribe.saver.DebugOutputFor(new RangeEntranceSave {door=door,terminal=terminal}));
                string path=Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path,"<root><entrance>"+document.DocumentElement.InnerXml+"</entrance></root>");
                    Scribe.loader.InitLoading(path);RangeEntranceSave restored=null;Scribe_Deep.Look(ref restored,"entrance");
                    ((LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler),"loadedObjectDirectory").GetValue(Scribe.loader.crossRefs)).RegisterLoaded(ally);
                    Scribe.loader.FinalizeLoading();
                    Check(restored.door.Locked!=unlocked,"native door lock survives Scribe: "+unlocked);
                    Check(restored.door.Faction==ally,"native door owner survives Scribe");
                    Check(restored.terminal.GetComp<CompTutorialRangeBriefing>().entranceDoor==restored.door,
                        "briefing binds restored door rather than old instance");
                }
                finally {Scribe.ForceStop();File.Delete(path);}
            }
        }
        finally {harmony.Unpatch(content,HarmonyPatchType.Prefix,harmony.Id);}
    }

    static void RangeChecks(Game game, Harmony harmony, string directory)
    {
        LoadedModManager.RunningModsListForReading[0].assemblies.loadedAssemblies.Add(typeof(Program).Assembly);
        harmony.Patch(typeof(CompRefuelable).TypeInitializer,
            prefix:new HarmonyMethod(typeof(Program),nameof(SkipFuelGraphics)));
        Set(game.World.factionManager,"ofPlayer",new Faction { def=new FactionDef { isPlayer=true } });
        var platform = new XmlDocument(); platform.Load(Path.Combine(directory,"Tutorial/WeaponRange.xml"));
        var target = new XmlDocument(); target.Load(Path.Combine(directory,"Tutorial/ShieldTarget.xml"));
        RangeEntrance(platform,harmony,directory);
        var weaponNames = new[] { "DeltaV_Weapon_RapidMainMedium", "DeltaV_Weapon_HeavyMainMedium",
            "DeltaV_Weapon_LaserMainMedium", "DeltaV_Weapon_RailgunLoader" };
        int group=0;
        foreach (string name in weaponNames)
        {
            group++;
            foreach(XmlNode state in platform.SelectNodes("/MapPrefab/thingStates/li[def='"+name+"']/state/saveable"))
            {
            Check(state != null && state["groupNumber"].InnerText == group.ToString(), "four distinct fire-control groups");
            var building=Raw<Building>(); building.def=Raw<ThingDef>(); building.def.defName=name;
            Set(building,"factionInt",new Faction {def=new FactionDef()});
            Set(building,"rotationInt",Rot4.East);
            var doc=new XmlDocument(); doc.LoadXml(state.OuterXml);
            string file=Path.GetTempFileName();
            try
            {
                File.WriteAllText(file,"<root><weapon>"+doc.DocumentElement.InnerXml+"</weapon></root>");
                RangeWeaponSave savedWeapon=null;
                Scribe.loader.InitLoading(file); Scribe_Deep.Look(ref savedWeapon,"weapon",building);
                Scribe.loader.FinalizeLoading();
                var weapon=savedWeapon.weapon;
                Check(weapon.groupNumber==group && weapon.coverage.arc==90f && weapon.EffectiveRange==150f,
                    "native weapon Scribe preserves group and limited firing sector");
                Check(weapon.salvoSet==Enum.Parse<SpaceWeaponSalvoSet>(state["salvoSet"]?.InnerText ?? "Main"),"native Scribe preserves main/secondary bank");
                Check(weapon.CoverageError(0f)==0f && weapon.CoverageError(Mathf.PI)>100f
                    && weapon.CoverageError(46f*Mathf.Deg2Rad)>0f,"station behind platform and off-sector aim rejected");
                Check(!weapon.CompGetGizmosExtra().Any(),"allied weapon has no player settings gizmos");
                if (group!=3)
                {
                    Check(savedWeapon.supply.Fuel>0f,"live ammunition survives native compartment Scribe");
                }
                if (state["gun"]!=null)
                    Check(state["gun"]["def"].InnerText.StartsWith("DeltaV_GroundGun_")
                        && state["gun"].SelectSingleNode("verbTracker/verbs/li").Attributes["Class"].Value=="DeltaV.Verb_SpaceTurretGround",
                        "turret internal gun and verb match current Def family");
            }
            finally { Scribe.ForceStop(); File.Delete(file); }
        }
        }
        Check(platform.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_Weapon_RailgunAccelerator']").Count==9,"railgun has nine real accelerators");
        Check(platform.SelectSingleNode("/MapPrefab/thingStates/li[def='PilotConsole']/state/saveable/combat/fireControlMode").InnerText=="Disabled",
            "platform stores disabled fire control in the native combat record");
        Check(!new ShipCombatState().weaponsArmed
            && new ShipCombatState().fireControlMode==ShipFireControlMode.Automatic,
            "new player ships default to disarmed with automatic fire control preselected");
        var armour=target.SelectNodes("/MapPrefab/thingStates/li[def='Wall' and stuff='Steel']/pos").Cast<XmlNode>()
            .Select(n=>IntVec3.FromString(n.InnerText)).Where(c=>c.x<6).ToList();
        Check(armour.Count==258 && Enumerable.Range(0,43).All(z=>Enumerable.Range(0,6).All(x=>armour.Contains(new IntVec3(x,0,z)))),
            "six uninterrupted steel armour layers including the rail lane");
        Check(platform.SelectNodes("/MapPrefab/thingStates/li[def='Battery']").Count==60,"platform has a finite 36000 Wd bank");
        Check(target.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator']").Count==3,"target has three same-ship shield generators");
        var shields = new List<CompSpaceShield>();
        foreach(XmlNode entry in target.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator']"))
        {
            var b=Raw<Building>(); b.def=Raw<ThingDef>(); Set(b,"mapIndexOrState",(sbyte)-1);
            b.def.size=new IntVec2(3,3);
            Set(b,"positionInt",IntVec3.FromString(entry["pos"].InnerText));
            Set(b,"rotationInt",new Rot4(int.Parse(entry["rotation"].InnerText)));
            string path=Path.GetTempFileName(); RangeShieldSave savedShield=null;
            try
            {
                File.WriteAllText(path,"<root><shield>"+entry.SelectSingleNode("state/saveable").InnerXml+"</shield></root>");
                Scribe.loader.InitLoading(path);Scribe_Deep.Look(ref savedShield,"shield",b);Scribe.loader.FinalizeLoading();
            }
            finally {Scribe.ForceStop();File.Delete(path);}
            var s=savedShield.shield;
            var flick=new CompFlickable { parent=b,props=new CompProperties_Flickable() };
            var fuel=new CompRefuelable { parent=b,props=new CompProperties_Refuelable { fuelCapacity=2000 } };
            Set(fuel,"fuel",777f); Set(s,"regularCapacity",123d); Set(s,"recoveryWaitTicks",200);
            Set(b,"comps",new List<ThingComp> { s,flick,fuel }); shields.Add(s);
        }
        shields=shields.OrderByDescending(s=>s.groundRadius).ToList();
        Check(shields.Select(s=>s.angleDeg).OrderBy(a=>a).SequenceEqual(new[]{-30f,0f,30f}),"stored straight and opposite tilted shields");
        foreach(var s in shields)
        {
            Vector2 center=SpaceWeaponGeometry.FootprintCenter(s.parent);
            float laneOffset=22.5f-center.y;
            Check(Math.Abs(laneOffset)<s.groundRadius
                && center.x-Math.Sqrt(s.groundRadius*s.groundRadius-laneOffset*laneOffset)<0,
                "each guard dome crosses the rail axis before the six armour layers");
            s.GetSegment(out var a,out var b,out var n);
            Check(a.x<0 && b.x<0 && n.x<0,"whole native shield segment sits ahead of front armour");
            Check(Math.Abs((b-a).magnitude-10f)<.001f,"native shield length survives prefab Scribe");
            Check(Math.Abs(SpaceTransform.IncidenceAngle(Vector2.right,n)*Mathf.Rad2Deg-Math.Abs(s.angleDeg))<.01f,
                "native shield normal exposes distinct incidence angles");
        }
        shields[0].GetSegment(out var centralA,out var centralB,out _);
        Check(centralA.y<22.5f && centralB.y>22.5f,"fixed rail axis crosses the central shield");
        RangeLaserSightlines(game,platform,target,shields);
        var definitions=new XmlDocument();definitions.Load(Path.Combine(directory,"../../1.6/Defs/Tutorial/DeltaV_TutorialRange.xml"));
        var presets=definitions.SelectNodes("/Defs/ThingDef/comps/li[@Class='DeltaV.CompProperties_TutorialShieldTerminal']").Cast<XmlNode>().ToList();
        Check(presets.Count==9,"nine distinct test presets plus entry briefing");
        foreach(string mode in new[]{"Kinetic","Electromagnetic"})
            Check(presets.Where(n=>n["preset"].InnerText==mode).Select(n=>int.Parse(n["strengthPercent"].InnerText))
                .OrderBy(n=>n).SequenceEqual(mode=="Kinetic" ? new[]{0,20,100} : new[]{0,50,100}),"three real strength settings: "+mode);
        foreach(var setting in presets)
        {
            var preset=Enum.Parse<TutorialShieldPreset>(setting["preset"].InnerText);
            int strength=int.Parse(setting["strengthPercent"]?.InnerText ?? "0");
            bool line=preset==TutorialShieldPreset.Kinetic || preset==TutorialShieldPreset.Electromagnetic;
            CompTutorialShieldTerminal.ApplyPreset(shields,preset,strength);
            Check(shields.All(s=>s.RegularCapacity==123d && s.EmergencyCapacity==777d && s.RecoveryWaitTicks==200),
                "preset does not refill or reset recovery: "+preset);
            Check(shields[0].FlickedOn==(preset!=TutorialShieldPreset.Unshielded)
                && shields.Skip(1).All(s=>s.FlickedOn==(line || preset==TutorialShieldPreset.Guard)),"preset participation: "+preset);
            Check(shields.All(s=>s.mode==(preset==TutorialShieldPreset.Kinetic ? SpaceShieldMode.Kinetic
                : preset==TutorialShieldPreset.Electromagnetic ? SpaceShieldMode.Electromagnetic
                : SpaceShieldMode.Guard)),"preset selects real shield mode on all three generators: "+preset);
            Check(shields.All(s=>s.kineticStrengthPercent==strength && s.electromagneticStrengthPercent==strength),
                "terminal strength reaches all generators: "+preset+strength);
            Check(shields.Select(s=>s.angleDeg).OrderBy(a=>a).SequenceEqual(new[]{-30f,0f,30f}),"switching preserves authored shield angles");
            if(line)
                foreach(var s in shields)
                {
                    s.GetSegment(out _,out _,out var n);
                    var ray=SpaceShieldRefraction.Calculate(Vector2.right,n,strength).Direction(1f);
                    if(strength==100) Check(ray.x<-.999f,"full funded EM returns beam from straight and tilted segments");
                    if(strength==0) Check(s.angleDeg==0 ? ray.x>.999f : Math.Abs(ray.y)>.1f,"passive EM distinguishes normal and oblique incidence");
                    Vector2 v=new Vector2(180,0), tangent=new Vector2(n.y,-n.x);
                    var result=SpaceKinetics.ApplyShieldImpulse(1,v,n,SpaceKinetics.ShieldImpulse(strength));
                    Check(Math.Abs(Vector2.Dot(result-v,tangent))<.001f,"kinetic pulse preserves tangent on authored segment");
                }
        }
        Vector2 deflected=SpaceShieldRefraction.Calculate(Vector2.right,Vector2.left,50).Direction(1f);
        Vector2 reflected=SpaceShieldRefraction.Calculate(Vector2.right,Vector2.left,100).Direction(1f);
        Check(Mathf.Abs(deflected.x)<.001f && reflected.x<-.999f,"actual electromagnetic presets deflect sideways and reflect back");
        var saved=new TerminalSave(); saved.terminal.targetObjectId=435; saved.terminal.primaryShieldId=987;
        var xml=new XmlDocument();xml.LoadXml(Scribe.saver.DebugOutputFor(saved));string temp=Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp,"<root><terminal>"+xml.DocumentElement.InnerXml+"</terminal></root>");
            Scribe.loader.InitLoading(temp);Scribe.EnterNode("terminal");var loaded=new TerminalSave();loaded.ExposeData();
            Scribe.ExitNode();Scribe.loader.FinalizeLoading();
            Check(loaded.terminal.targetObjectId==435 && loaded.terminal.primaryShieldId==987,"terminal bindings survive native Scribe");
        }
        finally {Scribe.ForceStop();File.Delete(temp);}
        Check(AccessTools.Method(typeof(JobDriver_UseItem),"Use")!=null
            && typeof(CompUsable).IsAssignableFrom(typeof(CompUsable_TutorialShieldTerminal))
            && typeof(CompUseEffect).IsAssignableFrom(typeof(CompTutorialShieldTerminal)),"native UseItem/Usable/UseEffect chain");
        var universe=game.GetComponent<UniverseManager>();var quest=new Quest {id=84,acceptanceTick=0};
        var run=new TutorialRunState {quest=quest,stage=TutorialStage.Range};universe.tutorialRun=run;
        var assetCheck=AccessTools.Method(typeof(DeltaVTutorialService),"CheckFlightAssets");
        harmony.Patch(assetCheck,prefix:new HarmonyMethod(typeof(Program),nameof(AssetsPresent)));
        try
        {
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"DeferRange"));
            Check(run.stage==TutorialStage.Range && universe.Tutorial.Active,"visiting the range voluntarily keeps guidance available");
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"GoToShipyard"));
            Check(run.stage==TutorialStage.Visit,"zero shots, terminal uses and soft docks still permit ship handover");
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"DeferRange"));
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"GoToShipyard"));
            Check(run.stage==TutorialStage.Visit && universe.Tutorial.Active,"stale range answers cannot complete handover");
            ((List<Action>)AccessTools.Field(typeof(UniverseManager),"frameActions").GetValue(universe)).Clear();
            universe.Tutorial.Ended(quest);
        }
        finally {harmony.Unpatch(assetCheck,HarmonyPatchType.Prefix,harmony.Id);}
    }
}

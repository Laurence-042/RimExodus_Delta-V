using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;
using UnityEngine;

internal static partial class Program
{
    // Only the native static UI icons/materials require a Unity renderer.
    static bool SkipFuelGraphics() => false;
    static bool RangeOwner(ref int __result) { __result=1; return false; }
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
        var segmentNode=Def(segments[0].def.defName).SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']");
        int id=900;
        foreach(var b in segments)
        {
            b.thingIDNumber=id++;
            var segment=new CompSpaceRailgun { parent=b,props=new CompProperties_SpaceRailgun {
                nextRootOffset=IntVec3.FromString(segmentNode["nextRootOffset"].InnerText),
                outputOffset=IntVec3.FromString(segmentNode["outputOffset"].InnerText),
                speedBonus=float.Parse(segmentNode["speedBonus"].InnerText,System.Globalization.CultureInfo.InvariantCulture) } };
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
            Check(chain.segments.Count==3 && chain.speedBonus==360f,"production trace reaches all three accelerators");
            var last=chain.segments.Last().parent.OccupiedRect();
            var muzzleCell=new IntVec3(Mathf.FloorToInt(chain.muzzleMap.Value.x),0,Mathf.FloorToInt(chain.muzzleMap.Value.y));
            Check(chain.muzzleMap.HasValue && chain.muzzleMap.Value.x>last.maxX+1
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

    static void RangeChecks(Game game, Harmony harmony, string directory)
    {
        LoadedModManager.RunningModsListForReading[0].assemblies.loadedAssemblies.Add(typeof(Program).Assembly);
        harmony.Patch(typeof(CompRefuelable).TypeInitializer,
            prefix:new HarmonyMethod(typeof(Program),nameof(SkipFuelGraphics)));
        Set(game.World.factionManager,"ofPlayer",new Faction { def=new FactionDef { isPlayer=true } });
        var platform = new XmlDocument(); platform.Load(Path.Combine(directory,"WeaponRange.xml"));
        var target = new XmlDocument(); target.Load(Path.Combine(directory,"ShieldTarget.xml"));
        var weaponNames = new[] { "DeltaV_Weapon_RapidMainMedium", "DeltaV_Weapon_HeavyMainMedium",
            "DeltaV_Weapon_LaserMainMedium", "DeltaV_Weapon_RailgunLoader" };
        int group=0;
        foreach (string name in weaponNames)
        {
            var state=platform.SelectSingleNode("/MapPrefab/thingStates/li[def='"+name+"']/state/saveable");
            Check(state != null && state["groupNumber"].InnerText == (++group).ToString(), "four distinct fire-control groups");
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
                Check(weapon.groupNumber==group && weapon.coverage.arc==24f && weapon.EffectiveRange==150f,
                    "native weapon Scribe preserves group and limited firing sector");
                Check(weapon.CoverageError(0f)==0f && weapon.CoverageError(Mathf.PI)>100f
                    && weapon.CoverageError(13f*Mathf.Deg2Rad)>0f,"station behind platform and off-sector aim rejected");
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
        Check(platform.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_Weapon_RailgunAccelerator']").Count==3,"railgun has three real accelerators");
        Check(platform.SelectNodes("/MapPrefab/thingStates/li[def='Battery']").Count==60,"platform has a finite 36000 Wd bank");
        Check(target.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator']").Count==3,"target has three same-ship shield generators");
        var shields = new List<CompSpaceShield>();
        for(int i=0;i<3;i++)
        {
            var b=Raw<Building>(); b.def=Raw<ThingDef>(); Set(b,"mapIndexOrState",(sbyte)-1);
            var s=new CompSpaceShield { parent=b,props=new CompProperties_SpaceShield() };
            var flick=new CompFlickable { parent=b,props=new CompProperties_Flickable() };
            var fuel=new CompRefuelable { parent=b,props=new CompProperties_Refuelable { fuelCapacity=2000 } };
            Set(fuel,"fuel",777f); Set(s,"regularCapacity",123d); Set(s,"recoveryWaitTicks",200);
            Set(b,"comps",new List<ThingComp> { s,flick,fuel }); shields.Add(s);
        }
        foreach(TutorialShieldPreset preset in Enum.GetValues<TutorialShieldPreset>())
        {
            CompTutorialShieldTerminal.ApplyPreset(shields,preset);
            Check(shields.All(s=>s.RegularCapacity==123d && s.EmergencyCapacity==777d && s.RecoveryWaitTicks==200),
                "preset does not refill or reset recovery: "+preset);
            Check(shields[0].FlickedOn==(preset!=TutorialShieldPreset.Unshielded)
                && shields.Skip(1).All(s=>s.FlickedOn==(preset==TutorialShieldPreset.Guard)),"preset participation: "+preset);
            Check(shields[0].mode==(preset==TutorialShieldPreset.Kinetic ? SpaceShieldMode.Kinetic
                : preset==TutorialShieldPreset.Deflection || preset==TutorialShieldPreset.Reflection ? SpaceShieldMode.Electromagnetic
                : SpaceShieldMode.Guard),"preset selects real shield mode: "+preset);
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
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"ContinueAfterRange"));
            Check(run.stage==TutorialStage.Salvage,"zero visits, zero shots, zero terminal uses still allow continuation");
            universe.Tutorial.Receive(quest,DeltaVTutorialService.Signal(quest,"ContinueAfterRange"));
            Check(run.stage==TutorialStage.Salvage,"duplicate optional-range answer cannot skip salvage");
            ((List<Action>)AccessTools.Field(typeof(UniverseManager),"frameActions").GetValue(universe)).Clear();
            universe.Tutorial.Ended(quest);
        }
        finally {harmony.Unpatch(assetCheck,HarmonyPatchType.Prefix,harmony.Id);}
    }
}

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
    const string RangeRapidDef = "DeltaV_Weapon_TutorialRapidMainMedium";
    const string RangeRailDef = "DeltaV_Weapon_TutorialRailgunLoader";
    const string RangeHeavyDef = "DeltaV_Weapon_HeavyMainMedium";
    const string RangeLaserDef = "DeltaV_Weapon_LaserMainMedium";

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
        foreach(var shield in json.RootElement.EnumerateArray().Where(r=>r.GetProperty("name").GetString()=="DeltaV_ShieldGenerator"))
        {
            var root=shield.GetProperty("root");
            var cell=new IntVec3(root[0].GetInt32()+20,0,root[1].GetInt32()+20);
            Check(PowerConnectionMaker.BestTransmitterForConnector(cell,transmitters[0].Map)?.transNet==live,
                "native connector joins each target shield to the working grid");
        }
    }
    static void RangeLayout(List<Building> buildings, string recordsPath, int expectedSegments = 9)
    {
        XmlNode Def(string name) => sourceDefs[name];
        var attachments=buildings.Where(b=>b.def.defName=="DeltaV_Building_AutoAimPluginII").ToList();
        foreach(var turret in buildings.Where(b=>expectedSegments==9 && b.def.defName.EndsWith("MainMedium")))
        {
            var slot=IntVec3.FromString(Def(turret.def.defName).SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/attachmentSlots/li/cell").InnerText);
            Check(attachments.Count(a=>a.Position==SpaceWeaponGeometry.RotatedCell(turret,slot)
                && a.Rotation==turret.Rotation)==1,"native rotated attachment slot contains exactly one advanced auto-aim");
        }
        var loader=buildings.Single(b=>b.def.defName==(expectedSegments == 9 ? RangeRailDef : "DeltaV_Weapon_RailgunLoader"));
        var segments=buildings.Where(b=>b.def.defName=="DeltaV_Weapon_RailgunAccelerator").ToList();
        if (expectedSegments == 9)
        {
            var guard = buildings.Single(b => b.def.defName == "DeltaV_ShieldGenerator");
            var net = PowerConnectionMaker.BestTransmitterForConnector(guard.Position, guard.Map)?.transNet;
            var terminals = buildings.Where(b => b.def.defName.StartsWith("DeltaV_RangeTerminal_")).ToList();
            Check(terminals.Count == 12 && net != null
                && terminals.All(b => PowerConnectionMaker.BestTransmitterForConnector(b.Position, b.Map)?.transNet == net),
                "platform guard, briefing and eleven shield terminals connect to the working power grid");
            var lastRow = terminals.Where(b => b.def.defName.EndsWith("ObliqueFull")).ToList();
            Check(lastRow.Count == 2 && lastRow.All(b => b.InteractionCell == b.Position + IntVec3.South
                && !buildings.Any(battery => battery.def.defName == "Battery" && battery.OccupiedRect().Contains(b.InteractionCell))),
                "both southern line-shield terminal controls have a free operating cell above the batteries");
        }
        foreach(var definition in new[] {loader.def,segments[0].def})
        for(int rotation=0;rotation<4;rotation++)
        {
            var part=Raw<Building>(); part.def=definition;
            Set(part,"mapIndexOrState",(sbyte)-1);
            Set(part,"positionInt",new IntVec3(50,0,50)); Set(part,"rotationInt",new Rot4(rotation));
            var footprint=part.OccupiedRect();
            Check(footprint.Count()==3 && SpaceWeaponGeometry.FootprintCenter(part)==new Vector2(50.5f,50.5f),
                "three-cell rail stays centered in every native rotation");
            var slots = Def(definition.defName).SelectNodes("comps/li/attachmentSlots/li/cell")
                .Cast<XmlNode>().Select(slot=>IntVec3.FromString(slot.InnerText)).ToArray();
            Check(slots.ToHashSet().SetEquals(new[] { new IntVec3(-1,0,0), new IntVec3(1,0,0) })
                && slots.Length == 2, "rail part has exactly two side mounts and an open central rail");
            foreach(var slot in slots)
                Check(footprint.Contains(SpaceWeaponGeometry.RotatedCell(part,slot)),
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
            Check(chain.segments.Count==expectedSegments && chain.kineticEnergyBonus==112500f*expectedSegments,
                "production trace reaches each authored accelerator");
            var cells=segments.Append(loader).SelectMany(b=>b.OccupiedRect()).Distinct().ToList();
            int width=cells.Max(c=>c.x)-cells.Min(c=>c.x)+1, height=cells.Max(c=>c.z)-cells.Min(c=>c.z)+1;
            bool north=loader.Rotation==Rot4.North;
            Check(cells.Count==3*(expectedSegments+1)
                && (north ? height : width)==expectedSegments+1 && (north ? width : height)==3,
                "native rail footprint follows the authored axis");
            var last=chain.segments.Last().parent.OccupiedRect();
            Check(chain.muzzleMap.HasValue, "production rail trace has an exit muzzle");
            var muzzleCell=new IntVec3(Mathf.FloorToInt(chain.muzzleMap.Value.x),0,Mathf.FloorToInt(chain.muzzleMap.Value.y));
            Check((north ? chain.muzzleMap.Value.y>=last.maxZ+1 : chain.muzzleMap.Value.x>=last.maxX+1)
                && !buildings.Any(b=>b.def.passability==Traversability.Impassable
                    && b.OccupiedRect().Contains(muzzleCell)),
                "production rail muzzle exits unobstructed beyond the final segment");
            if (north)
            {
                using var authored=JsonDocument.Parse(File.ReadAllText(recordsPath));
                bool module = Path.GetFileName(recordsPath) == "ModuleRailgun.records.json";
                float stored=authored.RootElement.EnumerateArray()
                    .Where(r=>r.GetProperty("name").GetString()=="DeltaV_SafetyBattery"
                        || module && r.GetProperty("name").GetString()=="Battery")
                    .Sum(r=>r.GetProperty("stateOverrides").GetProperty("storedPower").GetSingle());
                float Pulse(string value) => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                float loaderPulse=Pulse(Def(loader.def.defName)
                    .SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']/shotEnergy").InnerText);
                Check(stored>=loaderPulse+expectedSegments*Pulse(segmentNode["shotEnergy"].InnerText),
                    "local reserve covers one full rail discharge");
            }
        }
        finally {harmony.Unpatch(owner,HarmonyPatchType.Prefix,harmony.Id);}
    }
    sealed class RangeWeaponSave : IExposable
    {
        internal CompSpaceWeapon weapon;
        internal CompGunSupply supply;
        internal CompGunSupply propellant;
        internal CompWeaponTuning tuning;
        public RangeWeaponSave(Building building, CompProperties_WeaponTuning settings,
            CompProperties_GunSupply heads, CompProperties_GunSupply charge, CompProperties_SpaceWeapon weaponProps)
        {
            weapon = new CompSpaceWeapon { parent=building, props=weaponProps };
            if (settings != null) tuning = new CompWeaponTuning { parent=building, props=settings };
            if (heads != null)
            {
                supply = new CompGunSupply { parent=building };
                supply.Initialize(heads);
            }
            if (charge != null)
            {
                propellant = new CompGunSupply { parent=building };
                propellant.Initialize(charge);
            }
        }
        public void ExposeData()
        {
            weapon.PostExposeData(); supply?.PostExposeData(); propellant?.PostExposeData(); tuning?.PostExposeData();
        }
    }

    static ThingDef RangeAmmoDef(string name)
    {
        if (name == null) return null;
        var found = DefDatabase<ThingDef>.GetNamedSilentFail(name);
        if (found != null) return found;
        var def = Raw<ThingDef>();
        def.defName = def.label = name;
        def.virtualDefs = new List<ThingDef>();
        DefDatabase<ThingDef>.Add(def);
        return def;
    }

    static CompProperties_GunSupply RangeSupply(XmlNode comps, GunSupplyKind kind)
    {
        var config = comps.SelectSingleNode($"li[@Class='DeltaV.CompProperties_GunSupply'][kind='{kind}']");
        if (config == null) return null;
        var filter = new ThingFilter();
        filter.SetAllow(RangeAmmoDef(config.SelectSingleNode("fuelFilter/thingDefs/li").InnerText), true);
        return new CompProperties_GunSupply
        {
            kind = kind,
            fuelCapacity = int.Parse(config["fuelCapacity"].InnerText),
            initialConfigurableTargetFuelLevel = int.Parse(config["initialConfigurableTargetFuelLevel"].InnerText),
            alternateHead = RangeAmmoDef(config["alternateHead"]?.InnerText),
            fuelFilter = filter,
        };
    }

    static Dictionary<(int group, SpaceWeaponSalvoSet bank), (float mass, float speed, string projectile)>
        RangeAmmunition(XmlDocument platform, XmlDocument definitions, List<RangeWeaponSave> cannons, string directory)
    {
        const string magazineName = "DeltaV_Building_UraniumMagazine";
        var magazineConfig = definitions.SelectSingleNode($"/Defs/ThingDef[defName='{magazineName}']/comps");
        var headsConfig = RangeSupply(magazineConfig, GunSupplyKind.Heads);
        var chargeConfig = RangeSupply(magazineConfig, GunSupplyKind.Propellant);
        var ammoConfig = magazineConfig.SelectSingleNode("li[@Class='DeltaV.CompProperties_SpaceWeaponMagazine']");
        var allowed = magazineConfig.SelectNodes("li/allowedHostDefs/li").Cast<XmlNode>().Select(node => node.InnerText).ToHashSet();
        var steelHead = RangeAmmoDef("DeltaV_SteelHead");
        var uraniumHead = RangeAmmoDef("DeltaV_UraniumHead");
        var propellant = RangeAmmoDef("DeltaV_Propellant");
        Check(headsConfig.fuelFilter.AnyAllowedDef == uraniumHead && headsConfig.alternateHead == steelHead
            && chargeConfig.fuelFilter.AnyAllowedDef == propellant,
            "the range uses a configurable auxiliary that supports both head types and stores its own propellant");
        var magazineDef=Raw<ThingDef>();
        magazineDef.defName=magazineName; magazineDef.thingClass=typeof(Building); magazineDef.category=ThingCategory.Building;
        magazineDef.size=IntVec2.One; magazineDef.building=new BuildingProperties { isEdifice=false };
        magazineDef.comps=new List<CompProperties> {
                new CompProperties_SpaceWeaponAttachment { allowedHostDefs=cannons.Select(cannon => cannon.weapon.parent.def)
                    .Where(def => allowed.Contains(def.defName)).ToList() },
                headsConfig, chargeConfig,
                new CompProperties_SpaceWeaponMagazine { attachment=true,
                    defaultPriority=int.Parse(ammoConfig["defaultPriority"].InnerText) }
            };
        DefDatabase<ThingDef>.Add(magazineDef);
        var entries = platform.SelectNodes($"/MapPrefab/thingStates/li[def='{magazineName}']").Cast<XmlNode>().ToArray();
        Check(entries.Length == 4, "four auxiliary magazines, one for each rapid and heavy cannon");
        var priorLanguage=LanguageDatabase.activeLanguage;
        List<Thing> mounts;
        try
        {
            LanguageDatabase.activeLanguage=StatsFixtureLanguage(directory,"English");
            mounts=RimExodus.Maps.MapPrefabThingState.Instantiate(entries.Select(entry => new RimExodus.Maps.MapPrefabThingEntry {
                def=magazineDef, stateXml=entry.SelectSingleNode("state/saveable").OuterXml }).ToList(),"range magazine mounts",null);
        }
        finally { LanguageDatabase.activeLanguage=priorLanguage; }
        var map = Raw<Map>();
        IntVec2 layoutSize = IntVec2.FromString(platform.SelectSingleNode("/MapPrefab/prefab/size").InnerText);
        map.info = new MapInfo { Size = new IntVec3(layoutSize.x, 1, layoutSize.z) };
        map.cellIndices=new CellIndices(map); map.thingGrid=new ThingGrid(map); map.edificeGrid=new EdificeGrid(map);
        map.components=new List<MapComponent>();
        Find.Maps.Add(map);
        try
        {
            void PlaceOnGrid(Thing thing)
            {
                Set(thing,"mapIndexOrState",(sbyte)Find.Maps.IndexOf(map));
                map.thingGrid.Register(thing);
            }
            foreach (var gun in cannons)
            {
                var turret=(Building)gun.weapon.parent;
                var own=new CompSpaceWeaponMagazine { parent=turret, props=new CompProperties_SpaceWeaponMagazine { unitMass=1f } };
                Set(turret,"comps",new List<ThingComp> {gun.weapon,gun.tuning,gun.supply,gun.propellant,own});
                PlaceOnGrid(turret);
                map.edificeGrid.Register(turret);
            }
            foreach (var mount in mounts) PlaceOnGrid(mount);
            var shots = new Dictionary<(int, SpaceWeaponSalvoSet), (float mass, float speed, string projectile)>();
            foreach (var gun in cannons)
            {
                var weapon = gun.weapon;
                bool secondary = weapon.salvoSet == SpaceWeaponSalvoSet.Secondary;
                string cannon = $"group {weapon.groupNumber} {weapon.salvoSet}";
                var expectedHead = secondary ? uraniumHead : steelHead;
                var oppositeHead = secondary ? steelHead : uraniumHead;
                var expectedProjectile = secondary ? weapon.WeaponProps.armorPiercingProjectileDef : weapon.WeaponProps.projectileDef;
                var mountsOnWeapon = weapon.AttachedThings().Where(thing => thing.def == magazineDef).ToArray();
                Check(mountsOnWeapon.Length == 1 && mountsOnWeapon[0].Position == SpaceWeaponGeometry.RotatedCell(
                    weapon.parent, weapon.WeaponProps.attachmentSlots[1].cell), cannon + " has one auxiliary in its second native slot");
                var selected = weapon.SelectMagazine();
                Check(selected?.parent == mountsOnWeapon[0] && weapon.MagazineAvailable(selected),
                    cannon + " selects its full auxiliary first");
                var auxiliaryHeads = selected.Supply(GunSupplyKind.Heads);
                var auxiliaryCharge = selected.Supply(GunSupplyKind.Propellant);
                Check(auxiliaryHeads.Fuel == headsConfig.fuelCapacity && auxiliaryHeads.TargetFuelLevel == headsConfig.fuelCapacity
                    && auxiliaryCharge.Fuel == chargeConfig.fuelCapacity && auxiliaryCharge.TargetFuelLevel == chargeConfig.fuelCapacity
                    && auxiliaryCharge.Props.fuelFilter.Allows(propellant), cannon + " restores full auxiliary heads and propellant");
                Check(auxiliaryHeads.SelectedHead == expectedHead && auxiliaryHeads.Props.fuelFilter.Allows(expectedHead)
                    && !auxiliaryHeads.Props.fuelFilter.Allows(oppositeHead) && gun.supply.SelectedHead == expectedHead
                    && gun.supply.Props.fuelFilter.Allows(expectedHead) && !gun.supply.Props.fuelFilter.Allows(oppositeHead),
                    cannon + " restores the same head selection and filter in both magazines");
                var projectile = selected.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                Check(projectile == expectedProjectile, cannon + " selects the projectile for its authored head type");
                shots.Add((weapon.groupNumber, weapon.salvoSet), (SpaceWeaponBallistics.ProjectileMass(weapon, selected),
                    SpaceWeaponBallistics.BaseSpeed(weapon, selected, 0f), projectile.defName));

                float builtInHeads = gun.supply.Fuel;
                float builtInCharge = gun.propellant.Fuel;
                selected.ConsumeLoad(selected.RequiredHeads(weapon), weapon.RequiredPropellant);
                Check(selected.HeadCount == headsConfig.fuelCapacity - gun.tuning.mass
                    && selected.PropellantCount == chargeConfig.fuelCapacity - gun.tuning.charge
                    && gun.supply.Fuel == builtInHeads && gun.propellant.Fuel == builtInCharge,
                    cannon + " draws both materials for a shot from the auxiliary");

                Set(auxiliaryCharge, "fuel", (float)gun.tuning.charge - 1f);
                var fallback = weapon.SelectMagazine();
                Check(fallback?.parent == weapon.parent
                    && (fallback.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef) == projectile
                    && fallback.UnitMass(weapon) == selected.UnitMass(weapon)
                    && fallback.PenetrationScale(weapon) == selected.PenetrationScale(weapon),
                    cannon + " falls back to the internal magazine with identical ballistics when the auxiliary lacks propellant");
                Set(gun.supply, "fuel", (float)gun.tuning.mass - 1f);
                Check(weapon.SelectMagazine() == null && !weapon.MagazineAvailable(null),
                    cannon + " cannot use the auxiliary heads with the internal propellant");

                Set(gun.supply, "fuel", builtInHeads);
                Set(auxiliaryCharge, "fuel", chargeConfig.fuelCapacity);
                Set(auxiliaryHeads, "fuel", (float)selected.RequiredHeads(weapon) - 1f);
                Check(weapon.SelectMagazine() == fallback, cannon + " also falls back when the auxiliary lacks heads");
                Set(gun.propellant, "fuel", (float)gun.tuning.charge - 1f);
                Check(weapon.SelectMagazine() == null && !weapon.MagazineAvailable(null),
                    cannon + " cannot use the internal heads with the auxiliary propellant");
            }
            foreach (int group in new[] { 1, 2 })
            {
                var steel = shots[(group, SpaceWeaponSalvoSet.Main)];
                var uranium = shots[(group, SpaceWeaponSalvoSet.Secondary)];
                Check(uranium.mass == steel.mass * 1.5f && uranium.speed < steel.speed
                    && Math.Abs(SpaceKinetics.Energy(steel.mass, steel.speed) / SpaceKinetics.Energy(uranium.mass, uranium.speed) - 1) < 1e-5,
                    "at equal charge uranium leaves the firing muzzle slower with equal muzzle-frame kinetic energy");
            }
            return shots;
        }
        finally { Find.Maps.Remove(map); }
    }
    sealed class TerminalSave : IExposable
    {
        internal CompTutorialShieldTerminal terminal = new CompTutorialShieldTerminal();
        public void ExposeData() => terminal.PostExposeData();
    }

    sealed class RangeShieldSave : IExposable
    {
        internal CompSpaceShield shield;
        internal CompFlickable flick;
        internal CompRefuelable fuel;
        public RangeShieldSave(Building parent)
        {
            shield = new CompSpaceShield { parent=parent, props=new CompProperties_SpaceShield() };
            flick = new CompFlickable { parent=parent, props=new CompProperties_Flickable() };
            fuel = new CompRefuelable { parent=parent, props=new CompProperties_Refuelable { fuelCapacity=750 } };
            Set(parent,"comps",new List<ThingComp> { shield, flick, fuel });
        }
        public void ExposeData()
        {
            shield.PostExposeData();
            flick.PostExposeData();
            fuel.PostExposeData();
        }
    }

    static void RangeSightlines(Game game, XmlDocument platform, XmlDocument target,
        CompSpaceShield platformGuard, IReadOnlyList<CompSpaceShield> shields, IReadOnlyList<CompTutorialShieldTerminal> terminals,
        IReadOnlyDictionary<(int group, SpaceWeaponSalvoSet bank), (float mass, float speed, string projectile)> shots,
        string directory)
    {
        InitializeOrbitFixture(game);
        Map MapFor(XmlDocument doc)
        {
            var map = Raw<Map>();
            map.info = new MapInfo { Size = ShipPocketMapFactory.CompactSize(
                IntVec2.FromString(doc.SelectSingleNode("/MapPrefab/prefab/size").InnerText)) };
            return map;
        }

        var source = new MapSpaceObject(8001, MapFor(platform), new SpacePose(Vector3.forward, 0));
        var destination = new MapSpaceObject(8002, MapFor(target), DeltaVTutorialService.CombatRangeTargetPose(source.pose));
        var margin = new Vector2(12, 12);
        var railEnd = platform.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_Weapon_RailgunAccelerator']/pos")
            .Cast<XmlNode>().Select(node => IntVec3.FromString(node.InnerText)).OrderBy(c => c.x).Last();
        var output = IntVec3.FromString(sourceDefs["DeltaV_Weapon_RailgunAccelerator"].SelectSingleNode(
            "comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']/outputOffset").InnerText);
        var railMuzzle = new Vector2(railEnd.x + .5f, railEnd.z + .5f) + margin
            + SpaceTransform.RotatePlacementOffset(new Vector2(output.x, output.z), Rot4.East);
        var coverage = new[] { new Vector2(34,21), new Vector2(34,24), new Vector2(44,21), new Vector2(44,24), railMuzzle-margin };
        Check(platformGuard.mode == SpaceShieldMode.Guard && platformGuard.FlickedOn && platformGuard.GuardCenter.x > 34f
            && coverage.All(p => Vector2.Distance(p, platformGuard.GuardCenter) < platformGuard.groundRadius),
            "the enabled east platform guard covers the rail footprint and muzzle");
        var lanes = new List<(int group, SpaceWeaponSalvoSet bank, CompSpaceWeapon gun, CompSpaceWeaponTraverse traverse, float maxSpread)>();
        int pluginSkill = int.Parse(sourceDefs["DeltaV_Building_AutoAimPluginII"].SelectSingleNode(
            "comps/li[@Class='DeltaV.CompProperties_SpaceAutoAimPlugin']/equivalentSkillLevel").InnerText);
        foreach (var (name, group) in new[] { (RangeRapidDef,1), (RangeHeavyDef,2), (RangeLaserDef,3) })
        foreach (XmlNode entry in platform.SelectNodes("/MapPrefab/thingStates/li[def='" + name + "']"))
        {
            var body = Raw<Building>();
            body.def = Raw<ThingDef>();
            body.def.size = IntVec2.FromString(SourceChain(name).Select(x => x["size"]).First(x => x != null).InnerText);
            Set(body, "positionInt", IntVec3.FromString(entry["pos"].InnerText) + new IntVec3(12, 0, 12));
            Set(body, "rotationInt", new Rot4(int.Parse(entry["rotation"].InnerText)));
            Check(body.Rotation == Rot4.East, "the authored turret faces east");
            var props = sourceDefs[name].SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']");
            var gun = new CompSpaceWeapon { parent = body, props = new CompProperties_SpaceWeapon {
                topTurret = true, turretMuzzleDistance = XmlFloat(props, "turretMuzzleDistance") } };
            var traverse = new CompSpaceWeaponTraverse { parent = body, props = new CompProperties_SpaceWeaponTraverse() };
            Set(traverse, "currentAngle", 0f);
            Set(body, "comps", new List<ThingComp> { gun, traverse });
            var bank = Enum.Parse<SpaceWeaponSalvoSet>(entry.SelectSingleNode("state/saveable/salvoSet").InnerText);
            float spread = 1.2f * XmlFloat(props, "baseSpreadDeg") * SpaceWeaponService.SpreadFactorForSkill(pluginSkill);
            lanes.Add((group, bank, gun, traverse, spread));
        }
        var muzzles = lanes.Select(lane => SpaceWeaponGeometry.MuzzleMapPoint(lane.gun)).ToList();
        Check(muzzles.Count == 6 && muzzles.GroupBy(p => p.y).Select(row => row.Count()).SequenceEqual(new[] {3, 3})
            && muzzles.GroupBy(p => p.x).Count(column => column.Count() == 2) == 3,
            "the platform has three turret pairs in two rows");
        Check(muzzles.Min(p => p.y) < railMuzzle.y && railMuzzle.y < muzzles.Max(p => p.y),
            "the east rail muzzle has a separate lane between turret rows");
        var railProps = sourceDefs[RangeRailDef].SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeapon']");
        lanes.Add((4, SpaceWeaponSalvoSet.Secondary, null, null, 1.2f * XmlFloat(railProps, "baseSpreadDeg")
            * SpaceWeaponService.SpreadFactorForSkill((int)XmlFloat(railProps, "intrinsicAimSkill"))));

        Vector2 InTarget(Vector2 muzzle) => SpaceTransform.UniverseToMapPoint(destination, destination.map,
            SpaceTransform.MapPointToUniverse(source, source.map, muzzle)) - margin;

        var primary = shields.Single(s => s.parent.Position == new IntVec3(22, 0, 35));
        var angled = shields.Single(s => s.parent.Position == new IntVec3(7, 0, 8));
        DeltaVTutorialService.BindCombatRangeTerminals(terminals, destination, shields);
        Check(terminals.All(t => t.targetObjectId == destination.objectId
            && t.shieldThingId == (t.UsesAngledShield ? angled : primary).parent.thingIDNumber),
            "all eleven shield terminals bind to the runtime target and the correctly placed emitter");
        Check(shields.All(s => !s.FlickedOn), "the connector starts at the no-shield reference");

        void SelectGuard(TutorialShieldPreset preset, int expectedEmitters)
        {
            var terminal = terminals.Single(t => t.Preset == preset);
            terminal.ApplyTo(shields.OrderByDescending(s => s.parent.thingIDNumber == terminal.shieldThingId).ToArray());
            var guards = shields.Where(s => s.FlickedOn).ToArray();
            Check(guards.Length == expectedEmitters && guards.All(s => s.mode == SpaceShieldMode.Guard)
                && (guards.Length != 1 || guards[0] == primary), "guard preset enables the required emitters");
            var surface = new SpaceCollision.Surface();
            foreach (var guard in guards)
                surface.arcs.Add(new SpaceCollision.ShieldArc { center=guard.GuardCenter, radius=guard.groundRadius,
                    span=2f*Mathf.PI, building=(Building)guard.parent });
            var arcHit = AccessTools.Method(typeof(SpaceCollision), "TryFindArcHit");
            var from = InTarget(railMuzzle);
            float spreadLimit = lanes.Single(lane => lane.group == 4).maxSpread;
            foreach (float scatter in new[] { -spreadLimit, 0f, spreadLimit })
            {
                var direction = (InTarget(railMuzzle + SpaceTransform.Rotate(Vector2.right, scatter * Mathf.Deg2Rad)) - from).normalized;
                object[] args = { surface, from, direction, 150f, null, null, null,
                    new Predicate<SpaceCollision.ShieldArc>(_ => true) };
                bool hit = (bool)arcHit.Invoke(null,args);
                Check(hit == (expectedEmitters != 0) && (!hit ||
                    ((SpaceCollision.ShieldArc)args[4]).building == primary.parent && ((Vector2)args[5]).x < 0f),
                    "fixed railgun lane and spread hit the northern guard before the west wall when enabled");
                args[7] = new Predicate<SpaceCollision.ShieldArc>(arc => arc.building != primary.parent);
                Check(!(bool)arcHit.Invoke(null,args), "the entire railgun trace clears both lower guards");
            }
        }
        SelectGuard(TutorialShieldPreset.GuardSingle, 1);
        SelectGuard(TutorialShieldPreset.Guard, 3);

        var projectileDocs = new XmlDocument();
        projectileDocs.Load(Path.Combine(directory, "../../1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml"));
        var railProjectile = sourceDefs[railProps["projectileDef"].InnerText];
        CompProperties_SpaceProjectile Projectile(XmlNode def)
        {
            var xml = def.SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceProjectile']");
            var props = new CompProperties_SpaceProjectile();
            props.shieldFragmentCount = (int)XmlFloat(xml, "shieldFragmentCount");
            props.shieldFragmentCost = XmlFloat(xml, "shieldFragmentCost");
            props.shieldTumbleDeltaV = XmlFloat(xml, "shieldTumbleDeltaV", props.shieldTumbleDeltaV);
            props.shieldBreakDeltaV = XmlFloat(xml, "shieldBreakDeltaV", props.shieldBreakDeltaV);
            return props;
        }
        var railMagazine = sourceDefs[RangeRailDef].SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceWeaponMagazine']");
        float railMass = XmlFloat(railMagazine, "ammoPerShot") * XmlFloat(railMagazine, "unitMass");
        Check(railMass == 45f, "the tutorial rail has its own calibrated forty-five mass load");
        float railEnergy = XmlFloat(railProps, "launchKineticEnergy") + 9f * XmlFloat(sourceDefs["DeltaV_Weapon_RailgunAccelerator"]
            .SelectSingleNode("comps/li[@Class='DeltaV.CompProperties_SpaceRailgun']"), "kineticEnergyBonus");
        var laser = new CompProperties_SpaceProjectile { laserRange=150f };
        var lineHit = AccessTools.Method(typeof(SpaceCollision), "TryFindLineHit");
        var lightDirections = new List<Vector2>();
        var heavyDeflection = new Dictionary<SpaceWeaponSalvoSet, float>();
        // Slots follow main/secondary within each cannon family, then rail. A null allows either tumble or tear.
        var stability = new Dictionary<(float angle, int strength), int?[]> {
            [(0f,35)] = new int?[] {0,0,0,0,0},
            [(-45f,7)] = new int?[] {1,0,0,0,1},
            [(-45f,35)] = new int?[] {1,1,1,0,4},
            [(-45f,100)] = new int?[] {3,null,1,1,4}
        };
        foreach (var shield in shields)
        {
            Set(shield, "regularCapacity", 123d);
            Set(shield, "recoveryWaitTicks", 200);
            Set(shield.parent.TryGetComp<CompRefuelable>(), "fuel", 456f);
        }
        foreach (var terminal in terminals.Where(t => t.Preset == TutorialShieldPreset.Kinetic
            || t.Preset == TutorialShieldPreset.Electromagnetic))
        {
            var shield = shields.Single(s => s.parent.thingIDNumber == terminal.shieldThingId);
            var ordered = shields.OrderByDescending(s => s == shield).ToArray();
            terminal.ApplyTo(ordered);
            bool kineticMode = terminal.Preset == TutorialShieldPreset.Kinetic;
            var settings = (CompProperties_TutorialShieldTerminal)terminal.props;
            int strength = kineticMode ? shield.kineticStrengthPercent : shield.electromagneticStrengthPercent;
            Check(shield.FlickedOn && ordered.Skip(1).All(s => !s.FlickedOn)
                && shield.mode == (kineticMode ? SpaceShieldMode.Kinetic : SpaceShieldMode.Electromagnetic)
                && shield.angleDeg == settings.angleDeg && strength == settings.strengthPercent,
                "terminal enables the bound line emitter in its authored mode, angle and strength");
            shield.GetSegment(out Vector2 a, out Vector2 b, out Vector2 front);
            Check(Math.Abs((b - a).magnitude - shield.ShieldProps.maxLength) < .001f && a.x < 0f && b.x < 0f,
                "the entire maximum-length shield segment stays west of the painted wall");
            var surface = new SpaceCollision.Surface();
            surface.lineShields.Add(shield);

            foreach (var lane in lanes)
            {
                bool isLaser = lane.group == 3, isRail = lane.group == 4;
                bool secondary = lane.bank == SpaceWeaponSalvoSet.Secondary;
                var ammo = isLaser ? default : isRail ? (railMass, SpaceKinetics.Speed(railMass, railEnergy),
                    railProjectile["defName"].InnerText) : shots[(lane.group, lane.bank)];
                var projectile = isLaser ? laser : Projectile(isRail ? railProjectile : projectileDocs.SelectSingleNode(
                    $"/Defs/ThingDef[defName='{ammo.Item3}']"));
                int aimLimit = isRail ? 0 : 5;
                for (int aim = -aimLimit; aim <= aimLimit; aim++)
                {
                    if (!isRail) Set(lane.traverse, "currentAngle", aim * Mathf.Deg2Rad);
                    var muzzle = isRail ? railMuzzle : SpaceWeaponGeometry.MuzzleMapPoint(lane.gun);
                    var from = InTarget(muzzle);
                    foreach (float scatter in new[] { -lane.maxSpread, 0f, lane.maxSpread })
                    {
                        var launch = SpaceTransform.Rotate(Vector2.right, (aim + scatter) * Mathf.Deg2Rad);
                        var dir = (InTarget(muzzle + launch) - from).normalized;
                        object[] args = { surface, from, dir, 150f, projectile, null, null, null, null, null, null,
                            new Predicate<CompSpaceShield>(_ => true) };
                        bool hit = (bool)lineHit.Invoke(null, args);
                        var contact = (Vector2)args[6];
                        Check(hit == (kineticMode != isLaser) && (!hit || ReferenceEquals(args[5], shield) && contact.x < 0f),
                            $"line mode, coverage and first contact: group {lane.group}, {lane.bank}, {shield.mode}, {settings.angleDeg}, {aim}, {scatter}");
                        if (!hit) continue;
                        if (aim == 0 && scatter == 0f)
                            Check(Math.Abs(SpaceTransform.IncidenceAngle(dir, front) * Mathf.Rad2Deg - Math.Abs(shield.angleDeg)) < .02f,
                                "nominal due-east firing observes the angle set by the terminal");
                        if (isLaser)
                        {
                            if (!secondary && aim == 0 && scatter == 0f)
                                lightDirections.Add(SpaceShieldRefraction.Calculate(dir, front, strength).Direction(1f));
                            continue;
                        }

                        float mass = ammo.Item1;
                        Vector2 incoming = dir * ammo.Item2;
                        float request = SpaceKinetics.ShieldImpulse(strength);
                        float paid = SpaceKinetics.FundedShieldImpulse(mass, Vector2.Dot(incoming, front),
                            request, projectile.shieldFragmentCost, 600);
                        Check(paid == request, "one charged target battery funds the test pulse");
                        int fragments = SpaceKinetics.ShieldFragmentCount(mass, incoming, front, paid, projectile);
                        int ammoIndex = isRail ? 4 : (lane.group - 1) * 2 + (secondary ? 1 : 0);
                        int? expected = stability[(settings.angleDeg, strength)][ammoIndex];
                        Check(expected.HasValue ? fragments == expected : fragments == 1 || fragments == projectile.shieldFragmentCount,
                            $"calibrated stability: group {lane.group}, {lane.bank}, {settings.angleDeg}, {strength}, {aim}, {scatter}, fragments={fragments}");
                        if (strength == 35 && settings.angleDeg != 0f && lane.group == 2)
                        {
                            var outgoing = SpaceKinetics.ApplyShieldImpulse(mass, incoming, front, paid);
                            if (aim == 0 && scatter == 0f)
                                heavyDeflection.Add(lane.bank, Vector2.Angle(incoming, outgoing));
                            int row = Mathf.FloorToInt(contact.y - contact.x * outgoing.y / outgoing.x);
                            Check(outgoing.x > 0f && target.SelectSingleNode(
                                $"/MapPrefab/thingStates/li[pos='(0, 0, {row})' and def='Wall']") != null,
                                "both comparison rounds continue into the west armour throughout the aiming envelope");
                        }
                    }
                }
            }
            Check(shields.All(s => s.RegularCapacity == 123d && s.EmergencyCapacity == 456d && s.RecoveryWaitTicks == 200),
                "changing the preset preserves capacities and recovery");
        }
        Check(lightDirections.Count == 4
            && lightDirections.Zip(lightDirections.Skip(1), Vector2.Distance).All(distance => distance > .5f)
            && lightDirections[1].x > 0f && lightDirections[2].x < 0f
            && Vector2.Distance(lightDirections[3], -Vector2.right) < .001f,
            "the oblique beam bends forward at 20%, turns toward the front at 50%, and returns along its path at 100%");
        Check(heavyDeflection[SpaceWeaponSalvoSet.Secondary] < heavyDeflection[SpaceWeaponSalvoSet.Main],
            "heavier uranium deflects less than steel at the same nominal incidence, impulse and charge");
        SelectGuard(TutorialShieldPreset.Unshielded, 0);
    }

    static float XmlFloat(XmlNode node, string path, float fallback = 0f) => node.SelectSingleNode(path) is { } value
        ? float.Parse(value.InnerText, System.Globalization.CultureInfo.InvariantCulture) : fallback;

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
            var walls = platform.SelectNodes("/MapPrefab/thingStates/li[def='GravshipHull']/pos").Cast<XmlNode>()
                .Select(node => IntVec3.FromString(node.InnerText)).ToHashSet();
            bool WallAt(int x, int z) => walls.Contains(new IntVec3(x,0,z));
            var westPort = IntVec3.FromString(platform.SelectSingleNode("/MapPrefab/thingStates/li[def='DeltaV_DockingPort']/pos").InnerText);
            Check(westPort == new IntVec3(0,0,2) && door.Position == new IntVec3(6,0,2)
                && terminal.Position == new IntVec3(4,0,4), "the briefing room occupies the southwest corner");
            Check(Enumerable.Range(0,7).All(x => WallAt(x,0) && WallAt(x,5))
                && Enumerable.Range(1,4).Where(z => z != 2).All(z => WallAt(0,z) && WallAt(6,z))
                && !walls.Any(c => c.x > 0 && c.x < 6 && c.z > 0 && c.z < 5),
                "the sealed entrance shares the west and south outer walls and adds only two partitions");
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
        var weaponDefinitions = new XmlDocument();
        weaponDefinitions.Load(Path.Combine(directory,"../../1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml"));
        RangeEntrance(platform,harmony,directory);
        var weaponNames = new[] { RangeRapidDef, RangeHeavyDef, RangeLaserDef, RangeRailDef };
        int group=0;
        var cannons=new List<RangeWeaponSave>();
        var rangeLanguage=StatsFixtureLanguage(directory,"English");
        foreach (string name in weaponNames)
        {
            group++;
            var config=sourceDefs[name];
            var comps=config["comps"];
            var weaponConfig=comps.SelectSingleNode("li[@Class='DeltaV.CompProperties_SpaceWeapon']");
            var settings=group<=3 ? DirectXmlToObject.ObjectFromXml<CompProperties_WeaponTuning>(
                comps.SelectSingleNode("li[@Class='DeltaV.CompProperties_WeaponTuning']"),false) : null;
            var heads=RangeSupply(comps,GunSupplyKind.Heads);
            var charge=RangeSupply(comps,GunSupplyKind.Propellant);
            if(group==4)
                Check(comps.SelectSingleNode("li[@Class='DeltaV.CompProperties_GunSupply'][kind='Heads']/fuelFilter/thingDefs/li")
                    .InnerText=="DeltaV_UraniumHead","the east-end railgun uses its uranium-only internal compartment");
            foreach(XmlNode state in platform.SelectNodes("/MapPrefab/thingStates/li[def='"+name+"']/state/saveable"))
            {
            Check(state != null && state["groupNumber"].InnerText == group.ToString(), "four distinct fire-control groups");
            var building=Raw<Building>(); building.def=Raw<ThingDef>(); building.def.defName=name;
            Set(building,"factionInt",new Faction {def=new FactionDef()});
            Set(building,"rotationInt",Rot4.East);
            var position=IntVec3.FromString(state["pos"].InnerText);
            Set(building,"positionInt",position);
            if (settings!=null)
                building.def.size=IntVec2.FromString(SourceChain(name).Select(node=>node["size"]).First(node=>node!=null).InnerText);
            var doc=new XmlDocument(); doc.LoadXml(state.OuterXml);
            string file=Path.GetTempFileName();
            var priorLanguage=LanguageDatabase.activeLanguage;
            try
            {
                File.WriteAllText(file,"<root><weapon>"+doc.DocumentElement.InnerXml+"</weapon></root>");
                RangeWeaponSave savedWeapon=null;
                LanguageDatabase.activeLanguage=rangeLanguage;
                var weaponProps = new CompProperties_SpaceWeapon {
                    range=XmlFloat(weaponConfig,"range"), fireArcDeg=XmlFloat(weaponConfig,"fireArcDeg"),
                    lockArcDeg=XmlFloat(weaponConfig,"lockArcDeg",-1f),
                    defaultFireDeviationDeg=XmlFloat(weaponConfig,"defaultFireDeviationDeg",1f),
                    defaultManualFireDeviationDeg=XmlFloat(weaponConfig,"defaultManualFireDeviationDeg",-1f),
                    shotEnergy=XmlFloat(weaponConfig,"shotEnergy") };
                Scribe.loader.InitLoading(file); Scribe_Deep.Look(ref savedWeapon,"weapon",building,settings,heads,charge,weaponProps);
                Scribe.loader.FinalizeLoading();
                var weapon=savedWeapon.weapon;
                Check(weapon.groupNumber==group && weapon.coverage.arc==(group==4 ? 360f : 90f)
                    && weapon.coverage.range==(group==4 ? 0f : 150f),
                    "native weapon Scribe preserves group and each weapon's firing sector");
                var expectedBank=group==4 || position.z<22 ? SpaceWeaponSalvoSet.Secondary : SpaceWeaponSalvoSet.Main;
                Check(weapon.salvoSet==expectedBank,"native Scribe restores northern main, southern secondary, and east-end secondary rail");
                if (group == 4)
                    Check(weapon.CoverageError(Mathf.PI) == 0f && weapon.manualFireDeviationDeg == 180f && weapon.fireDeviationDeg == 1f,
                        "trial rail uses unrestricted manual pointing and range, with the native one-degree automatic tolerance");
                else
                    Check(weapon.CoverageError(0f)==0f && weapon.CoverageError(Mathf.PI)>100f
                        && weapon.CoverageError(46f*Mathf.Deg2Rad)>0f,"turret rejects the station behind the platform and off-sector aim");
                Check(!weapon.CompGetGizmosExtra().Any(),"allied weapon has no player settings gizmos");
                if (heads!=null) Check(savedWeapon.supply.Fuel==heads.fuelCapacity
                    && savedWeapon.supply.TargetFuelLevel==heads.fuelCapacity,"full head stock and loading target survive native Scribe");
                if (charge!=null) Check(savedWeapon.propellant.Fuel==charge.fuelCapacity
                    && savedWeapon.propellant.TargetFuelLevel==charge.fuelCapacity,"full propellant stock and loading target survive native Scribe");
                if (group == 3)
                {
                    Set(building, "comps", new List<ThingComp> { weapon, savedWeapon.tuning });
                    int expectedPower = expectedBank == SpaceWeaponSalvoSet.Main ? 3 : 6;
                    Check(settings.maxPower == 6 && savedWeapon.tuning.power == expectedPower
                        && weapon.ShotEnergy == expectedPower * 2f,
                        "the stock medium laser restores main power 3 or secondary power 6 and spends 2 Wd per power");
                }
                if (group<=2)
                {
                    weapon.WeaponProps.projectileDef=Raw<ThingDef>();
                    weapon.WeaponProps.projectileDef.defName=weaponConfig["projectileDef"].InnerText;
                    weapon.WeaponProps.armorPiercingProjectileDef=Raw<ThingDef>();
                    weapon.WeaponProps.armorPiercingProjectileDef.defName=weaponConfig["armorPiercingProjectileDef"].InnerText;
                    weapon.WeaponProps.armorPiercingHeadDef=RangeAmmoDef(weaponConfig["armorPiercingHeadDef"].InnerText);
                    weapon.WeaponProps.armorPiercingUnitMass=float.Parse(weaponConfig["armorPiercingUnitMass"].InnerText,
                        System.Globalization.CultureInfo.InvariantCulture);
                    weapon.WeaponProps.armorPiercingPenetrationScale=float.Parse(weaponConfig["armorPiercingPenetrationScale"].InnerText,
                        System.Globalization.CultureInfo.InvariantCulture);
                    Check(weapon.WeaponProps.armorPiercingProjectileDef.defName==weapon.WeaponProps.projectileDef.defName+"Uranium",
                        "the cannon has its matching armour-piercing projectile");
                    weapon.WeaponProps.attachmentSlots=weaponConfig.SelectNodes("attachmentSlots/li/cell").Cast<XmlNode>()
                        .Select(slot=>new SpaceWeaponAttachmentSlot { cell=IntVec3.FromString(slot.InnerText) }).ToList();
                    Check(group==2 ? savedWeapon.tuning.charge==48 && savedWeapon.tuning.mass==48
                        : savedWeapon.tuning.charge==4 && savedWeapon.tuning.mass==10,
                        "native Scribe restores the matched training charge and count of heads");
                    cannons.Add(savedWeapon);
                }
                if (state["gun"]!=null)
                    Check(state["gun"]["def"].InnerText.StartsWith("DeltaV_GroundGun_")
                        && state["gun"].SelectSingleNode("verbTracker/verbs/li").Attributes["Class"].Value=="DeltaV.Verb_SpaceTurretGround",
                        "turret internal gun and verb match current Def family");
            }
            finally { LanguageDatabase.activeLanguage=priorLanguage; Scribe.ForceStop(); File.Delete(file); }
        }
        }
        var shots = RangeAmmunition(platform,weaponDefinitions,cannons,directory);
        Check(platform.SelectNodes("/MapPrefab/thingStates/li[def='DeltaV_Weapon_RailgunAccelerator']").Count==9,"railgun has nine real accelerators");
        Check(platform.SelectSingleNode("/MapPrefab/thingStates/li[def='PilotConsole']/state/saveable/combat/fireControlMode").InnerText=="Disabled",
            "platform stores disabled fire control in the native combat record");
        Check(!new ShipCombatState().weaponsArmed
            && new ShipCombatState().fireControlMode==ShipFireControlMode.Automatic,
            "new player ships default to disarmed with automatic fire control preselected");
        var armour = target.SelectNodes("/MapPrefab/thingStates/li[def='Wall' and stuff='Steel']/pos").Cast<XmlNode>()
            .Select(n => IntVec3.FromString(n.InnerText)).Where(c => c.x < 6).ToHashSet();
        Check(armour.Count == 264 && Enumerable.Range(0, 44).All(z => Enumerable.Range(0, 6)
            .All(x => armour.Contains(new IntVec3(x, 0, z)))), "six uninterrupted steel layers on the west face");
        var paint = target.SelectNodes("/MapPrefab/thingStates/li[state/saveable/paintColorDef]").Cast<XmlNode>().ToList();
        Check(paint.Count == 44 && paint.All(n => n["def"].InnerText == "Wall" && IntVec3.FromString(n["pos"].InnerText).x == 0)
            && paint.Select(n => n.SelectSingleNode("state/saveable/paintColorDef").InnerText).Distinct().Count() == 3,
            "all colour bands are painted on the real west wall");
        var batteries = platform.SelectNodes("/MapPrefab/thingStates/li[def='Battery']").Cast<XmlNode>().ToList();
        Check(batteries.Count == 60 && batteries.All(b => b["rotation"].InnerText == "1")
            && batteries.GroupBy(b => IntVec3.FromString(b["pos"].InnerText).z).OrderBy(row => row.Key)
                .Select(row => (row.Key,row.Count())).SequenceEqual(new[] {(1,15),(3,15),(5,15),(7,15)}),
            "the 36000 Wd bank has four horizontal rows in the south cabin");
        foreach (XmlDocument layout in new[] { platform, target })
        {
            IntVec2 size = IntVec2.FromString(layout.SelectSingleNode("/MapPrefab/prefab/size").InnerText);
            var deck = layout.SelectNodes("/MapPrefab/foundations/li/pos").Cast<XmlNode>()
                .Select(pos => IntVec3.FromString(pos.InnerText)).ToHashSet();
            Check(deck.Count == size.x * size.z && deck.All(c => c.x >= 0 && c.x < size.x && c.z >= 0 && c.z < size.z),
                "the platform and target are complete rectangular decks without extensions");
        }
        Check(!platform.SelectNodes("/MapPrefab/thingStates/li/def").Cast<XmlNode>()
            .Any(n => n.InnerText.Contains("PawnSpawner")), "shared platform prefab has no default pawn marker");

        CompSpaceShield LoadShield(XmlNode entry, int id)
        {
            var building = Raw<Building>();
            building.def = Raw<ThingDef>();
            building.thingIDNumber = id;
            building.def.size = new IntVec2(3, 3);
            Set(building, "mapIndexOrState", (sbyte)-1);
            Set(building, "positionInt", IntVec3.FromString(entry["pos"].InnerText));
            Set(building, "rotationInt", new Rot4(int.Parse(entry["rotation"].InnerText)));
            string shieldPath = Path.GetTempFileName();
            RangeShieldSave savedShield = null;
            try
            {
                File.WriteAllText(shieldPath, "<root><shield>" + entry.SelectSingleNode("state/saveable").InnerXml + "</shield></root>");
                Scribe.loader.InitLoading(shieldPath);
                Scribe_Deep.Look(ref savedShield, "shield", building);
                Scribe.loader.FinalizeLoading();
            }
            finally { Scribe.ForceStop(); File.Delete(shieldPath); }
            return savedShield.shield;
        }
        const string shieldXPath = "/MapPrefab/thingStates/li[def='DeltaV_ShieldGenerator']";
        var platformShield = LoadShield(platform.SelectSingleNode(shieldXPath), 986);
        var shields = target.SelectNodes(shieldXPath).Cast<XmlNode>().Select((node,index) => LoadShield(node,987+index)).ToArray();
        Check(shields.OrderBy(s => s.parent.Position.z).Select(s => s.groundRadius).SequenceEqual(new[] {18f,14f,39f})
            && shields.All(s => s.parent.Rotation == Rot4.West && s.mode == SpaceShieldMode.Guard && !s.FlickedOn
                && s.RegularCapacity == 750 && s.EmergencyCapacity == 750),
            "native Scribe loads three distinct target emitters with full capacities and flicked off");

        var definitions = new XmlDocument();
        definitions.Load(Path.Combine(directory, "../../1.6/Defs/Tutorial/DeltaV_TutorialRange.xml"));
        var presets = definitions.SelectNodes("/Defs/ThingDef/comps/li[@Class='DeltaV.CompProperties_TutorialShieldTerminal']")
            .Cast<XmlNode>().ToList();
        var terminals = presets.Select(n => new CompTutorialShieldTerminal {
            props = DirectXmlToObject.ObjectFromXml<CompProperties_TutorialShieldTerminal>(n, false) }).ToList();
        var terminalColumns = new Dictionary<TutorialShieldPreset, int> {
            [TutorialShieldPreset.Unshielded]=10, [TutorialShieldPreset.Kinetic]=16, [TutorialShieldPreset.Electromagnetic]=22,
            [TutorialShieldPreset.GuardSingle]=28, [TutorialShieldPreset.Guard]=28 };
        var kineticPairs = new[] {(0f,35),(-45f,7),(-45f,35),(-45f,100)};
        var electricPairs = new[] {(0f,20),(-45f,20),(-45f,50),(-45f,100)};
        foreach (var mode in new[] {TutorialShieldPreset.Kinetic,TutorialShieldPreset.Electromagnetic})
            Check(terminals.Where(t => t.Preset == mode).Select(t => (CompProperties_TutorialShieldTerminal)t.props)
                .Select(p => (p.angleDeg,p.strengthPercent)).SequenceEqual(mode == TutorialShieldPreset.Kinetic ? kineticPairs : electricPairs),
                "the four presets in each line-shield column match their authored angles and strength");
        var columnRows = new Dictionary<int, int>();
        for (int i=0; i<presets.Count; i++)
        {
            string defName = presets[i].ParentNode.ParentNode["defName"].InnerText;
            var terminal = platform.SelectNodes("/MapPrefab/thingStates/li[def='" + defName + "']").Cast<XmlNode>().Single();
            int column = terminalColumns[terminals[i].Preset];
            int row = columnRows.GetValueOrDefault(column);
            var expected = new IntVec3(column, 0, 15-2*row);
            columnRows[column] = row+1;
            Check(IntVec3.FromString(terminal["pos"].InnerText) == expected && terminal["rotation"].InnerText == "2",
                "terminal is aligned with its assigned column and row, with the interaction cell facing south");
        }
        RangeSightlines(game, platform, target, platformShield, shields, terminals, shots, directory);
        var saved=new TerminalSave(); saved.terminal.targetObjectId=435; saved.terminal.shieldThingId=987;
        var xml=new XmlDocument();xml.LoadXml(Scribe.saver.DebugOutputFor(saved));string temp=Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp,"<root><terminal>"+xml.DocumentElement.InnerXml+"</terminal></root>");
            Scribe.loader.InitLoading(temp);Scribe.EnterNode("terminal");var loaded=new TerminalSave();loaded.ExposeData();
            Scribe.ExitNode();Scribe.loader.FinalizeLoading();
            Check(loaded.terminal.targetObjectId==435 && loaded.terminal.shieldThingId==987,"terminal bindings survive native Scribe");
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
        HandoverChecks(game, harmony, directory);
    }

    sealed class HandoverSignals : ISignalReceiver
    {
        internal readonly List<string> Tags = new List<string>();
        public void Notify_SignalReceived(Signal signal) => Tags.Add(signal.tag);
    }

    static bool SkipHandoverMessage() => false;

    static void HandoverChecks(Game game, Harmony harmony, string prefabRoot)
    {
        var universe = game.GetComponent<UniverseManager>();
        var quest = new Quest { id = 85, acceptanceTick = 0 };
        var run = new TutorialRunState { quest = quest, stage = TutorialStage.Visit };
        var signals = new HandoverSignals();
        var oldResearch = game.researchManager;
        var oldLanguage = LanguageDatabase.activeLanguage;
        var message = AccessTools.Method(typeof(Messages), nameof(Messages.Message),
            new[] { typeof(string), typeof(MessageTypeDef), typeof(bool) });
        string accept = DeltaVTutorialService.Signal(quest, "AcceptShip");
        string completed = DeltaVTutorialService.Signal(quest, "Completed");
        string path = Path.GetTempFileName();
        universe.tutorialRun = run;
        game.signalManager.RegisterReceiver(signals);
        harmony.Patch(message, prefix: new HarmonyMethod(typeof(Program), nameof(SkipHandoverMessage)));
        try
        {
            LanguageDatabase.activeLanguage = StatsFixtureLanguage(prefabRoot, "English");
            game.researchManager = new ResearchManager();
            universe.Tutorial.Receive(quest, accept);
            Check(signals.Tags.Count == 0 && universe.Tutorial.Active,
                "handover keeps waiting until the original inspection result is set");

            game.researchManager.gravEngineInspected = true;
            universe.Tutorial.Receive(quest, accept);
            Check(signals.Tags.SequenceEqual(new[] { completed }),
                "the inspection result alone completes handover during the visit");

            signals.Tags.Clear();
            run.stage = TutorialStage.Flight;
            game.researchManager = new ResearchManager { gravEngineInspected = true };
            var document = new XmlDocument();
            document.LoadXml(Scribe.saver.DebugOutputFor(game.researchManager));
            File.WriteAllText(path, "<root><researchManager>" + document.DocumentElement.InnerXml + "</researchManager></root>");
            Scribe.loader.InitLoading(path);
            ResearchManager reloaded = null;
            Scribe_Deep.Look(ref reloaded, "researchManager");
            game.researchManager = reloaded;
            Scribe.loader.FinalizeLoading();
            Check(reloaded?.gravEngineInspected == true, "native Scribe retains the earlier inspection");

            run.stage = TutorialStage.Visit;
            universe.Tutorial.Receive(quest, accept);
            Check(signals.Tags.SequenceEqual(new[] { completed }),
                "the reloaded inspection from flight training completes handover without a pawn or target ship");
        }
        finally
        {
            Scribe.ForceStop();
            File.Delete(path);
            harmony.Unpatch(message, HarmonyPatchType.Prefix, harmony.Id);
            game.signalManager.DeregisterReceiver(signals);
            game.researchManager = oldResearch;
            LanguageDatabase.activeLanguage = oldLanguage;
            universe.Tutorial.Ended(quest);
        }
    }
}

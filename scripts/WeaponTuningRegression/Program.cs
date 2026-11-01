using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static class Program
{
    static int checks;
    static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static bool Near(float a, float b) => Math.Abs(a - b) < .001f * Math.Max(1, Math.Abs(b));
    static readonly MethodInfo Advance = typeof(CompWeaponTuning).GetMethod("AdvanceThermalTick", BindingFlags.Instance | BindingFlags.NonPublic);
    static void Tick(CompWeaponTuning gun, int ticks) { for (int i = 0; i < ticks; i++) Advance.Invoke(gun, null); }
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { Run(args[1]); Console.WriteLine($"PASS {checks} weapon checks"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static CompWeaponTuning Gun(XElement def)
    {
        var props = new CompProperties_WeaponTuning();
        foreach (var node in def.Element("comps").Elements("li").Single(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_WeaponTuning").Elements())
        {
            var field = props.GetType().GetField(node.Name.LocalName);
            field.SetValue(props, Convert.ChangeType(node.Value, field.FieldType, System.Globalization.CultureInfo.InvariantCulture));
        }
        var gun = new CompWeaponTuning(); gun.Initialize(props); return gun;
    }

    static CompSpaceWeaponMagazine Magazine(XElement def)
    {
        var props = new System.Collections.Generic.List<CompProperties>();
        foreach (var node in def.Element("comps").Elements("li").Where(x => (string)x.Attribute("Class") == "DeltaV.CompProperties_GunSupply"))
        {
            var supply = new CompProperties_GunSupply {
                kind = Enum.Parse<GunSupplyKind>((string)node.Element("kind")),
                fuelCapacity = (float)node.Element("fuelCapacity"),
                initialConfigurableTargetFuelLevel = (float)node.Element("initialConfigurableTargetFuelLevel"),
                fuelFilter = new ThingFilter()
            };
            supply.fuelFilter.SetAllow(new ThingDef { defName = node.Descendants("thingDefs").Single().Element("li").Value }, true);
            props.Add(supply);
        }
        props.Add(new CompProperties_SpaceWeaponMagazine());
        var thing = new ThingWithComps { def = new ThingDef { comps = props } };
        thing.InitializeComps();
        return thing.GetComp<CompSpaceWeaponMagazine>();
    }

    static void ExposeMagazine(CompSpaceWeaponMagazine magazine)
    {
        foreach (ThingComp comp in magazine.parent.AllComps) comp.PostExposeData();
    }
    static void Run(string repo)
    {
        DeepProfiler.enabled = false;
        var defs = XDocument.Load(Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceWeapons.xml"));
        XElement Def(string name) => defs.Root.Elements().Single(x => (string)x.Element("defName") == name);
        float v = SpaceKinetics.LaunchSpeed(1, 1), heavy = SpaceKinetics.LaunchSpeed(1, 16);
        Check(Near(v, 60) && Near(heavy, 15), "fixed charge: mass slows the projectile");
        Check(SpaceKinetics.ImpactFactor(16, heavy) > SpaceKinetics.ImpactFactor(1, v), "heavy shot gains penetration budget at equal charge");
        Check(Near(SpaceKinetics.Energy(16, heavy), SpaceKinetics.Energy(1, v)), "launch energy depends on charge, not head mass");
        Check(Near(SpaceKinetics.LaunchSpeed(4, 1), 120), "more charge increases speed");
        Check(SpaceKinetics.ImpactFactor(100, 0) == 0, "stationary fragments cause no impact damage");
        var effect = new ImpactOutcomePart { damage = 24, kineticMassScaling = true };
        Check(Near(SpaceImpactEffects.ComputeDamage(effect, heavy, 0, 1, 16), 96), "production damage uses actual mass and speed");
        foreach (float mass in new[] { 1f, 24f, 48f })
        {
            float reverse = SpaceKinetics.ReverseEnergy(50), incoming = SpaceKinetics.Energy(mass, 60);
            float cost = SpaceKinetics.ShieldCost(mass, 60, reverse, 2, true);
            float reflected = SpaceKinetics.AfterShield(mass, 60, cost, 2, true);
            Check(reflected < 0 && Near(SpaceKinetics.Energy(mass, reflected), reverse), "full payment assigns fixed reverse energy " + mass);
            Check(Near(SpaceKinetics.AfterShield(mass, 60, 2, 2, true), 60), "base-only payment cannot remove kinetic energy " + mass);
            float stopped = SpaceKinetics.AfterShield(mass, 60, 2 + incoming / SpaceKinetics.EnergyUnitsPerWattDay, 2, true);
            Check(Math.Abs(stopped) < .01f, "exact stopping payment has no reverse energy " + mass);
            Check(Near(SpaceKinetics.AfterShield(mass, 60, cost, 2, false), 60), "passive shield preserves fragment speed " + mass);
            Check(Near(3 * SpaceKinetics.Energy(mass / 3, reflected), reverse), "fragment group shares one reverse budget " + mass);
        }
        Check(SpaceKinetics.ShieldCost(16, 60, 0, 2, true) > SpaceKinetics.ShieldCost(1, 60, 0, 2, true), "same speed heavy shot costs more to stop");
        var rapid = Gun(Def("DeltaV_Weapon_RapidMainSmall"));
        var cannon = Gun(Def("DeltaV_Weapon_CannonTurret"));
        cannon.charge = cannon.mass = 1; cannon.CommitShot();
        Check(cannon.RemainingTicks >= 300 && cannon.overheated, "1/1 heavy shot retains mechanical cycle");
        cannon.charge = cannon.mass = 32;
        Tick(cannon, 299); Check(cannon.RemainingTicks == 1, "retuning cannot lengthen or shorten existing mechanical cycle");
        Tick(cannon, 1); Check(cannon.RemainingTicks == 0 && !cannon.overheated, "heavy cycle completes normally");
        cannon.CommitShot(); Check(cannon.RemainingTicks == 640, "high load extends heavy cooling beyond mechanical minimum");
        cannon.charge = cannon.mass = 1;
        Tick(cannon, 300); Check(cannon.overheated && cannon.RemainingTicks > 0, "lowering load cannot bypass thermal lock");
        Tick(cannon, 341); Check(cannon.RemainingTicks == 0 && !cannon.overheated, "heavy heat fully dissipates");
        rapid.interval = rapid.Settings.minInterval;
        for (int i = 0; i < 1000 && !rapid.overheated; i++) { if (rapid.RemainingTicks == 0) rapid.CommitShot(); Tick(rapid, 1); }
        Check(rapid.overheated, "rapid burst reaches overheat lock");
        rapid.interval = rapid.Settings.maxInterval; Tick(rapid, 200);
        Check(rapid.overheated && rapid.heat > 0 && rapid.heat < rapid.Settings.heatCapacity, "rapid remains locked below threshold until completely cold");
        Tick(rapid, 500); Check(!rapid.overheated && rapid.RemainingTicks == 0, "rapid unlocks only after full cooling");
        for (int i = 0; i < 2000; i++) { if (rapid.RemainingTicks == 0) rapid.CommitShot(); Tick(rapid, 1); }
        Check(!rapid.overheated, "lowest rate can balance continuous cooling");
        var laser = Gun(Def("DeltaV_Weapon_LaserTurret")); laser.power = 4; laser.CommitShot();
        Check(laser.heat == 0 && !laser.overheated && laser.RemainingTicks == 12, "laser has power tuning without added thermal system");
        var magazine = Magazine(Def("DeltaV_Weapon_CannonTurret"));
        var heads = magazine.Supply(GunSupplyKind.Heads);
        var propellant = magazine.Supply(GunSupplyKind.Propellant);
        Check(!magazine.HasHeads(1) && !magazine.HasPropellant(1), "new magazines start empty");
        Check(heads is CompRefuelable && propellant is CompRefuelable, "both inventories are native refuelable components");
        Check(heads.TargetFuelLevel == heads.Props.fuelCapacity && propellant.TargetFuelLevel == propellant.Props.fuelCapacity,
            "new loading targets default to physical capacity");
        heads.Refuel(37); propellant.Refuel(19);
        Check(magazine.HasHeads(37) && !magazine.HasHeads(38) && magazine.HasPropellant(19) && !magazine.HasPropellant(20), "independent integer stock gates full requested load");
        heads.TargetFuelLevel = 50; propellant.TargetFuelLevel = 22;
        Check(heads.Missing == 13 && propellant.Missing == 3, "refueling quantities respect independent configured targets");
        heads.TargetFuelLevel = 0;
        Check(heads.Missing == 0 && !heads.CanLoad(true) && heads.Fuel == 37, "zero target stops loading without deleting existing ammunition");
        propellant.TargetFuelLevel = 10;
        Check(propellant.Missing == 0 && !propellant.CanLoad(true), "lowering target while carrying prevents extra refueling");
        heads.TargetFuelLevel = 50; propellant.TargetFuelLevel = 22; heads.allowAutoRefuel = false;
        Check(!heads.CanLoad(false) && heads.CanLoad(true), "automatic loading toggle still permits forced loading");
        magazine.ConsumeHeads(7); magazine.ConsumePropellant(3);
        Check(heads.Fuel == 30 && propellant.Fuel == 16, "shot commit consumes the native stocks exactly once");
        heads.Refuel(7); propellant.Refuel(3);
        var headBar = new Gizmo_GunSupply(heads); var chargeBar = new Gizmo_GunSupply(propellant);
        var targetProperty = typeof(Gizmo_GunSupply).GetProperty("Target", BindingFlags.Instance | BindingFlags.NonPublic);
        var dragProperty = typeof(Gizmo_GunSupply).GetProperty("DraggingBar", BindingFlags.Instance | BindingFlags.NonPublic);
        targetProperty.SetValue(headBar, 0.251f);
        Check(heads.TargetFuelLevel == 151 && propellant.TargetFuelLevel == 22, "native slider sets integer target on the selected compartment only");
        dragProperty.SetValue(headBar, true);
        Check((bool)dragProperty.GetValue(headBar) && !(bool)dragProperty.GetValue(chargeBar), "dual native sliders do not share dragging state");
        cannon.charge = 21; cannon.mass = 27; cannon.CommitShot(); Tick(cannon, 43);
        string save = Path.Combine(repo, "Source/obj/WeaponTuningRegression/state.xml");
        Scribe.saver.InitSaving(save, "weapon"); cannon.PostExposeData(); ExposeMagazine(magazine); Scribe.saver.FinalizeSaving();
        var restored = Gun(Def("DeltaV_Weapon_CannonTurret"));
        var restoredMagazine = Magazine(Def("DeltaV_Weapon_CannonTurret"));
        Scribe.loader.InitLoading(save); restored.PostExposeData(); ExposeMagazine(restoredMagazine); Scribe.loader.FinalizeLoading();
        Check(restored.charge == 21 && restored.mass == 27 && restored.overheated
            && Near(restored.heat, cannon.heat) && restored.RemainingTicks == cannon.RemainingTicks, "Scribe round trip preserves tuning, heat and mechanical lock");
        Check(restoredMagazine.HeadCount == 37 && restoredMagazine.PropellantCount == 19, "Scribe round trip preserves both stock counts");
        var restoredHeads = restoredMagazine.Supply(GunSupplyKind.Heads);
        var restoredCharge = restoredMagazine.Supply(GunSupplyKind.Propellant);
        Check(restoredHeads.TargetFuelLevel == 151 && restoredCharge.TargetFuelLevel == 22
            && !restoredHeads.allowAutoRefuel && restoredCharge.allowAutoRefuel, "Scribe preserves independent native targets and auto-refuel switches");
        var pose = new SpacePose(Vector3.forward, .3f) { velocity = new Vector2(8, -2), angularVelocity = .2f };
        Vector3 contact = SpaceTransform.ShipLocalToUniverse(pose, new Vector2(10, 0));
        var incomingPose = new SpacePose(contact, 0) { velocity = SpaceTransform.VelocityAtUniversePoint(pose, contact) + new Vector2(30, 4) };
        Check((SpaceTransform.RelativeVelocityAtContact(incomingPose, pose, contact) - new Vector2(30, 4)).magnitude < .001f, "contact velocity includes defender rotation");
        var common = new Vector2(20, 10);
        incomingPose.velocity += SpaceTransform.TransportTangent(common, pose.position, contact); pose.velocity += common;
        Check((SpaceTransform.RelativeVelocityAtContact(incomingPose, pose, contact) - new Vector2(30, 4)).magnitude < .001f, "common drift does not change impact energy");
        var recipes = XDocument.Load(Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_GunSupplies.xml"));
        Check(recipes.Root.Elements("RecipeDef").All(x => (float)x.Element("ingredients").Element("li").Element("count") == 10
            && (int)x.Element("products").Elements().Single() == 200), "batch economy: 10 material yields 200 components");
        Check((string)recipes.Root.Elements("RecipeDef").First().Descendants("thingDefs").First().Element("li") == "Steel", "propellant requires steel, not fuel");
        var effects = XDocument.Load(Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml"));
        Check(effects.Root.Elements().Where(x => x.Element("projectileDefs")?.Elements().Any(y => y.Value == "DeltaV_Projectile_Cannon") == true)
            .All(x => x.Element("projectileDefs").Elements().Any(y => y.Value == "DeltaV_Projectile_Rapid")), "rapid and heavy share every impact definition");
        CheckTurretPowerAndShieldFacing();
    }

    static void CheckTurretPowerAndShieldFacing()
    {
        Game previous = Current.Game;
        Current.Game = (Game)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Game));
        Current.Game.tickManager = (TickManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TickManager));
        var ticks = typeof(TickManager).GetField("ticksGameInt", BindingFlags.Instance | BindingFlags.NonPublic);
        var powered = typeof(CompPowerTrader).GetField("powerOnInt", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            var body = new ThingWithComps { def = new ThingDef { comps = new() {
                new CompProperties_SpaceWeaponTraverse(),
                new CompProperties_Power { compClass = typeof(CompPowerTrader) }
            } } };
            body.InitializeComps();
            var traverse = body.GetComp<CompSpaceWeaponTraverse>();
            var power = body.GetComp<CompPowerTrader>();
            ticks.SetValue(Find.TickManager, 1);
            traverse.AimAt(1f);
            ticks.SetValue(Find.TickManager, 2);
            traverse.AimAtVanilla(1f);
            Check(traverse.CurrentAngle == 0f, "unpowered manual and vanilla aiming preserve turret heading");
            powered.SetValue(power, true);
            traverse.AimAtVanilla(1f);
            Check(traverse.CurrentAngle > 0f, "power restoration resumes turning without waiting for a new target");
            float stopped = traverse.CurrentAngle;
            powered.SetValue(power, false);
            traverse.AimAt(-1f);
            Check(traverse.CurrentAngle == stopped, "power loss in the same tick prevents another turn");
            powered.SetValue(power, true);
            ticks.SetValue(Find.TickManager, 3);
            traverse.AimAt(1f);
            Check(traverse.CurrentAngle > stopped, "powered space aiming still advances the turret");

            var projector = new Building { def = new ThingDef { thingClass = typeof(Building), comps = new() {
                new CompProperties_SpaceShield()
            }, size = new IntVec2(3, 3), building = new BuildingProperties { turretTopDrawSize = 2.8f } } };
            projector.InitializeComps();
            var shield = projector.GetComp<CompSpaceShield>();
            Check(ReferenceEquals(SpaceTurretTopRenderer.SourceFor(projector), shield),
                "ordinary Building restored from prefab resolves its shield component as the top source");
            Check(SpaceTurretTopRenderer.Supports(projector.def),
                "shell render rule accepts the ordinary Building plus shield component contract");
            foreach (Rot4 rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            foreach (float angle in new[] { -90f, -30f, 0f, 60f, 90f })
            {
                projector.Rotation = rotation;
                shield.mode = SpaceShieldMode.Kinetic;
                shield.angleDeg = angle;
                shield.GetSegment(out _, out _, out Vector2 normal);
                Matrix4x4 local = SpaceTurretTopRenderer.MatrixFor(projector, shield, Vector3.zero,
                    Vector3.zero, 0f, Matrix4x4.identity, Quaternion.identity);
                Vector3 direction = local.MultiplyVector(Vector3.forward).normalized;
                var renderedForward = new Vector2(direction.x, direction.z);
                Check((renderedForward - normal).sqrMagnitude < .00001f,
                    $"shared turret artwork faces real shield normal: {rotation}, {angle}");
                Quaternion turn = Quaternion.Euler(0f, 37f, 0f);
                Matrix4x4 transform = Matrix4x4.TRS(new Vector3(13f, 0f, -8f), turn, Vector3.one);
                Matrix4x4 shell = SpaceTurretTopRenderer.MatrixFor(projector, shield, Vector3.zero,
                    Vector3.zero, 0f, transform, turn);
                Check((shell.MultiplyPoint3x4(Vector3.forward) - transform.MultiplyPoint3x4(
                    local.MultiplyPoint3x4(Vector3.forward))).sqrMagnitude < .00001f,
                    $"shell top uses the transformed map draw geometry: {rotation}, {angle}");
            }
        }
        finally { Current.Game = previous; }
    }
}

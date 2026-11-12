using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // Reuse the frozen hull only after its movement measurement. Added equipment is
    // removed before the independent danger-sector fixture starts.
    static void CheckEngagementComposition(UniverseManager universe, ManeuverMotionObject own,
        ManeuverMotionObject target, CompSpaceWeapon laser, Faction player, Faction enemy)
    {
        var patch = new Harmony("DeltaV.Regression.Composition");
        patch.Patch(typeof(CompProjectileInterceptor).TypeInitializer,
            prefix: new HarmonyMethod(typeof(Program), nameof(BattleSceneOnly)));
        var cannonDef = TestBuildingDef("compositionCannonRound");
        var props = new CompProperties_SpaceProjectile();
        var node = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceProjectiles.xml").Root.Elements()
            .Single(n => (string)n.Element("defName") == "DeltaV_Projectile_Cannon")
            .Element("comps").Elements().Single(n => (string)n.Attribute("Class") == "DeltaV.CompProperties_SpaceProjectile");
        foreach (var value in node.Elements())
        {
            var field = props.GetType().GetField(value.Name.LocalName);
            if (field == null || !(field.FieldType.IsPrimitive || field.FieldType.IsEnum)) continue;
            field.SetValue(props, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.Value)
                : Convert.ChangeType(value.Value, field.FieldType, CultureInfo.InvariantCulture));
        }
        cannonDef.comps.Add(props);
        var effects = XDocument.Load("1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        void Impact(string name, ThingDef projectile, ThingDef receiver = null)
        {
            var effectNode = effects.Root.Elements().Single(n => (string)n.Element("defName") == name);
            var effect = new SpaceImpactEffectDef { defName = "Composition_" + (string)effectNode.Element("defName"),
                projectileDefs = new List<ThingDef> { projectile }, targetThingDefs = receiver == null ? null : new List<ThingDef> { receiver },
                targetsSubstructure = (bool?)effectNode.Element("targetsSubstructure") ?? false,
                substructureHitChance = (float?)effectNode.Element("substructureHitChance") ?? SpaceImpactEffectDef.DefaultSubstructureHitChance,
                attackerEffect = new ImpactOutcomePart { kind = ImpactEffectKind.DestroySelf }, defenderEffect = new ImpactOutcomePart() };
            foreach (var value in effectNode.Element("defenderEffect").Elements())
            {
                var field = typeof(ImpactOutcomePart).GetField(value.Name.LocalName);
                field.SetValue(effect.defenderEffect, field.FieldType == typeof(DamageDef) ? DamageDefOf.Bullet
                    : field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.Value)
                    : Convert.ChangeType(value.Value, field.FieldType, CultureInfo.InvariantCulture));
            }
            DefDatabase<SpaceImpactEffectDef>.Add(effect);
        }
        Impact("DeltaV_Impact_CannonVsStructure", cannonDef);
        Impact("DeltaV_Impact_CannonVsSubstructure", cannonDef);
        SpaceImpactEffects.ResetCache();
        var cannon = new CompSpaceWeapon { props = new CompProperties_SpaceWeapon { projectileDef = cannonDef,
            launchKineticEnergy = SpaceKinetics.Energy(32, 60), range = 120, intrinsicAimSkill = 20, topTurret = true }, safeFire = false };
        var gun = AddTestBuilding(own.map, player, 157001, TestBuildingDef("compositionGun"), cannon);
        MoveCombatFixture(gun, new IntVec3(9, 0, 12));
        gun.def.useHitPoints = true; gun.def.fillPercent = 1; Set(typeof(Thing), gun, "hitPointsInt", 300);
        var shield = new CompSpaceShield { props = new CompProperties_SpaceShield { regularCapacity = 50000 },
            mode = SpaceShieldMode.Kinetic, projection = 10, length = 20,
            kineticStrengthPercent = 100, electromagneticStrengthPercent = 100 };
        var projector = AddTestBuilding(target.map, enemy, 157002, TestBuildingDef("compositionShield"), shield);
        MoveCombatFixture(projector, new IntVec3(12, 0, 9)); projector.Rotation = Rot4.East;
        projector.def.useHitPoints = true; projector.def.fillPercent = 1; Set(typeof(Thing), projector, "hitPointsInt", 500);
        var battery = new CompPowerBattery { parent = projector, props = new CompProperties_Battery { storedEnergyMax = 100000 } };
        Set(battery, "storedEnergy", 100000f);
        var net = (PowerNet)Raw(typeof(PowerNet)); net.batteryComps = new List<CompPowerBattery> { battery };
        net.powerComps = new List<CompPowerTrader>();
        var power = new CompPowerTrader { parent = projector, props = new CompProperties_Power(), transNet = net };
        Set(power, "powerOnInt", true);
        var interceptor = (CompProjectileInterceptor)Raw(typeof(CompProjectileInterceptor));
        interceptor.parent = projector; interceptor.props = new CompProperties_ProjectileInterceptor { radius = 14, hitPoints = 0 };
        interceptor.currentHitPoints = -1;
        Set(interceptor, "lastInterceptTicks", -999999); Set(interceptor, "startedChargingTick", -1);
        projector.AllComps.AddRange(new ThingComp[] { battery, power, interceptor }); shield.InitializeGuard();
        Impact("DeltaV_Impact_VsShield", cannonDef, projector.def);
        Impact("DeltaV_Impact_LaserVsShield", laser.WeaponProps.projectileDef, projector.def);
        SpaceImpactEffects.ResetCache();
        int originalInterval = laser.Tuning.interval, originalRemaining = laser.Tuning.RemainingTicks;
        float laserEnergy = laser.WeaponProps.shotEnergy, cannonEnergy = cannon.WeaponProps.shotEnergy;
        try
        {
            var settings = CombatEngagementSettings.Default();
            var other = ShipRegistry.ShipPose(target.member); other.angle -= target.member.FrameAngle;
            (CombatEngagementEvaluation.Result rear, CombatEngagementEvaluation.Result side) Evaluate(bool mostlyLaser,
                bool suppressCannon = false, bool suppressLaser = false)
            {
                laser.Tuning.interval = mostlyLaser ? 90 : 1800;
                laser.WeaponProps.shotEnergy = suppressLaser ? 1000000 : laserEnergy;
                cannon.WeaponProps.shotEnergy = suppressCannon ? 1000000 : cannonEnergy;
                cannon.WeaponProps.cooldownTicks = mostlyLaser ? 3600 : 90;
                SubstructureOwnership.BumpStructureVersion();
                var evaluator = new CombatEngagementEvaluation(universe, own.member, target.member, other,
                    Array.Empty<ShipBase>(), true);
                CombatEngagementEvaluation.Result At(float phase) => evaluator.Evaluate(new SpacePose(
                    OrbitSphere.Move(other.position, new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * 100), phase + Mathf.PI));
                var values = (rear: At(0), side: At(Mathf.PI / 2));
                Console.WriteLine($"COMPOSITION mode={shield.mode} laser={mostlyLaser} suppress={suppressCannon}/{suppressLaser} out={values.rear.outgoing:F1}/{values.side.outgoing:F1} score={values.rear.Score(settings):F1}/{values.side.Score(settings):F1}");
                return values;
            }
            var laserKinetic = Evaluate(true); var cannonKinetic = Evaluate(false);
            Check(laserKinetic.rear.Score(settings) > laserKinetic.side.Score(settings)
                && cannonKinetic.rear.Score(settings) < cannonKinetic.side.Score(settings),
                "actual mixed battery cadence changes the preferred side between kinetic-shielded thin armor and unshielded thick armor");
            var savedPose = own.pose;
            float Plan(bool mostlyLaser)
            {
                laser.Tuning.interval = mostlyLaser ? 90 : 1800; cannon.WeaponProps.cooldownTicks = mostlyLaser ? 3600 : 90;
                Set(laser.Tuning, "remainingTicks", 0); cannon.cooldownUntilTick = 0;
                // Start both batteries on the kinetic shield's verified protected approach.
                own.pose = new SpacePose(OrbitSphere.Move(other.position, Vector2.right * 100), Mathf.PI);
                SubstructureOwnership.BumpStructureVersion();
                var planner = new ShipAutoManeuverControl(universe, own.member.Identity);
                Set(planner, "anchorTarget", CombatTarget.ForShip(target.member.Identity));
                Set(planner, "anchorDistance", 100f); Set(planner, "offenseEnabled", true);
                own.TryGetDrive(own.member.Identity, out var drive);
                object[] args = { own, ShipRegistry.ShipPose(target.member), 10f, drive, null };
                Check((bool)typeof(ShipAutoManeuverControl).GetMethod("PlanTacticTrajectory", Private).Invoke(planner, args),
                    "mixed composition executes the common planner and flight-reference path");
                return ((CombatReposition)typeof(ShipAutoManeuverControl).GetField("reposition", Private).GetValue(planner)).phase;
            }
            try
            {
                float laserPhase = Plan(true), cannonPhase = Plan(false);
                Console.WriteLine($"COMPOSITION_PLAN laser={laserPhase * Mathf.Rad2Deg:F1} cannon={cannonPhase * Mathf.Rad2Deg:F1}");
                Check(Mathf.Abs(SpaceTransform.WrapAngle(laserPhase)) < Mathf.PI / 4
                    && Mathf.Abs(SpaceTransform.WrapAngle(cannonPhase)) > Mathf.PI / 4,
                    "common maneuver planner changes actual selected bearing with weapon composition on the same protected hull");
            }
            finally { own.pose = savedPose; }
            var onlyLaserKinetic = Evaluate(true, suppressCannon: true);
            var onlyCannonKinetic = Evaluate(false, suppressLaser: true);
            shield.mode = SpaceShieldMode.Electromagnetic;
            var laserEm = Evaluate(true); var cannonEm = Evaluate(false);
            var onlyLaserEm = Evaluate(true, suppressCannon: true);
            var onlyCannonEm = Evaluate(false, suppressLaser: true);
            Check(onlyLaserEm.rear.outgoing < onlyLaserKinetic.rear.outgoing
                && onlyCannonEm.rear.outgoing > onlyCannonKinetic.rear.outgoing,
                "electromagnetic replacement blocks the available laser battery while allowing kinetic shots through");
            Check(laserEm.rear.contributions.Where(c => c.family == CombatWeaponFamily.Laser).Sum(c => c.damage)
                == onlyLaserEm.rear.outgoing && cannonEm.rear.outgoing > onlyLaserEm.rear.outgoing,
                "one weapon's estimated hit does not change another weapon's shield snapshot");
            shield.mode = SpaceShieldMode.Guard;
            var laserGuard = Evaluate(true); var cannonGuard = Evaluate(false);
            Check(laserGuard.rear.outgoing == 0 && laserGuard.side.outgoing == 0
                && cannonGuard.rear.outgoing == 0 && cannonGuard.side.outgoing == 0
                && laserGuard.rear.contributions.Sum(c => c.guard) > 0 && cannonGuard.side.contributions.Sum(c => c.guard) > 0,
                "guard capacity protects both sides against both real weapon mixtures while retaining nonzero shield-drain value");
            Check(battery.StoredEnergy == 100000 && shield.RegularCapacity == 50000 && projector.HitPoints == 500,
                "candidate composition matrix never consumes real shield power, capacity or hull HP");
        }
        finally
        {
            laser.WeaponProps.shotEnergy = laserEnergy;
            cannon.WeaponProps.shotEnergy = cannonEnergy;
            laser.Tuning.interval = originalInterval;
            Set(laser.Tuning, "remainingTicks", originalRemaining);
            foreach (var building in new[] { gun, projector })
            {
                var map = building.Map; map.thingGrid.Deregister(building);
                map.listerThings.AllThings.Remove(building); map.listerThings.ThingsOfDef(building.def).Remove(building);
                ((ThingOwner<Thing>)map.spawnedThings).InnerListForReading.Remove(building);
                Set(typeof(Thing), building, "mapIndexOrState", (sbyte)-2);
            }
            SubstructureOwnership.BumpStructureVersion();
            patch.UnpatchAll(patch.Id);
        }
    }
}

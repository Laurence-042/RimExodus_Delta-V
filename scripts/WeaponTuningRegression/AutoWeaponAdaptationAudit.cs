using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DeltaV;
using RimWorld;

internal static partial class Program
{
    private sealed class AdaptationPhysics : IAutoSpaceWeaponPhysics
    {
        private const int Limit = 1000000;
        private readonly ImpactOutcomePart steel;
        private readonly ImpactOutcomePart uranium;

        internal AdaptationPhysics(ImpactOutcomePart steel, ImpactOutcomePart uranium)
        {
            this.steel = steel;
            this.uranium = uranium;
        }

        public int HeadsForBlastDamage(float target, out bool safetyLimited)
        {
            int lo = 1, hi = 1;
            while (hi < Limit && BlastDamage(hi) + .0001f < target) hi = Math.Min(Limit, hi * 2);
            safetyLimited = hi == Limit && BlastDamage(hi) + .0001f < target;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (BlastDamage(mid) + .0001f >= target) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        public int ChargeForPenetrationBudget(float target, float projectileMass, out bool safetyLimited)
        {
            int lo = 1, hi = 1;
            while (hi < Limit && PenetrationBudget(hi, projectileMass) + .0001f < target)
                hi = Math.Min(Limit, hi * 2);
            safetyLimited = hi == Limit && PenetrationBudget(hi, projectileMass) + .0001f < target;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (PenetrationBudget(mid, projectileMass) + .0001f >= target) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        public float BlastDamage(int heads) => SpaceImpactEffects.ComputeDamage(steel,
            SpaceKinetics.LaunchSpeed(1, Math.Max(1, heads)), 0, 1, Math.Max(1, heads));

        public float PenetrationBudget(int charge, float projectileMass) => SpaceImpactEffects.ComputeDamage(uranium,
            SpaceKinetics.LaunchSpeed(Math.Max(1, charge), Math.Max(.001f, projectileMass)),
            0, 1, Math.Max(.001f, projectileMass));

        public float BlastDamageUpperBound => steel.highExplosive.damageByMass.Points.Max(point => point.y);
        public float PenetrationBudgetUpperBound => uranium.penetrationMaximum;
    }

    private static AutoSpaceWeaponSource CoreTurretSource(XDocument core, string turretName)
    {
        var all = core.Root.Elements("ThingDef").Where(x => x.Element("defName") != null
            || x.Attribute("Name") != null).ToList();
        XElement Named(string name) => all.Single(x => (string)x.Element("defName") == name
            || (string)x.Attribute("Name") == name);
        XElement turret = Named(turretName);
        XElement Inherited(XElement def, Func<XElement, XElement> select)
        {
            for (XElement current = def; current != null;)
            {
                XElement value = select(current);
                if (value != null) return value;
                string parent = (string)current.Attribute("ParentName");
                current = parent == null ? null : all.SingleOrDefault(x => (string)x.Attribute("Name") == parent);
            }
            return null;
        }
        XElement InheritedField(XElement def, string container, string field) =>
            Inherited(def, x => x.Element(container)?.Element(field));
        XElement InheritedComp(XElement def, string className) => Inherited(def, x =>
            x.Element("comps")?.Elements("li").FirstOrDefault(y => (string)y.Attribute("Class") == className));
        XElement gun = Named((string)InheritedField(turret, "building", "turretGunDef"));
        XElement verb = gun.Element("verbs").Elements("li").Single();
        XElement projectile = Named((string)verb.Element("defaultProjectile"));
        XElement projectileProps = projectile.Element("projectile");
        float damage = (float)projectileProps.Element("damageAmountBase");
        float armor = (float?)projectileProps.Element("armorPenetrationBase") ?? damage * .015f;
        XElement size = Inherited(turret, x => x.Element("size"));
        int area = 1;
        if (size != null)
        {
            string[] xy = size.Value.Trim('(', ')').Split(',');
            area = int.Parse(xy[0]) * int.Parse(xy[1]);
        }
        XElement cooldown = InheritedField(turret, "building", "turretBurstCooldownTime")
            ?? InheritedField(gun, "statBases", "RangedWeapon_Cooldown");
        XElement turretAccuracy = InheritedField(turret, "statBases", "ShootingAccuracyTurret");
        XElement longAccuracy = InheritedField(gun, "statBases", "AccuracyLong");
        XElement turretWarmup = InheritedField(turret, "building", "turretBurstWarmupTime");
        float averageTurretWarmup = turretWarmup == null
            ? new BuildingProperties().turretBurstWarmupTime.Average
            : AverageRange(turretWarmup.Value);
        XElement refuelable = InheritedComp(turret, "CompProperties_Refuelable");
        float consumeFuelPerShot = (float?)verb.Element("consumeFuelPerShot") ?? 0f;
        float consumeFuelPerBurst = (float?)verb.Element("consumeFuelPerBurst") ?? 0f;
        int burstShotCount = (int?)verb.Element("burstShotCount") ?? 1;
        float fuelCapacity = (float?)refuelable?.Element("fuelCapacity") ?? 0f;
        int sourceSupportedShots = AutoSpaceWeaponPolicy.SourceSupportedShots(fuelCapacity,
            consumeFuelPerShot, consumeFuelPerBurst, burstShotCount);
        return new AutoSpaceWeaponSource
        {
            defName = turretName,
            kinetic = true,
            damage = damage,
            armorPenetration = armor,
            projectileSpeed = (float)projectileProps.Element("speed"),
            burstShotCount = burstShotCount,
            ticksBetweenBurstShots = (int?)verb.Element("ticksBetweenBurstShots") ?? 0,
            warmupSeconds = ((float?)verb.Element("warmupTime") ?? 0) + averageTurretWarmup,
            cooldownSeconds = cooldown == null ? 0 : (float)cooldown,
            sourceFuelCapacity = fuelCapacity,
            sourceFuelPerShot = consumeFuelPerShot,
            sourceFuelPerBurst = consumeFuelPerBurst,
            sourceUsesMortarBarrel = (bool?)refuelable?.Element("fuelIsMortarBarrel") ?? false,
            sourceSupportedShots = sourceSupportedShots,
            footprintArea = area,
            range = (float?)verb.Element("range") ?? 0,
            turretAccuracy = (float?)turretAccuracy ?? .96f,
            longRangeAccuracy = (float?)longAccuracy ?? .5f
        };
    }

    private static float AverageRange(string value)
    {
        string text = value.Trim();
        if (!text.StartsWith("(", StringComparison.Ordinal) || !text.EndsWith(")", StringComparison.Ordinal))
            return float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        string[] parts = text.Trim('(', ')').Split(',');
        return (float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
            + float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)) * .5f;
    }

    private static AutoSpaceWeaponSource SyntheticBeamRepeaterFixture(XDocument odyssey)
    {
        XElement gun = odyssey.Root.Elements("ThingDef").Single(x => (string)x.Element("defName") == "Gun_BeamRepeater");
        XElement projectile = odyssey.Root.Elements("ThingDef").Single(x => (string)x.Element("defName") == "Bullet_BeamRepeater");
        XElement verb = gun.Element("verbs").Elements("li").Single();
        XElement props = projectile.Element("projectile");
        return new AutoSpaceWeaponSource
        {
            defName = "Synthetic_LaserFixture_BeamRepeater",
            beam = true,
            damage = (float)props.Element("damageAmountBase"),
            armorPenetration = (float)props.Element("armorPenetrationBase"),
            projectileSpeed = 2000,
            burstShotCount = (int)verb.Element("burstShotCount"),
            ticksBetweenBurstShots = (int)verb.Element("ticksBetweenBurstShots"),
            warmupSeconds = (float)verb.Element("warmupTime"),
            cooldownSeconds = (float)gun.Element("statBases").Element("RangedWeapon_Cooldown"),
            footprintArea = 1,
            range = (float)verb.Element("range")
        };
    }

    static void RunAutoWeaponAdaptationAudit(string repo)
    {
        string reference = Path.GetFullPath(Path.Combine(repo, "../_references/RimWorldData"));
        string corePath = Path.Combine(reference, "Core/Defs/ThingDefs_Buildings/Buildings_Security_Turrets.xml");
        string beamPath = Path.Combine(reference, "Odyssey/Defs/ThingDefs_Misc/Weapons/Ranged_Spacer.xml");
        string effectsPath = Path.Combine(repo, "1.6/Defs/SpaceCombat/DeltaV_SpaceImpactEffects.xml");
        var effects = XDocument.Load(effectsPath);
        var physics = new AdaptationPhysics(AuditEffect(effects, "DeltaV_Impact_SteelShellVsStructure"),
            AuditEffect(effects, "DeltaV_Impact_CannonVsStructure"));
        var core = XDocument.Load(corePath);
        var originalTurretSources = new List<AutoSpaceWeaponSource>
        {
            CoreTurretSource(core, "Turret_MiniTurret"),
            CoreTurretSource(core, "Turret_Autocannon"),
            CoreTurretSource(core, "Turret_Sniper")
        };
        var beamDocument = XDocument.Load(beamPath);
        var syntheticFixtures = new List<AutoSpaceWeaponSource>
        {
            SyntheticBeamRepeaterFixture(beamDocument),
            new AutoSpaceWeaponSource
            {
                defName = "Synthetic_Extreme_100x20",
                kinetic = true,
                damage = 100,
                armorPenetration = 1.5f,
                projectileSpeed = 120,
                burstShotCount = 20,
                ticksBetweenBurstShots = 5,
                cooldownSeconds = 3,
                footprintArea = 1,
                range = 40
            },
            new AutoSpaceWeaponSource
            {
                defName = "Synthetic_Impossible_1200_AP1000",
                kinetic = true,
                damage = 1200,
                armorPenetration = 10f,
                projectileSpeed = 300,
                burstShotCount = 1,
                cooldownSeconds = 3.2f,
                footprintArea = 4,
                range = 45
            },
            new AutoSpaceWeaponSource
            {
                defName = "Synthetic_SprayExplosion",
                verbClassName = "Synthetic.Verb_ArcSprayProjectile",
                kinetic = true,
                explosiveProjectile = true,
                damage = 27,
                armorPenetration = .41f,
                projectileSpeed = 110,
                burstShotCount = 18,
                ticksBetweenBurstShots = 2,
                warmupSeconds = 2,
                cooldownSeconds = 3.2f,
                footprintArea = 9,
                range = 59.9f,
                sprayWidth = 5,
                sprayArching = 4,
                sprayThicknessCells = 3
            }
        };
        var sources = originalTurretSources.Concat(syntheticFixtures).ToList();
        var profiles = sources.Select(x => AutoSpaceWeaponResolver.Resolve(x, physics)).ToList();
        AutoSpaceWeaponProfile Profile(string name) => profiles.Single(x => x.defName == name);
        var mini = Profile("Turret_MiniTurret");
        var auto = Profile("Turret_Autocannon");
        var slug = Profile("Turret_Sniper");
        var beam = Profile("Synthetic_LaserFixture_BeamRepeater");
        var extreme = Profile("Synthetic_Extreme_100x20");
        var impossible = Profile("Synthetic_Impossible_1200_AP1000");
        var sprayExplosion = Profile("Synthetic_SprayExplosion");

        foreach (var profile in profiles)
            Console.WriteLine($"AUTO PROFILE {profile.defName}: {profile.family}, C/M={profile.charge}/{profile.heads}, "
                + $"interval={profile.intervalTicks}, burst={profile.burstShots}, rest={profile.restTicks}, "
                + $"blast={profile.targetBlastDamage:R}/{profile.actualBlastDamage:R}, "
                + $"penetration={profile.targetPenetrationBudget:R}/{profile.actualPenetrationBudget:R}, "
                + $"cooling={profile.coolingPerSecond:0.###}, heatCapacity={profile.heatCapacity:0.###}, "
                + $"range={profile.spaceRange:0.###}, spread={profile.baseSpreadDeg:0.###}, "
                + $"turn={profile.turnRateDegPerSecond:0.###}, skill={profile.intrinsicAimSkill}, "
                + $"magazine=shots:{profile.sourceSupportedShots}, source:{profile.sourceHeadRequirement}/{profile.sourcePropellantRequirement}, "
                + $"area:{profile.areaCapacity}, burst:{profile.burstHeadRequirement}/{profile.burstPropellantRequirement}"
                + $"->{profile.headCapacity}/{profile.propellantCapacity}, duration={profile.magazineDurationSeconds:0.###}s");

        XElement beamGun = beamDocument.Root.Elements("ThingDef")
            .Single(x => (string)x.Element("defName") == "Gun_BeamRepeater");
        Check((string)beamGun.Attribute("ParentName") == "BaseHumanMakeableGun",
            "Odyssey beam repeater is a handheld calibration fixture, not an automatic turret candidate");
        Check(Math.Abs(originalTurretSources[0].CycleSeconds * 60 - 326) < .001
            && Math.Abs(originalTurretSources[1].CycleSeconds * 60 - 274) < .001
            && Math.Abs(originalTurretSources[2].CycleSeconds * 60 - 222) < .001,
            "source burst cycles include N-1 shot intervals, average aiming and post-burst cooldown");
        Check(mini.family == AutoSpaceWeaponFamily.Rapid && auto.family == AutoSpaceWeaponFamily.Rapid
            && slug.family == AutoSpaceWeaponFamily.Heavy && beam.family == AutoSpaceWeaponFamily.Laser,
            "turret anchors and synthetic laser fixture classify as rapid/rapid/heavy/laser");
        Check(mini.spaceRange < auto.spaceRange && auto.spaceRange < slug.spaceRange
            && mini.baseSpreadDeg > auto.baseSpreadDeg && auto.baseSpreadDeg > slug.baseSpreadDeg
            && mini.turnRateDegPerSecond > auto.turnRateDegPerSecond
            && Math.Abs(auto.turnRateDegPerSecond - slug.turnRateDegPerSecond) < .001
            && mini.intrinsicAimSkill == 8 && auto.intrinsicAimSkill == 8
            && slug.intrinsicAimSkill == 14,
            "range, long-range gun dispersion, footprint traverse and turret electronics use fixed monotonic mappings");
        Check(mini.charge == 1 && mini.heads == 1,
            "mini-turret resolves to the 1/1 chemical anchor");
        Check(auto.charge == 4 && auto.heads == 2,
            "autocannon resolves to the 4/2 chemical anchor");
        Check(slug.charge == 24 && slug.heads == 24 && slug.burstShots == 1
            && slug.heatPerShot == 96 && Math.Abs(slug.coolingPerSecond - 16) < .001
            && slug.heatCapacity == 96 && slug.intervalTicks == 360 && slug.restTicks == 360,
            "uranium slug resolves to the complete 24/24 small-heavy thermal anchor");
        Check(mini.sourceSupportedShots == 60 && mini.sourceHeadRequirement == 60
            && mini.sourcePropellantRequirement == 60 && mini.headCapacity == 60 && mini.propellantCapacity == 60,
            "mini-turret preserves its original 60 complete shots at fixed 1/1 consumption");
        Check(auto.sourceSupportedShots == 90 && auto.sourceHeadRequirement == 180
            && auto.sourcePropellantRequirement == 360 && auto.headCapacity == 180 && auto.propellantCapacity == 360,
            "autocannon preserves its original 90 complete shots at fixed 4/2 consumption");
        Check(slug.sourceSupportedShots == 30 && slug.sourceHeadRequirement == 720
            && slug.sourcePropellantRequirement == 720 && slug.headCapacity == 720 && slug.propellantCapacity == 720,
            "uranium slug preserves its original 30 complete shots at fixed 24/24 consumption");
        Check(profiles.Where(x => x.family != AutoSpaceWeaponFamily.Laser)
                .All(x => x.fullMagazineBursts >= .9999f
                    && x.headCapacity >= x.burstShots * x.heads
                    && x.propellantCapacity >= x.burstShots * x.charge),
            "every chemical profile stores at least one complete resolved burst");
        Check(extreme.charge > auto.charge && extreme.heads > auto.heads
            && extreme.heatPerShot > auto.heatPerShot
            && extreme.coolingPerSecond > auto.coolingPerSecond
            && extreme.heatCapacity > auto.heatCapacity
            && extreme.propellantPerSecond > auto.propellantPerSecond
            && extreme.headsPerSecond > auto.headsPerSecond,
            "100 damage 20-shot weapon retains extreme ammunition and thermal throughput");
        Check(extreme.headCapacity >= extreme.burstShots * extreme.heads
            && extreme.propellantCapacity >= extreme.burstShots * extreme.charge,
            "extreme weapon cannot exceed a full magazine in one shot or one burst");
        Check(extreme.sourceSupportedShots == 0 && extreme.headCapacity == extreme.burstHeadRequirement
            && extreme.propellantCapacity == extreme.burstPropellantRequirement,
            "sources without a finite magazine fall back to the larger area or complete-burst requirement");
        Check(sprayExplosion.family == AutoSpaceWeaponFamily.Rapid
            && sprayExplosion.heads > 0 && sprayExplosion.charge > 0
            && sprayExplosion.burstShots > 1 && sprayExplosion.sourceSupportedShots == 0
            && sprayExplosion.baseSpreadDeg > 4f,
            "generic spray/explosive inputs retain burst timing and map per-shot damage without ground AOE");
        Check(AutoSpaceWeaponPolicy.SourceSupportedShots(90f, 1f, 0f, 3) == 90
            && AutoSpaceWeaponPolicy.SourceSupportedShots(20f, 1f, 0f, 1) == 20,
            "per-shot resources preserve supported shots regardless of ammo or barrel semantics");
        Check(AutoSpaceWeaponPolicy.SourceSupportedShots(10f, 0f, 2f, 3) == 15
            && AutoSpaceWeaponPolicy.SourceSupportedShots(7f, 1f, 2f, 3) == 5
            && AutoSpaceWeaponPolicy.SourceSupportedShots(2f, 1f, 2f, 3) == 2,
            "per-burst and mixed source resource costs preserve complete and trailing shots");
        Check(impossible.performanceCapped && !impossible.safetyLimited
            && impossible.requestedBlastDamage > impossible.targetBlastDamage
            && impossible.requestedPenetrationBudget > impossible.targetPenetrationBudget
            && Math.Abs(impossible.targetBlastDamage - physics.BlastDamageUpperBound * .95f) < .01f
            && Math.Abs(impossible.targetPenetrationBudget
                - physics.PenetrationBudgetUpperBound * .95f) < .01f
            && impossible.heads == 89 && impossible.charge == 283,
            "unreachable 1200 damage and 1000% AP request caps at 95% of production performance bounds");
        AutoSpaceWeaponWarning risky = AutoSpaceWeaponWarning.CustomVerb
            | AutoSpaceWeaponWarning.ExplosiveProjectile | AutoSpaceWeaponWarning.MortarBarrel;
        Check(AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true, risky, false)
            && !AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.Recommended,
                AutoSpaceWeaponOverride.Inherit, true, AutoSpaceWeaponWarning.CustomFlight, false)
            && AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.EnableAll,
                AutoSpaceWeaponOverride.Inherit, true, risky, false)
            && AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.DisableAll,
                AutoSpaceWeaponOverride.Enabled, true, risky, false)
            && !AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.EnableAll,
                AutoSpaceWeaponOverride.Disabled, true, AutoSpaceWeaponWarning.None, false)
            && !AutoSpaceWeaponPolicy.Enabled(AutoSpaceWeaponModPolicy.EnableAll,
                AutoSpaceWeaponOverride.Enabled, false, AutoSpaceWeaponWarning.None, false),
            "weapon override, mod policy, unsafe custom flight and hard rejection have stable precedence");
        Check(AutoSpaceWeaponPolicy.WeaponKey("Example.Mod", "SyntheticTurret")
                == AutoSpaceWeaponPolicy.WeaponKey("example.mod", "SyntheticTurret"),
            "automatic adaptation keys normalize package IDs while preserving turret identity");
        Check(typeof(AutoSpaceWeaponCandidate).GetField("profile") != null,
            "automatic adaptation candidates retain the exact resolved profile used for injection and reporting");
        Check(beam.laserBaseDamage > 0 && beam.laserShotEnergy > 0 && beam.laserMaxPower >= 4
            && beam.laserEnergyPerSecond == beam.laserShotEnergy * beam.targetSustainedRate
            && beam.laserMaxEnergyPerSecond == beam.laserEnergyPerSecond * beam.laserMaxPower,
            "synthetic beam fixture produces coupled laser damage, energy and power range");

        AutoSpaceWeaponSource HeavyFixture(float cooldown, int area) => new AutoSpaceWeaponSource
        {
            defName = $"HeavyFixture_{area}_{cooldown}",
            kinetic = true,
            damage = 55,
            armorPenetration = .825f,
            projectileSpeed = 120,
            burstShotCount = 1,
            warmupSeconds = .5f,
            cooldownSeconds = cooldown,
            footprintArea = area,
            extension = new DefModExtension_AutoSpaceWeapon { family = AutoSpaceWeaponFamily.Heavy }
        };
        var heavyFourCells = AutoSpaceWeaponResolver.Resolve(HeavyFixture(3.2f, 4), physics);
        var heavyNineCells = AutoSpaceWeaponResolver.Resolve(HeavyFixture(3.2f, 9), physics);
        var heavyFastSource = AutoSpaceWeaponResolver.Resolve(HeavyFixture(1.6f, 4), physics);
        Check(Math.Abs(heavyFourCells.coolingPerSecond - 16) < .001
            && Math.Abs(heavyNineCells.coolingPerSecond - 24) < .001,
            "heavy cooling hardware follows the 2x2=16 and 3x3=24 production anchors");
        Check(heavyFourCells.coolingPerSecond < 40 && heavyNineCells.coolingPerSecond < 85,
            "heavy cooling hardware remains less efficient than same-footprint rapid anchors");
        Check(heavyFastSource.coolingPerSecond > heavyFourCells.coolingPerSecond
            && heavyFastSource.intervalTicks < heavyFourCells.intervalTicks,
            "stronger source throughput raises heavy cooling without a native-weapon cap");

        AutoSpaceWeaponSource Monotonic(float damage) => new AutoSpaceWeaponSource
        {
            defName = "Monotonic_" + damage,
            kinetic = true,
            damage = damage,
            armorPenetration = damage * .015f,
            projectileSpeed = 88,
            burstShotCount = 3,
            ticksBetweenBurstShots = 17,
            warmupSeconds = .5f,
            cooldownSeconds = 3.5f,
            footprintArea = 4
        };
        var monotonic = new[] { 12f, 24f, 48f, 96f }.Select(x =>
            AutoSpaceWeaponResolver.Resolve(Monotonic(x), physics)).ToArray();
        Check(monotonic.Zip(monotonic.Skip(1), (a, b) => b.charge >= a.charge && b.heads >= a.heads
            && b.heatPerShot >= a.heatPerShot).All(x => x),
            "single-shot strength monotonically increases charge, heads and shot heat");

        var repeatMini = AutoSpaceWeaponResolver.Resolve(sources[0], physics);
        string Signature(AutoSpaceWeaponProfile value) => JsonSerializer.Serialize(value,
            new JsonSerializerOptions { IncludeFields = true });
        Check(Signature(mini) == Signature(repeatMini),
            "repeated resolution of the same frozen source is field-for-field deterministic");

        var overridden = Monotonic(27);
        overridden.extension = new DefModExtension_AutoSpaceWeapon
        {
            headMultiplier = 2,
            chargeMultiplier = 2,
            intervalMultiplier = .5f,
            burstMultiplier = 2,
            restMultiplier = .5f,
            coolingMultiplier = 1.5f,
            heatCapacityMultiplier = 2
        };
        var overriddenProfile = AutoSpaceWeaponResolver.Resolve(overridden, physics);
        Check(overriddenProfile.heads > auto.heads && overriddenProfile.charge > auto.charge
            && overriddenProfile.intervalTicks < auto.intervalTicks
            && overriddenProfile.burstShots > auto.burstShots
            && overriddenProfile.coolingPerSecond > auto.coolingPerSecond
            && overriddenProfile.heatCapacity > auto.heatCapacity,
            "explicit extension multipliers apply after generic resolution without flattening output");

        var slow = Monotonic(27); slow.cooldownSeconds = 8;
        var fast = Monotonic(27); fast.cooldownSeconds = 1;
        var slowProfile = AutoSpaceWeaponResolver.Resolve(slow, physics);
        var fastProfile = AutoSpaceWeaponResolver.Resolve(fast, physics);
        Check(fastProfile.intervalTicks <= slowProfile.intervalTicks
            && fastProfile.propellantPerSecond > slowProfile.propellantPerSecond
            && fastProfile.coolingPerSecond > slowProfile.coolingPerSecond,
            "higher source sustained rate increases target ammunition and cooling throughput");

        var reportDocument = new AutoSpaceAdaptationReportDocument
        {
            generatedAtUtc = "2026-11-24T00:00:00.0000000Z",
            rimWorldVersion = "synthetic",
            deltaVAssemblyVersion = "synthetic",
            mods = new List<AutoSpaceAdaptationModReport>
            {
                new AutoSpaceAdaptationModReport
                {
                    loadOrder = 7,
                    name = "Synthetic Weapons",
                    packageId = "example.weapons",
                    rootPath = @"C:\Mods\Synthetic Weapons",
                    activeLoadFolders = new List<string> { @"C:\Mods\Synthetic Weapons\1.7" },
                    weaponPolicy = AutoSpaceWeaponModPolicy.Recommended.ToString(),
                    weapons = new List<AutoSpaceAdaptationWeaponReport>
                    {
                        new AutoSpaceAdaptationWeaponReport
                        {
                            defName = "SyntheticRapid",
                            structurallySupported = true,
                            warnings = new List<string> { AutoSpaceWeaponWarning.CustomVerb.ToString() },
                            family = AutoSpaceWeaponFamily.Rapid.ToString(),
                            configuredEnabled = false,
                            loadedDecisionEnabled = true,
                            injectedThisLaunch = true,
                            source = new AutoSpaceAdaptationWeaponSourceReport
                            {
                                sourceFuelCapacity = 90,
                                sourceFuelPerShot = 1,
                                sourceSupportedShots = 90,
                            },
                            profile = new AutoSpaceAdaptationWeaponProfileReport
                            {
                                family = AutoSpaceWeaponFamily.Rapid.ToString(),
                                charge = 4,
                                heads = 2,
                                burstShots = 12,
                                headCapacity = 180,
                                propellantCapacity = 360,
                            },
                        },
                        new AutoSpaceAdaptationWeaponReport
                        {
                            defName = "RejectedFixture",
                            structurallySupported = false,
                            rejection = AutoSpaceWeaponRejection.NotTurret.ToString(),
                            warnings = new List<string>(),
                            family = AutoSpaceWeaponFamily.Rapid.ToString(),
                            configuredEnabled = false,
                            loadedDecisionEnabled = false,
                            injectedThisLaunch = false,
                        },
                    },
                    shieldObservations = new List<AutoSpaceAdaptationShieldObservation>
                    {
                        new AutoSpaceAdaptationShieldObservation
                        {
                            defName = "SyntheticCapacityShield",
                            building = true,
                            positiveCapacityBuildingCandidate = true,
                            observationReason = "positiveCapacityBuilding",
                            radius = 8,
                            hitPoints = 900,
                        },
                    },
                },
                new AutoSpaceAdaptationModReport
                {
                    loadOrder = 8,
                    name = "No Candidates",
                    packageId = "example.empty",
                    rootPath = @"D:\Workshop\No Candidates",
                    weaponPolicy = AutoSpaceWeaponModPolicy.Recommended.ToString(),
                },
            },
        };
        AutoSpaceAdaptationReport.FinalizeSummaries(reportDocument);
        Check(reportDocument.summary.activeMods == 2
            && reportDocument.summary.weaponCandidates == 2
            && reportDocument.summary.structurallySupportedWeapons == 1
            && reportDocument.summary.rejectedWeapons == 1
            && reportDocument.summary.weaponsWithWarnings == 1
            && reportDocument.summary.configuredWeapons == 0
            && reportDocument.summary.injectedWeaponsThisLaunch == 1
            && reportDocument.summary.shieldObservations == 1
            && reportDocument.summary.positiveCapacityBuildingShields == 1,
            "report totals are derived from per-mod weapon and shield details without conflating configuration and launch state");
        Check(reportDocument.mods[1].weaponSummary.candidates == 0
            && reportDocument.mods[1].weapons.Count == 0
            && reportDocument.mods[1].shieldObservations.Count == 0,
            "mods with no candidates remain present with explicit zero summaries");
        byte[] reportBytes = AutoSpaceAdaptationReport.Serialize(reportDocument);
        using (JsonDocument reportJson = JsonDocument.Parse(reportBytes))
        {
            JsonElement root = reportJson.RootElement;
            JsonElement firstMod = root.GetProperty("mods")[0];
            Check(root.GetProperty("schemaVersion").GetInt32() == 3
                && root.GetProperty("settingsRequireRestart").GetBoolean()
                && root.GetProperty("summary").GetProperty("activeMods").GetInt32() == 2,
                "report JSON exposes a versioned restart-only schema and aggregate summary");
            Check(firstMod.GetProperty("rootPath").GetString() == @"C:\Mods\Synthetic Weapons"
                && firstMod.GetProperty("activeLoadFolders")[0].GetString()
                    == @"C:\Mods\Synthetic Weapons\1.7"
                && firstMod.GetProperty("weapons")[0].GetProperty("profile")
                    .GetProperty("charge").GetInt32() == 4
                && firstMod.GetProperty("weapons")[0].GetProperty("profile")
                    .TryGetProperty("performanceCapped", out _)
                && firstMod.GetProperty("weapons")[0].GetProperty("source")
                    .TryGetProperty("sourceSupportedShots", out _),
                "report JSON preserves local paths and the actual resolved weapon profile");
            Check(firstMod.GetProperty("weapons")[0].GetProperty("configuredEnabled").GetBoolean() == false
                && firstMod.GetProperty("weapons")[0].GetProperty("loadedDecisionEnabled").GetBoolean()
                && firstMod.GetProperty("weapons")[0].GetProperty("injectedThisLaunch").GetBoolean(),
                "report JSON keeps current settings, startup decision and actual injection as separate facts");
        }

        foreach (var profile in profiles.Where(x => x.family != AutoSpaceWeaponFamily.Laser))
        {
            if (profile.family == AutoSpaceWeaponFamily.Heavy)
            {
                int expectedTicks = (int)Math.Ceiling(
                    profile.heatPerShot * 60d / profile.coolingPerSecond - 1e-8);
                Check(profile.burstShots == 1 && profile.intervalTicks == expectedTicks
                    && profile.restTicks == expectedTicks
                    && profile.averageHeatPerSecond <= profile.coolingPerSecond + .001f,
                    profile.defName + " heavy interval is derived from full per-shot cooling");
                continue;
            }
            double active = (profile.burstShots - 1) * profile.intervalTicks / 60d;
            double rest = profile.restTicks / 60d;
            double expected = profile.burstShots * profile.heatPerShot / Math.Max(1d / 60d, active + rest);
            Check(Math.Abs(profile.coolingPerSecond - expected) <= Math.Max(.02, expected / 120),
                profile.defName + " thermal throughput satisfies the resolved discrete cycle");
        }

        string output = Path.Combine(repo, "Source/obj/WeaponTuningRegression/auto-space-weapon-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Status = "Production automatic-weapon resolver and policy audit; ThingDef injection and runtime behavior still require game validation.",
            Assumptions = "60 ticks/s; source cycle is (N-1) internal shot intervals plus verb warmup, average turret aim and post-burst cooldown; target-search hash latency is excluded; the first source CompRefuelable follows vanilla comp order and both per-shot and per-burst costs determine supported shots without interpreting resource labels; sources without a measurable finite resource fall back to area or a complete target burst; unreachable damage and penetration requests cap at 95% of the production effect bounds before C/M inversion; original turret snapshots read from checked-in RimWorld reference XML; Odyssey Beam repeater is a handheld weapon used only through a synthetic laser fixture; synthetic extreme uses 100 damage, AP 1.5, speed 120, 20 shots at 5 ticks, 3 seconds rest, 1-cell footprint; synthetic impossible uses 1200 damage, 1000% AP and speed 300; synthetic spray/explosive uses 27 damage, AP .41, speed 110, 18 shots at 2 ticks, 2 seconds warmup, 3.2 seconds cooldown, 5-cell spray width and no inherited ground AOE; production Delta-V impact effects are used to invert C/M; magazine starts empty.",
            OriginalTurretSources = originalTurretSources,
            SyntheticFixtures = syntheticFixtures,
            Profiles = profiles,
            Monotonic = monotonic,
            Override = overriddenProfile,
            HeavyAnchors = new { FourCells = heavyFourCells, NineCells = heavyNineCells, FastSource = heavyFastSource },
            RateComparison = new { Slow = slowProfile, Fast = fastProfile }
        }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        Console.WriteLine($"AUTO WEAPON ADAPTATION AUDIT: {profiles.Count} reference profiles; {output}");
    }
}

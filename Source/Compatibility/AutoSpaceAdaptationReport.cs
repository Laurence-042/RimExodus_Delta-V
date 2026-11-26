using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using RimWorld;
using Verse;

namespace DeltaV
{
    [DataContract]
    public sealed class AutoSpaceAdaptationReportDocument
    {
        [DataMember(Order = 0)] public int schemaVersion = 3;
        [DataMember(Order = 1)] public string generatedAtUtc;
        [DataMember(Order = 2)] public string rimWorldVersion;
        [DataMember(Order = 3)] public string deltaVAssemblyVersion;
        [DataMember(Order = 4)] public bool settingsRequireRestart = true;
        [DataMember(Order = 5)] public AutoSpaceAdaptationReportSummary summary;
        [DataMember(Order = 6)] public List<AutoSpaceAdaptationModReport> mods =
            new List<AutoSpaceAdaptationModReport>();
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationReportSummary
    {
        [DataMember(Order = 0)] public int activeMods;
        [DataMember(Order = 1)] public int weaponCandidates;
        [DataMember(Order = 2)] public int structurallySupportedWeapons;
        [DataMember(Order = 3)] public int rejectedWeapons;
        [DataMember(Order = 4)] public int weaponsWithWarnings;
        [DataMember(Order = 5)] public int configuredWeapons;
        [DataMember(Order = 6)] public int injectedWeaponsThisLaunch;
        [DataMember(Order = 7)] public int rapidWeapons;
        [DataMember(Order = 8)] public int heavyWeapons;
        [DataMember(Order = 9)] public int laserWeapons;
        [DataMember(Order = 10)] public int shieldObservations;
        [DataMember(Order = 11)] public int positiveCapacityBuildingShields;
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationModReport
    {
        [DataMember(Order = 0)] public int loadOrder;
        [DataMember(Order = 1)] public string name;
        [DataMember(Order = 2)] public string packageId;
        [DataMember(Order = 3)] public string rootPath;
        [DataMember(Order = 4)] public List<string> activeLoadFolders = new List<string>();
        [DataMember(Order = 5)] public string weaponPolicy;
        [DataMember(Order = 6)] public AutoSpaceAdaptationWeaponSummary weaponSummary;
        [DataMember(Order = 7)] public List<AutoSpaceAdaptationWeaponReport> weapons =
            new List<AutoSpaceAdaptationWeaponReport>();
        [DataMember(Order = 8)] public List<AutoSpaceAdaptationShieldObservation> shieldObservations =
            new List<AutoSpaceAdaptationShieldObservation>();
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationWeaponSummary
    {
        [DataMember(Order = 0)] public int candidates;
        [DataMember(Order = 1)] public int structurallySupported;
        [DataMember(Order = 2)] public int rejected;
        [DataMember(Order = 3)] public int withWarnings;
        [DataMember(Order = 4)] public int configuredEnabled;
        [DataMember(Order = 5)] public int injectedThisLaunch;
        [DataMember(Order = 6)] public int rapid;
        [DataMember(Order = 7)] public int heavy;
        [DataMember(Order = 8)] public int laser;
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationWeaponReport
    {
        [DataMember(Order = 0)] public string defName;
        [DataMember(Order = 1)] public string label;
        [DataMember(Order = 2)] public string thingClass;
        [DataMember(Order = 3)] public int sizeX;
        [DataMember(Order = 4)] public int sizeZ;
        [DataMember(Order = 5)] public string gunDefName;
        [DataMember(Order = 6)] public string verbClass;
        [DataMember(Order = 7)] public string projectileDefName;
        [DataMember(Order = 8)] public string projectileClass;
        [DataMember(Order = 9)] public string damageDefName;
        [DataMember(Order = 10)] public bool structurallySupported;
        [DataMember(Order = 11)] public string rejection;
        [DataMember(Order = 12)] public List<string> warnings = new List<string>();
        [DataMember(Order = 13)] public string family;
        [DataMember(Order = 14)] public string modPolicy;
        [DataMember(Order = 15)] public string weaponOverride;
        [DataMember(Order = 16)] public bool configuredEnabled;
        [DataMember(Order = 17)] public bool loadedDecisionEnabled;
        [DataMember(Order = 18)] public bool injectedThisLaunch;
        [DataMember(Order = 19, EmitDefaultValue = false)] public AutoSpaceAdaptationWeaponSourceReport source;
        [DataMember(Order = 20, EmitDefaultValue = false)] public AutoSpaceAdaptationWeaponProfileReport profile;
        [DataMember(Order = 21)] public string mode;
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationWeaponSourceReport
    {
        [DataMember(Order = 0)] public float damage;
        [DataMember(Order = 1)] public float armorPenetration;
        [DataMember(Order = 2)] public float projectileSpeed;
        [DataMember(Order = 3)] public int burstShotCount;
        [DataMember(Order = 4)] public int ticksBetweenBurstShots;
        [DataMember(Order = 5)] public float warmupSeconds;
        [DataMember(Order = 6)] public float cooldownSeconds;
        [DataMember(Order = 7)] public double activeSeconds;
        [DataMember(Order = 8)] public double restSeconds;
        [DataMember(Order = 9)] public double cycleSeconds;
        [DataMember(Order = 10)] public double sustainedRate;
        [DataMember(Order = 11)] public double peakRate;
        [DataMember(Order = 12)] public float sourceFuelCapacity;
        [DataMember(Order = 13)] public float sourceFuelPerShot;
        [DataMember(Order = 14)] public float sourceFuelPerBurst;
        [DataMember(Order = 15)] public int sourceSupportedShots;
        [DataMember(Order = 16)] public int footprintArea;
        [DataMember(Order = 17)] public float range;
        [DataMember(Order = 18)] public float sprayWidth;
        [DataMember(Order = 19)] public float sprayArching;
        [DataMember(Order = 20)] public int sprayThicknessCells;
        [DataMember(Order = 21)] public float turretAccuracy;
        [DataMember(Order = 22)] public float longRangeAccuracy;
        [DataMember(Order = 23)] public bool kinetic;
        [DataMember(Order = 24)] public bool beam;
        [DataMember(Order = 25)] public bool blunt;
        [DataMember(Order = 26)] public bool explosiveProjectile;
        [DataMember(Order = 27)] public bool flyOverhead;
        [DataMember(Order = 28)] public bool sourceUsesMortarBarrel;
        [DataMember(Order = 29)] public bool nativeProjectile;
        [DataMember(Order = 30)] public bool customFlight;
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationWeaponProfileReport
    {
        [DataMember(Order = 0)] public string family;
        [DataMember(Order = 1)] public int charge;
        [DataMember(Order = 2)] public int heads;
        [DataMember(Order = 3)] public int intervalTicks;
        [DataMember(Order = 4)] public int burstShots;
        [DataMember(Order = 5)] public int restTicks;
        [DataMember(Order = 6)] public float heatPerShot;
        [DataMember(Order = 7)] public float coolingPerSecond;
        [DataMember(Order = 8)] public float heatCapacity;
        [DataMember(Order = 9)] public int sourceSupportedShots;
        [DataMember(Order = 10)] public int sourceHeadRequirement;
        [DataMember(Order = 11)] public int sourcePropellantRequirement;
        [DataMember(Order = 12)] public int areaCapacity;
        [DataMember(Order = 13)] public int burstHeadRequirement;
        [DataMember(Order = 14)] public int burstPropellantRequirement;
        [DataMember(Order = 15)] public int headCapacity;
        [DataMember(Order = 16)] public int propellantCapacity;
        [DataMember(Order = 17)] public float requestedBlastDamage;
        [DataMember(Order = 18)] public float targetBlastDamage;
        [DataMember(Order = 19)] public float actualBlastDamage;
        [DataMember(Order = 20)] public float requestedPenetrationBudget;
        [DataMember(Order = 21)] public float targetPenetrationBudget;
        [DataMember(Order = 22)] public float actualPenetrationBudget;
        [DataMember(Order = 23)] public float laserBaseDamage;
        [DataMember(Order = 24)] public float laserShotEnergy;
        [DataMember(Order = 25)] public int laserMaxPower;
        [DataMember(Order = 26)] public float laserEnergyPerSecond;
        [DataMember(Order = 27)] public float laserMaxEnergyPerSecond;
        [DataMember(Order = 28)] public float sourceSustainedRate;
        [DataMember(Order = 29)] public float targetSustainedRate;
        [DataMember(Order = 30)] public float propellantPerSecond;
        [DataMember(Order = 31)] public float headsPerSecond;
        [DataMember(Order = 32)] public float averageHeatPerSecond;
        [DataMember(Order = 33)] public float magazineDurationSeconds;
        [DataMember(Order = 34)] public float fullMagazineBursts;
        [DataMember(Order = 35)] public float spaceRange;
        [DataMember(Order = 36)] public float baseSpreadDeg;
        [DataMember(Order = 37)] public float turnRateDegPerSecond;
        [DataMember(Order = 38)] public float fireToleranceDeg;
        [DataMember(Order = 39)] public int intrinsicAimSkill;
        [DataMember(Order = 40)] public bool performanceCapped;
        [DataMember(Order = 41)] public bool safetyLimited;
        [DataMember(Order = 42)] public string mode;
    }

    [DataContract]
    public sealed class AutoSpaceAdaptationShieldObservation
    {
        [DataMember(Order = 0)] public string defName;
        [DataMember(Order = 1)] public string label;
        [DataMember(Order = 2)] public string thingClass;
        [DataMember(Order = 3)] public string compClass;
        [DataMember(Order = 4)] public int compIndex;
        [DataMember(Order = 5)] public bool building;
        [DataMember(Order = 6)] public bool existingDeltaVShield;
        [DataMember(Order = 7)] public bool positiveCapacityBuildingCandidate;
        [DataMember(Order = 8)] public string observationReason;
        [DataMember(Order = 9)] public float radius;
        [DataMember(Order = 10)] public int hitPoints;
        [DataMember(Order = 11)] public int cooldownTicks;
        [DataMember(Order = 12)] public int disarmedByEmpForTicks;
        [DataMember(Order = 13)] public bool interceptGroundProjectiles;
        [DataMember(Order = 14)] public bool interceptAirProjectiles;
        [DataMember(Order = 15)] public bool interceptNonHostileProjectiles;
        [DataMember(Order = 16)] public bool interceptOutgoingProjectiles;
        [DataMember(Order = 17)] public int chargeIntervalTicks;
        [DataMember(Order = 18)] public int chargeDurationTicks;
        [DataMember(Order = 19)] public int rechargeHitPointsIntervalTicks;
        [DataMember(Order = 20)] public bool hitPointsRestoreInstantlyAfterCharge;
        [DataMember(Order = 21)] public bool startWithMaxHitPoints;
        [DataMember(Order = 22)] public bool activated;
        [DataMember(Order = 23)] public int activeDuration;
    }

    /// <summary>Explicit, local-only diagnostic snapshot of loaded automatic-adaptation inputs.</summary>
    public static class AutoSpaceAdaptationReport
    {
        internal static string DirectoryPath => Path.Combine(GenFilePaths.SaveDataFolderPath,
            "DeltaV", "AutoAdaptationReports");

        internal static string Export(DeltaVSettings settings)
        {
            string path = Path.Combine(DirectoryPath, "auto-adaptation-"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            AutoSpaceAdaptationReportDocument document = Build(settings);
            return ExportFileService.Write("automatic adaptation report", path,
                exportedPath => Write(exportedPath, document));
        }

        internal static AutoSpaceAdaptationReportDocument Build(DeltaVSettings settings)
        {
            settings ??= DeltaVMod.CurrentSettings;
            List<AutoSpaceWeaponCandidate> weapons = AutoSpaceWeaponDefGenerator.Candidates.ToList();
            List<ShieldEntry> shields = ShieldEntries().ToList();
            var document = new AutoSpaceAdaptationReportDocument
            {
                generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                rimWorldVersion = VersionControl.CurrentVersionStringWithRev,
                deltaVAssemblyVersion = typeof(DeltaVMod).Assembly.GetName().Version?.ToString() ?? "unknown",
            };

            List<ModContentPack> running = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < running.Count; i++)
            {
                ModContentPack mod = running[i];
                string packageId = PackageId(mod);
                List<AutoSpaceWeaponCandidate> modWeapons = weapons
                    .Where(candidate => candidate.packageId == packageId)
                    .OrderBy(candidate => candidate.turret?.defName, StringComparer.Ordinal).ToList();
                List<ShieldEntry> modShields = shields.Where(entry => entry.packageId == packageId)
                    .OrderBy(entry => entry.def.defName, StringComparer.Ordinal)
                    .ThenBy(entry => entry.index).ToList();
                AutoSpaceWeaponModPolicy policy = settings?.GetAutoWeaponModPolicy(packageId)
                    ?? AutoSpaceWeaponModPolicy.Recommended;
                var report = new AutoSpaceAdaptationModReport
                {
                    loadOrder = mod.loadOrder,
                    name = mod.Name,
                    packageId = packageId,
                    rootPath = FullPath(mod.RootDir),
                    activeLoadFolders = (mod.foldersToLoadDescendingOrder ?? new List<string>())
                        .Select(FullPath).ToList(),
                    weaponPolicy = policy.ToString(),
                    weapons = modWeapons.Select(candidate => Weapon(candidate, settings, policy)).ToList(),
                    shieldObservations = modShields.Select(Shield).ToList(),
                };
                document.mods.Add(report);
            }

            FinalizeSummaries(document);
            return document;
        }

        public static void FinalizeSummaries(AutoSpaceAdaptationReportDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.mods ??= new List<AutoSpaceAdaptationModReport>();
            foreach (AutoSpaceAdaptationModReport mod in document.mods)
            {
                mod.weapons ??= new List<AutoSpaceAdaptationWeaponReport>();
                mod.shieldObservations ??= new List<AutoSpaceAdaptationShieldObservation>();
                mod.weaponSummary = WeaponSummary(mod.weapons);
            }
            document.summary = Summary(document.mods);
        }

        private static AutoSpaceAdaptationWeaponReport Weapon(AutoSpaceWeaponCandidate candidate,
            DeltaVSettings settings, AutoSpaceWeaponModPolicy modPolicy)
        {
            ThingDef turret = candidate.turret;
            ThingDef gun = turret?.building?.turretGunDef;
            VerbProperties verb = gun?.Verbs?.Count == 1 ? gun.Verbs[0] : null;
            ThingDef projectile = verb?.defaultProjectile;
            if (candidate.source?.extension is { sourceProjectile: { } sourceProjectile }
                && !sourceProjectile.NullOrEmpty())
                projectile = DefDatabase<ThingDef>.GetNamedSilentFail(sourceProjectile) ?? projectile;
            DamageDef damage = projectile?.projectile?.damageDef ?? verb?.beamDamageDef;
            AutoSpaceWeaponOverride weaponOverride = settings?.GetAutoWeaponOverride(candidate.packageId,
                turret?.defName) ?? AutoSpaceWeaponOverride.Inherit;
            bool configured = AutoSpaceWeaponPolicy.Enabled(modPolicy, weaponOverride,
                candidate.StructurallySupported, candidate.warnings,
                candidate.source?.extension?.force == true);
            return new AutoSpaceAdaptationWeaponReport
            {
                defName = turret?.defName,
                label = turret?.label,
                thingClass = turret?.thingClass?.FullName,
                sizeX = turret?.size.x ?? 0,
                sizeZ = turret?.size.z ?? 0,
                gunDefName = gun?.defName,
                verbClass = candidate.source?.verbClassName ?? verb?.verbClass?.FullName,
                projectileDefName = projectile?.defName,
                projectileClass = projectile?.thingClass?.FullName,
                damageDefName = damage?.defName,
                structurallySupported = candidate.StructurallySupported,
                rejection = candidate.rejection.ToString(),
                warnings = WarningNames(candidate.warnings),
                family = candidate.family.ToString(),
                mode = candidate.profile?.mode.ToString(),
                modPolicy = modPolicy.ToString(),
                weaponOverride = weaponOverride.ToString(),
                configuredEnabled = configured,
                loadedDecisionEnabled = candidate.enabled,
                injectedThisLaunch = turret?.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>() != null,
                source = Source(candidate.source),
                profile = Profile(candidate.profile),
            };
        }

        private static AutoSpaceAdaptationWeaponSourceReport Source(AutoSpaceWeaponSource source)
        {
            if (source == null) return null;
            return new AutoSpaceAdaptationWeaponSourceReport
            {
                damage = source.damage,
                armorPenetration = source.armorPenetration,
                projectileSpeed = source.projectileSpeed,
                burstShotCount = source.burstShotCount,
                ticksBetweenBurstShots = source.ticksBetweenBurstShots,
                warmupSeconds = source.warmupSeconds,
                cooldownSeconds = source.cooldownSeconds,
                activeSeconds = source.ActiveSeconds,
                restSeconds = source.RestSeconds,
                cycleSeconds = source.CycleSeconds,
                sustainedRate = source.SustainedRate,
                peakRate = source.PeakRate,
                sourceFuelCapacity = source.sourceFuelCapacity,
                sourceFuelPerShot = source.sourceFuelPerShot,
                sourceFuelPerBurst = source.sourceFuelPerBurst,
                sourceSupportedShots = source.sourceSupportedShots,
                footprintArea = source.footprintArea,
                range = source.range,
                sprayWidth = source.sprayWidth,
                sprayArching = source.sprayArching,
                sprayThicknessCells = source.sprayThicknessCells,
                turretAccuracy = source.turretAccuracy,
                longRangeAccuracy = source.longRangeAccuracy,
                kinetic = source.kinetic,
                beam = source.beam,
                blunt = source.blunt,
                explosiveProjectile = source.explosiveProjectile,
                flyOverhead = source.flyOverhead,
                sourceUsesMortarBarrel = source.sourceUsesMortarBarrel,
                nativeProjectile = source.nativeProjectile,
                customFlight = source.customFlight,
            };
        }

        private static AutoSpaceAdaptationWeaponProfileReport Profile(AutoSpaceWeaponProfile profile)
        {
            if (profile == null) return null;
            return new AutoSpaceAdaptationWeaponProfileReport
            {
                family = profile.family.ToString(), mode = profile.mode.ToString(),
                charge = profile.charge, heads = profile.heads,
                intervalTicks = profile.intervalTicks, burstShots = profile.burstShots,
                restTicks = profile.restTicks, heatPerShot = profile.heatPerShot,
                coolingPerSecond = profile.coolingPerSecond, heatCapacity = profile.heatCapacity,
                sourceSupportedShots = profile.sourceSupportedShots,
                sourceHeadRequirement = profile.sourceHeadRequirement,
                sourcePropellantRequirement = profile.sourcePropellantRequirement,
                areaCapacity = profile.areaCapacity, burstHeadRequirement = profile.burstHeadRequirement,
                burstPropellantRequirement = profile.burstPropellantRequirement,
                headCapacity = profile.headCapacity, propellantCapacity = profile.propellantCapacity,
                requestedBlastDamage = profile.requestedBlastDamage,
                targetBlastDamage = profile.targetBlastDamage, actualBlastDamage = profile.actualBlastDamage,
                requestedPenetrationBudget = profile.requestedPenetrationBudget,
                targetPenetrationBudget = profile.targetPenetrationBudget,
                actualPenetrationBudget = profile.actualPenetrationBudget,
                laserBaseDamage = profile.laserBaseDamage, laserShotEnergy = profile.laserShotEnergy,
                laserMaxPower = profile.laserMaxPower, laserEnergyPerSecond = profile.laserEnergyPerSecond,
                laserMaxEnergyPerSecond = profile.laserMaxEnergyPerSecond,
                sourceSustainedRate = profile.sourceSustainedRate,
                targetSustainedRate = profile.targetSustainedRate,
                propellantPerSecond = profile.propellantPerSecond, headsPerSecond = profile.headsPerSecond,
                averageHeatPerSecond = profile.averageHeatPerSecond,
                magazineDurationSeconds = profile.magazineDurationSeconds,
                fullMagazineBursts = profile.fullMagazineBursts, spaceRange = profile.spaceRange,
                baseSpreadDeg = profile.baseSpreadDeg,
                turnRateDegPerSecond = profile.turnRateDegPerSecond,
                fireToleranceDeg = profile.fireToleranceDeg,
                intrinsicAimSkill = profile.intrinsicAimSkill,
                performanceCapped = profile.performanceCapped, safetyLimited = profile.safetyLimited,
            };
        }

        private static IEnumerable<ShieldEntry> ShieldEntries()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def?.comps == null) continue;
                int index = 0;
                foreach (CompProperties comp in def.comps)
                {
                    if (comp is CompProperties_ProjectileInterceptor interceptor)
                        yield return new ShieldEntry(def, interceptor, index, PackageId(def.modContentPack));
                    index++;
                }
            }
        }

        private static AutoSpaceAdaptationShieldObservation Shield(ShieldEntry entry)
        {
            ThingDef def = entry.def;
            CompProperties_ProjectileInterceptor comp = entry.comp;
            bool building = def.thingClass != null && typeof(Building).IsAssignableFrom(def.thingClass);
            bool deltaV = def.GetCompProperties<CompProperties_SpaceShield>() != null;
            bool candidate = building && !deltaV && comp.radius > 0f && comp.hitPoints > 0;
            string reason = candidate ? "positiveCapacityBuilding"
                : deltaV ? "existingDeltaVShield"
                : !building ? "notBuilding"
                : !(comp.radius > 0f) ? "nonPositiveRadius"
                : "nonPositiveCapacity";
            return new AutoSpaceAdaptationShieldObservation
            {
                defName = def.defName, label = def.label, thingClass = def.thingClass?.FullName,
                compClass = comp.compClass?.FullName, compIndex = entry.index, building = building,
                existingDeltaVShield = deltaV, positiveCapacityBuildingCandidate = candidate,
                observationReason = reason, radius = comp.radius, hitPoints = comp.hitPoints,
                cooldownTicks = comp.cooldownTicks, disarmedByEmpForTicks = comp.disarmedByEmpForTicks,
                interceptGroundProjectiles = comp.interceptGroundProjectiles,
                interceptAirProjectiles = comp.interceptAirProjectiles,
                interceptNonHostileProjectiles = comp.interceptNonHostileProjectiles,
                interceptOutgoingProjectiles = comp.interceptOutgoingProjectiles,
                chargeIntervalTicks = comp.chargeIntervalTicks,
                chargeDurationTicks = comp.chargeDurationTicks,
                rechargeHitPointsIntervalTicks = comp.rechargeHitPointsIntervalTicks,
                hitPointsRestoreInstantlyAfterCharge = comp.hitPointsRestoreInstantlyAfterCharge,
                startWithMaxHitPoints = comp.startWithMaxHitPoints, activated = comp.activated,
                activeDuration = comp.activeDuration,
            };
        }

        private static AutoSpaceAdaptationWeaponSummary WeaponSummary(
            IReadOnlyList<AutoSpaceAdaptationWeaponReport> weapons) => new AutoSpaceAdaptationWeaponSummary
        {
            candidates = weapons.Count,
            structurallySupported = weapons.Count(x => x.structurallySupported),
            rejected = weapons.Count(x => !x.structurallySupported),
            withWarnings = weapons.Count(x => x.warnings.Count > 0),
            configuredEnabled = weapons.Count(x => x.configuredEnabled),
            injectedThisLaunch = weapons.Count(x => x.injectedThisLaunch),
            rapid = weapons.Count(x => x.family == AutoSpaceWeaponFamily.Rapid.ToString()),
            heavy = weapons.Count(x => x.family == AutoSpaceWeaponFamily.Heavy.ToString()),
            laser = weapons.Count(x => x.family == AutoSpaceWeaponFamily.Laser.ToString()),
        };

        private static AutoSpaceAdaptationReportSummary Summary(
            IReadOnlyList<AutoSpaceAdaptationModReport> mods) => new AutoSpaceAdaptationReportSummary
        {
            activeMods = mods.Count,
            weaponCandidates = mods.Sum(x => x.weaponSummary.candidates),
            structurallySupportedWeapons = mods.Sum(x => x.weaponSummary.structurallySupported),
            rejectedWeapons = mods.Sum(x => x.weaponSummary.rejected),
            weaponsWithWarnings = mods.Sum(x => x.weaponSummary.withWarnings),
            configuredWeapons = mods.Sum(x => x.weaponSummary.configuredEnabled),
            injectedWeaponsThisLaunch = mods.Sum(x => x.weaponSummary.injectedThisLaunch),
            rapidWeapons = mods.Sum(x => x.weaponSummary.rapid),
            heavyWeapons = mods.Sum(x => x.weaponSummary.heavy),
            laserWeapons = mods.Sum(x => x.weaponSummary.laser),
            shieldObservations = mods.Sum(x => x.shieldObservations.Count),
            positiveCapacityBuildingShields = mods.Sum(x =>
                x.shieldObservations.Count(shield => shield.positiveCapacityBuildingCandidate)),
        };

        private static List<string> WarningNames(AutoSpaceWeaponWarning warnings)
        {
            var names = new List<string>();
            foreach (AutoSpaceWeaponWarning warning in Enum.GetValues(typeof(AutoSpaceWeaponWarning)))
                if (warning != AutoSpaceWeaponWarning.None && warnings.HasFlag(warning))
                    names.Add(warning.ToString());
            return names;
        }

        private static string PackageId(ModContentPack mod) => AutoSpaceWeaponPolicy.NormalizePackageId(
            mod?.PackageIdPlayerFacing ?? mod?.PackageId ?? "ludeon.rimworld");

        private static string FullPath(string path) => path.NullOrEmpty() ? path : Path.GetFullPath(path);

        private static void Write(string path, AutoSpaceAdaptationReportDocument document)
        {
            File.WriteAllBytes(path, Serialize(document));
        }

        public static byte[] Serialize(AutoSpaceAdaptationReportDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var serializer = new DataContractJsonSerializer(typeof(AutoSpaceAdaptationReportDocument),
                new DataContractJsonSerializerSettings
                {
                    MaxItemsInObjectGraph = int.MaxValue,
                    UseSimpleDictionaryFormat = true,
                });
            using var memory = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(memory, Encoding.UTF8,
                false, true, "  "))
                serializer.WriteObject(writer, document);
            return memory.ToArray();
        }

        private readonly struct ShieldEntry
        {
            internal readonly ThingDef def;
            internal readonly CompProperties_ProjectileInterceptor comp;
            internal readonly int index;
            internal readonly string packageId;
            internal ShieldEntry(ThingDef def, CompProperties_ProjectileInterceptor comp,
                int index, string packageId)
            { this.def = def; this.comp = comp; this.index = index; this.packageId = packageId; }
        }
    }
}

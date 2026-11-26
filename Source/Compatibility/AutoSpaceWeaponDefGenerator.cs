using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class AutoSpaceWeaponCandidate
    {
        public ThingDef turret;
        public string packageId;
        public string modName;
        public string weaponKey;
        public AutoSpaceWeaponSource source;
        public AutoSpaceWeaponProfile profile;
        public AutoSpaceWeaponFamily family;
        public AutoSpaceWeaponRejection rejection;
        public AutoSpaceWeaponWarning warnings;
        public bool enabled;

        public bool StructurallySupported => rejection == AutoSpaceWeaponRejection.None;
    }

    /// <summary>Extracts final source values and injects a frozen Delta-V weapon profile once.</summary>
    public static class AutoSpaceWeaponDefGenerator
    {
        private const int SearchLimit = 1000000;
        private static readonly List<AutoSpaceWeaponCandidate> candidates =
            new List<AutoSpaceWeaponCandidate>();

        public static IReadOnlyList<AutoSpaceWeaponCandidate> Candidates => candidates;

        private sealed class ProductionPhysics : IAutoSpaceWeaponPhysics
        {
            private readonly ImpactOutcomePart steel;
            private readonly ImpactOutcomePart uranium;

            internal ProductionPhysics(ImpactOutcomePart steel, ImpactOutcomePart uranium)
            {
                this.steel = steel;
                this.uranium = uranium;
            }

            public int HeadsForBlastDamage(float target, out bool safetyLimited)
            {
                int lo = 1, hi = 1;
                while (hi < SearchLimit && BlastDamage(hi) + .0001f < target)
                    hi = Math.Min(SearchLimit, hi * 2);
                safetyLimited = hi == SearchLimit && BlastDamage(hi) + .0001f < target;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (BlastDamage(mid) + .0001f >= target) hi = mid;
                    else lo = mid + 1;
                }
                return lo;
            }

            public int ChargeForPenetrationBudget(float target, float projectileMass,
                out bool safetyLimited)
            {
                int lo = 1, hi = 1;
                while (hi < SearchLimit && PenetrationBudget(hi, projectileMass) + .0001f < target)
                    hi = Math.Min(SearchLimit, hi * 2);
                safetyLimited = hi == SearchLimit
                    && PenetrationBudget(hi, projectileMass) + .0001f < target;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (PenetrationBudget(mid, projectileMass) + .0001f >= target) hi = mid;
                    else lo = mid + 1;
                }
                return lo;
            }

            public float BlastDamage(int heads) => SpaceImpactEffects.ComputeDamage(steel,
                SpaceKinetics.LaunchSpeed(1, Math.Max(1, heads)), 0f, 1f, Math.Max(1, heads));

            public float PenetrationBudget(int charge, float projectileMass) =>
                SpaceImpactEffects.ComputeDamage(uranium,
                    SpaceKinetics.LaunchSpeed(Math.Max(1, charge), Math.Max(.001f, projectileMass)),
                    0f, 1f, Math.Max(.001f, projectileMass));

            public float BlastDamageUpperBound => steel?.highExplosive?.damageByMass?.Points
                .Select(point => point.y).DefaultIfEmpty(BlastDamage(SearchLimit)).Max()
                ?? BlastDamage(SearchLimit);

            public float PenetrationBudgetUpperBound => uranium?.penetrationMaximum > 0f
                ? uranium.penetrationMaximum : PenetrationBudget(SearchLimit, 1f);
        }

        public static void InjectAll(bool hotReload)
        {
            SpaceImpactEffectDef steel = DefDatabase<SpaceImpactEffectDef>
                .GetNamedSilentFail("DeltaV_Impact_SteelShellVsStructure");
            SpaceImpactEffectDef uranium = DefDatabase<SpaceImpactEffectDef>
                .GetNamedSilentFail("DeltaV_Impact_CannonVsStructure");
            ThingDef steelHead = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_SteelHead");
            ThingDef uraniumHead = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_UraniumHead");
            ThingDef propellant = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Propellant");
            ThingDef rapid = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_Rapid");
            ThingDef rapidUranium = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_RapidUranium");
            ThingDef heavy = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_Cannon");
            ThingDef heavyUranium = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_CannonUranium");
            ThingDef laser = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_Projectile_Laser");
            ThingDef nativeProjectile = DefDatabase<ThingDef>.GetNamedSilentFail("DeltaV_PawnNativeProjectileProxy");
            if (steel?.defenderEffect == null || uranium?.defenderEffect == null || steelHead == null
                || uraniumHead == null || propellant == null || rapid == null || rapidUranium == null
                || heavy == null || heavyUranium == null || laser == null || nativeProjectile == null) return;

            var physics = new ProductionPhysics(steel.defenderEffect, uranium.defenderEffect);
            if (!hotReload) candidates.Clear();
            foreach (ThingDef turret in DefDatabase<ThingDef>.AllDefsListForReading.ToList())
            {
                if (turret?.thingClass == null
                    || !typeof(Building_Turret).IsAssignableFrom(turret.thingClass)
                    || turret.GetModExtension<DefModExtension_AutoSpaceWeaponResolved>() != null
                    || turret.HasComp<CompSpaceWeapon>()) continue;
                bool supported = TrySnapshot(turret, out AutoSpaceWeaponSource source,
                    out AutoSpaceWeaponRejection rejection, out AutoSpaceWeaponWarning warnings);
                string packageId = PackageId(turret);
                DeltaVSettings settings = DeltaVMod.CurrentSettings;
                AutoSpaceWeaponModPolicy modPolicy = settings?.GetAutoWeaponModPolicy(packageId)
                    ?? AutoSpaceWeaponModPolicy.Recommended;
                AutoSpaceWeaponOverride weaponOverride = settings?.GetAutoWeaponOverride(packageId,
                    turret.defName) ?? AutoSpaceWeaponOverride.Inherit;
                bool enabled = AutoSpaceWeaponPolicy.Enabled(modPolicy, weaponOverride, supported,
                    warnings, source?.extension?.force == true);
                AutoSpaceWeaponProfile profile = null;
                if (supported)
                {
                    try { profile = AutoSpaceWeaponResolver.Resolve(source, physics); }
                    catch (ArgumentException)
                    {
                        rejection = AutoSpaceWeaponRejection.InvalidNumbers;
                        enabled = false;
                    }
                }
                UpsertCandidate(new AutoSpaceWeaponCandidate
                {
                    turret = turret,
                    packageId = packageId,
                    modName = turret.modContentPack?.Name ?? packageId,
                    weaponKey = AutoSpaceWeaponPolicy.WeaponKey(packageId, turret.defName),
                    source = source,
                    profile = profile,
                    family = profile?.family ?? AutoSpaceWeaponFamily.Auto,
                    rejection = rejection,
                    warnings = warnings,
                    enabled = enabled
                });
                if (!enabled || profile == null) continue;
                Inject(turret, profile, steelHead, uraniumHead, propellant,
                    rapid, rapidUranium, heavy, heavyUranium, laser, nativeProjectile, hotReload);
            }
        }

        private static string PackageId(ThingDef turret) =>
            AutoSpaceWeaponPolicy.NormalizePackageId(turret?.modContentPack?.PackageIdPlayerFacing
                ?? turret?.modContentPack?.PackageId ?? "ludeon.rimworld");

        private static void UpsertCandidate(AutoSpaceWeaponCandidate candidate)
        {
            int index = candidates.FindIndex(x => x.weaponKey == candidate.weaponKey);
            if (index >= 0) candidates[index] = candidate;
            else candidates.Add(candidate);
        }

        private static void Inject(ThingDef turret, AutoSpaceWeaponProfile profile,
            ThingDef steelHead, ThingDef uraniumHead, ThingDef propellant,
            ThingDef rapid, ThingDef rapidUranium, ThingDef heavy, ThingDef heavyUranium,
            ThingDef laser, ThingDef nativeProjectile, bool hotReload)
        {
            var comps = turret.comps == null
                ? new List<CompProperties>() : new List<CompProperties>(turret.comps);
            comps.Add(new CompProperties_SpaceWeaponTraverse
            {
                turnRateDegPerSec = profile.turnRateDegPerSecond,
                fireToleranceDeg = profile.fireToleranceDeg,
                mechanicalArcDeg = 360f
            });

            bool native = profile.mode == AutoSpaceWeaponMode.NativeProjectile;
            bool chemical = !native && profile.family != AutoSpaceWeaponFamily.Laser;
            if (chemical)
            {
                comps.Add(Supply(GunSupplyKind.Heads, profile.headCapacity, steelHead, uraniumHead));
                comps.Add(Supply(GunSupplyKind.Propellant, profile.propellantCapacity, propellant, null));
                comps.Add(new CompProperties_SpaceWeaponMagazine
                {
                    ammoPerShot = 1f,
                    unitMass = 1f,
                    defaultPriority = 0
                });
            }

            ThingDef projectile = native ? nativeProjectile
                : profile.family == AutoSpaceWeaponFamily.Rapid ? rapid
                : profile.family == AutoSpaceWeaponFamily.Heavy ? heavy : laser;
            ThingDef apProjectile = native ? null
                : profile.family == AutoSpaceWeaponFamily.Rapid ? rapidUranium
                : profile.family == AutoSpaceWeaponFamily.Heavy ? heavyUranium : null;
            float muzzleDistance = Math.Max(.5f, Math.Max(turret.size.x, turret.size.z) * .5f
                + (turret.building?.turretTopOffset.magnitude ?? 0f) + .15f);
            var weapon = new CompProperties_SpaceWeapon
            {
                projectileDef = projectile,
                armorPiercingProjectileDef = apProjectile,
                armorPiercingHeadDef = chemical ? uraniumHead : null,
                armorPiercingUnitMass = 1.5f,
                armorPiercingPenetrationScale = 1.5f,
                topTurret = true,
                fireArcDeg = 360f,
                baseSpreadDeg = profile.baseSpreadDeg,
                cooldownTicks = profile.intervalTicks,
                range = profile.spaceRange,
                minRange = profile.family == AutoSpaceWeaponFamily.Heavy ? 15f : 10f,
                groundModeAvailable = true,
                defaultEngagementMode = SpaceWeaponEngagementMode.Ground,
                shotEnergy = !native && profile.family == AutoSpaceWeaponFamily.Laser ? profile.laserShotEnergy : 0f,
                impactScale = !native && profile.family == AutoSpaceWeaponFamily.Laser
                    ? profile.laserBaseDamage / 8f : 1f,
                intrinsicAimSkill = turret.HasComp<CompMannable>() ? -1 : profile.intrinsicAimSkill,
                turretMuzzleDistance = muzzleDistance,
                muzzleFlashSize = profile.family == AutoSpaceWeaponFamily.Rapid ? .65f
                    : profile.family == AutoSpaceWeaponFamily.Heavy ? 2.2f : 0f,
                recoilDistance = profile.family == AutoSpaceWeaponFamily.Rapid ? .08f
                    : profile.family == AutoSpaceWeaponFamily.Heavy ? .16f : 0f,
                recoilTicks = profile.family == AutoSpaceWeaponFamily.Rapid ? 8 : 20,
                defaultFireDeviationDeg = Math.Max(.5f, Math.Min(2f, profile.fireToleranceDeg)),
                defaultManualFireDeviationDeg = 6f
            };
            comps.Add(weapon);
            if (!native) comps.Add(Tuning(profile));
            comps.Add(new CompProperties_AutoSpaceWeaponTurretBridge());
            turret.comps = comps;

            var extensions = turret.modExtensions == null
                ? new List<DefModExtension>() : new List<DefModExtension>(turret.modExtensions);
            extensions.Add(new DefModExtension_AutoSpaceWeaponResolved
            {
                sourceDefName = turret.defName,
                family = profile.family,
                mode = profile.mode
            });
            turret.modExtensions = extensions;

            string ruleName = "DeltaV_AutoSpaceWeaponShell_" + turret.defName;
            ShellRenderRuleDef rule = DefDatabase<ShellRenderRuleDef>.GetNamedSilentFail(ruleName);
            if (rule == null)
            {
                rule = new ShellRenderRuleDef
                {
                    defName = ruleName,
                    thingDef = turret,
                    render = new ShellRenderConfig
                    {
                        source = ShellGraphicSource.TurretTop,
                        layer = ShellSurfaceLayer.Exposed
                    },
                    modContentPack = turret.modContentPack
                };
                DefGenerator.AddImpliedDef(rule, hotReload);
            }
            else
            {
                rule.thingDef = turret;
                rule.render ??= new ShellRenderConfig();
                rule.render.source = ShellGraphicSource.TurretTop;
                rule.render.layer = ShellSurfaceLayer.Exposed;
            }
        }

        private static CompProperties_GunSupply Supply(GunSupplyKind kind, int capacity,
            ThingDef accepted, ThingDef alternate)
        {
            var filter = new ThingFilter();
            filter.SetAllow(accepted, true);
            return new CompProperties_GunSupply
            {
                kind = kind,
                alternateHead = alternate,
                fuelCapacity = capacity,
                initialFuelPercent = 0f,
                initialConfigurableTargetFuelLevel = capacity,
                fuelFilter = filter
            };
        }

        private static CompProperties_WeaponTuning Tuning(AutoSpaceWeaponProfile profile)
        {
            if (profile.family == AutoSpaceWeaponFamily.Laser)
            {
                return new CompProperties_WeaponTuning
                {
                    chemical = false,
                    minInterval = profile.intervalTicks,
                    maxInterval = profile.intervalTicks,
                    defaultInterval = profile.intervalTicks,
                    heatPerShot = 0f,
                    maxPower = profile.laserMaxPower
                };
            }
            bool heavy = profile.family == AutoSpaceWeaponFamily.Heavy;
            return new CompProperties_WeaponTuning
            {
                chemical = true,
                maxCharge = profile.charge,
                defaultCharge = profile.charge,
                maxMass = profile.heads,
                defaultMass = profile.heads,
                minInterval = profile.intervalTicks,
                maxInterval = profile.intervalTicks,
                defaultInterval = profile.intervalTicks,
                heatPerShot = heavy ? 4f : 5f,
                coolingPerSecond = profile.coolingPerSecond,
                heatCapacity = profile.heatCapacity,
                lockEveryShot = heavy
            };
        }

        public static bool TrySnapshot(ThingDef turret, out AutoSpaceWeaponSource source,
            out AutoSpaceWeaponRejection rejection)
        {
            return TrySnapshot(turret, out source, out rejection, out _);
        }

        public static bool TrySnapshot(ThingDef turret, out AutoSpaceWeaponSource source,
            out AutoSpaceWeaponRejection rejection, out AutoSpaceWeaponWarning warnings)
        {
            source = null;
            rejection = AutoSpaceWeaponRejection.None;
            warnings = AutoSpaceWeaponWarning.None;
            if (turret?.thingClass == null || !typeof(Building_Turret).IsAssignableFrom(turret.thingClass))
                return Reject(AutoSpaceWeaponRejection.NotTurret, out rejection);
            if (turret.HasComp<CompSpaceWeapon>())
                return Reject(AutoSpaceWeaponRejection.ExistingSpaceWeapon, out rejection);
            DefModExtension_AutoSpaceWeapon extension = turret.GetModExtension<DefModExtension_AutoSpaceWeapon>();
            if (extension?.disabled == true)
                return Reject(AutoSpaceWeaponRejection.Disabled, out rejection);
            if (extension?.externalResourceTransaction == true)
                warnings |= AutoSpaceWeaponWarning.ExternalResourceTransaction;
            ThingDef gun = turret.building?.turretGunDef;
            if (gun == null) return Reject(AutoSpaceWeaponRejection.MissingGun, out rejection);
            List<VerbProperties> verbs = gun.Verbs?.Where(v => v != null && v.IsMeleeAttack == false).ToList();
            if (verbs == null || verbs.Count != 1)
                return Reject(AutoSpaceWeaponRejection.AmbiguousVerb, out rejection);
            VerbProperties verb = verbs[0];
            if (verb.verbClass == null)
                return Reject(AutoSpaceWeaponRejection.UnsupportedVerb, out rejection);
            bool standardProjectileVerb = typeof(Verb_LaunchProjectile).IsAssignableFrom(verb.verbClass);
            bool standardBeamVerb = typeof(Verb_ShootBeam).IsAssignableFrom(verb.verbClass);
            if (!standardProjectileVerb && !standardBeamVerb)
                warnings |= AutoSpaceWeaponWarning.CustomVerb;
            ThingDef projectile = verb.defaultProjectile;
            if (extension != null && !extension.sourceProjectile.NullOrEmpty())
                projectile = DefDatabase<ThingDef>.GetNamedSilentFail(extension.sourceProjectile);
            ProjectileProperties props = projectile?.projectile;
            DamageDef damageDef = props?.damageDef ?? verb.beamDamageDef;
            if (props == null && (!standardBeamVerb || damageDef == null))
                return Reject(AutoSpaceWeaponRejection.MissingProjectile, out rejection);
            bool nativeProjectile = projectile?.thingClass != null
                && typeof(Verse.Projectile).IsAssignableFrom(projectile.thingClass);
            // Entity identity wins over damage category: plasma and other Heat projectiles
            // still have a real instance whose Impact must be replayed.
            bool beam = !nativeProjectile && (standardBeamVerb
                || projectile?.thingClass != null && typeof(Beam).IsAssignableFrom(projectile.thingClass)
                || damageDef?.armorCategory?.defName == "Heat");
            if (!beam && !nativeProjectile)
                return Reject(AutoSpaceWeaponRejection.UnsupportedProjectile, out rejection);
            bool customFlight = nativeProjectile && HasCustomFlight(projectile.thingClass);
            if (customFlight) warnings |= AutoSpaceWeaponWarning.CustomFlight;
            bool kinetic = damageDef?.armorCategory?.defName is "Sharp" or "Blunt";
            if (props?.explosionRadius > 0f)
                warnings |= AutoSpaceWeaponWarning.ExplosiveProjectile;
            if (props?.flyOverhead == true)
                warnings |= AutoSpaceWeaponWarning.FlyOverhead;
            if (!beam && !kinetic)
            {
                if (extension?.force == true && damageDef != null)
                {
                    kinetic = true;
                    warnings |= AutoSpaceWeaponWarning.ThirdPartyMechanics;
                }
                else return Reject(damageDef == null
                    ? AutoSpaceWeaponRejection.UnsupportedProjectile
                    : AutoSpaceWeaponRejection.UnsupportedDamage, out rejection);
            }
            if (UsesThirdPartyMechanics(turret, verb))
                warnings |= AutoSpaceWeaponWarning.ThirdPartyMechanics;
            float cooldown = turret.building.turretBurstCooldownTime >= 0
                ? turret.building.turretBurstCooldownTime
                : gun.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown);
            CompProperties_Refuelable refuelable = turret.comps?
                .OfType<CompProperties_Refuelable>().FirstOrDefault();
            bool usesMortarBarrel = refuelable?.fuelIsMortarBarrel == true;
            if (usesMortarBarrel) warnings |= AutoSpaceWeaponWarning.MortarBarrel;
            int sourceSupportedShots = refuelable == null ? 0
                : AutoSpaceWeaponPolicy.SourceSupportedShots(refuelable.fuelCapacity,
                    verb.consumeFuelPerShot, verb.consumeFuelPerBurst, verb.burstShotCount);
            float rawDamage = props != null ? props.GetDamageAmount(null) : verb.beamTotalDamage;
            float buildingDamageFactor = damageDef == null ? 1f : Math.Max(0f, damageDef.buildingDamageFactor);
            source = new AutoSpaceWeaponSource
            {
                defName = turret.defName,
                verbClassName = verb.verbClass.FullName,
                kinetic = kinetic,
                beam = beam,
                blunt = damageDef?.armorCategory?.defName == "Blunt",
                explosiveProjectile = props?.explosionRadius > 0f,
                flyOverhead = props?.flyOverhead == true,
                nativeProjectile = nativeProjectile,
                customFlight = customFlight,
                sourceUsesMortarBarrel = usesMortarBarrel,
                sourceFuelCapacity = Math.Max(0f, refuelable?.fuelCapacity ?? 0f),
                sourceFuelPerShot = Math.Max(0f, verb.consumeFuelPerShot),
                sourceFuelPerBurst = Math.Max(0f, verb.consumeFuelPerBurst),
                damage = rawDamage * buildingDamageFactor,
                armorPenetration = Math.Max(0f, props?.GetArmorPenetration()
                    ?? damageDef?.defaultArmorPenetration ?? 0f),
                projectileSpeed = Math.Max(0f, props?.speed ?? 0f),
                burstShotCount = Math.Max(1, verb.burstShotCount),
                ticksBetweenBurstShots = Math.Max(0, verb.ticksBetweenBurstShots),
                warmupSeconds = Math.Max(0f, verb.warmupTime + turret.building.turretBurstWarmupTime.Average),
                cooldownSeconds = Math.Max(0f, cooldown),
                sourceSupportedShots = Math.Max(0, sourceSupportedShots),
                footprintArea = Math.Max(1, turret.size.Area),
                range = Math.Max(0f, verb.range),
                sprayWidth = Math.Max(0f, verb.sprayWidth),
                sprayArching = Math.Max(0f, verb.sprayArching),
                sprayThicknessCells = Math.Max(0, verb.sprayThicknessCells),
                turretAccuracy = Math.Max(0f,
                    turret.GetStatValueAbstract(StatDefOf.ShootingAccuracyTurret)),
                longRangeAccuracy = Math.Max(0f, gun.GetStatValueAbstract(StatDefOf.AccuracyLong)),
                extension = extension
            };
            if (!(source.damage > 0) || float.IsNaN(source.damage) || float.IsInfinity(source.damage))
            {
                source = null;
                return Reject(AutoSpaceWeaponRejection.InvalidNumbers, out rejection);
            }
            return true;
        }

        private static bool UsesThirdPartyMechanics(ThingDef turret, VerbProperties verb)
        {
            var gameAssembly = typeof(Thing).Assembly;
            if (turret.thingClass?.Assembly != gameAssembly || verb.verbClass?.Assembly != gameAssembly)
                return true;
            return turret.comps?.Any(comp => comp != null
                && (comp.GetType().Assembly != gameAssembly
                    || comp.compClass != null && comp.compClass.Assembly != gameAssembly)) == true;
        }

        private static bool HasCustomFlight(Type projectileType)
        {
            if (projectileType == null || projectileType.Assembly == typeof(Verse.Projectile).Assembly)
                return false;
            for (Type type = projectileType; type != null && type != typeof(Verse.Projectile);
                type = type.BaseType)
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly;
                if (type.GetMethod("Tick", flags,
                        null, Type.EmptyTypes, null) != null
                    || type.GetMethod("TickInterval", flags,
                        null, new[] { typeof(int) }, null) != null)
                    return true;
            }
            return false;
        }

        private static bool Reject(AutoSpaceWeaponRejection value,
            out AutoSpaceWeaponRejection rejection)
        {
            rejection = value;
            return false;
        }
    }

    [HarmonyPatch(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve))]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_AutoSpaceWeaponDefGenerator
    {
        private static void Postfix(bool hotReload) => AutoSpaceWeaponDefGenerator.InjectAll(hotReload);
    }
}

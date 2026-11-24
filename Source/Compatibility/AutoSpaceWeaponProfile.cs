using System;
using Verse;

namespace DeltaV
{
    public enum AutoSpaceWeaponFamily : byte
    {
        Auto,
        Rapid,
        Heavy,
        Laser
    }

    public enum AutoSpaceWeaponRejection : byte
    {
        None,
        Disabled,
        NotTurret,
        ExistingSpaceWeapon,
        MissingGun,
        AmbiguousVerb,
        MissingProjectile,
        UnsupportedVerb,
        UnsupportedProjectile,
        UnsupportedDamage,
        SpecialResourceTransaction,
        InvalidNumbers
    }

    /// <summary>Third-party XML override for automatic space-weapon adaptation.</summary>
    public sealed class DefModExtension_AutoSpaceWeapon : DefModExtension
    {
        public bool disabled;
        public bool force;
        public AutoSpaceWeaponFamily family = AutoSpaceWeaponFamily.Auto;
        public string sourceProjectile;
        public float chargeMultiplier = 1f;
        public float headMultiplier = 1f;
        public float intervalMultiplier = 1f;
        public float burstMultiplier = 1f;
        public float restMultiplier = 1f;
        public float coolingMultiplier = 1f;
        public float heatCapacityMultiplier = 1f;
        public float laserDamageMultiplier = 1f;
        public float laserEnergyMultiplier = 1f;
        public bool externalResourceTransaction;
    }

    /// <summary>Generated once from the final source Def; runtime bridges only read this marker.</summary>
    public sealed class DefModExtension_AutoSpaceWeaponResolved : DefModExtension
    {
        public string sourceDefName;
        public AutoSpaceWeaponFamily family;
        public int profileVersion = 1;
    }

    /// <summary>Frozen final-Def observations. No runtime Thing state belongs here.</summary>
    public sealed class AutoSpaceWeaponSource
    {
        public string defName;
        public string verbClassName;
        public bool kinetic;
        public bool beam;
        public bool blunt;
        public bool explosiveProjectile;
        public bool flyOverhead;
        public bool sourceUsesMortarBarrel;
        public float damage;
        public float armorPenetration;
        public float projectileSpeed;
        public int burstShotCount = 1;
        public int ticksBetweenBurstShots;
        public float warmupSeconds;
        public float cooldownSeconds;
        public float sourceFuelCapacity;
        public float sourceFuelPerShot;
        public float sourceFuelPerBurst;
        public int sourceSupportedShots;
        public int footprintArea = 1;
        public float range;
        public float sprayWidth;
        public float sprayArching;
        public int sprayThicknessCells = 1;
        public float turretAccuracy = .96f;
        public float longRangeAccuracy = .5f;
        public DefModExtension_AutoSpaceWeapon extension;

        public double ActiveSeconds => Math.Max(0, burstShotCount - 1) * Math.Max(0, ticksBetweenBurstShots) / 60d;
        public double RestSeconds => Math.Max(0, warmupSeconds) + Math.Max(0, cooldownSeconds);
        public double CycleSeconds => Math.Max(1d / 60d, ActiveSeconds + RestSeconds);
        public double SustainedRate => Math.Max(1, burstShotCount) / CycleSeconds;
        public double PeakRate => burstShotCount > 1 && ticksBetweenBurstShots > 0
            ? 60d / ticksBetweenBurstShots : SustainedRate;
    }

    public sealed class AutoSpaceWeaponProfile
    {
        public string defName;
        public AutoSpaceWeaponFamily family;
        public int charge;
        public int heads;
        public int intervalTicks;
        public int burstShots;
        public int restTicks;
        public float heatPerShot;
        public float coolingPerSecond;
        public float heatCapacity;
        public int sourceSupportedShots;
        public int sourceHeadRequirement;
        public int sourcePropellantRequirement;
        public int areaCapacity;
        public int burstHeadRequirement;
        public int burstPropellantRequirement;
        public int headCapacity;
        public int propellantCapacity;
        public float requestedBlastDamage;
        public float targetBlastDamage;
        public float actualBlastDamage;
        public float requestedPenetrationBudget;
        public float targetPenetrationBudget;
        public float actualPenetrationBudget;
        public float laserBaseDamage;
        public float laserShotEnergy;
        public int laserMaxPower;
        public float laserEnergyPerSecond;
        public float laserMaxEnergyPerSecond;
        public float sourceSustainedRate;
        public float targetSustainedRate;
        public float propellantPerSecond;
        public float headsPerSecond;
        public float averageHeatPerSecond;
        public float magazineDurationSeconds;
        public float fullMagazineBursts;
        public float spaceRange;
        public float baseSpreadDeg;
        public float turnRateDegPerSecond;
        public float fireToleranceDeg;
        public int intrinsicAimSkill;
        public bool performanceCapped;
        public bool safetyLimited;
    }

    /// <summary>Adapter over the production impact effects; implementations must not copy their curves.</summary>
    public interface IAutoSpaceWeaponPhysics
    {
        int HeadsForBlastDamage(float target, out bool safetyLimited);
        int ChargeForPenetrationBudget(float target, float projectileMass, out bool safetyLimited);
        float BlastDamage(int heads);
        float PenetrationBudget(int charge, float projectileMass);
        float BlastDamageUpperBound { get; }
        float PenetrationBudgetUpperBound { get; }
    }

    /// <summary>Pure, deterministic candidate resolver used by the stage-A audit and later Def generation.</summary>
    public static class AutoSpaceWeaponResolver
    {
        private const int SafetyIntegerLimit = 1000000;
        private static readonly double RapidSpeedExponent = Math.Log(88d / 70d) / Math.Log(27d / 12d);
        // The source turret spends 3.2 s cooling and an average 0.5 s aiming before its next shot.
        // Target-search hash latency is scheduling noise, not weapon hardware, and is intentionally excluded.
        private static readonly double SlugChargeThroughput = 24d / (3.2d + .5d);
        private const double BeamRepeaterSustainedDamage = 5d * 30d / (4d + 3d + 29d * 10d / 60d);

        public static AutoSpaceWeaponProfile Resolve(AutoSpaceWeaponSource source, IAutoSpaceWeaponPhysics physics)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (physics == null) throw new ArgumentNullException(nameof(physics));
            Validate(source);
            DefModExtension_AutoSpaceWeapon ext = source.extension;
            AutoSpaceWeaponFamily family = ext != null && ext.family != AutoSpaceWeaponFamily.Auto
                ? ext.family : Classify(source);
            var result = new AutoSpaceWeaponProfile
            {
                defName = source.defName,
                family = family,
                sourceSustainedRate = (float)source.SustainedRate
            };
            ResolveGeometry(source, result);
            if (family == AutoSpaceWeaponFamily.Laser)
            {
                ResolveLaser(source, result, ext);
                return result;
            }

            ResolveChemical(source, physics, result, ext);
            return result;
        }

        private static readonly float[] AccuracyByEvenSkill =
        {
            .89f, .93f, .94f, .95f, .96f, .97f, .975f, .98f, .98333f, .98666f, .99f
        };

        private static void ResolveGeometry(AutoSpaceWeaponSource source, AutoSpaceWeaponProfile result)
        {
            // Preserve relative ground-weapon reach while moving the whole family into tactical space ranges.
            result.spaceRange = FinitePositiveFloat(60d + Math.Max(0d, source.range) * 2.5d);
            // The gun's long-range accuracy controls barrel dispersion; turret electronics remain a separate skill.
            double sourceSprayRadius = Math.Max(Math.Max(0d, source.sprayWidth),
                Math.Max(0, source.sprayThicknessCells) * .5d + Math.Max(0d, source.sprayArching));
            double sourceSprayAngle = sourceSprayRadius <= 0 ? 0
                : Math.Atan(sourceSprayRadius / Math.Max(1d, source.range)) * 180d / Math.PI;
            result.baseSpreadDeg = FinitePositiveFloat(Math.Max(.5d, Math.Max(
                Math.Min(4d, 3.2d - 2.4d * Math.Max(0d, Math.Min(1d, source.longRangeAccuracy))),
                Math.Min(12d, sourceSprayAngle))));
            result.turnRateDegPerSecond = FinitePositiveFloat(180d / Math.Sqrt(Math.Max(1, source.footprintArea)));
            result.fireToleranceDeg = FinitePositiveFloat(Math.Max(.5d, Math.Min(4d, result.baseSpreadDeg)));
            result.intrinsicAimSkill = SkillForAccuracy(source.turretAccuracy);
        }

        private static int SkillForAccuracy(float accuracy)
        {
            if (!(accuracy > AccuracyByEvenSkill[0])) return 0;
            int last = AccuracyByEvenSkill.Length - 1;
            if (accuracy >= AccuracyByEvenSkill[last]) return 20;
            for (int i = 1; i < AccuracyByEvenSkill.Length; i++)
            {
                if (accuracy > AccuracyByEvenSkill[i]) continue;
                float low = AccuracyByEvenSkill[i - 1], high = AccuracyByEvenSkill[i];
                double skill = (i - 1) * 2d + 2d * (accuracy - low) / Math.Max(1e-6f, high - low);
                return (int)Math.Max(0d, Math.Min(20d, Math.Round(skill)));
            }
            return 20;
        }

        public static AutoSpaceWeaponFamily Classify(AutoSpaceWeaponSource source)
        {
            if (source.beam) return AutoSpaceWeaponFamily.Laser;
            double sustained = Math.Max(.01, source.SustainedRate);
            double peak = Math.Max(sustained, source.PeakRate);
            double damage = Math.Max(1, source.damage);
            double rapidScore = Log2(sustained / .35) + .6 * Log2(peak / .35)
                - .65 * Log2(damage / 24d);
            return rapidScore >= 0 ? AutoSpaceWeaponFamily.Rapid : AutoSpaceWeaponFamily.Heavy;
        }

        private static void ResolveChemical(AutoSpaceWeaponSource source, IAutoSpaceWeaponPhysics physics,
            AutoSpaceWeaponProfile result, DefModExtension_AutoSpaceWeapon ext)
        {
            bool rapid = result.family == AutoSpaceWeaponFamily.Rapid;
            double damageAnchor = rapid ? 12d : 55d;
            double ratio = Math.Max(.001, source.damage / damageAnchor);
            double expectedAp = Math.Max(.001, source.damage * .015d);
            double apBias = Math.Pow(Math.Max(.05, source.armorPenetration / expectedAp), rapid ? .5 : .6);
            double expectedSpeed = (rapid ? 70d : 120d) * Math.Pow(ratio, rapid ? RapidSpeedExponent : .15);
            double speedBias = Math.Pow(Math.Max(.1, source.projectileSpeed / expectedSpeed), .25);
            double bluntMassBias = source.blunt ? 1.25 : 1d;
            double blast = (rapid
                ? LogMap(source.damage, 12d, physics.BlastDamage(1), 27d, physics.BlastDamage(2))
                : physics.BlastDamage(24) * Math.Pow(ratio, .9)) * bluntMassBias;
            double penetration = (rapid
                ? LogMap(source.damage, 12d, physics.PenetrationBudget(1, 1.5f),
                    27d, physics.PenetrationBudget(4, 3f))
                : physics.PenetrationBudget(24, 36f) * ratio) * apBias * speedBias;
            if (ext != null)
            {
                blast *= Positive(ext.headMultiplier);
                penetration *= Positive(ext.chargeMultiplier);
            }
            result.requestedBlastDamage = FiniteFloat(blast);
            result.requestedPenetrationBudget = FiniteFloat(penetration);
            float blastCap = FinitePositiveFloat(physics.BlastDamageUpperBound * .95d);
            float penetrationCap = FinitePositiveFloat(physics.PenetrationBudgetUpperBound * .95d);
            result.targetBlastDamage = Math.Min(result.requestedBlastDamage, blastCap);
            result.targetPenetrationBudget = Math.Min(result.requestedPenetrationBudget, penetrationCap);
            result.performanceCapped = result.requestedBlastDamage > result.targetBlastDamage + .0001f
                || result.requestedPenetrationBudget > result.targetPenetrationBudget + .0001f;
            bool headLimited, chargeLimited;
            int rawHeads = physics.HeadsForBlastDamage(result.targetBlastDamage, out headLimited);
            float uraniumMass = Math.Max(1, rawHeads) * 1.5f;
            int rawCharge = physics.ChargeForPenetrationBudget(result.targetPenetrationBudget,
                uraniumMass, out chargeLimited);
            result.heads = SafePositiveInt(rawHeads);
            result.charge = SafePositiveInt(rawCharge);
            result.safetyLimited = headLimited || chargeLimited || rawHeads > SafetyIntegerLimit || rawCharge > SafetyIntegerLimit;
            result.actualBlastDamage = physics.BlastDamage(result.heads);
            result.actualPenetrationBudget = physics.PenetrationBudget(result.charge, result.heads * 1.5f);

            if (rapid) ResolveRapidCycle(source, result, ext);
            else ResolveHeavyCycle(source, result, ext);
            result.propellantPerSecond = result.charge * result.targetSustainedRate;
            result.headsPerSecond = result.heads * result.targetSustainedRate;
            result.averageHeatPerSecond = result.heatPerShot * result.targetSustainedRate;
            result.sourceSupportedShots = Math.Max(0, source.sourceSupportedShots);
            result.areaCapacity = SafePositiveInt(60L * (Math.Max(1, source.footprintArea) + 1L));
            result.burstHeadRequirement = SafeCapacityProduct(result.burstShots, result.heads, ref result.safetyLimited);
            result.burstPropellantRequirement = SafeCapacityProduct(result.burstShots, result.charge, ref result.safetyLimited);
            if (result.sourceSupportedShots > 0)
            {
                result.sourceHeadRequirement = SafeCapacityProduct(result.sourceSupportedShots,
                    result.heads, ref result.safetyLimited);
                result.sourcePropellantRequirement = SafeCapacityProduct(result.sourceSupportedShots,
                    result.charge, ref result.safetyLimited);
            }
            int headBaseline = result.sourceSupportedShots > 0
                ? result.sourceHeadRequirement : result.areaCapacity;
            int propellantBaseline = result.sourceSupportedShots > 0
                ? result.sourcePropellantRequirement : result.areaCapacity;
            result.headCapacity = Math.Max(headBaseline, result.burstHeadRequirement);
            result.propellantCapacity = Math.Max(propellantBaseline, result.burstPropellantRequirement);
            result.fullMagazineBursts = Math.Min(result.headCapacity / (float)Math.Max(1L, (long)result.burstShots * result.heads),
                result.propellantCapacity / (float)Math.Max(1L, (long)result.burstShots * result.charge));
            result.magazineDurationSeconds = Math.Min(result.headCapacity / Math.Max(float.Epsilon, result.headsPerSecond),
                result.propellantCapacity / Math.Max(float.Epsilon, result.propellantPerSecond));
        }

        private static void ResolveRapidCycle(AutoSpaceWeaponSource source, AutoSpaceWeaponProfile result,
            DefModExtension_AutoSpaceWeapon ext)
        {
            double intervalMultiplier = ext == null ? 1d : Positive(ext.intervalMultiplier);
            result.intervalTicks = SafePositiveInt(Math.Round(12d
                * Math.Pow(.5 / Math.Max(.001, source.SustainedRate), .35) * intervalMultiplier));
            double burstMultiplier = ext == null ? 1d : Positive(ext.burstMultiplier);
            result.burstShots = SafePositiveInt(Math.Ceiling(24d
                * Math.Pow(Math.Max(1, source.burstShotCount), .72)
                * Math.Pow(Math.Max(.02, source.SustainedRate / .5), .1) * burstMultiplier));
            double restSeconds = source.RestSeconds <= 0 ? 0
                : (1.5 + 1.2 * Math.Log(1 + source.RestSeconds))
                    * (ext == null ? 1d : Positive(ext.restMultiplier));
            result.restTicks = SafeNonNegativeInt(Math.Round(restSeconds * 60));
            restSeconds = result.restTicks / 60d;
            double heat = 5d * result.charge;
            double activeSeconds = (result.burstShots - 1) * result.intervalTicks / 60d;
            double cycleSeconds = activeSeconds + restSeconds;
            double cooling, capacity;
            if (cycleSeconds <= 0 || restSeconds <= 0)
            {
                cooling = heat * 60d / result.intervalTicks;
                capacity = heat;
            }
            else
            {
                cooling = result.burstShots * heat / cycleSeconds;
                capacity = result.burstShots * heat - cooling * activeSeconds;
            }
            cooling *= ext == null ? 1d : Positive(ext.coolingMultiplier);
            capacity *= ext == null ? 1d : Positive(ext.heatCapacityMultiplier);
            result.heatPerShot = FiniteFloat(heat);
            result.coolingPerSecond = FinitePositiveFloat(cooling);
            result.heatCapacity = FinitePositiveFloat(Math.Max(heat, capacity));
            result.targetSustainedRate = (float)(result.burstShots / Math.Max(1d / 60d, cycleSeconds));
        }

        private static void ResolveHeavyCycle(AutoSpaceWeaponSource source, AutoSpaceWeaponProfile result,
            DefModExtension_AutoSpaceWeapon ext)
        {
            result.burstShots = 1;
            double heat = 4d * result.charge;
            double areaCooling = 8d * Math.Sqrt(Math.Max(1, source.footprintArea));
            double sourceChargeThroughput = result.charge * Math.Max(.001, source.SustainedRate);
            double timingMultiplier = ext == null ? 1d
                : Positive(ext.intervalMultiplier) * Positive(ext.restMultiplier);
            double coolingMultiplier = ext == null ? 1d : Positive(ext.coolingMultiplier);
            double cooling = areaCooling * sourceChargeThroughput / SlugChargeThroughput
                * coolingMultiplier / timingMultiplier;
            result.heatPerShot = FiniteFloat(heat);
            result.coolingPerSecond = FinitePositiveFloat(cooling);
            double capacityMultiplier = ext == null ? 1d : Positive(ext.heatCapacityMultiplier);
            result.heatCapacity = FinitePositiveFloat(Math.Max(heat, heat * capacityMultiplier));
            result.intervalTicks = SafePositiveInt(Math.Ceiling(
                result.heatPerShot * 60d / result.coolingPerSecond - 1e-8));
            result.restTicks = result.intervalTicks;
            result.targetSustainedRate = 60f / result.intervalTicks;
        }

        private static void ResolveLaser(AutoSpaceWeaponSource source, AutoSpaceWeaponProfile result,
            DefModExtension_AutoSpaceWeapon ext)
        {
            double damageScale = ext == null ? 1d : Positive(ext.laserDamageMultiplier);
            double energyScale = ext == null ? 1d : Positive(ext.laserEnergyMultiplier);
            result.intervalTicks = SafePositiveInt(Math.Round(12d
                * Math.Pow(.5 / Math.Max(.001, source.SustainedRate), .35)
                * (ext == null ? 1d : Positive(ext.intervalMultiplier))));
            result.burstShots = SafePositiveInt(Math.Ceiling(24d
                * Math.Pow(Math.Max(1, source.burstShotCount), .72)
                * Math.Pow(Math.Max(.02, source.SustainedRate / .5), .1)
                * (ext == null ? 1d : Positive(ext.burstMultiplier))));
            result.restTicks = 0;
            result.laserBaseDamage = FinitePositiveFloat(8d
                * Math.Pow(Math.Max(.001, source.damage / 5d), .85) * damageScale);
            result.laserShotEnergy = FinitePositiveFloat(result.laserBaseDamage / 8d * energyScale);
            double sourceDps = source.damage * source.SustainedRate;
            result.laserMaxPower = SafePositiveInt(Math.Ceiling(4d
                * Math.Pow(Math.Max(.001, sourceDps / BeamRepeaterSustainedDamage), .35)));
            result.targetSustainedRate = 60f / result.intervalTicks;
            result.laserEnergyPerSecond = result.laserShotEnergy * result.targetSustainedRate;
            result.laserMaxEnergyPerSecond = result.laserEnergyPerSecond * result.laserMaxPower;
        }

        private static void Validate(AutoSpaceWeaponSource source)
        {
            if (!(source.damage > 0) || !(source.armorPenetration >= 0) || !(source.projectileSpeed >= 0)
                || source.burstShotCount < 1 || source.ticksBetweenBurstShots < 0
                || source.warmupSeconds < 0 || source.cooldownSeconds < 0 || source.footprintArea < 1)
                throw new ArgumentException($"{source.defName ?? "<unnamed>"}: invalid automatic weapon source numbers");
            if (!source.kinetic && !source.beam)
                throw new ArgumentException($"{source.defName ?? "<unnamed>"}: neither kinetic nor beam");
        }

        private static double Positive(float value) => value > 0 && !float.IsInfinity(value) && !float.IsNaN(value) ? value : 1d;
        private static double LogMap(double value, double x0, double y0, double x1, double y1)
        {
            double t = Math.Log(Math.Max(1e-12, value) / x0) / Math.Log(x1 / x0);
            return y0 * Math.Pow(y1 / y0, t);
        }
        private static double Log2(double value) => Math.Log(Math.Max(1e-12, value), 2d);
        private static int SafeCapacityProduct(int a, int b, ref bool safetyLimited)
        {
            long product = Math.Max(1L, (long)a * b);
            if (product > int.MaxValue) safetyLimited = true;
            return (int)Math.Min(int.MaxValue, product);
        }
        private static int SafePositiveInt(double value) => (int)Math.Min(SafetyIntegerLimit,
            Math.Max(1d, double.IsNaN(value) || double.IsInfinity(value) ? SafetyIntegerLimit : value));
        private static int SafePositiveInt(long value) => (int)Math.Min(SafetyIntegerLimit, Math.Max(1L, value));
        private static int SafeNonNegativeInt(double value) => (int)Math.Min(SafetyIntegerLimit,
            Math.Max(0d, double.IsNaN(value) || double.IsInfinity(value) ? SafetyIntegerLimit : value));
        private static float FiniteFloat(double value) => (float)Math.Min(float.MaxValue,
            Math.Max(0d, double.IsNaN(value) || double.IsInfinity(value) ? float.MaxValue : value));
        private static float FinitePositiveFloat(double value) => Math.Max(float.Epsilon, FiniteFloat(value));
    }
}

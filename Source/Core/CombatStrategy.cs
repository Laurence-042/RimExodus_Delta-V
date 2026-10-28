using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum CombatMetric : byte
    {
        OwnHullFraction, OwnThrustFraction, OwnShieldFraction,
        ReadyWeaponFraction, WeaponCooldownSeconds, WeaponHasAmmo,
        WeaponHasEnergy, WeaponArcErrorDegrees, EnemyDistance,
        EnemyFirepower, IncomingProjectileSeconds
    }

    public enum CombatComparison : byte { Less, LessOrEqual, Greater, GreaterOrEqual }

    public sealed class CombatCriterion : IExposable
    {
        public CombatMetric metric = CombatMetric.EnemyDistance;
        public CombatComparison comparison = CombatComparison.Less;
        public float value = 100f;

        public bool Matches(CombatStrategyContext context)
        {
            float actual = context.Value(metric);
            return comparison switch
            {
                CombatComparison.Less => actual < value,
                CombatComparison.LessOrEqual => actual <= value,
                CombatComparison.Greater => actual > value,
                _ => actual >= value
            };
        }

        public CombatCriterion Copy() => new CombatCriterion
            { metric = metric, comparison = comparison, value = value };

        public void ExposeData()
        {
            Scribe_Values.Look(ref metric, "metric", CombatMetric.EnemyDistance);
            Scribe_Values.Look(ref comparison, "comparison", CombatComparison.Less);
            Scribe_Values.Look(ref value, "value", 100f);
        }
    }

    public sealed class CombatStrategyRule : IExposable
    {
        public string name = "Rule";
        public bool enabled = true;
        public bool matchAny;
        public List<CombatCriterion> conditions = new List<CombatCriterion>();
        // Fire-control weights; maneuver preferences live in maneuverCurves.
        public float outgoingWeight = 1f;
        public float targetThreatWeight = 0.5f;
        public float finishingWeight = 0.5f;
        public CombatManeuverCurves maneuverCurves;

        public bool Matches(CombatStrategyContext context)
        {
            if (!enabled) return false;
            if (conditions == null || conditions.Count == 0) return true;
            return matchAny ? conditions.Any(c => c != null && c.Matches(context))
                : conditions.All(c => c != null && c.Matches(context));
        }

        public CombatStrategyRule Copy() => new CombatStrategyRule
        {
            name = name, enabled = enabled, matchAny = matchAny,
            conditions = conditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            outgoingWeight = outgoingWeight, targetThreatWeight = targetThreatWeight,
            finishingWeight = finishingWeight,
            maneuverCurves = maneuverCurves?.Copy()
        };

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name", "Rule");
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref matchAny, "matchAny");
            Scribe_Collections.Look(ref conditions, "conditions", LookMode.Deep);
            Scribe_Values.Look(ref outgoingWeight, "outgoingWeight", 1f);
            Scribe_Values.Look(ref targetThreatWeight, "targetThreatWeight", 0.5f);
            Scribe_Values.Look(ref finishingWeight, "finishingWeight", 0.5f);
            Scribe_Deep.Look(ref maneuverCurves, "maneuverCurves");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                conditions ??= new List<CombatCriterion>();
        }
    }

    public sealed class CombatStrategy : IExposable
    {
        public const int CurrentFormat = 2;
        public int format = CurrentFormat;
        public string name = "Default";
        public List<CombatStrategyRule> maneuverRules = new List<CombatStrategyRule>();
        public List<CombatStrategyRule> fireRules = new List<CombatStrategyRule>();
        public List<CombatCriterion> npcRetreatConditions = new List<CombatCriterion>();
        public List<CombatCriterion> npcBoardConditions = new List<CombatCriterion>();
        public bool npcRetreatMatchAny;
        public bool npcBoardMatchAny;

        private static readonly CombatStrategyRule DefaultManeuver = new CombatStrategyRule
            { name = "Default" };
        private static readonly CombatStrategyRule DefaultFire = new CombatStrategyRule
            { name = "Default", outgoingWeight = 1f, targetThreatWeight = 0.5f, finishingWeight = 0.5f };

        public CombatStrategyRule Maneuver(CombatStrategyContext context) =>
            maneuverRules?.FirstOrDefault(r => r?.Matches(context) == true) ?? DefaultManeuver;
        public CombatStrategyRule Fire(CombatStrategyContext context) =>
            fireRules?.FirstOrDefault(r => r?.Matches(context) == true) ?? DefaultFire;

        public CombatStrategy Copy() => new CombatStrategy
        {
            format = format, name = name,
            maneuverRules = maneuverRules?.Select(r => r?.Copy()).ToList() ?? new List<CombatStrategyRule>(),
            fireRules = fireRules?.Select(r => r?.Copy()).ToList() ?? new List<CombatStrategyRule>(),
            npcRetreatConditions = npcRetreatConditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            npcBoardConditions = npcBoardConditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            npcRetreatMatchAny = npcRetreatMatchAny, npcBoardMatchAny = npcBoardMatchAny
        };

        public void Validate()
        {
            if (format != CurrentFormat) throw new InvalidDataException("Unsupported strategy format: " + format);
            if (name == null || name.Length > 100) throw new InvalidDataException("Invalid strategy name.");
            ValidateRules(maneuverRules, true); ValidateRules(fireRules, false);
            ValidateConditions(npcRetreatConditions); ValidateConditions(npcBoardConditions);
        }

        private static void ValidateRules(List<CombatStrategyRule> rules, bool maneuver)
        {
            if (rules == null || rules.Count > 64) throw new InvalidDataException("Invalid strategy rule count.");
            foreach (CombatStrategyRule rule in rules)
            {
                if (rule == null || rule.name == null || rule.name.Length > 100
                    || !ValidWeight(rule.outgoingWeight) || !ValidWeight(rule.targetThreatWeight)
                    || !ValidWeight(rule.finishingWeight))
                    throw new InvalidDataException("Invalid strategy rule.");
                ValidateConditions(rule.conditions);
                if (maneuver) rule.maneuverCurves?.Validate();
            }
        }

        private static bool ValidWeight(float value) => !float.IsNaN(value)
            && !float.IsInfinity(value) && value >= 0f && value <= 10f;

        private static void ValidateConditions(List<CombatCriterion> conditions)
        {
            if (conditions == null || conditions.Count > 16)
                throw new InvalidDataException("Invalid condition count.");
            foreach (CombatCriterion condition in conditions)
                if (condition == null || !Enum.IsDefined(typeof(CombatMetric), condition.metric)
                    || !Enum.IsDefined(typeof(CombatComparison), condition.comparison)
                    || float.IsNaN(condition.value) || float.IsInfinity(condition.value)
                    || Mathf.Abs(condition.value) > 100000f)
                    throw new InvalidDataException("Invalid strategy condition.");
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref format, "format", CurrentFormat);
            Scribe_Values.Look(ref name, "name", "Default");
            Scribe_Collections.Look(ref maneuverRules, "maneuverRules", LookMode.Deep);
            Scribe_Collections.Look(ref fireRules, "fireRules", LookMode.Deep);
            Scribe_Collections.Look(ref npcRetreatConditions, "npcRetreatConditions", LookMode.Deep);
            Scribe_Collections.Look(ref npcBoardConditions, "npcBoardConditions", LookMode.Deep);
            Scribe_Values.Look(ref npcRetreatMatchAny, "npcRetreatMatchAny");
            Scribe_Values.Look(ref npcBoardMatchAny, "npcBoardMatchAny");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                maneuverRules ??= new List<CombatStrategyRule>();
                fireRules ??= new List<CombatStrategyRule>();
                npcRetreatConditions ??= new List<CombatCriterion>();
                npcBoardConditions ??= new List<CombatCriterion>();
            }
        }
    }

    public readonly struct CombatStrategyContext
    {
        public readonly float hull, thrust, shield, readyWeapons, cooldown, ammo, energy;
        public readonly float arcError, distance, enemyFirepower, incomingSeconds;
        public CombatStrategyContext(float hull, float thrust, float shield, float readyWeapons,
            float cooldown, float ammo, float energy, float arcError, float distance,
            float enemyFirepower, float incomingSeconds)
        {
            this.hull = hull; this.thrust = thrust; this.shield = shield;
            this.readyWeapons = readyWeapons; this.cooldown = cooldown; this.ammo = ammo;
            this.energy = energy; this.arcError = arcError; this.distance = distance;
            this.enemyFirepower = enemyFirepower; this.incomingSeconds = incomingSeconds;
        }
        public float Value(CombatMetric metric) => metric switch
        {
            CombatMetric.OwnHullFraction => hull,
            CombatMetric.OwnThrustFraction => thrust,
            CombatMetric.OwnShieldFraction => shield,
            CombatMetric.ReadyWeaponFraction => readyWeapons,
            CombatMetric.WeaponCooldownSeconds => cooldown,
            CombatMetric.WeaponHasAmmo => ammo,
            CombatMetric.WeaponHasEnergy => energy,
            CombatMetric.WeaponArcErrorDegrees => arcError,
            CombatMetric.EnemyDistance => distance,
            CombatMetric.EnemyFirepower => enemyFirepower,
            _ => incomingSeconds
        };
    }
}

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
        // IncomingProjectileSeconds retains the serialized enum name; rules read
        // enemy weapon readiness and flight time rather than projectile instances.
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
        public CombatScope scope;
        public CombatWeaponFilter weapons = new CombatWeaponFilter();
        public bool allWeapons;
        public bool hysteresis;
        public float exitValue = 100f;

        public bool Matches(CombatStrategyContext context, bool retaining = false)
        {
            if (context.evaluate != null) return context.evaluate(this, retaining);
            return Compare(context.Value(metric), retaining);
        }

        public bool Compare(float actual, bool retaining = false)
        {
            if (float.IsNaN(actual) || float.IsInfinity(actual)) return false;
            float threshold = retaining && hysteresis ? exitValue : value;
            return comparison switch
            {
                CombatComparison.Less => actual < threshold,
                CombatComparison.LessOrEqual => actual <= threshold,
                CombatComparison.Greater => actual > threshold,
                _ => actual >= threshold
            };
        }

        public CombatCriterion Copy() => new CombatCriterion
            { metric = metric, comparison = comparison, value = value, scope = scope,
                weapons = weapons?.Copy(), allWeapons = allWeapons,
                hysteresis = hysteresis, exitValue = exitValue };

        public void ExposeData()
        {
            Scribe_Values.Look(ref metric, "metric", CombatMetric.EnemyDistance);
            Scribe_Values.Look(ref comparison, "comparison", CombatComparison.Less);
            Scribe_Values.Look(ref value, "value", 100f);
            Scribe_Values.Look(ref scope, "scope");
            Scribe_Deep.Look(ref weapons, "weapons");
            Scribe_Values.Look(ref allWeapons, "allWeapons");
            Scribe_Values.Look(ref hysteresis, "hysteresis");
            Scribe_Values.Look(ref exitValue, "exitValue", 100f);
        }
    }

    public sealed class CombatStrategyRule : IExposable
    {
        public string name = "Rule";
        public bool enabled = true;
        public bool matchAny;
        public List<CombatCriterion> conditions = new List<CombatCriterion>();
        // Rules select a persistent tactic; curves optionally bias its attack pose.
        public float outgoingWeight = 1f;
        public float targetThreatWeight = 0.5f;
        public float finishingWeight = 0.5f;
        public CombatManeuverCurves maneuverCurves;
        public CombatEngagementSettings engagement = new CombatEngagementSettings();
        public CombatTacticSettings tactic = new CombatTacticSettings();
        public CombatTargetOrder targetOrder;
        public bool includeDisarmed;
        public CombatFireAction fireAction;
        public CombatPart part;
        public bool anchorOnly;

        public bool Matches(CombatStrategyContext context, bool retaining = false,
            CombatRuleEvaluation evaluation = null)
        {
            if (!enabled) return false;
            if (conditions == null || conditions.Count == 0) return true;
            foreach (var condition in conditions)
            {
                bool matched = condition != null && condition.Matches(context, retaining);
                evaluation?.conditions.Add(matched);
                if (matchAny && matched) return true;
                if (!matchAny && !matched) return false;
            }
            return !matchAny;
        }

        public CombatStrategyRule Copy() => new CombatStrategyRule
        {
            name = name, enabled = enabled, matchAny = matchAny,
            conditions = conditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            outgoingWeight = outgoingWeight, targetThreatWeight = targetThreatWeight,
            finishingWeight = finishingWeight,
            maneuverCurves = maneuverCurves?.Copy(),
            engagement = engagement?.Copy(),
            tactic = tactic?.Copy() ?? new CombatTacticSettings(),
            targetOrder = targetOrder, includeDisarmed = includeDisarmed,
            fireAction = fireAction, part = part, anchorOnly = anchorOnly
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
            Scribe_Deep.Look(ref engagement, "engagement");
            Scribe_Deep.Look(ref tactic, "tactic");
            Scribe_Values.Look(ref targetOrder, "targetOrder");
            Scribe_Values.Look(ref includeDisarmed, "includeDisarmed");
            Scribe_Values.Look(ref fireAction, "fireAction");
            Scribe_Values.Look(ref part, "part");
            Scribe_Values.Look(ref anchorOnly, "anchorOnly");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                conditions ??= new List<CombatCriterion>();
                tactic ??= new CombatTacticSettings();
            }
        }
    }

    public sealed class CombatStrategy : IExposable
    {
        public const int CurrentFormat = 3;
        public int format = CurrentFormat;
        public string name = "Default";
        public List<CombatStrategyRule> maneuverRules = new List<CombatStrategyRule>();
        public List<CombatStrategyRule> fireRules = new List<CombatStrategyRule>();
        public List<CombatStrategyRule> targetRules = new List<CombatStrategyRule>();
        public List<CombatCriterion> npcRetreatConditions = new List<CombatCriterion>();
        public List<CombatCriterion> npcBoardConditions = new List<CombatCriterion>();
        public bool npcRetreatMatchAny;
        public bool npcBoardMatchAny;

        private readonly CombatStrategyRule DefaultManeuver = new CombatStrategyRule
            { name = "Default", engagement = CombatEngagementSettings.Default() };
        private readonly CombatStrategyRule DefaultFire = new CombatStrategyRule
            { name = "Default", outgoingWeight = 1f, targetThreatWeight = 0.5f, finishingWeight = 0.5f };

        // The editor reads copies of the exact fallback used by selection.
        internal CombatStrategyRule BuiltInManeuver => DefaultManeuver.Copy();
        internal CombatStrategyRule BuiltInFire => DefaultFire.Copy();

        public CombatStrategyRule Maneuver(CombatStrategyContext context, CombatStrategyRule previous = null,
            CombatSelectionTrace trace = null) =>
            Select(maneuverRules, context, previous, trace) ?? DefaultManeuver;
        public CombatStrategyRule Fire(CombatStrategyContext context, CombatStrategyRule previous = null) =>
            Select(fireRules, context, previous) ?? DefaultFire;
        public CombatStrategyRule Target(CombatStrategyContext context, CombatStrategyRule previous = null) =>
            Select(targetRules, context, previous) ?? DefaultFire;
        private static CombatStrategyRule Select(List<CombatStrategyRule> rules,
            CombatStrategyContext context, CombatStrategyRule previous, CombatSelectionTrace trace = null)
        {
            trace?.rules.Clear();
            if (rules != null)
                foreach (var rule in rules)
                {
                    var evaluation = trace != null ? new CombatRuleEvaluation(rule, rule == previous) : null;
                    bool matched = rule?.Matches(context, rule == previous, evaluation) == true;
                    if (evaluation != null) { evaluation.matched = matched; trace.rules.Add(evaluation); }
                    if (matched) return rule;
                }
            return null;
        }

        public CombatStrategy Copy() => new CombatStrategy
        {
            format = format, name = name,
            maneuverRules = maneuverRules?.Select(r => r?.Copy()).ToList() ?? new List<CombatStrategyRule>(),
            fireRules = fireRules?.Select(r => r?.Copy()).ToList() ?? new List<CombatStrategyRule>(),
            targetRules = targetRules?.Select(r => r?.Copy()).ToList() ?? new List<CombatStrategyRule>(),
            npcRetreatConditions = npcRetreatConditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            npcBoardConditions = npcBoardConditions?.Select(c => c?.Copy()).ToList() ?? new List<CombatCriterion>(),
            npcRetreatMatchAny = npcRetreatMatchAny, npcBoardMatchAny = npcBoardMatchAny
        };

        public void Validate()
        {
            if (format != CurrentFormat) throw new InvalidDataException("Unsupported strategy format: " + format);
            if (name == null || name.Length > 100) throw new InvalidDataException("Invalid strategy name.");
            ValidateRules(maneuverRules, true); ValidateRules(fireRules, false);
            ValidateRules(targetRules, false);
            ValidateConditions(npcRetreatConditions); ValidateConditions(npcBoardConditions);
            foreach (var rule in maneuverRules.Concat(targetRules))
                if (rule.conditions.Any(c => c.scope == CombatScope.CurrentWeapon))
                    throw new InvalidDataException("Current weapon scope requires a fire rule.");
            if (npcRetreatConditions.Concat(npcBoardConditions).Any(c => c.scope == CombatScope.CurrentWeapon))
                throw new InvalidDataException("Current weapon scope requires a fire rule.");
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
                if (!Enum.IsDefined(typeof(CombatTargetOrder), rule.targetOrder)
                    || !Enum.IsDefined(typeof(CombatFireAction), rule.fireAction)
                    || !Enum.IsDefined(typeof(CombatPart), rule.part))
                    throw new InvalidDataException("Invalid rule action.");
                if (maneuver)
                {
                    rule.maneuverCurves?.Validate();
                    if (rule.engagement == null) throw new InvalidDataException("Missing engagement settings.");
                    rule.engagement.Validate();
                }
                {
                    if (rule.tactic == null) throw new InvalidDataException("Missing combat tactic settings.");
                    rule.tactic.Validate();
                }
            }
        }

        private static bool ValidWeight(float value) => !float.IsNaN(value)
            && !float.IsInfinity(value) && value >= 0f && value <= 10f;

        private static void ValidateConditions(List<CombatCriterion> conditions)
        {
            if (conditions == null || conditions.Count > 16)
                throw new InvalidDataException("Invalid condition count.");
            foreach (CombatCriterion condition in conditions)
            {
                if (condition == null || !Enum.IsDefined(typeof(CombatMetric), condition.metric)
                    || !Enum.IsDefined(typeof(CombatComparison), condition.comparison)
                    || float.IsNaN(condition.value) || float.IsInfinity(condition.value)
                    || Mathf.Abs(condition.value) > 100000f)
                    throw new InvalidDataException("Invalid strategy condition.");
                if (!Enum.IsDefined(typeof(CombatScope), condition.scope) || condition.weapons == null
                    || float.IsNaN(condition.exitValue) || float.IsInfinity(condition.exitValue)
                    || Mathf.Abs(condition.exitValue) > 100000f)
                    throw new InvalidDataException("Invalid condition scope or exit threshold.");
                bool weaponMetric = condition.metric >= CombatMetric.ReadyWeaponFraction
                    && condition.metric <= CombatMetric.WeaponArcErrorDegrees;
                if ((condition.scope == CombatScope.WeaponGroup || condition.scope == CombatScope.CurrentWeapon) && !weaponMetric
                    || condition.scope == CombatScope.OwnShip && condition.metric >= CombatMetric.EnemyDistance
                    || condition.scope == CombatScope.EnemyShip && weaponMetric)
                    throw new InvalidDataException("Metric does not belong to the selected scope.");
                if (condition.hysteresis && ((condition.comparison <= CombatComparison.LessOrEqual)
                    ? condition.exitValue < condition.value : condition.exitValue > condition.value))
                    throw new InvalidDataException("Exit threshold must widen the matching interval.");
                condition.weapons.Validate();
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref format, "format", CurrentFormat);
            Scribe_Values.Look(ref name, "name", "Default");
            Scribe_Collections.Look(ref maneuverRules, "maneuverRules", LookMode.Deep);
            Scribe_Collections.Look(ref fireRules, "fireRules", LookMode.Deep);
            Scribe_Collections.Look(ref targetRules, "targetRules", LookMode.Deep);
            Scribe_Collections.Look(ref npcRetreatConditions, "npcRetreatConditions", LookMode.Deep);
            Scribe_Collections.Look(ref npcBoardConditions, "npcBoardConditions", LookMode.Deep);
            Scribe_Values.Look(ref npcRetreatMatchAny, "npcRetreatMatchAny");
            Scribe_Values.Look(ref npcBoardMatchAny, "npcBoardMatchAny");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                maneuverRules ??= new List<CombatStrategyRule>();
                fireRules ??= new List<CombatStrategyRule>();
                targetRules ??= new List<CombatStrategyRule>();
                npcRetreatConditions ??= new List<CombatCriterion>();
                npcBoardConditions ??= new List<CombatCriterion>();
            }
        }
    }

    // Observations from the actual selector; never serialized or evaluated from a UI repaint.
    public sealed class CombatSelectionTrace
    {
        public readonly List<CombatRuleEvaluation> rules = new List<CombatRuleEvaluation>();
    }

    public sealed class CombatRuleEvaluation
    {
        public readonly CombatStrategyRule rule;
        public readonly bool retaining;
        public bool matched;
        public readonly List<bool> conditions = new List<bool>();
        public CombatRuleEvaluation(CombatStrategyRule rule, bool retaining)
        { this.rule = rule; this.retaining = retaining; }
    }

    public readonly struct CombatStrategyContext
    {
        public readonly float hull, thrust, shield, readyWeapons, cooldown, ammo, energy;
        public readonly float arcError, distance, enemyFirepower, incomingSeconds;
        internal readonly Func<CombatCriterion, bool, bool> evaluate;
        public CombatStrategyContext(float hull, float thrust, float shield, float readyWeapons,
            float cooldown, float ammo, float energy, float arcError, float distance,
            float enemyFirepower, float incomingSeconds, Func<CombatCriterion, bool, bool> evaluate = null)
        {
            this.hull = hull; this.thrust = thrust; this.shield = shield;
            this.readyWeapons = readyWeapons; this.cooldown = cooldown; this.ammo = ammo;
            this.energy = energy; this.arcError = arcError; this.distance = distance;
            this.enemyFirepower = enemyFirepower; this.incomingSeconds = incomingSeconds;
            this.evaluate = evaluate;
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

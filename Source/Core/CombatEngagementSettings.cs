using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace DeltaV
{
    public enum CombatEngagementMetric : byte
    {
        StructureDamage, GuardDrain, CapabilityLoss, BurstFraction,
        TravelSeconds, VelocitySeconds, ReverseSeconds, Flanking, Uncertainty, CriticalDamage
    }
    public enum CombatEngagementSide : byte { Outgoing, Incoming }

    public sealed class CombatEngagementTerm : IExposable
    {
        public CombatEngagementMetric metric;
        public CombatEngagementSide side;
        public CombatWeaponFilter weapons = new CombatWeaponFilter();
        public float weight = 1;
        public CombatScoreCurve curve = new CombatScoreCurve(true,
            new CombatScorePoint(0, 0), new CombatScorePoint(100, 1));
        public CombatEngagementTerm Copy() => new CombatEngagementTerm
        { metric = metric, side = side, weapons = weapons?.Copy(), weight = weight, curve = curve?.Copy() };
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(CombatEngagementMetric), metric)
                || !Enum.IsDefined(typeof(CombatEngagementSide), side) || weapons == null || curve == null
                || float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0 || weight > 100)
                throw new InvalidDataException("Invalid engagement score term.");
            weapons.Validate(); curve.Validate(0, 100000);
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref metric, "metric"); Scribe_Values.Look(ref side, "side");
            Scribe_Values.Look(ref weight, "weight", 1); Scribe_Deep.Look(ref weapons, "weapons");
            Scribe_Deep.Look(ref curve, "curve");
        }
    }

    public sealed class CombatEngagementSettings : IExposable
    {
        public float windowSeconds = 30, burstSeconds = 3, switchThreshold = 2;
        public List<CombatEngagementTerm> terms = new List<CombatEngagementTerm>();
        public CombatEngagementSettings Copy() => new CombatEngagementSettings
        {
            windowSeconds = windowSeconds, burstSeconds = burstSeconds, switchThreshold = switchThreshold,
            terms = terms?.Select(t => t?.Copy()).ToList()
        };
        public void Validate()
        {
            if (!Finite(windowSeconds, 1, 120) || !Finite(burstSeconds, 0.1f, windowSeconds)
                || !Finite(switchThreshold, 0, 10000) || terms == null || terms.Count > 32)
                throw new InvalidDataException("Invalid engagement window or terms.");
            foreach (var term in terms)
            { if (term == null) throw new InvalidDataException("Missing engagement term."); term.Validate(); }
        }
        private static bool Finite(float value, float min, float max) => !float.IsNaN(value)
            && !float.IsInfinity(value) && value >= min && value <= max;
        internal float Score(CombatEngagementMetric metric, float value)
        {
            float result = 0;
            foreach (var term in terms) if (term.metric == metric && term.weight != 0)
                result += term.weight * term.curve.Evaluate(value);
            return result;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref windowSeconds, "windowSeconds", 30);
            Scribe_Values.Look(ref burstSeconds, "burstSeconds", 3);
            Scribe_Values.Look(ref switchThreshold, "switchThreshold", 2);
            Scribe_Collections.Look(ref terms, "terms", LookMode.Deep);
        }
        internal static CombatEngagementSettings Default()
        {
            var result = new CombatEngagementSettings();
            void Add(CombatEngagementMetric metric, CombatEngagementSide side, float x, float y) => result.terms.Add(
                new CombatEngagementTerm { metric = metric, side = side,
                    curve = new CombatScoreCurve(true, new CombatScorePoint(0, 0), new CombatScorePoint(x, y)) });
            Add(CombatEngagementMetric.StructureDamage, CombatEngagementSide.Outgoing, 100, 3);
            Add(CombatEngagementMetric.StructureDamage, CombatEngagementSide.Incoming, 100, -4);
            Add(CombatEngagementMetric.GuardDrain, CombatEngagementSide.Outgoing, 100, .3f);
            Add(CombatEngagementMetric.CriticalDamage, CombatEngagementSide.Outgoing, 1, 80);
            Add(CombatEngagementMetric.CriticalDamage, CombatEngagementSide.Incoming, 1, -160);
            result.terms.Add(new CombatEngagementTerm { metric = CombatEngagementMetric.BurstFraction,
                side = CombatEngagementSide.Incoming, curve = new CombatScoreCurve(true,
                    new CombatScorePoint(0, 0), new CombatScorePoint(.1f, -15),
                    new CombatScorePoint(.3f, -150), new CombatScorePoint(.6f, -1000)) });
            Add(CombatEngagementMetric.TravelSeconds, CombatEngagementSide.Outgoing, 10, -1.5f);
            Add(CombatEngagementMetric.VelocitySeconds, CombatEngagementSide.Outgoing, 10, -2);
            Add(CombatEngagementMetric.ReverseSeconds, CombatEngagementSide.Outgoing, 10, -5);
            Add(CombatEngagementMetric.Flanking, CombatEngagementSide.Outgoing, 1, 2);
            Add(CombatEngagementMetric.Uncertainty, CombatEngagementSide.Incoming, 100, -4);
            return result;
        }
    }
}

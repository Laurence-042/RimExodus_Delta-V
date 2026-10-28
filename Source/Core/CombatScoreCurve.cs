using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Verse;

namespace DeltaV
{
    public enum ManeuverCurveMetric : byte
    {
        Distance, Outgoing, Incoming, ProjectileSeconds, HullOverlap, AlignmentError
    }

    public sealed class CombatScorePoint : IExposable
    {
        public float x;
        public float y;

        public CombatScorePoint() { }
        public CombatScorePoint(float x, float y) { this.x = x; this.y = y; }
        public CombatScorePoint Copy() => new CombatScorePoint(x, y);

        public void ExposeData()
        {
            Scribe_Values.Look(ref x, "x");
            Scribe_Values.Look(ref y, "y");
        }
    }

    // Point data is shared by Scribe and standalone XML. Verse's CurvePoint has
    // read-only coordinates, so it cannot be the standalone XML contract.
    public sealed class CombatScoreCurve : IExposable
    {
        public List<CombatScorePoint> points = new List<CombatScorePoint>();
        public bool extendRight;
        [XmlIgnore] private SimpleCurve compiled;

        public CombatScoreCurve() { }
        public CombatScoreCurve(bool extendRight, params CombatScorePoint[] points)
        {
            this.extendRight = extendRight;
            this.points = points.ToList();
        }

        public CombatScoreCurve Copy() => new CombatScoreCurve
        {
            extendRight = extendRight,
            points = points?.Select(p => p?.Copy()).ToList()
        };

        public void Invalidate() => compiled = null;

        public SimpleCurve AsSimpleCurve()
        {
            if (compiled == null)
                compiled = new SimpleCurve(points.Select(p => new CurvePoint(p.x, p.y)));
            return compiled;
        }

        public float Evaluate(float x)
        {
            if (extendRight && x > points[points.Count - 1].x)
            {
                CombatScorePoint last = points[points.Count - 1];
                CombatScorePoint before = points[points.Count - 2];
                double result = last.y + (double)(x - last.x) * (last.y - before.y)
                    / (last.x - before.x);
                return (float)Math.Max(-1000000d, Math.Min(1000000d, result));
            }
            return AsSimpleCurve().Evaluate(x);
        }

        public void Validate(float minX, float maxX)
        {
            if (points == null || points.Count < 2 || points.Count > 16)
                throw new InvalidDataException("Invalid maneuver curve point count.");
            float previous = float.NegativeInfinity;
            foreach (CombatScorePoint point in points)
            {
                if (point == null || float.IsNaN(point.x) || float.IsInfinity(point.x)
                    || float.IsNaN(point.y) || float.IsInfinity(point.y)
                    || point.x < minX || point.x > maxX || point.x <= previous
                    || Math.Abs(point.y) > 10000f)
                    throw new InvalidDataException("Invalid maneuver curve point.");
                previous = point.x;
            }
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref points, "points", LookMode.Deep);
            Scribe_Values.Look(ref extendRight, "extendRight");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                points ??= new List<CombatScorePoint>();
                compiled = null;
            }
        }
    }

    public sealed class CombatManeuverCurves : IExposable
    {
        public CombatScoreCurve distance;
        public CombatScoreCurve outgoing;
        public CombatScoreCurve incoming;
        public CombatScoreCurve projectileSeconds;
        public CombatScoreCurve hullOverlap;
        public CombatScoreCurve alignmentError;

        public CombatScoreCurve Get(ManeuverCurveMetric metric) => metric switch
        {
            ManeuverCurveMetric.Distance => distance,
            ManeuverCurveMetric.Outgoing => outgoing,
            ManeuverCurveMetric.Incoming => incoming,
            ManeuverCurveMetric.ProjectileSeconds => projectileSeconds,
            ManeuverCurveMetric.HullOverlap => hullOverlap,
            _ => alignmentError
        };

        public void Set(ManeuverCurveMetric metric, CombatScoreCurve curve)
        {
            switch (metric)
            {
                case ManeuverCurveMetric.Distance: distance = curve; break;
                case ManeuverCurveMetric.Outgoing: outgoing = curve; break;
                case ManeuverCurveMetric.Incoming: incoming = curve; break;
                case ManeuverCurveMetric.ProjectileSeconds: projectileSeconds = curve; break;
                case ManeuverCurveMetric.HullOverlap: hullOverlap = curve; break;
                default: alignmentError = curve; break;
            }
        }

        public CombatManeuverCurves Copy() => new CombatManeuverCurves
        {
            distance = distance?.Copy(), outgoing = outgoing?.Copy(),
            incoming = incoming?.Copy(), projectileSeconds = projectileSeconds?.Copy(),
            hullOverlap = hullOverlap?.Copy(), alignmentError = alignmentError?.Copy()
        };

        public void Validate()
        {
            distance?.Validate(0f, 100000f);
            outgoing?.Validate(0f, 100000f);
            incoming?.Validate(0f, 100000f);
            projectileSeconds?.Validate(0f, 100000f);
            hullOverlap?.Validate(0f, 1f);
            alignmentError?.Validate(0f, 180f);
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref distance, "distance");
            Scribe_Deep.Look(ref outgoing, "outgoing");
            Scribe_Deep.Look(ref incoming, "incoming");
            Scribe_Deep.Look(ref projectileSeconds, "projectileSeconds");
            Scribe_Deep.Look(ref hullOverlap, "hullOverlap");
            Scribe_Deep.Look(ref alignmentError, "alignmentError");
        }
    }
}

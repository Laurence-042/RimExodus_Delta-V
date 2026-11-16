#if MOTHERSHIP_OFFLINE
using System;
using System.Collections.Generic;
using DeltaV;
using UnityEngine;
using Verse;

internal static partial class Program
{
    // A curve describes a resolved impact rule at a given output, independent of its launcher.
    sealed class MothershipDamageSurface
    {
        internal readonly record struct Mechanism(ImpactEffectKind Kind, DamageDef Damage,
            bool Penetrates, bool Kinetic, float DamageAtReference, float ReferenceSpeed, float SpeedExponent,
            float GlancingFactor, float HitChance, float RicochetHp, float RicochetAngle,
            float Blast, float StripRadius, float StripChance, float FoundationThreshold, string DepthPattern)
        {
            internal static Mechanism Resolve(ImpactOutcomePart part, float chance) => new(
                part.kind, part.damageDef, part.penetrates, part.kineticMassScaling, part.damage,
                part.referenceSpeed, part.speedExponent, part.incidenceMinFactor, chance, part.ricochetMinHitPoints,
                part.ricochetMinIncidenceDeg, part.explosionRadius, part.stripSubstructureRadius,
                part.stripSubstructureChance, part.substructureBreakThreshold,
                part.penetrationLayers == null ? "" : System.Text.Json.JsonSerializer.Serialize(
                    part.penetrationLayers, MothershipJson));
        }

        internal readonly record struct Response(int FirstBreach, int Broken, float RemainingWall, float Through, float Damage);

        // Equal absorbing layers and a stream of equal impacts admit a closed form.
        internal static Response Evaluate(bool penetrates, float perHit, int count, float hp, int depth)
        {
            if (count <= 0 || depth <= 0 || perHit <= 0 || hp <= 0)
                return new(0, 0, Mathf.Max(0, hp), hp <= 0 ? perHit * count : 0, 0);
            int first = Mathf.CeilToInt(hp / perHit);
            if (penetrates)
            {
                float total = perHit * count;
                int broken = Mathf.Min(depth, Mathf.FloorToInt(total / hp));
                return new(first, broken, broken == depth ? 0 : hp - (total - broken * hp),
                    Mathf.Max(0, total - depth * hp), Mathf.Min(total, depth * hp));
            }
            int full = Math.Min(depth, count / first);
            int extra = count - full * first;
            float remainder = full == depth ? 0 : hp - extra * perHit;
            return new(first, full, remainder, full == depth ? extra * perHit : 0,
                full * hp + (full == depth ? 0 : extra * perHit));
        }

        internal static (float Damage, float Through) AtAverage(int count, float budget, float penetrating,
            double totalHp, int layers)
        {
            if (count == 0 || budget <= 0) return default;
            if (layers == 0) return (0, budget);
            float hp = (float)(totalHp / layers);
            float perHit = budget / count;
            if (penetrating <= 0 || penetrating >= budget)
            {
                var response = Evaluate(penetrating >= budget, perHit, count, hp, layers);
                return (response.Damage, response.Through);
            }

            // A mixed volley shares one HP budget. Blend the two bounds for
            // its common mean hit rather than spending the wall twice.
            var stopped = Evaluate(false, perHit, count, hp, layers);
            var piercing = Evaluate(true, perHit, count, hp, layers);
            float share = penetrating / budget;
            return (Mathf.Lerp(stopped.Damage, piercing.Damage, share),
                Mathf.Lerp(stopped.Through, piercing.Through, share));
        }

        readonly Dictionary<(Mechanism Rule, float Output), Curve> curves = new();
        readonly bool timed;
        internal MothershipDamageSurface(bool timed = false) => this.timed=timed;
        internal int Count => curves.Count;
        internal int Reused { get; private set; }
        internal long BuildTicks { get; private set; }

        internal Curve Get(Mechanism rule, float output)
        {
            if (curves.TryGetValue((rule, output), out var curve)) { Reused++; return curve; }
            long start=timed?System.Diagnostics.Stopwatch.GetTimestamp():0;
            curve = new Curve(rule.Penetrates, output);
            curves.Add((rule, output), curve);
            if(timed) BuildTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-start;
            return curve;
        }

        internal sealed class Curve
        {
            readonly bool penetrates;
            readonly float output;
            internal Curve(bool penetrates, float output) { this.penetrates=penetrates; this.output=output; }
            internal Response At(int count, float hp, int depth) => Evaluate(penetrates, output, count, hp, depth);
        }
    }
}
#endif

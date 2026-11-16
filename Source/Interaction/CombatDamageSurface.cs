using System;
using UnityEngine;

namespace DeltaV
{
    internal static class CombatDamageSurface
    {
        internal static (float Damage, float Through) AtAverage(int count, float budget, float penetrating,
            double totalHp, int layers)
        {
            if (count <= 0 || budget <= 0) return default;
            if (layers <= 0 || totalHp <= 0) return (0, budget);
            float hp = (float)(totalHp / layers);
            float total = (float)totalHp;
            var piercing = (Damage: Math.Min(budget, total), Through: Math.Max(0, budget - total));
            if (penetrating >= budget) return piercing;

            float perHit = budget / count;
            double first = Math.Max(1, Math.Ceiling(hp / perHit));
            int broken = first > count ? 0 : Math.Min(layers, count / (int)first);
            float extra = (float)(count - broken * first) * perHit;
            var stopped = broken == layers ? (Damage: total, Through: extra)
                : (Damage: Math.Min(total, broken * hp + extra), Through: 0f);
            if (penetrating <= 0) return stopped;

            float share = penetrating / budget;
            return (Mathf.Lerp(stopped.Damage, piercing.Damage, share),
                Mathf.Lerp(stopped.Through, piercing.Through, share));
        }
    }
}

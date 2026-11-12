using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Local shield pulse debits within one shot, including its fragments.
    internal sealed class CombatResourceLedger
    {
        private readonly Dictionary<CompPowerBattery, float> batteries = new Dictionary<CompPowerBattery, float>();
        internal readonly HashSet<Thing> destroyed = new HashSet<Thing>();
        internal void Reset() { batteries.Clear(); destroyed.Clear(); }
        internal float Stored(CompPowerBattery battery) => battery == null || battery.StunnedByEMP
            || destroyed.Contains(battery.parent) ? 0 : batteries.TryGetValue(battery, out float value) ? value : battery.StoredEnergy;
        internal float Draw(CompPowerBattery battery, float amount)
        {
            float before = Stored(battery), paid = Mathf.Min(before, Mathf.Max(0, amount));
            batteries[battery] = before - paid; return paid;
        }
        internal float Grid(PowerNet net)
        {
            float total = 0;
            if (net != null) foreach (var battery in net.batteryComps) total += Stored(battery);
            return total;
        }
        internal float Draw(PowerNet net, float amount)
        {
            float remaining = Mathf.Max(0, amount);
            if (net != null) foreach (var battery in net.batteryComps)
            { if (remaining <= 0) break; remaining -= Draw(battery, remaining); }
            return amount - remaining;
        }
    }
}

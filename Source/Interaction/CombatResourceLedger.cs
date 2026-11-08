using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Shared by every gun and shield in a single candidate evaluation. No Commit here
    // invokes live equipment; the production energy allocator supplies the reservations.
    internal sealed class CombatResourceLedger
    {
        private readonly Dictionary<CompPowerBattery, float> batteries = new Dictionary<CompPowerBattery, float>();
        private readonly Dictionary<CompSpaceWeaponAttachment, float> attachments = new Dictionary<CompSpaceWeaponAttachment, float>();
        private readonly Dictionary<CompRefuelable, float> fuel = new Dictionary<CompRefuelable, float>();
        private readonly HashSet<CompChangeableProjectile> firedShells = new HashSet<CompChangeableProjectile>();
        internal readonly HashSet<Thing> destroyed = new HashSet<Thing>();
        private readonly HashSet<PowerNet> networks = new HashSet<PowerNet>();
        private readonly Dictionary<CompPowerTrader, float> powerOutput = new Dictionary<CompPowerTrader, float>();
        internal void Observe(PowerNet net) { if (net != null) networks.Add(net); }
        internal void SetPower(CompPowerTrader power, float watts)
        { if (power != null) { Observe(power.PowerNet); powerOutput[power] = watts; } }
        private float Gain(PowerNet net)
        {
            float gain = 0;
            if (net.powerComps != null)
                foreach (var comp in net.powerComps)
                    if (comp.PowerOn && !destroyed.Contains(comp.parent))
                        gain += powerOutput.TryGetValue(comp, out float watts)
                            ? watts * CompPower.WattsToWattDaysPerTick : comp.EnergyOutputPerTick;
            return gain;
        }
        internal bool Powered(CompPowerTrader power) => power == null || power.PowerOn
            && (power.PowerNet == null || Gain(power.PowerNet) >= 0 || Grid(power.PowerNet) > .000001f);
        // Forecast current powered generation/load. Switching, refuelling and future
        // destruction of wires are not invented; callers report those as uncertainty.
        internal void AdvanceTick()
        {
            foreach (var net in networks)
            {
                float gain = Gain(net);
                if (gain < 0) DrawContinuous(net, -gain);
                else if (gain > 0)
                {
                    var accepting = new List<CompPowerBattery>();
                    foreach (var battery in net.batteryComps)
                        if (!battery.StunnedByEMP && battery.Props != null && battery.Props.efficiency > 0
                            && !destroyed.Contains(battery.parent) && battery.parent?.IsBrokenDown() != true
                            && Stored(battery) < battery.Props.storedEnergyMax) accepting.Add(battery);
                    while (gain > .0000001f && accepting.Count > 0)
                    {
                        float share = gain / accepting.Count;
                        for (int i = accepting.Count - 1; i >= 0; i--)
                        {
                            var battery = accepting[i]; float before = Stored(battery);
                            float room = (battery.Props.storedEnergyMax - before) / battery.Props.efficiency;
                            float added = Mathf.Min(room, share);
                            batteries[battery] = before + added * battery.Props.efficiency;
                            gain -= added;
                            if (room <= share) accepting.RemoveAt(i);
                        }
                        if (gain <= share * .00001f) break;
                    }
                }
                foreach (var battery in net.batteryComps)
                    Draw(battery, 5f * CompPower.WattsToWattDaysPerTick);
            }
        }
        internal float Stored(CompPowerBattery battery) => battery == null || battery.StunnedByEMP
            || destroyed.Contains(battery.parent) ? 0 : batteries.TryGetValue(battery, out float value) ? value : battery.StoredEnergy;
        internal float Draw(CompPowerBattery battery, float amount)
        {
            float before = Stored(battery), paid = Mathf.Min(before, Mathf.Max(0, amount));
            batteries[battery] = before - paid; return paid;
        }
        internal float Grid(PowerNet net)
        {
            Observe(net);
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
        // PowerNet.ChangeStoredEnergy uses a fixed equal share per pass, unlike the
        // weapon pulse allocator. Keep its bounded redistribution when a bank empties.
        private void DrawContinuous(PowerNet net, float amount)
        {
            int count = 0;
            foreach (var battery in net.batteryComps) if (Stored(battery) > 1e-7f) count++;
            if (count == 0) return;
            float share = amount / count;
            for (int pass = 0; pass < 11 && amount > 1e-7f; pass++)
                foreach (var battery in net.batteryComps)
                {
                    amount -= Draw(battery, share);
                    if (amount < 1e-7f) return;
                }
        }
        internal float Capacitor(CompSpaceWeaponAttachment attachment)
        {
            if (!attachment.Enabled || destroyed.Contains(attachment.parent)) return 0;
            if (attachment.ShotEnergyBattery != null) return Stored(attachment.ShotEnergyBattery);
            return attachments.TryGetValue(attachment, out float value) ? value : attachment.AvailableShotEnergy;
        }
        internal bool CanSupply(CompSpaceWeaponAttachment attachment, float cost) => attachment.Enabled && cost > 0
            && attachment.ShotEnergyBattery?.parent?.IsBrokenDown() != true
            && (attachment.ShotEnergyBattery != null || attachment.CanSupplyShotEnergy(cost))
            && Capacitor(attachment) + .0001f >= cost;
        internal void Draw(CompSpaceWeaponAttachment attachment, float amount)
        {
            if (attachment.ShotEnergyBattery != null) Draw(attachment.ShotEnergyBattery, amount);
            else attachments[attachment] = Mathf.Max(0, Capacitor(attachment) - amount);
        }
        internal float Fuel(CompRefuelable supply) => supply == null || destroyed.Contains(supply.parent) ? 0
            : fuel.TryGetValue(supply, out float amount) ? amount : supply.Fuel;
        internal bool HasHeads(CompSpaceWeaponMagazine magazine, CompSpaceWeapon weapon)
        {
            if (magazine == null || destroyed.Contains(magazine.parent)) return false;
            if (magazine.ChangeableProjectile != null)
                return magazine.HasShot && !firedShells.Contains(magazine.ChangeableProjectile);
            var heads = magazine.Supply(GunSupplyKind.Heads);
            return Fuel(heads ?? magazine.Refuelable) + .0001f >= (heads != null
                ? magazine.RequiredHeads(weapon) : magazine.MagazineProps.ammoPerShot);
        }
        internal bool TakeAmmo(CompSpaceWeapon weapon, CompSpaceWeaponMagazine magazine)
        {
            var own = weapon.Component<CompSpaceWeaponMagazine>();
            if (own != null && (magazine == null || !HasHeads(magazine, weapon))) return false;
            int charge = weapon.Tuning?.Settings.chemical == true ? weapon.Tuning.charge : 0;
            var propellant = own?.Supply(GunSupplyKind.Propellant);
            if (charge > 0 && Fuel(propellant) < charge) return false;
            if (magazine != null)
            {
                if (!HasHeads(magazine, weapon)) return false;
                if (magazine.ChangeableProjectile != null) firedShells.Add(magazine.ChangeableProjectile);
                else
                {
                    var heads = magazine.Supply(GunSupplyKind.Heads);
                    var source = heads ?? magazine.Refuelable;
                    fuel[source] = Fuel(source) - (heads != null ? magazine.RequiredHeads(weapon) : magazine.MagazineProps.ammoPerShot);
                }
            }
            if (charge > 0) fuel[propellant] = Fuel(propellant) - charge;
            return true;
        }
    }
}

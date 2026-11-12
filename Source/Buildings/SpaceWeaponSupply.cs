using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum GunSupplyKind { Heads, Propellant }

    public class CompProperties_GunSupply : CompProperties_Refuelable
    {
        public GunSupplyKind kind;

        public CompProperties_GunSupply()
        {
            compClass = typeof(CompGunSupply);
            fuelConsumptionRate = 0f;
            consumeFuelOnlyWhenUsed = true;
            targetFuelLevelConfigurable = true;
            showAllowAutoRefuelToggle = true;
            hideGizmosIfNotPlayerFaction = true;
            drawOutOfFuelOverlay = false;
            autoRefuelPercent = 0.5f;
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            // Capacity counts components, not shots; the magazine supplies per-shot statistics.
            yield break;
        }

        internal CompProperties_GunSupply WithTranslatedLabels()
        {
            var copy = (CompProperties_GunSupply)MemberwiseClone();
            copy.fuelLabel = copy.fuelGizmoLabel = (kind == GunSupplyKind.Heads
                ? "DeltaV_HeadSupply" : "DeltaV_PropellantSupply").Translate();
            return copy;
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (fuelCapacity < 1f || fuelCapacity != Mathf.Floor(fuelCapacity)
                || fuelConsumptionRate != 0f || initialFuelPercent != 0f || atomicFueling || factorByDifficulty
                || !consumeFuelOnlyWhenUsed || FuelMultiplierCurrentDifficulty != 1f)
                yield return "Gun supplies require empty, integer, one-item-per-unit refuelable storage without passive consumption.";
            int count = 0;
            foreach (CompProperties comp in parentDef.comps)
                if (comp is CompProperties_GunSupply supply && supply.kind == kind) count++;
            if (count != 1) yield return "A gun can only have one refuelable compartment of each supply kind.";
        }
    }

    public class CompGunSupply : CompRefuelable
    {
        public GunSupplyKind Kind => ((CompProperties_GunSupply)props).kind;
        internal bool draggingFuelBar;
        public int Missing => IsFull ? 0 : GetFuelCountToFullyRefuel();

        public override void Initialize(CompProperties props) =>
            base.Initialize(((CompProperties_GunSupply)props).WithTranslatedLabels());

        public bool CanLoad(bool forced) => GunSupplyUtility.CanLoad(this, forced);

        public override void PostExposeData()
        {
            // ThingWithComps does not give each comp its own Scribe node.
            if (!Scribe.EnterNode(Kind == GunSupplyKind.Heads ? "headSupply" : "propellantSupply")) return;
            try { base.PostExposeData(); }
            finally { Scribe.ExitNode(); }
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats() { yield break; }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                if (gizmo is Gizmo_SetFuelLevel)
                    yield return new Gizmo_GunSupply(this);
                else
                {
                    // Native multi-select commands must group heads and propellant separately.
                    if (gizmo is Command command)
                    {
                        command.defaultLabel = Props.FuelGizmoLabel + ": " + command.defaultLabel;
                        command.groupKey = 817430 + (int)Kind;
                    }
                    yield return gizmo;
                }
            }
        }
    }

    public sealed class Gizmo_GunSupply : Gizmo_SetFuelLevel
    {
        private readonly CompGunSupply supply;
        public Gizmo_GunSupply(CompGunSupply supply) : base(supply) { this.supply = supply; }
        // Vanilla uses one static drag flag: two fuel bars on one building would move together.
        protected override bool DraggingBar { get => supply.draggingFuelBar; set => supply.draggingFuelBar = value; }
        protected override int Increments => Mathf.RoundToInt(supply.Props.fuelCapacity);
        protected override float Target
        {
            get => base.Target;
            set => supply.TargetFuelLevel = Mathf.RoundToInt(value * supply.Props.fuelCapacity);
        }
    }

    public partial class CompSpaceWeaponMagazine
    {
        public CompGunSupply Supply(GunSupplyKind kind)
        {
            foreach (ThingComp comp in CompSpaceWeapon.ForComponent(this)?.Mount.Components ?? parent.AllComps)
                if (comp is CompGunSupply supply && supply.Kind == kind) return supply;
            return null;
        }
        public CompGunSupply SupplyFor(ThingDef def)
        {
            foreach (ThingComp comp in CompSpaceWeapon.ForComponent(this)?.Mount.Components ?? parent.AllComps)
                if (comp is CompGunSupply supply && supply.Props.fuelFilter.Allows(def)) return supply;
            return null;
        }
        public int HeadCount => Mathf.FloorToInt(Supply(GunSupplyKind.Heads)?.Fuel ?? 0f);
        public int PropellantCount => Mathf.FloorToInt(Supply(GunSupplyKind.Propellant)?.Fuel ?? 0f);
        public int RequiredHeads(CompSpaceWeapon weapon) => weapon?.Tuning?.mass
            ?? Mathf.CeilToInt(weapon?.Component<CompSpaceWeaponMagazine>()?.MagazineProps.ammoPerShot ?? MagazineProps.ammoPerShot);
        public bool HasHeads(int count) => Supply(GunSupplyKind.Heads) != null ? HeadCount >= count : HasShot;
        public bool HasPropellant(int count) => count <= 0 || PropellantCount >= count;
        internal void NotifySupplyChanged() => (CompSpaceWeapon.ForComponent(this)
            ?? parent.TryGetComp<CompSpaceWeaponAttachment>()?.HostWeapon)?.NotifyParametersChanged();
        public void ConsumeHeads(int count)
        {
            CompGunSupply supply = Supply(GunSupplyKind.Heads);
            if (supply == null) { ConsumeShot(); return; }
            supply.ConsumeFuel(count);
            ShipControlService.NotifyGunnerChange(parent.Map);
        }
        public void ConsumePropellant(int count) => Supply(GunSupplyKind.Propellant)?.ConsumeFuel(count);
    }
}

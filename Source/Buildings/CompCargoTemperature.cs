using System;
using System.Collections.Generic;
using AdaptiveStorage;
using AdaptiveStorage.HarmonyPatches;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class DefModExtension_CargoAttachments : DefModExtension
    {
        public List<SpaceWeaponAttachmentSlot> slots = new List<SpaceWeaponAttachmentSlot>();
        public List<ThingDef> allowed = new List<ThingDef>();
    }

    public partial class Building_SpaceCargoContainer
    {
        public DefModExtension_CargoAttachments AttachmentProps => def.GetModExtension<DefModExtension_CargoAttachments>();
        public SpaceWeaponAttachmentSlot AttachmentSlotAt(IntVec3 cell)
        {
            foreach (var slot in AttachmentProps.slots)
                if (SpaceWeaponGeometry.RotatedCell(this, slot.cell) == cell) return slot;
            return null;
        }

        public IEnumerable<ThingWithComps> AttachedThings()
        {
            if (!Spawned) yield break;
            foreach (var slot in AttachmentProps.slots)
                foreach (Thing thing in SpaceWeaponGeometry.RotatedCell(this, slot.cell).GetThingList(Map))
                    if (thing is ThingWithComps attached && thing != this && !thing.Destroyed
                        && AttachmentProps.allowed.Contains(thing.def)
                        && attached.TryGetComp<CompSpaceWeaponAttachment>() is { } attachment
                        && attachment.HostCargo == this && attachment.CompatibleWith(def)) yield return attached;
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            SpaceWeaponAttachmentSlots.DrawSelection(this, AttachmentProps.slots, AttachedThings());
        }
    }

    public class CompProperties_CargoTemperature : CompProperties_TempControl
    {
        public float coolingWatts = 200f, heatingWatts = 175f, temperatureOffset = 100f;
        public CompProperties_CargoTemperature() => compClass = typeof(CompCargoTemperature);
    }

    // Keep the vanilla target-temperature controls and ASF's temperature transform.
    public class CompCargoTemperature : CompTempControl
    {
        private static readonly Func<float, Extension.TemperatureProperties, float> ApplyTemperature =
            AccessTools.MethodDelegate<Func<float, Extension.TemperatureProperties, float>>(
                AccessTools.DeclaredMethod(typeof(AdjustThingTemperature), "GetNewTemperature"));
        private readonly Extension.TemperatureProperties temperature = new Extension.TemperatureProperties();
        private CompProperties_CargoTemperature Settings => (CompProperties_CargoTemperature)props;
        private CompSpaceWeaponAttachment Attachment => parent.TryGetComp<CompSpaceWeaponAttachment>();
        public override float TargetTemperature
        {
            get => base.TargetTemperature;
            set { base.TargetTemperature = value; RefreshPower(); }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            RefreshPower();
        }

        public override void CompTick() => RefreshPower();

        public void RefreshPower()
        {
            if (PowerTrader == null) return;
            var cargo = Attachment?.HostCargo;
            float watts = 0f;
            if (Attachment?.Enabled == true && cargo != null)
            {
                float outdoor = cargo.Map.mapTemperature.OutdoorTemp;
                watts = outdoor > TargetTemperature ? Settings.coolingWatts
                    : outdoor < TargetTemperature ? Settings.heatingWatts : 0f;
            }
            operatingAtHighPower = watts > 0f;
            PowerTrader.PowerOutput = -watts;
        }

        public float CargoTemperature(float outdoor)
        {
            if (Attachment?.Enabled != true || PowerTrader?.PowerOn != true
                || parent.IsBrokenDown()) return outdoor;
            temperature.coolingOffset = outdoor > TargetTemperature ? Settings.temperatureOffset : 0f;
            temperature.heatingOffset = outdoor < TargetTemperature ? Settings.temperatureOffset : 0f;
            temperature.coolingMin = temperature.heatingMax = TargetTemperature;
            return ApplyTemperature(outdoor, temperature);
        }
    }
}

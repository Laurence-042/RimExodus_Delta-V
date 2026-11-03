using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_SpaceCargo : CompProperties
    {
        public Vector2 capacityBarOffset = new Vector2(0f, -1.26f);
        public Vector2 capacityBarSize = new Vector2(1.02f, .18f);
        public CompProperties_SpaceCargo() => compClass = typeof(CompSpaceCargo);
    }

    // Shared cargo-specific controls only. Inventories, hauling and opening belong
    // to ASF ThingClass and vanilla Building_Crate respectively.
    public partial class CompSpaceCargo : ThingComp
    {
        private bool initializeOnFirstSpawn;
        private StorageSettings savedStorageSettings;
        private int savedSlotLimit = 48;
        private string savedLabel;
        private ISpaceCargoContainer Cargo => (ISpaceCargoContainer)parent;
        public bool StorageMode => parent is Building_SpaceCargoContainer;
        public int SavedSlotLimit => savedSlotLimit;
        public float StoredMass => Cargo.Contents.Sum(t => t.GetStatValue(StatDefOf.Mass) * t.stackCount);
        public float CargoTemperature
        {
            get
            {
                float outdoor = parent.MapHeld.mapTemperature.OutdoorTemp;
                foreach (ThingWithComps attachment in AttachedThings())
                    if (attachment.TryGetComp<CompCargoTemperature>() is { } control)
                        return control.CargoTemperature(outdoor);
                return outdoor;
            }
        }

        public override void PostPostMake() => initializeOnFirstSpawn = true;
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            if (!initializeOnFirstSpawn) return;
            initializeOnFirstSpawn = false;
            if (parent.Faction == Faction.OfPlayer) parent.GetComp<CompHackable>()?.HackNow();
        }

        public override void PostExposeData()
        {
            // ASF already saves its active settings. Only the sealed form needs
            // to retain the settings that will be restored on unsealing.
            if (StorageMode) return;
            Scribe_Deep.Look(ref savedStorageSettings, "cargoStorageSettings");
            Scribe_Values.Look(ref savedSlotLimit, "cargoSlotLimit", 48);
            Scribe_Values.Look(ref savedLabel, "cargoStorageLabel");
        }

        public void NotifyInventoryChanged(Thing item)
        {
            if (parent.Spawned) ShipPhysics.InvalidateMass(parent.Map);
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == Building_Crate.CrateContentsChanged) NotifyInventoryChanged(null);
        }

        public static bool IsLooseCargo(Thing thing)
            => thing != null && !thing.Destroyed && !thing.Spawned && thing.holdingOwner == null
                && thing.def.category == ThingCategory.Item && thing.stackCount > 0
                && thing.stackCount <= thing.def.stackLimit;

        internal CompSpaceCargo SetStorageMode(bool enabled)
        {
            if (!parent.Spawned || !Cargo.IsUnlocked || enabled == StorageMode) return this;
            ThingWithComps previous = parent;
            Map map = previous.Map;
            IntVec3 position = previous.Position;
            Rot4 rotation = previous.Rotation;
            Thing[] goods = Cargo.Contents.ToArray();
            var replacement = (ThingWithComps)ThingMaker.MakeThing(
                DefDatabase<ThingDef>.GetNamed(enabled ? "DeltaV_CargoStorage" : "DeltaV_CargoContainer"),
                previous.Stuff);
            var next = replacement.GetComp<CompSpaceCargo>();
            next.initializeOnFirstSpawn = false;
            next.savedStorageSettings = new StorageSettings();
            next.savedStorageSettings.CopyFrom(previous is Building_SpaceCargoContainer storage
                ? storage.GetStoreSettings() : savedStorageSettings ?? previous.def.building.defaultStorageSettings);
            next.savedSlotLimit = Cargo.CurrentSlotLimit;
            next.savedLabel = previous is Building_SpaceCargoContainer named ? named.label : savedLabel;
            replacement.SetFactionDirect(previous.Faction);
            ((Building)replacement).ChangePaint(((Building)previous).PaintColorDef);
            replacement.HitPoints = previous.HitPoints;
            replacement.SetForbidden(previous.IsForbidden(Faction.OfPlayer), false);
            bool selected = Find.Selector.IsSelected(previous);
            bool placementFailed = false;

            // Hold each original stack before despawning either building. Neither
            // transition invokes Destroy, so installed modules are not destroyed.
            foreach (Thing item in goods)
            {
                if (item.Spawned) item.DeSpawn();
                else item.holdingOwner.Remove(item);
            }
            previous.DeSpawn();
            try
            {
                GenSpawn.Spawn(replacement, position, map, rotation, WipeMode.Vanish);
                replacement.GetComp<CompHackable>()?.HackNow(); // empty crate: no cargo is ejected
                replacement.SetFactionDirect(previous.Faction);
                if (replacement is Building_SpaceCargoContainer asf)
                {
                    asf.settings.CopyFrom(next.savedStorageSettings);
                    asf.label = next.savedLabel;
                }
                // ASF allows lowering its slot limit below the existing count.
                // Restore that limit only after transferring the existing stacks.
                int limit = next.savedSlotLimit;
                next.savedSlotLimit = Math.Max(limit, goods.Length);
                foreach (Thing item in goods)
                    if (!((ISpaceCargoContainer)replacement).TryStore(item))
                    {
                        placementFailed = true;
                        throw new InvalidOperationException("Cargo mode transfer could not place an existing stack.");
                    }
                next.savedSlotLimit = limit;
                if (replacement is Building_SpaceCargoContainer resized) resized.CurrentSlotLimit = limit;
            }
            catch
            {
                foreach (Thing item in goods)
                {
                    if (item.Spawned) item.DeSpawn();
                    else item.holdingOwner?.Remove(item);
                }
                if (replacement.Spawned) replacement.DeSpawn();
                GenSpawn.Spawn(previous, position, map, rotation, WipeMode.Vanish, respawningAfterLoad: true);
                int limit = Cargo.CurrentSlotLimit;
                if (previous is Building_SpaceCargoContainer asf) asf.CurrentSlotLimit = asf.TotalSlots;
                savedSlotLimit = Math.Max(limit, goods.Length);
                foreach (Thing item in goods)
                    if (!Cargo.TryStore(item))
                        GenPlace.TryPlaceThing(item, position, map, ThingPlaceMode.Near);
                savedSlotLimit = limit;
                if (previous is Building_SpaceCargoContainer restored) restored.CurrentSlotLimit = limit;
                replacement.Destroy(DestroyMode.Vanish);
                if (selected) Find.Selector.Select(previous);
                if (placementFailed)
                {
                    Messages.Message("DeltaV_CargoModeNoRoom".Translate(), MessageTypeDefOf.RejectInput, false);
                    return this;
                }
                throw;
            }
            foreach (Building_SpaceTradeTerminal terminal in map.listerThings.AllThings.OfType<Building_SpaceTradeTerminal>())
                terminal.ReplaceCargoBinding(Cargo, (ISpaceCargoContainer)replacement);
            previous.Destroy(DestroyMode.Vanish);
            if (selected) Find.Selector.Select(replacement);
            ShipPhysics.InvalidateMass(map);
            return next;
        }

        private void LockForDebug()
        {
            if (!DebugSettings.godMode || !parent.Spawned) return;
            CompSpaceCargo sealedCargo = StorageMode ? SetStorageMode(false) : this;
            CompHackable hack = sealedCargo.parent.GetComp<CompHackable>();
            if (hack == null) return;
            // Native hackable has no relock API. This reset is exclusively a dev action.
            AccessTools.Field(typeof(CompHackable), "hacked").SetValue(hack, false);
            AccessTools.Field(typeof(CompHackable), "progress").SetValue(hack, 0f);
            AccessTools.Field(typeof(CompHackable), "progressLastLockout").SetValue(hack, 0f);
            AccessTools.Field(typeof(CompHackable), "lockedOutUntilTick").SetValue(hack, -1);
            AccessTools.Field(typeof(CompHackable), "lockedOutPermanently").SetValue(hack, false);
            AccessTools.Field(typeof(CompHackable), "autohack").SetValue(hack, false);
            hack.defence = hack.Props.defence;
            sealedCargo.parent.DirtyMapMesh(sealedCargo.parent.Map);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in SpaceWeaponAttachmentSlots.BuildGizmos(parent,
                AttachmentProps.slots, AttachmentProps.allowed)) yield return gizmo;
            if (parent.Spawned && Cargo.IsUnlocked && (parent.Faction == Faction.OfPlayer || DebugSettings.godMode))
                yield return new Command_Action
                {
                    defaultLabel = (StorageMode ? "DeltaV_CargoModeStorage" : "DeltaV_CargoModeSealed").Translate(),
                    defaultDesc = "DeltaV_CargoStorageModeDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get(StorageMode ? "UI/Commands/CargoStorage" : "UI/Commands/CargoSealed"),
                    action = () => SetStorageMode(!StorageMode)
                };
            if (!parent.Spawned || !DebugSettings.godMode) yield break;
            if (Cargo.IsUnlocked)
                yield return new Command_Action { defaultLabel = "DeltaV_DevCargoLock".Translate(), action = LockForDebug };
            yield return new Command_Action { defaultLabel = "DeltaV_DevCargoAddSample".Translate(), action = AddDebugSample };
            yield return new Command_Action { defaultLabel = "DeltaV_DevCargoInspect".Translate(), action = InspectDebugContents };
            if (parent is Building_Crate crate)
                yield return new Command_Action {
                    defaultLabel = "DeltaV_DevCargoOpen".Translate(),
                    action = () => { if (!Cargo.IsUnlocked) parent.GetComp<CompHackable>().HackNow(); else crate.Open(); } };
            yield return new Command_Action {
                defaultLabel = "DeltaV_DevCargoClear".Translate(),
                action = () => { foreach (Thing thing in Cargo.Contents) thing.Destroy(); NotifyInventoryChanged(null); } };
            yield return new Command_Action {
                defaultLabel = "DeltaV_DevCargoDestroy".Translate(), action = () => parent.Destroy(DestroyMode.KillFinalize) };
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            float outdoor = parent.Map.mapTemperature.OutdoorTemp;
            return "DeltaV_CargoCapacity".Translate(Cargo.Contents.Count, Cargo.CurrentSlotLimit)
                + "\n" + "DeltaV_CargoTemperature".Translate(outdoor.ToString("F1"), CargoTemperature.ToString("F1"));
        }

        private void AddDebugSample()
        {
            AddDebugThing(ThingDefOf.Steel, 75);
            AddDebugThing(ThingDefOf.Silver, 75);
            AddDebugThing(DefDatabase<ThingDef>.GetNamedSilentFail("Gun_AssaultRifle"), 1, QualityCategory.Excellent, true);
            AddDebugThing(DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_FlakVest"), 1, QualityCategory.Good, false);
        }

        private void AddDebugThing(ThingDef thingDef, int count, QualityCategory? quality = null, bool damaged = false)
        {
            if (thingDef == null) return;
            Thing thing = ThingMaker.MakeThing(thingDef);
            thing.stackCount = Mathf.Min(count, thingDef.stackLimit);
            if (quality.HasValue) thing.TryGetComp<CompQuality>()?.SetQuality(quality.Value, ArtGenerationContext.Colony);
            if (damaged) thing.HitPoints = Mathf.Max(1, thing.MaxHitPoints / 2);
            if (!Cargo.TryStore(thing)) thing.Destroy(DestroyMode.Vanish);
        }

        private void InspectDebugContents()
        {
            var report = new StringBuilder("DeltaV_CargoCapacity".Translate(Cargo.Contents.Count, Cargo.CurrentSlotLimit));
            foreach (Thing thing in Cargo.Contents.ToArray().OrderBy(t => t.def.defName).Take(40))
                report.Append('\n').Append(thing.LabelCap).Append(" × ").Append(thing.stackCount)
                    .Append(" (HP ").Append(thing.HitPoints).Append('/').Append(thing.MaxHitPoints).Append(')');
            if (Cargo.Contents.Count > 40) report.Append('\n').Append("DeltaV_DevMoreCargo".Translate());
            Find.WindowStack.Add(new Dialog_MessageBox(report.ToString()));
        }


        internal void ApplyDestructionLoss(DestroyMode mode)
        {
            if (!parent.Spawned || (mode != DestroyMode.KillFinalize && mode != DestroyMode.KillFinalizeLeavingsOnly)) return;
            foreach (Thing thing in Cargo.Contents)
            {
                int keep = thing.stackCount == 1 ? (Rand.Chance(0.25f) ? 1 : 0)
                    : Mathf.FloorToInt(thing.stackCount * 0.25f);
                if (keep <= 0) thing.Destroy();
                else if (keep < thing.stackCount) thing.SplitOff(thing.stackCount - keep).Destroy();
            }
        }
    }
}

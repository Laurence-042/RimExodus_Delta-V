using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdaptiveStorage;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    // ASF owns the map-spawned inventory. Hacking unlocks access without moving it.
    public partial class Building_SpaceCargoContainer : ThingClass, IHackable, IHaulDestination
    {
        private bool storageMode;
        private StoragePriority activePriority = StoragePriority.Preferred;

        public bool IsUnlocked => GetComp<CompHackable>()?.IsHacked == true;
        public ThingCollection Contents => StoredThings;
        public bool StorageMode => storageMode;
        bool IHaulDestination.HaulDestinationEnabled => IsUnlocked && storageMode;
        public override bool AllowItemForbiddingAccess => IsUnlocked && storageMode;
        public float StoredMass => Contents.ToArray().Sum(t => t.GetStatValue(StatDefOf.Mass) * t.stackCount);
        public float CargoTemperature
        {
            get
            {
                float outdoor = Map.mapTemperature.OutdoorTemp;
                foreach (ThingWithComps attachment in AttachedThings())
                    if (attachment.TryGetComp<CompCargoTemperature>() is { } control)
                        return control.CargoTemperature(outdoor);
                return outdoor;
            }
        }

        protected override void OnSpawn(Map map, SpawnMode spawnMode)
        {
            base.OnSpawn(map, spawnMode);
            ReceivedThing -= OnCargoChanged;
            LostThing -= OnCargoChanged;
            ItemStackChanged -= OnCargoChanged;
            ReceivedThing += OnCargoChanged;
            LostThing += OnCargoChanged;
            ItemStackChanged += OnCargoChanged;
            ApplyStorageMode();
        }

        private void OnCargoChanged(Thing item)
        {
            if (Spawned) ShipPhysics.InvalidateMass(Map);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref storageMode, "spaceCargoStorageMode", false);
            Scribe_Values.Look(ref activePriority, "spaceCargoActivePriority", StoragePriority.Preferred);
        }

        private void ApplyStorageMode()
        {
            if (!Spawned) return;
            bool usable = IsUnlocked && storageMode;
            settings.Priority = usable ? activePriority : StoragePriority.Unstored;
            foreach (Thing item in Contents) item.SetForbidden(!usable, false);
            Map.listerHaulables.RecalcAllInCells(AllSlotCellsList());
        }

        private void SetStorageMode(bool enabled)
        {
            if (!IsUnlocked || storageMode == enabled) return;
            if (storageMode && settings.Priority != StoragePriority.Unstored)
                activePriority = settings.Priority;
            storageMode = enabled;
            ApplyStorageMode();
        }

        // The Def permits eight physical stacks in each of six cells.
        public bool HasRoomFor(Thing thing)
        {
            if (!Spawned || thing == null || thing.Destroyed || thing.def.category != ThingCategory.Item
                || !AnyFreeSlots) return false;
            return AllSlotCellsList().Any(cell => cell.GetThingList(Map)
                .Count(t => t.def.category == ThingCategory.Item) < GetMaxItemsForCell(cell));
        }

        public bool TryStore(Thing thing)
        {
            if (thing == null || thing.Spawned || thing.holdingOwner != null || !HasRoomFor(thing))
                return false;
            foreach (IntVec3 cell in AllSlotCellsList())
            {
                if (cell.GetThingList(Map).Count(t => t.def.category == ThingCategory.Item)
                    >= GetMaxItemsForCell(cell)) continue;
                Thing placed = GenSpawn.Spawn(thing, cell, Map);
                placed.SetForbidden(!IsUnlocked || !storageMode, false);
                ShipPhysics.InvalidateMass(Map);
                return true;
            }
            return false;
        }

        public Thing Take(Thing thing, int count)
        {
            if (thing == null || count <= 0 || !Contents.Contains(thing)) return null;
            Thing result = thing.SplitOff(count);
            ShipPhysics.InvalidateMass(Map);
            return result;
        }

        public void OnLockedOut(Pawn pawn = null) { }
        public void OnHacked(Pawn pawn = null)
        {
            SetFaction(pawn?.Faction ?? Faction.OfPlayer);
            ApplyStorageMode();
            if (Renderer != null)
            {
                Renderer.BuildingGraphicsDirty = true;
                Renderer.TryDirtyParentMapMesh();
            }
        }

        public void OpenContents()
        {
            if (!Spawned || !IsUnlocked) return;
            foreach (Thing thing in Contents.ToArray()) Eject(thing, forbid: false);
            ShipPhysics.InvalidateMass(Map);
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn pawn)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(pawn)) yield return option;
            if (!pawn.IsColonistPlayerControlled || !pawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
                yield break;
            if (IsUnlocked)
            {
                if (Contents.Count > 0)
                    yield return new FloatMenuOption("DeltaV_CargoOpen".Translate(), () =>
                        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_OpenCargo, this), JobTag.Misc));
            }
            else if (GetComp<CompHackable>() is { } hack && hack.CanHackNow(pawn).Accepted)
                yield return new FloatMenuOption("DeltaV_CargoHack".Translate(), () =>
                    pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Hack, this), JobTag.Misc));
        }

        public override IEnumerable<InspectTabBase> GetInspectTabs()
            => IsUnlocked ? base.GetInspectTabs() : Enumerable.Empty<InspectTabBase>();

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            foreach (Gizmo gizmo in SpaceWeaponAttachmentSlots.BuildGizmos(this,
                AttachmentProps.slots, AttachmentProps.allowed)) yield return gizmo;
            if (Spawned && IsUnlocked && (Faction == Faction.OfPlayer || DebugSettings.godMode))
                yield return new Command_Toggle
                {
                    defaultLabel = "DeltaV_CargoStorageMode".Translate(),
                    defaultDesc = "DeltaV_CargoStorageModeDesc".Translate(),
                    isActive = () => StorageMode,
                    toggleAction = () => SetStorageMode(!StorageMode)
                };
            if (!Spawned || !DebugSettings.godMode) yield break;
            yield return new Command_Action { defaultLabel = "DeltaV_DevCargoAddSample".Translate(), action = AddDebugSample };
            yield return new Command_Action { defaultLabel = "DeltaV_DevCargoInspect".Translate(), action = InspectDebugContents };
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_DevCargoOpen".Translate(),
                action = () =>
                {
                    CompHackable hack = GetComp<CompHackable>();
                    if (hack != null && !hack.IsHacked)
                        hack.HackNow();
                    else OpenContents();
                }
            };
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_DevCargoClear".Translate(),
                action = () => { foreach (Thing thing in Contents.ToArray()) thing.Destroy(); ShipPhysics.InvalidateMass(Map); }
            };
            yield return new Command_Action { defaultLabel = "DeltaV_DevCargoDestroy".Translate(), action = () => Destroy(DestroyMode.KillFinalize) };
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (!Spawned) return text;
            float outdoor = Map.mapTemperature.OutdoorTemp;
            return text + "\n" + "DeltaV_CargoCapacity".Translate(Contents.Count, CurrentSlotLimit)
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
            if (!TryStore(thing)) thing.Destroy(DestroyMode.Vanish);
        }

        private void InspectDebugContents()
        {
            var report = new StringBuilder("DeltaV_CargoCapacity".Translate(Contents.Count, CurrentSlotLimit));
            foreach (Thing thing in Contents.ToArray().OrderBy(t => t.def.defName).Take(40))
                report.Append('\n').Append(thing.LabelCap).Append(" × ").Append(thing.stackCount)
                    .Append(" (HP ").Append(thing.HitPoints).Append('/').Append(thing.MaxHitPoints).Append(')');
            if (Contents.Count > 40) report.Append('\n').Append("DeltaV_DevMoreCargo".Translate());
            Find.WindowStack.Add(new Dialog_MessageBox(report.ToString()));
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            bool killed = map != null && (mode == DestroyMode.KillFinalize
                || mode == DestroyMode.KillFinalizeLeavingsOnly);
            bool deconstructed = map != null && (mode == DestroyMode.Deconstruct
                || mode == DestroyMode.WillReplace);
            if (killed || deconstructed)
                foreach (Thing thing in Contents.ToArray())
                {
                    int keep = deconstructed ? thing.stackCount : thing.stackCount == 1
                        ? (Rand.Chance(0.25f) ? 1 : 0)
                        : Mathf.FloorToInt(thing.stackCount * 0.25f);
                    if (keep <= 0) thing.Destroy();
                    else
                    {
                        if (keep < thing.stackCount) thing.SplitOff(thing.stackCount - keep).Destroy();
                        Eject(thing, forbid: false);
                    }
                }
            base.Destroy(mode);
            if (map != null && !map.Disposed) ShipPhysics.InvalidateMass(map);
        }
    }

    public class JobDriver_OpenSpaceCargo : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Do(() =>
            {
                if (TargetThingA is Building_SpaceCargoContainer cargo && cargo.Spawned) cargo.OpenContents();
            });
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public class CompProperties_MiningCargoAttachment : CompProperties_SpaceWeaponAttachment
    {
        public CompProperties_MiningCargoAttachment()
        {
            compClass = typeof(CompMiningCargoAttachment);
        }
    }

    public class CompMiningCargoAttachment : CompSpaceWeaponAttachment
    {
        internal Building_MiningCargoAttachment Cargo => parent as Building_MiningCargoAttachment;
    }

    /// <summary>原版储物架语义；容量补丁只适配与炮座共格的非 edifice 插件。</summary>
    public class Building_MiningCargoAttachment : Building_Storage
    {
        internal bool TryStoreMiningOutput(Thing item)
        {
            if (!Spawned || !CompSpaceCargo.IsLooseCargo(item) || !Accepts(item)) return false;
            GenPlace.TryPlaceThing(item, Position, Map, ThingPlaceMode.Direct);
            return item.Destroyed || item.Spawned;
        }
    }

    public class CompProperties_MiningTargetIdentifier : CompProperties_SpaceWeaponAttachment
    {
        public CompProperties_MiningTargetIdentifier()
        {
            compClass = typeof(CompMiningTargetIdentifier);
        }
    }

    public class CompMiningTargetIdentifier : CompSpaceWeaponAttachment, IStoreSettingsParent
    {
        private StorageSettings targetSettings;

        public bool StorageTabVisible => true;
        public StorageSettings GetParentStoreSettings() => StorageSettings.EverStorableFixedSettings();
        public StorageSettings GetStoreSettings()
        {
            if (targetSettings == null)
            {
                targetSettings = new StorageSettings(this);
                targetSettings.filter.SetAllowAll(GetParentStoreSettings().filter);
            }
            return targetSettings;
        }

        internal virtual bool Allows(Thing thing) => thing?.def.category == ThingCategory.Item
            && GetStoreSettings().AllowedToAccept(thing);

        public void Notify_SettingsChanged() { }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) yield return gizmo;
            if (parent.Faction != Faction.OfPlayer) yield break;
            yield return new Command_BatchSetting<CompMiningTargetIdentifier>("MiningTargetFilter", this,
                targets => Find.WindowStack.Add(new Dialog_MiningTargetFilter(targets)))
            {
                defaultLabel = "DeltaV_MiningTargetFilter".Translate(),
                defaultDesc = "DeltaV_MiningTargetFilterDesc".Translate(),
                icon = parent.def.uiIcon
            };
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) GetStoreSettings();
            Scribe_Deep.Look(ref targetSettings, "miningTargetFilter", this);
        }
    }

    internal sealed class MiningTargetFilterSelection : IStoreSettingsParent
    {
        private readonly CompMiningTargetIdentifier[] targets;
        private readonly StorageSettings settings;

        internal MiningTargetFilterSelection(CompMiningTargetIdentifier[] targets)
        {
            this.targets = targets;
            settings = new StorageSettings();
            settings.filter.CopyAllowancesFrom(targets[0].GetStoreSettings().filter);
            settings.owner = this; // Opening the shared window must not synchronize the selection.
        }

        public bool StorageTabVisible => true;
        public StorageSettings GetStoreSettings() => settings;
        public StorageSettings GetParentStoreSettings() => StorageSettings.EverStorableFixedSettings();
        public void Notify_SettingsChanged()
        {
            foreach (CompMiningTargetIdentifier target in targets)
                if (target.parent.Spawned && !target.parent.Destroyed
                    && target.parent.Faction == Faction.OfPlayer)
                    target.GetStoreSettings().filter.CopyAllowancesFrom(settings.filter);
        }
    }

    internal sealed class Dialog_MiningTargetFilter : Window
    {
        private readonly MiningTargetFilterSelection selection;
        private readonly ThingFilterUI.UIState filterState = new ThingFilterUI.UIState();

        public override Vector2 InitialSize => new Vector2(700f, 700f);

        internal Dialog_MiningTargetFilter(CompMiningTargetIdentifier[] targets)
        {
            selection = new MiningTargetFilterSelection(targets);
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        internal StorageSettings Settings => selection.GetStoreSettings();

        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width - 35f, 32f),
                "DeltaV_MiningTargetFilterTitle".Translate());
            ThingFilterUI.DoThingFilterConfigWindow(new Rect(0f, 40f, rect.width, rect.height - 40f),
                filterState, Settings.filter, selection.GetParentStoreSettings().filter, 8);
        }
    }

    internal static class MiningBeamOutputRouter
    {
        internal static void Deliver(UniverseManager universe, SpaceCollision.ImpactEvent impact,
            Thing receiver, Thing item, bool cutReturn)
        {
            if (receiver == null)
            {
                Eject(universe, impact, item, cutReturn);
                return;
            }

            CompSpaceWeapon weapon = impact.weapon ?? receiver?.TryGetComp<CompSpaceWeapon>();
            ThingWithComps[] attachments = weapon?.AttachedThings().ToArray()
                ?? System.Array.Empty<ThingWithComps>();
            CompMiningTargetIdentifier[] identifiers = attachments
                .Select(attachment => attachment.TryGetComp<CompMiningTargetIdentifier>())
                .Where(identifier => identifier?.Enabled == true).ToArray();
            if (!AcceptedBy(identifiers, item))
            {
                Eject(universe, impact, item, cutReturn);
                return;
            }

            foreach (Building_MiningCargoAttachment cargo in attachments
                         .Select((attachment, slot) => new
                         {
                             slot,
                             comp = attachment.TryGetComp<CompMiningCargoAttachment>()
                         })
                         .Where(entry => entry.comp?.Enabled == true && entry.comp.Cargo?.Spawned == true)
                         .OrderByDescending(entry => entry.comp.Cargo.GetStoreSettings().Priority)
                         .ThenBy(entry => entry.slot)
                         .Select(entry => entry.comp.Cargo))
            {
                cargo.TryStoreMiningOutput(item);
                if (item.Destroyed || item.Spawned) return;
            }

            if (!item.Destroyed && !item.Spawned && receiver?.Spawned == true)
                GenPlace.TryPlaceThing(item, receiver.Position, receiver.Map, ThingPlaceMode.Near,
                    extraValidator: cell => cell.GetSlotGroup(receiver.Map)?.parent
                        is not Building_MiningCargoAttachment);
        }

        internal static bool AcceptedBy(IReadOnlyCollection<CompMiningTargetIdentifier> identifiers,
            Thing item) => identifiers.Count == 0 || identifiers.Any(identifier => identifier.Allows(item));

        private static void Eject(UniverseManager universe, SpaceCollision.ImpactEvent impact,
            Thing item, bool cutReturn)
        {
            while (item.stackCount > item.def.stackLimit)
                universe.Scatter.QueueMiningEjection(item.SplitOff(item.def.stackLimit), impact.defender,
                    impact.hitDefenderLocal, -impact.incidentDir, cutReturn);
            universe.Scatter.QueueMiningEjection(item, impact.defender,
                impact.hitDefenderLocal, -impact.incidentDir, cutReturn);
        }
    }
}

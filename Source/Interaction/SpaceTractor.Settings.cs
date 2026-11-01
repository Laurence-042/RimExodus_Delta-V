using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public partial class CompSpaceTractor
    {
        private StorageSettings cargoSettings;
        internal bool collectLiving = true;
        // Rows: hostile, neutral, ally, own. Columns: humanlike, animal, mechanoid.
        internal const int DefaultPawnMask = 0xFF8;
        internal int pawnMask = DefaultPawnMask;
        public bool StorageTabVisible => true;
        public StorageSettings GetParentStoreSettings() => StorageSettings.EverStorableFixedSettings();
        public StorageSettings GetStoreSettings()
        {
            if (cargoSettings == null)
            {
                cargoSettings = new StorageSettings(this);
                cargoSettings.filter.SetAllowAll(GetParentStoreSettings().filter);
            }
            return cargoSettings;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) GetStoreSettings();
            Scribe_Deep.Look(ref cargoSettings, "tractorCargo", this);
            Scribe_Values.Look(ref collectLiving, "tractorLiving", true);
            Scribe_Values.Look(ref pawnMask, "tractorPawnMask", DefaultPawnMask);
        }

        internal static int PawnKindIndex(Pawn pawn) => pawn.RaceProps.IsMechanoid ? 2
            : pawn.RaceProps.Humanlike ? 0 : pawn.RaceProps.Animal ? 1 : -1;
        internal static int PawnRelationIndex(Pawn pawn, Faction receiver)
        {
            if (receiver != null && pawn.Faction == receiver) return 3;
            if (receiver != null && pawn.Faction?.HostileTo(receiver) == true) return 0;
            return receiver != null && pawn.Faction?.RelationKindWith(receiver) == FactionRelationKind.Ally ? 2 : 1;
        }
        internal bool Allows(Thing thing)
        {
            if (thing is Pawn pawn)
            {
                if (!collectLiving || pawn.Dead) return false;
                int kind = PawnKindIndex(pawn);
                return kind >= 0 && (pawnMask & (1 << (PawnRelationIndex(pawn, parent.Faction) * 3 + kind))) != 0;
            }
            return thing.def.category == ThingCategory.Item && GetStoreSettings().AllowedToAccept(thing);
        }

        public void Notify_SettingsChanged()
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            universe?.Scatter.ReleaseAutomatic(parent);
            var weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon != null) universe?.Controls.ForgetWeaponTarget(weapon);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer) yield break;
            yield return new Command_BatchToggle<CompSpaceTractor>("TractorLiving", this,
                c => c.collectLiving, (c, value) => { c.collectLiving = value; c.Notify_SettingsChanged(); })
            {
                defaultLabel = "DeltaV_TractorLiving".Translate(),
                defaultDesc = "DeltaV_TractorLivingDesc".Translate(),
                icon = TexCommand.ForbidOff
            };
            yield return new Command_BatchSetting<CompSpaceTractor>("TractorPawnFilter", this,
                targets => Find.WindowStack.Add(new Dialog_TractorPawnFilter(targets)))
            {
                defaultLabel = "DeltaV_TractorPawnFilter".Translate(),
                defaultDesc = "DeltaV_TractorPawnFilterDesc".Translate(),
                icon = TexCommand.ForbidOff
            };
        }
    }

    public class ITab_TractorCargo : ITab_Storage
    {
        private IStoreSettingsParent editing;
        public ITab_TractorCargo() { labelKey = "DeltaV_TractorCargoTab"; }
        protected override bool IsPrioritySettingVisible => false;
        internal static CompSpaceTractor[] SelectedTractors(IEnumerable<object> selection) => selection
            .OfType<Thing>().Select(t => t.TryGetComp<CompSpaceTractor>())
            .Where(c => c?.parent.Spawned == true && !c.parent.Destroyed && c.parent.Faction == Faction.OfPlayer)
            .Distinct().ToArray();
        public override bool IsVisible => SelectedTractors(AllSelObjects).Length > 0;
        protected override IStoreSettingsParent SelStoreSettingsParent =>
            editing ?? SelectedTractors(AllSelObjects).FirstOrDefault();
        protected override void FillTab()
        {
            CompSpaceTractor[] targets = SelectedTractors(AllSelObjects);
            if (targets.Length == 0) return;
            // The adapter lives only during this draw: shared ITabs must not retain selected game objects.
            editing = targets.Length == 1 ? (IStoreSettingsParent)targets[0] : new TractorCargoSelection(targets);
            try
            {
                base.FillTab();
                if (targets.Length > 1)
                {
                    var hint = new Rect(10f, 10f, 280f, 20f);
                    Widgets.Label(hint, "DeltaV_TractorBatchCargo".Translate(targets.Length));
                    TooltipHandler.TipRegion(hint, "DeltaV_TractorBatchCargoTip".Translate());
                }
            }
            finally { editing = null; }
        }
    }

    internal sealed class TractorCargoSelection : IStoreSettingsParent
    {
        private readonly CompSpaceTractor[] targets;
        private readonly StorageSettings settings;
        internal TractorCargoSelection(CompSpaceTractor[] targets)
        {
            this.targets = targets;
            settings = new StorageSettings();
            settings.filter.CopyAllowancesFrom(targets[0].GetStoreSettings().filter);
            settings.owner = this; // Reading/opening the tab must not synchronize the selection.
        }
        public bool StorageTabVisible => true;
        public StorageSettings GetStoreSettings() => settings;
        public StorageSettings GetParentStoreSettings() => StorageSettings.EverStorableFixedSettings();
        public void Notify_SettingsChanged()
        {
            foreach (CompSpaceTractor target in targets)
                if (target.parent.Spawned && !target.parent.Destroyed && target.parent.Faction == Faction.OfPlayer)
                    target.GetStoreSettings().filter.CopyAllowancesFrom(settings.filter);
        }
    }

    internal sealed class Dialog_TractorPawnFilter : Window
    {
        private readonly CompSpaceTractor[] targets;
        public override Vector2 InitialSize => new Vector2(560f, 320f);
        internal Dialog_TractorPawnFilter(CompSpaceTractor[] targets)
        { this.targets = targets; doCloseX = true; closeOnClickedOutside = true; absorbInputAroundWindow = true; }
        internal MultiCheckboxState State(int bit)
        {
            var active = targets.Where(t => t.parent.Spawned && !t.parent.Destroyed && t.parent.Faction == Faction.OfPlayer).ToArray();
            int enabled = active.Count(t => (t.pawnMask & bit) != 0);
            return enabled == 0 ? MultiCheckboxState.Off : enabled == active.Length
                ? MultiCheckboxState.On : MultiCheckboxState.Partial;
        }
        internal void SetAllowed(int bit, bool allowed)
        {
            foreach (var target in targets)
            {
                if (!target.parent.Spawned || target.parent.Destroyed || target.parent.Faction != Faction.OfPlayer) continue;
                target.pawnMask = allowed ? target.pawnMask | bit : target.pawnMask & ~bit;
                target.Notify_SettingsChanged();
            }
        }
        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0, 0, rect.width - 25, 32), "DeltaV_TractorPawnFilter".Translate());
            for (int kind = 0; kind < 3; kind++)
                Widgets.Label(new Rect(140 + kind * 120, 40, 120, 30), ("DeltaV_TractorKind" + kind).Translate());
            for (int relation = 0; relation < 4; relation++)
            {
                float y = 80 + relation * 40;
                Widgets.Label(new Rect(0, y, 135, 30), ("DeltaV_TractorRelation" + relation).Translate());
                for (int kind = 0; kind < 3; kind++)
                {
                    int bit = 1 << (relation * 3 + kind);
                    MultiCheckboxState previous = State(bit);
                    MultiCheckboxState next = Widgets.CheckboxMulti(new Rect(160 + kind * 120, y, 24, 24), previous);
                    if (next != previous) SetAllowed(bit, next == MultiCheckboxState.On);
                }
            }
        }
    }
}

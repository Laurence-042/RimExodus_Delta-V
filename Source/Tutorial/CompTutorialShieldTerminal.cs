using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum TutorialShieldPreset { Unshielded, Kinetic, Deflection, Reflection, GuardSingle, Guard }

    public sealed class CompUsable_TutorialShieldTerminal : CompUsable
    {
        protected override string FloatMenuOptionLabel(Pawn pawn) => "DeltaV_Range_UseTerminal".Translate(parent.Label);
    }

    public sealed class CompProperties_TutorialShieldTerminal : CompProperties_UseEffect
    {
        public TutorialShieldPreset preset;
        public CompProperties_TutorialShieldTerminal() { compClass = typeof(CompTutorialShieldTerminal); }
    }

    // The native UseItem job owns walking, reservation, interaction time and interruption.
    // Stable object/Thing IDs allow the target map to archive and restore without stale references.
    public sealed class CompTutorialShieldTerminal : CompUseEffect
    {
        internal int targetObjectId, primaryShieldId;
        private CompProperties_TutorialShieldTerminal Settings => (CompProperties_TutorialShieldTerminal)props;
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        internal List<CompSpaceShield> TargetShields()
        {
            Map map = Universe?.ObjectById(targetObjectId)?.map;
            if (map == null || map.Disposed) return new List<CompSpaceShield>();
            Thing primary = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == primaryShieldId);
            ShipBase ship = primary == null ? ShipBase.Invalid : ShipRegistry.ShipOf(primary, requireWholeFootprint: true);
            if (!ship.IsValid) return new List<CompSpaceShield>();
            return map.listerThings.AllThings.Select(t => t.TryGetComp<CompSpaceShield>())
                .Where(s => s != null && ShipRegistry.ShipOf(s.parent, requireWholeFootprint: true).Identity == ship.Identity)
                .OrderBy(s => s.parent.thingIDNumber == primaryShieldId ? 0 : 1).ToList();
        }

        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            if (p?.Faction != Faction.OfPlayer || !PublicFacility.PlayerMayUse(parent))
                return "DeltaV_Range_NoAccess".Translate();
            if (TargetShields().Count == 0) return "DeltaV_Range_TargetUnavailable".Translate();
            return true;
        }

        public override void DoEffect(Pawn usedBy)
        {
            if (!CanBeUsedBy(usedBy).Accepted || usedBy.Map != parent.Map
                || usedBy.Position != parent.InteractionCell) return;
            List<CompSpaceShield> shields = TargetShields();
            ApplyPreset(shields, Settings.preset);
            var expectedMap = parent.Map;
            Universe.QueueFrameAction(() =>
            {
                if (parent.Spawned && parent.Map == expectedMap)
                    Find.WindowStack.Add(new Dialog_MessageBox(("DeltaV_Range_Lesson_" + Settings.preset).Translate(),
                        title: "DeltaV_Range_Title".Translate()));
            });
        }

        internal static void ApplyPreset(IReadOnlyList<CompSpaceShield> shields, TutorialShieldPreset preset)
        {
            for (int i = 0; i < shields.Count; i++)
            {
                CompSpaceShield shield = shields[i];
                bool on = preset != TutorialShieldPreset.Unshielded && (i == 0 || preset == TutorialShieldPreset.Guard);
                shield.parent.TryGetComp<CompFlickable>().SwitchIsOn = on;
                shield.mode = preset == TutorialShieldPreset.Kinetic ? SpaceShieldMode.Kinetic
                    : preset == TutorialShieldPreset.Deflection || preset == TutorialShieldPreset.Reflection
                        ? SpaceShieldMode.Electromagnetic : SpaceShieldMode.Guard;
                shield.kineticStrengthPercent = 100;
                shield.electromagneticStrengthPercent = preset == TutorialShieldPreset.Reflection ? 100 : 50;
                shield.TryRejoinRegular();
                shield.SettingsChanged();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref targetObjectId, "rangeTargetObjectId");
            Scribe_Values.Look(ref primaryShieldId, "rangePrimaryShieldId");
        }
    }
}

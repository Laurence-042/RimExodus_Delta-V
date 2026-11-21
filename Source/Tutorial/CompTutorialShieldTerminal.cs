using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public enum TutorialShieldPreset { Unshielded, Kinetic, Electromagnetic, GuardSingle, Guard }

    public sealed class CompUsable_TutorialShieldTerminal : CompUsable
    {
        protected override string FloatMenuOptionLabel(Pawn pawn) => "DeltaV_Range_UseTerminal".Translate(parent.Label);
    }

    public sealed class CompProperties_TutorialShieldTerminal : CompProperties_UseEffect
    {
        public TutorialShieldPreset preset;
        public int strengthPercent;
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
            Thing primary = Universe.ShipStructure.For(map).Find(primaryShieldId);
            ShipBase ship = primary == null ? ShipBase.Invalid : ShipRegistry.ShipOf(primary, requireWholeFootprint: true);
            if (!ship.IsValid) return new List<CompSpaceShield>();
            return ship.StructuralState.Shields
                .Where(s => ShipRegistry.ShipOf(s.parent, requireWholeFootprint: true).Identity == ship.Identity)
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
            ApplyPreset(shields, Settings.preset, Settings.strengthPercent);
            var expectedMap = parent.Map;
            Universe.QueueFrameAction(() =>
            {
                if (parent.Spawned && parent.Map == expectedMap)
                    Find.WindowStack.Add(new Dialog_MessageBox(("DeltaV_Range_Lesson_" + Settings.preset).Translate(Settings.strengthPercent),
                        title: "DeltaV_Range_Title".Translate()));
            });
        }

        internal static void ApplyPreset(IReadOnlyList<CompSpaceShield> shields, TutorialShieldPreset preset, int strengthPercent = 0)
        {
            bool line = preset == TutorialShieldPreset.Kinetic || preset == TutorialShieldPreset.Electromagnetic;
            for (int i = 0; i < shields.Count; i++)
            {
                CompSpaceShield shield = shields[i];
                bool on = preset != TutorialShieldPreset.Unshielded && (i == 0 || line || preset == TutorialShieldPreset.Guard);
                shield.parent.TryGetComp<CompFlickable>().SwitchIsOn = on;
                shield.mode = preset == TutorialShieldPreset.Kinetic ? SpaceShieldMode.Kinetic
                    : preset == TutorialShieldPreset.Electromagnetic
                        ? SpaceShieldMode.Electromagnetic : SpaceShieldMode.Guard;
                shield.kineticStrengthPercent = strengthPercent;
                shield.electromagneticStrengthPercent = strengthPercent;
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

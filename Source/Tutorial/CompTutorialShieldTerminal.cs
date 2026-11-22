using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
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
        public float angleDeg;
        public int strengthPercent;
        public CompProperties_TutorialShieldTerminal() { compClass = typeof(CompTutorialShieldTerminal); }
    }

    // The native UseItem job owns walking, reservation, interaction time and interruption.
    // Stable object/Thing IDs allow the target map to archive and restore without stale references.
    public sealed class CompTutorialShieldTerminal : CompUseEffect
    {
        internal int targetObjectId, shieldThingId;
        private CompProperties_TutorialShieldTerminal Settings => (CompProperties_TutorialShieldTerminal)props;
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();
        internal TutorialShieldPreset Preset => Settings.preset;
        internal bool UsesAngledShield => Settings.angleDeg != 0f
            && (Preset == TutorialShieldPreset.Kinetic || Preset == TutorialShieldPreset.Electromagnetic);

        private IReadOnlyList<CompSpaceShield> TargetShields()
        {
            Map map = Universe?.ObjectById(targetObjectId)?.map;
            if (map == null || map.Disposed) return Array.Empty<CompSpaceShield>();
            var primary = Universe.ShipStructure.For(map).Find(shieldThingId)?.TryGetComp<CompSpaceShield>();
            return primary?.OwnedShields?.OrderByDescending(shield => shield == primary).ToArray()
                ?? Array.Empty<CompSpaceShield>();
        }

        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            if (p?.Faction != Faction.OfPlayer || parent?.Spawned != true || !PlayerAccess.CanOperate(parent))
                return "DeltaV_Range_NoAccess".Translate();
            if (TargetShields().Count == 0) return "DeltaV_Range_TargetUnavailable".Translate();
            return true;
        }

        public override void DoEffect(Pawn usedBy)
        {
            if (!CanBeUsedBy(usedBy).Accepted || usedBy.Map != parent.Map
                || usedBy.Position != parent.InteractionCell) return;
            ApplyTo(TargetShields());
            var lesson = ("DeltaV_Range_Lesson_" + Preset).Translate(Mathf.Abs(Settings.angleDeg), Settings.strengthPercent);
            var expectedMap = parent.Map;
            Universe.QueueFrameAction(() =>
            {
                if (parent.Spawned && parent.Map == expectedMap)
                    Find.WindowStack.Add(new Dialog_MessageBox(lesson, title: "DeltaV_Range_Title".Translate()));
            });
        }

        internal void ApplyTo(IReadOnlyList<CompSpaceShield> shields)
        {
            int strength = Mathf.Clamp(Settings.strengthPercent, 0, 100);
            for (int i = 0; i < shields.Count; i++)
            {
                var shield = shields[i];
                shield.mode = i == 0 ? Preset switch
                {
                    TutorialShieldPreset.Kinetic => SpaceShieldMode.Kinetic,
                    TutorialShieldPreset.Electromagnetic => SpaceShieldMode.Electromagnetic,
                    _ => SpaceShieldMode.Guard,
                } : SpaceShieldMode.Guard;
                if (shield.mode == SpaceShieldMode.Kinetic || shield.mode == SpaceShieldMode.Electromagnetic)
                {
                    shield.angleDeg = Mathf.Clamp(Settings.angleDeg, -90f, 90f);
                    if (shield.mode == SpaceShieldMode.Kinetic) shield.kineticStrengthPercent = strength;
                    else shield.electromagneticStrengthPercent = strength;
                }
                bool enabled = Preset != TutorialShieldPreset.Unshielded && (i == 0 || Preset == TutorialShieldPreset.Guard);
                shield.parent.TryGetComp<CompFlickable>().SwitchIsOn = enabled;
                shield.SettingsChanged();
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref targetObjectId, "rangeTargetObjectId");
            Scribe_Values.Look(ref shieldThingId, "rangeShieldThingId");
        }
    }
}

using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_WeaponCoverage : Window
    {
        private readonly CompSpaceWeapon[] targets;
        private CompSpaceWeapon owner => targets[0];
        private readonly CombatWeaponCoverage draft;
        private string range, arc, direction;
        public override Vector2 InitialSize => new Vector2(520f, 330f);
        internal Dialog_WeaponCoverage(CompSpaceWeapon[] targets)
        { this.targets = targets; draft = owner.coverage.Copy(); forcePause = true; doCloseX = true; }
        internal void Apply()
        {
            foreach (CompSpaceWeapon target in targets)
            {
                if (!target.parent.Spawned || target.parent.Destroyed
                    || (target.parent.Faction != RimWorld.Faction.OfPlayer && !DebugSettings.godMode)) continue;
                target.coverage = draft.Copy();
                target.coverage.range = Mathf.Clamp(draft.range, 0f, target.WeaponProps.range);
                target.coverage.direction = Mathf.Clamp(draft.direction, -target.HardwareArc * 0.5f, target.HardwareArc * 0.5f);
                target.Universe?.Weapons.NotifyWeaponModeChanged(target);
            }
            WeaponArcOverlay.ClearRuntimeCaches();
        }
        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0f, 0f, rect.width, 55f), "DeltaV_WeaponCoverageHelp".Translate());
            Widgets.Label(new Rect(0f, 65f, 240f, 28f), "DeltaV_WeaponCoverageRange".Translate());
            Widgets.TextFieldNumeric(new Rect(250f, 65f, 120f, 28f), ref draft.range, ref range, 0f, owner.WeaponProps.range);
            Widgets.Label(new Rect(0f, 100f, 240f, 28f), "DeltaV_WeaponCoverageArc".Translate());
            Widgets.TextFieldNumeric(new Rect(250f, 100f, 120f, 28f), ref draft.arc, ref arc, 0f, 360f);
            Widgets.Label(new Rect(0f, 135f, 240f, 28f), "DeltaV_WeaponCoverageDirection".Translate());
            Widgets.TextFieldNumeric(new Rect(250f, 135f, 120f, 28f), ref draft.direction, ref direction, -owner.HardwareArc * 0.5f, owner.HardwareArc * 0.5f);
            Widgets.CheckboxLabeled(new Rect(0f, 175f, rect.width, 28f), "DeltaV_WeaponCoverageFixed".Translate(), ref draft.fixedDirection);
            if (Widgets.ButtonText(new Rect(0f, rect.height - 40f, 120f, 32f), "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(rect.width - 120f, rect.height - 40f, 120f, 32f), "OK".Translate()))
            {
                if (float.IsNaN(draft.range) || float.IsNaN(draft.arc) || float.IsNaN(draft.direction)
                    || float.IsInfinity(draft.range) || float.IsInfinity(draft.arc) || float.IsInfinity(draft.direction)) return;
                Apply(); Close();
            }
        }
    }
}

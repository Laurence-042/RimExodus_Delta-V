using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_PointDefenseTargets : Window
    {
        private readonly CompSpaceWeapon[] weapons;
        private bool projectiles, shuttles, personnel;

        internal Dialog_PointDefenseTargets(CompSpaceWeapon source, CompSpaceWeapon[] weapons)
        {
            this.weapons = weapons;
            projectiles = (source.pdTargets & SpaceObjectTag.Projectile) != 0;
            shuttles = (source.pdTargets & SpaceObjectTag.Shuttle) != 0;
            personnel = (source.pdTargets & SpaceObjectTag.Personnel) != 0;
            doCloseX = true;
        }

        public override Vector2 InitialSize => new Vector2(370, 236);

        public override void DoWindowContents(Rect rect)
        {
            Widgets.Label(new Rect(0, 0, rect.width, 28), "DeltaV_PointDefenseTargets".Translate());
            Widgets.CheckboxLabeled(new Rect(0, 38, rect.width, 28), "DeltaV_PointDefenseProjectiles".Translate(), ref projectiles);
            Widgets.CheckboxLabeled(new Rect(0, 69, rect.width, 28), "DeltaV_PointDefenseShuttles".Translate(), ref shuttles);
            Widgets.CheckboxLabeled(new Rect(0, 100, rect.width, 28), "DeltaV_PointDefensePersonnel".Translate(), ref personnel);
            if (!Widgets.ButtonText(new Rect(0, rect.height - 35, rect.width, 35), "Confirm".Translate())) return;
            var tags = (projectiles ? SpaceObjectTag.Projectile : 0) | (shuttles ? SpaceObjectTag.Shuttle : 0)
                | (personnel ? SpaceObjectTag.Personnel : 0);
            foreach (CompSpaceWeapon weapon in weapons)
            {
                if (!weapon.parent.Spawned || weapon.aimMode != SpaceWeaponAimMode.PointDefense || weapon.pdTargets == tags) continue;
                weapon.pdTargets = tags;
                weapon.Universe?.Controls.RefreshPointDefenseFilter(weapon);
            }
            Close();
        }
    }
}

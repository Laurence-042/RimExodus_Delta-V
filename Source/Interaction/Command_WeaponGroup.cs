using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>One menu for the weapons collected by vanilla gizmo grouping.</summary>
    internal sealed class Command_WeaponGroup : Command
    {
        private readonly List<CompSpaceWeapon> weapons = new List<CompSpaceWeapon>();

        internal Command_WeaponGroup(CompSpaceWeapon weapon)
        {
            weapons.Add(weapon);
            defaultLabel = "DeltaV_WeaponGroupGizmo".Translate(
                ("DeltaV_WeaponSet" + weapon.salvoSet).Translate(), weapon.groupNumber.ToString());
            defaultDesc = "DeltaV_WeaponGroupGizmoTip".Translate();
            icon = TexCommand.Attack;
        }

        public override bool GroupsWith(Gizmo other) => other is Command_WeaponGroup;

        public override bool InheritInteractionsFrom(Gizmo other)
        {
            foreach (CompSpaceWeapon weapon in ((Command_WeaponGroup)other).weapons)
                if (!weapons.Contains(weapon)) weapons.Add(weapon);
            return false;
        }

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            Find.WindowStack.Add(new FloatMenu(BuildOptions(), "DeltaV_WeaponGroupMenuTitle".Translate()));
        }

        internal List<FloatMenuOption> BuildOptions()
        {
            // Keep the clicked selection even if the live selection changes while the menu is open.
            CompSpaceWeapon[] targets = weapons.ToArray();
            var options = new List<FloatMenuOption>();
            foreach (SpaceWeaponSalvoSet set in new[] { SpaceWeaponSalvoSet.Main, SpaceWeaponSalvoSet.Secondary })
            for (int group = 1; group <= 6; group++)
            {
                SpaceWeaponSalvoSet capturedSet = set;
                int capturedGroup = group;
                bool current = targets.All(w => w.salvoSet == set && w.groupNumber == group);
                options.Add(new FloatMenuOption(
                    "DeltaV_WeaponGroupEntry".Translate(
                        ("DeltaV_WeaponSet" + set).Translate(), group.ToString()) + (current ? " ✓" : string.Empty),
                    delegate
                    {
                        foreach (CompSpaceWeapon weapon in targets)
                        {
                            ThingWithComps parent = weapon.parent;
                            if (!parent.Spawned || parent.Destroyed
                                || parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) continue;
                            weapon.salvoSet = capturedSet;
                            weapon.groupNumber = capturedGroup;
                        }
                    }));
            }
            return options;
        }
    }
}

using System;
using System.IO;
using Verse;

namespace DeltaV
{
    public enum CombatScope : byte { Metric, OwnShip, EnemyShip, WeaponGroup, CurrentWeapon }
    public enum CombatWeaponSet : byte { All, Main, Secondary }
    public enum CombatTargetOrder : byte { Balanced, Nearest, Threat, Finish, MainWeapons }
    public enum CombatFireAction : byte { Fire, Hold }
    public enum CombatPart : byte { Automatic, Center, Weapons, Propulsion, Shield, Power, Console }
    public enum CombatBearing : byte { Free, Enemy, World }

    public sealed class CombatWeaponFilter : IExposable
    {
        public CombatWeaponSet set;
        public int group; // Zero includes all six groups in the selected set.
        public string weaponDef = "";
        public bool Includes(CompSpaceWeapon weapon) => weapon != null
            && weapon.SpaceModeEnabled && weapon.GroupActive
            && weapon.aimMode == SpaceWeaponAimMode.Volley && !weapon.WeaponProps.manualOnly
            && (set == CombatWeaponSet.All || (set == CombatWeaponSet.Main)
                == (weapon.salvoSet == SpaceWeaponSalvoSet.Main))
            && (group == 0 || weapon.groupNumber == group)
            && (string.IsNullOrEmpty(weaponDef) || weapon.parent.def.defName == weaponDef);
        public CombatWeaponFilter Copy() => (CombatWeaponFilter)MemberwiseClone();
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(CombatWeaponSet), set) || group < 0 || group > 6
                || weaponDef == null || weaponDef.Length > 120)
                throw new InvalidDataException("Invalid weapon filter.");
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref set, "set");
            Scribe_Values.Look(ref group, "group");
            Scribe_Values.Look(ref weaponDef, "weaponDef", "");
        }
    }
}

using System;
using System.IO;
using Verse;

namespace DeltaV
{
    public enum CombatScope : byte { Metric, OwnShip, EnemyShip, WeaponGroup, CurrentWeapon }
    public enum CombatWeaponSet : byte { All, Main, Secondary }
    public enum CombatWeaponFamily : byte { All, Laser, Kinetic, Rail, Guided }
    public enum CombatTargetOrder : byte { Balanced, Nearest, Threat, Finish, MainWeapons }
    public enum CombatFireAction : byte { Fire, Hold }
    public enum CombatPart : byte { Weapons, Propulsion }
    public enum CombatBearing : byte { Free, Enemy, World }

    public sealed class CombatWeaponFilter : IExposable
    {
        public CombatWeaponSet set;
        public CombatWeaponFamily family;
        public int group; // Zero includes all six groups in the selected set.
        public string weaponDef = "";
        public bool Includes(CompSpaceWeapon weapon) => Includes(weapon, null);
        internal bool Includes(CompSpaceWeapon weapon, CombatWeaponFamily? observedFamily) => weapon != null
            && weapon.SpaceModeEnabled && weapon.GroupActive
            && weapon.aimMode == SpaceWeaponAimMode.Volley && !weapon.WeaponProps.manualOnly
            && (set == CombatWeaponSet.All || (set == CombatWeaponSet.Main)
                == (weapon.salvoSet == SpaceWeaponSalvoSet.Main))
            && (group == 0 || weapon.groupNumber == group)
            && (string.IsNullOrEmpty(weaponDef) || weapon.parent.def.defName == weaponDef)
            && (family == CombatWeaponFamily.All || (observedFamily ?? FamilyOf(weapon)) == family);
        internal static CombatWeaponFamily FamilyOf(CompSpaceWeapon weapon)
        {
            return FamilyOf(weapon, weapon.SelectMagazine()?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef);
        }
        internal static CombatWeaponFamily FamilyOf(CompSpaceWeapon weapon, ThingDef projectileDef)
        {
            var projectile = projectileDef?.GetCompProperties<CompProperties_SpaceProjectile>();
            return projectile?.guided == true ? CombatWeaponFamily.Guided
                : projectile?.laserRange > 0 ? CombatWeaponFamily.Laser
                : weapon.Layout.acceleration.segments.Count > 0 ? CombatWeaponFamily.Rail : CombatWeaponFamily.Kinetic;
        }
        public CombatWeaponFilter Copy() => (CombatWeaponFilter)MemberwiseClone();
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(CombatWeaponSet), set) || !Enum.IsDefined(typeof(CombatWeaponFamily), family) || group < 0 || group > 6
                || weaponDef == null || weaponDef.Length > 120)
                throw new InvalidDataException("Invalid weapon filter.");
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref set, "set");
            Scribe_Values.Look(ref family, "family");
            Scribe_Values.Look(ref group, "group");
            Scribe_Values.Look(ref weaponDef, "weaponDef", "");
        }
    }
}

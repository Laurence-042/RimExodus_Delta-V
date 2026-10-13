using Verse;

namespace DeltaV
{
    /// <summary>原版同图射击 Verb；只让武器实例的地面模式决定其可用性。</summary>
    public sealed class Verb_SpaceTurretGround : Verb_Shoot
    {
        public override bool Available()
        {
            return caster is Building_SpaceTurretGun turret
                && turret.TryGetComp<CompSpaceWeapon>()?.GroundModeEnabled == true
                && base.Available();
        }
    }
}

using RimWorld;
using Verse;

namespace DeltaV
{
    internal static class SpaceTractor
    {
        internal static bool CanOperate(CompSpaceWeapon weapon) => weapon?.WeaponProps.tractor == true
            && weapon.parent.Spawned && !weapon.parent.Destroyed && weapon.SpaceModeEnabled
            && weapon.parent.GetComp<CompPowerTrader>()?.PowerOn == true
            && !weapon.parent.IsForbidden(Faction.OfPlayer)
            && MoonPoolUtility.IsMoonPoolAt(weapon.parent.Map, weapon.parent.Position);
    }
    public class CompProperties_SpaceTractor : CompProperties
    {
        public CompProperties_SpaceTractor() { compClass = typeof(CompSpaceTractor); }
    }
    public class CompSpaceTractor : ThingComp, ISpaceWeaponFireGate
    {
        private int blockedTick = -100;
        public bool CanFire(SpaceWeaponShot shot) => SpaceTractor.CanOperate(shot.weapon);
        internal void NotifyBlocked() { blockedTick = Find.TickManager.TicksGame; }
        public override string CompInspectStringExtra() => Find.TickManager.TicksGame - blockedTick <= 1
            ? "DeltaV_TractorBlocked".Translate().ToString() : null;
    }
    public class PlaceWorker_SpaceTractor : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center, Rot4 rot,
            Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            foreach (IntVec3 cell in GenAdj.OccupiedRect(center, rot, def.Size))
                if (!cell.InBounds(map) || !MoonPoolUtility.IsMoonPoolAt(map, cell))
                    return "DeltaV_TractorNeedsMoonPool".Translate();
            return true;
        }
    }
}

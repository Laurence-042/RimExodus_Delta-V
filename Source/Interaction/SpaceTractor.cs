using RimWorld;
using Verse;
using UnityEngine;

namespace DeltaV
{
    internal static class SpaceTractor
    {
        internal static bool CanOperate(CompSpaceWeapon weapon) => weapon?.WeaponProps.tractor == true
            && weapon.parent.Spawned && !weapon.parent.Destroyed && weapon.SpaceModeEnabled
            && weapon.Operational
            && weapon.OwnerShip.EffectiveFireControlMode != ShipFireControlMode.Disabled
            && weapon.Component<CompSpaceTractor>()?.CanOperate(weapon) == true;
    }
    public enum TractorReceiveResult { Rejected, Partial, Complete }
    public readonly struct TractorReceiveContext
    {
        public readonly UniverseManager Universe;
        public readonly CompSpaceWeapon Weapon;
        public readonly SpaceObject Target, Receiver;
        public TractorReceiveContext(UniverseManager universe, CompSpaceWeapon weapon, SpaceObject target, SpaceObject receiver)
        { Universe = universe; Weapon = weapon; Target = target; Receiver = receiver; }
        public Vector3 Destination => SpaceTransform.MapPointToUniverse(Receiver, Weapon.parent.Map,
            SpaceWeaponGeometry.MuzzleMapPoint(Weapon));
    }
    public class CompProperties_SpaceTractor : CompProperties
    {
        public CompProperties_SpaceTractor() { compClass = typeof(CompSpaceTractor); }
    }
    public partial class CompSpaceTractor : ThingComp, ISpaceWeaponFireGate, ISpaceWeaponLaunch, IStoreSettingsParent
    {
        internal const float CollectionRadius = 2f;
        private int blockedTick = -100;
        public bool CanFire(SpaceWeaponShot shot) => SpaceTractor.CanOperate(shot.weapon);
        public virtual bool ManualCargo => false;
        public virtual bool CanOperate(CompSpaceWeapon weapon) => weapon.Component<CompPowerTrader>()?.PowerOn == true
            && !parent.IsForbidden(Faction.OfPlayer) && MoonPoolUtility.IsMoonPoolAt(parent.Map, parent.Position);
        public bool TryLaunch(SpaceWeaponShot shot)
        {
            UniverseManager universe = Current.Game.GetComponent<UniverseManager>();
            var scatter = universe.Scatter;
            if (ManualCargo) return shot.manualFire && universe.TractorLinks.Trigger(shot);
            return shot.weapon.aimMode == SpaceWeaponAimMode.PointDefense
                ? scatter.Pull(shot.target, shot.weapon) : shot.manualFire && shot.LaunchProjectile();
        }
        public virtual bool HasReachedReceiver(TractorReceiveContext context)
            => OrbitSphere.ArcCells(context.Target.pose.position, context.Destination) <= CollectionRadius;
        public virtual TractorReceiveResult TryReceive(TractorReceiveContext context)
        {
            Map destination = parent.Map;
            AbstractThing payload = context.Target.Payload;
            if (payload?.Recoverable != true || destination == null || destination.Disposed
                || !Allows(payload.Entity) || !MoonPoolUtility.IsMoonPoolAt(destination, parent.Position))
                return TractorReceiveResult.Rejected;
            return payload.Handoff(thing =>
                GenPlace.TryPlaceThing(thing, parent.InteractionCell, destination, ThingPlaceMode.Near,
                    null, p => p != parent.Position && p != parent.InteractionCell));
        }
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

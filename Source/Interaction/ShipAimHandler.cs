using UnityEngine;
using Verse;
using RimWorld;

namespace DeltaV
{
    public class ShipAimHandler
    {
        protected readonly UniverseManager universe;
        protected readonly ShipBase ship;
        public ShipAimHandler(UniverseManager universe, ShipBase ship) { this.universe = universe; this.ship = ship; }
        public virtual void Aim(Vector2 mapPoint)
        {
            SpaceObject container = universe.FrameOf(ship.Map);
            foreach (CompSpaceWeapon weapon in ship.Weapons)
                if (weapon.SpaceModeEnabled && weapon.GroupActive)
                    universe.Weapons.ApplyControl(weapon, container, new WeaponControlCommand(mapPoint, false));
        }
    }

    public sealed class ArcadeShipAimHandler : ShipAimHandler
    {
        public ArcadeShipAimHandler(UniverseManager universe, ShipBase ship) : base(universe, ship) { }
        public override void Aim(Vector2 mapPoint)
        {
            base.Aim(mapPoint);
            SpaceObject container = universe.FrameOf(ship.Map);
            if (!ship.CanDriveIn(container) || Find.TickManager.Paused) return;
            Thing body = ship.Anchor;
            Vector2 center = ship.HullCenter;
            Vector3 screenCenter = Find.Camera.WorldToScreenPoint(new Vector3(center.x, 0f, center.y));
            Vector3 screenTarget = Find.Camera.WorldToScreenPoint(new Vector3(mapPoint.x, 0f, mapPoint.y));
            Vector2 aim = new Vector2(screenTarget.x - screenCenter.x, screenTarget.y - screenCenter.y);
            if (aim.sqrMagnitude < 0.0001f) return;
            Rot4 choice = SelectFacing(body.Rotation, aim, candidate =>
            {
                Vector2 direction = SpaceTransform.Rotate(Vector2.right, ShipOrientation.AngleFromRot4(candidate));
                Vector3 projected = Find.Camera.WorldToScreenPoint(new Vector3(center.x + direction.x, 0f, center.y + direction.y));
                return new Vector2(projected.x - screenCenter.x, projected.y - screenCenter.y);
            });
            if (choice == body.Rotation) return;
            foreach (IntVec3 cell in GenAdj.OccupiedRect(body.Position, choice, body.def.size))
                if (!cell.InBounds(body.Map)) return;
            body.Rotation = choice;
            body.Map.mapDrawer.MapMeshDirty(body.Position, MapMeshFlagDefOf.Things);
            ShipStructureChanges.Notify(body.Map);
            PlanetGlobeRenderer.InvalidateSilhouette(body.Map);
        }
        internal static Rot4 SelectFacing(Rot4 current, Vector2 aim, System.Func<Rot4, Vector2> projection)
        {
            if (aim.sqrMagnitude < .0001f) return current;
            Rot4 choice = current;
            float best = float.NegativeInfinity;
            // Start with the current rotation so ties do not make the body chatter.
            for (int i = 0; i < 4; i++)
            {
                Rot4 candidate = new Rot4((current.AsInt + i) % 4);
                float score = Vector2.Dot(aim.normalized, projection(candidate).normalized);
                if (score > best + 0.00001f) { best = score; choice = candidate; }
            }
            return choice;
        }

    }

    internal static class CameraFlightBasis
    {
        internal static DriveInputBasis Read()
        {
            Camera camera = Find.Camera;
            Vector3 pixel = new Vector3(camera.pixelWidth * .5f, camera.pixelHeight * .5f, 0f);
            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray origin = camera.ScreenPointToRay(pixel), up = camera.ScreenPointToRay(pixel + Vector3.up * 10f),
                left = camera.ScreenPointToRay(pixel - Vector3.right * 10f);
            if (!plane.Raycast(origin, out float d) || !plane.Raycast(up, out float u) || !plane.Raycast(left, out float l))
                return DriveInputBasis.ForAngle(Mathf.PI * .5f);
            Vector3 forward = up.GetPoint(u) - origin.GetPoint(d), side = left.GetPoint(l) - origin.GetPoint(d);
            return new DriveInputBasis(new Vector2(forward.x, forward.z), new Vector2(side.x, side.z));
        }
    }
}

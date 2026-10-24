using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum CombatTargetKind : byte { None, Ship, Container, Point }

    /// <summary>瞬时战术身份；不保存地图或船体快照，不参与存档。</summary>
    public readonly struct CombatTarget : IEquatable<CombatTarget>
    {
        public readonly CombatTargetKind Kind;
        public readonly DriveSubject Ship;
        public readonly int ObjectId;
        public readonly int SystemId;
        public readonly Vector2? MapPoint;
        public readonly Vector3 Point;
        private CombatTarget(CombatTargetKind kind, DriveSubject ship = default, int objectId = 0,
            int systemId = 0, Vector2? mapPoint = null, Vector3 point = default)
        { Kind = kind; Ship = ship; ObjectId = objectId; SystemId = systemId; MapPoint = mapPoint; Point = point; }
        public static CombatTarget ForShip(DriveSubject ship, int systemId = 0)
            => new CombatTarget(CombatTargetKind.Ship, ship: ship, systemId: systemId);
        public static CombatTarget ForContainer(SpaceObject obj, Vector2? point = null)
            => obj == null ? default : new CombatTarget(CombatTargetKind.Container, objectId: obj.objectId, mapPoint: point);
        public static CombatTarget At(Vector3 point) => new CombatTarget(CombatTargetKind.Point, point: point);
        public bool HasLock => Kind == CombatTargetKind.Ship || Kind == CombatTargetKind.Container;

        // 只解析位置与刚体点速度，不混入敌对、推进能力或任何战术资格。
        public bool TryResolve(UniverseManager universe, out ResolvedCombatTarget result)
        {
            result = default;
            if (Kind == CombatTargetKind.Point)
            { result = new ResolvedCombatTarget(null, null, new SpacePose(Point, 0f)); return true; }
            ShipBase ship = Kind == CombatTargetKind.Ship ? ShipRegistry.ShipByIdentity(Ship) : null;
            if (Kind == CombatTargetKind.Ship && ship?.IsValid != true) return false;
            SpaceObject container = Kind == CombatTargetKind.Ship ? universe.FrameOf(ship.Map)
                : Kind == CombatTargetKind.Container ? universe.ObjectById(ObjectId) : null;
            if (container == null || container.map?.Disposed == true
                || (ship != null || MapPoint.HasValue) && container.map == null) return false;
            Vector2? point = MapPoint;
            if (ship != null)
            {
                point = ship.HullCenter;
                int systemId = SystemId;
                Thing system = systemId == 0 ? null : ship.Map.listerThings.AllThings
                    .FirstOrDefault(t => t.thingIDNumber == systemId && t.Spawned && !t.Destroyed && ship.Contains(t));
                if (system != null) point = new Vector2(system.DrawPos.x, system.DrawPos.z);
            }
            SpacePose pose = point.HasValue ? SpaceTransform.RigidPointPose(container.pose,
                SpaceTransform.MapToShipLocal(container.map, point.Value), ship?.FrameAngle ?? 0f) : container.pose;
            result = new ResolvedCombatTarget(container, ship, pose);
            return true;
        }
        public bool Equals(CombatTarget other) => Kind == other.Kind && Ship.Equals(other.Ship)
            && ObjectId == other.ObjectId && SystemId == other.SystemId && MapPoint.Equals(other.MapPoint) && Point.Equals(other.Point);
        public override bool Equals(object obj) => obj is CombatTarget other && Equals(other);
        public override int GetHashCode() => ((int)Kind * 397 ^ Ship.GetHashCode()) * 397 ^ ObjectId ^ SystemId;
        public static bool operator ==(CombatTarget a, CombatTarget b) => a.Equals(b);
        public static bool operator !=(CombatTarget a, CombatTarget b) => !a.Equals(b);
    }

    /// <summary>仅供本次计算消费的解析结果，不作为跨 tick 目标状态。</summary>
    public readonly struct ResolvedCombatTarget
    {
        public readonly SpaceObject Container;
        public readonly ShipBase Ship;
        public readonly SpacePose Pose;
        internal ResolvedCombatTarget(SpaceObject container, ShipBase ship, SpacePose pose)
        { Container = container; Ship = ship; Pose = pose; }
    }

    public readonly struct WeaponFireRequest
    {
        public readonly Vector3 AimPoint;
        public readonly CombatTarget GuidanceTarget;
        public readonly bool ManualFire;
        public WeaponFireRequest(Vector3 aimPoint, CombatTarget guidanceTarget = default, bool manualFire = false)
        { AimPoint = aimPoint; GuidanceTarget = guidanceTarget; ManualFire = manualFire; }
    }

    public readonly struct WeaponAimInfo
    {
        public readonly Vector3 Muzzle;
        public readonly Vector2 MuzzleMap;
        public readonly ThingDef Projectile;
        public readonly bool Laser, Guided;
        public readonly float Speed, MinRange, Range;
        internal WeaponAimInfo(Vector3 muzzle, Vector2 muzzleMap, ThingDef projectile, float speed, float minRange, float range)
        {
            Muzzle = muzzle; MuzzleMap = muzzleMap; Projectile = projectile; Speed = speed; MinRange = minRange; Range = range;
            var props = projectile?.GetCompProperties<CompProperties_SpaceProjectile>();
            Laser = props?.laserRange > 0f; Guided = props?.guided == true;
        }
    }
}

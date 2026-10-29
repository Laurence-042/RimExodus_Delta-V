using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class CombatWeaponCoverage : IExposable
    {
        public float range; // Zero uses hardware range.
        public float arc = 360f;
        public float direction;
        public bool fixedDirection;
        public void ExposeData()
        {
            Scribe_Values.Look(ref range, "range");
            Scribe_Values.Look(ref arc, "arc", 360f);
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref fixedDirection, "fixedDirection");
        }
        public CombatWeaponCoverage Copy() => (CombatWeaponCoverage)MemberwiseClone();
    }

    public partial class CompSpaceWeapon
    {
        public CombatWeaponCoverage coverage = new CombatWeaponCoverage();
        public float EffectiveRange => coverage.range > 0f ? Mathf.Min(WeaponProps.range, coverage.range) : WeaponProps.range;
        internal float HardwareArc
        {
            get
            {
                float arc = WeaponProps.lockArcDeg >= 0f ? WeaponProps.lockArcDeg : WeaponProps.fireArcDeg;
                var traverse = parent.TryGetComp<CompSpaceWeaponTraverse>();
                return Mathf.Min(arc, traverse?.TraverseProps.mechanicalArcDeg ?? arc);
            }
        }
        internal void CoverageSector(out float center, out float arc)
        {
            float hardware = HardwareArc;
            float width = coverage.fixedDirection ? 2f * fireDeviationDeg : Mathf.Clamp(coverage.arc, 0f, 360f);
            float offset = Mathf.Clamp(coverage.direction, -180f, 180f);
            if (hardware < 359.99f)
            {
                float low = Mathf.Max(-hardware * 0.5f, offset - width * 0.5f);
                float high = Mathf.Min(hardware * 0.5f, offset + width * 0.5f);
                arc = Mathf.Max(0f, high - low);
                center = (high + low) * 0.5f;
            }
            else { arc = width; center = offset; }
            center = ShipOrientation.AngleFromRot4(parent.Rotation) + center * Mathf.Deg2Rad;
        }
        internal float CoverageError(float mapAngle)
        {
            float mount = ShipOrientation.AngleFromRot4(parent.Rotation);
            float hardwareError = Mathf.Max(0f, Mathf.Abs(SpaceTransform.WrapAngle(mapAngle - mount)) * Mathf.Rad2Deg - HardwareArc * 0.5f);
            float allowed = coverage.fixedDirection ? fireDeviationDeg : coverage.arc * 0.5f;
            float configuredError = Mathf.Max(0f, Mathf.Abs(SpaceTransform.WrapAngle(mapAngle - mount
                - coverage.direction * Mathf.Deg2Rad)) * Mathf.Rad2Deg - allowed);
            return Mathf.Max(hardwareError, configuredError);
        }
        internal bool Covers(SpaceObject frame, Vector3 point, Vector3 muzzle)
        {
            var delta = OrbitSphere.Offset(muzzle, point);
            return delta.magnitude >= WeaponProps.minRange && delta.magnitude <= EffectiveRange
                && CoverageError(Mathf.Atan2(delta.y, delta.x) - frame.pose.angle) <= 0.05f;
        }
        internal float ClampAim(float angle)
        {
            CoverageSector(out float center, out float arc);
            if (coverage.fixedDirection) return ShipOrientation.AngleFromRot4(parent.Rotation) + coverage.direction * Mathf.Deg2Rad;
            return center + Mathf.Clamp(SpaceTransform.WrapAngle(angle - center), -arc * 0.5f * Mathf.Deg2Rad, arc * 0.5f * Mathf.Deg2Rad);
        }
    }
}

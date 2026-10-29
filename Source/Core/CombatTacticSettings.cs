using System;
using System.IO;
using Verse;

namespace DeltaV
{
    public enum CombatTactic : byte { Automatic, HoldRange, Circle }
    public enum CombatFacing : byte { Automatic, Target, Relative }
    public enum CombatTurn : byte { Automatic, Clockwise, Counterclockwise }

    public sealed class CombatTacticSettings : IExposable
    {
        public CombatTactic behavior;
        public CombatFacing facing;
        public CombatTurn direction;
        // Zero selects a range from the ship's actual weapon geometry.
        public float distance;
        public float facingDegrees;
        public float movement = 0.8f;
        public CombatBearing bearing;
        public float bearingDegrees;
        public float repositionDegrees = 30f;
        public float intervalMin = 5f, intervalMax = 10f;
        public bool coordinateFlanking = true;
        public CombatWeaponFilter primaryWeapons = new CombatWeaponFilter();

        public CombatTacticSettings Copy()
        {
            var copy = (CombatTacticSettings)MemberwiseClone();
            copy.primaryWeapons = primaryWeapons?.Copy();
            return copy;
        }

        public void Validate()
        {
            if (!Enum.IsDefined(typeof(CombatTactic), behavior)
                || !Enum.IsDefined(typeof(CombatFacing), facing)
                || !Enum.IsDefined(typeof(CombatTurn), direction)
                || !Finite(distance, 0f, 100000f)
                || !Finite(facingDegrees, -180f, 180f)
                || !Finite(movement, 0.1f, 1f)
                || !Enum.IsDefined(typeof(CombatBearing), bearing)
                || !Finite(bearingDegrees, -180f, 180f)
                || !Finite(repositionDegrees, 0f, 90f)
                || !Finite(intervalMin, 1f, 600f) || !Finite(intervalMax, intervalMin, 600f)
                || bearing != CombatBearing.Free && behavior != CombatTactic.HoldRange
                || primaryWeapons == null)
                throw new InvalidDataException("Invalid combat tactic settings.");
            primaryWeapons.Validate();
        }

        private static bool Finite(float value, float min, float max) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

        public void ExposeData()
        {
            Scribe_Values.Look(ref behavior, "behavior");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref distance, "distance");
            Scribe_Values.Look(ref facingDegrees, "facingDegrees");
            Scribe_Values.Look(ref movement, "movement", 0.8f);
            Scribe_Values.Look(ref bearing, "bearing");
            Scribe_Values.Look(ref bearingDegrees, "bearingDegrees");
            Scribe_Values.Look(ref repositionDegrees, "repositionDegrees", 30f);
            Scribe_Values.Look(ref intervalMin, "intervalMin", 5f);
            Scribe_Values.Look(ref intervalMax, "intervalMax", 10f);
            Scribe_Values.Look(ref coordinateFlanking, "coordinateFlanking", true);
            Scribe_Deep.Look(ref primaryWeapons, "primaryWeapons");
        }
    }
}

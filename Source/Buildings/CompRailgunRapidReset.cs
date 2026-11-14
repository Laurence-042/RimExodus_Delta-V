namespace DeltaV
{
    public class CompProperties_RailgunRapidReset : CompProperties_SpaceWeaponAttachment
    {
        public CompProperties_RailgunRapidReset() => compClass = typeof(CompRailgunRapidReset);
    }

    public class CompRailgunRapidReset : CompSpaceWeaponAttachment
    {
        public override float ShotIntervalMultiplier => 0.5f;
    }
}

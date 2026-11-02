using AdaptiveStorage;
using Verse;

namespace DeltaV
{
    // ASF owns both state graphics; CompHackable remains the sole lock-state owner.
    public class StorageGraphicData_CargoState : StorageGraphicData
    {
        public GraphicData unlockedGraphicData;

        public override Graphic GraphicColoredFor(StorageRenderer renderer)
        {
            return renderer.Parent is Building_SpaceCargoContainer cargo && cargo.IsUnlocked
                ? unlockedGraphicData.Graphic : Graphic;
        }
    }
}

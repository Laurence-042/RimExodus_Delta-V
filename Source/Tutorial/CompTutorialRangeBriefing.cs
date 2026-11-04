using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed class CompProperties_TutorialRangeBriefing : CompProperties_UseEffect
    {
        public CompProperties_TutorialRangeBriefing() { compClass = typeof(CompTutorialRangeBriefing); }
    }

    public sealed class CompTutorialRangeBriefing : CompUseEffect
    {
        internal Building_HackableDoor entranceDoor;

        public override AcceptanceReport CanBeUsedBy(Pawn pawn)
        {
            if (pawn?.Faction != Faction.OfPlayer || !PublicFacility.PlayerMayUse(parent))
                return "DeltaV_Range_NoAccess".Translate();
            if (entranceDoor?.Spawned != true || entranceDoor.Map != parent.Map)
                return "DeltaV_Range_EntranceUnavailable".Translate();
            return true;
        }

        public override void DoEffect(Pawn usedBy)
        {
            if (!CanBeUsedBy(usedBy).Accepted || usedBy.Map != parent.Map
                || usedBy.Position != parent.InteractionCell) return;
            Map expectedMap = parent.Map;
            Building_HackableDoor expectedDoor = entranceDoor;
            Current.Game.GetComponent<UniverseManager>().QueueFrameAction(() =>
            {
                if (!parent.Spawned || parent.Map != expectedMap || !CanBeUsedBy(usedBy).Accepted) return;
                Find.WindowStack.Add(new Dialog_MessageBox("DeltaV_Range_Lesson_Introduction".Translate(),
                    "DeltaV_Range_Acknowledge".Translate(), () =>
                    {
                        if (parent.Spawned && parent.Map == expectedMap && entranceDoor == expectedDoor
                            && usedBy.Map == expectedMap && CanBeUsedBy(usedBy).Accepted)
                            UnlockEntrance(expectedDoor);
                    }, "DeltaV_Range_ReadLater".Translate(), title: "DeltaV_Range_Title".Translate()));
            });
        }

        internal static void UnlockEntrance(Building_HackableDoor door)
        {
            // Native remote completion with no hacker preserves the allied owner.
            if (door.Locked) door.Hackable.HackNow();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref entranceDoor, "rangeEntranceDoor");
        }
    }
}

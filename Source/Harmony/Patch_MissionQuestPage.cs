using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // Add actions at the native target section; no live ship reference is needed for navigation.
    [HarmonyPatch(typeof(MainTabWindow_Quests), "DoLookTargets")]
    internal static class Patch_MissionQuestPage
    {
        static void Postfix(Rect innerRect, ref float curY, Quest ___selected)
        {
            if (___selected?.Historical != false) return;
            var part = ___selected.PartsListForReading.OfType<QuestPart_SpaceMission>().FirstOrDefault();
            if (part == null) return;
            var service = Current.Game?.GetComponent<UniverseManager>()?.Missions;
            SpaceMission mission = service?.Find(part.missionId);
            if (mission == null) return;
            curY += 4f;
            float width = (innerRect.width - 10f) / 2f;
            if (Widgets.ButtonText(new Rect(innerRect.x, curY, width, 28), "DeltaV_NavCourse".Translate()))
            {
                Find.MainTabsRoot.EscapeCurrentTab();
                service.Locate(mission);
            }
            if (Widgets.ButtonText(new Rect(innerRect.x + width + 10f, curY, width, 28), "DeltaV_Mission_Abandon".Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DeltaV_Mission_AbandonConfirm".Translate(), () => service.Fail(mission)));
            curY += 32f;
        }
    }
}

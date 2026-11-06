using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(RecipeWorker), "ReportViolation")]
    internal static class Patch_MissionSurgery
    {
        static bool Prefix(Pawn pawn, Pawn billDoer, Faction factionToInform)
        {
            var service = Current.Game?.GetComponent<UniverseManager>()?.Missions;
            if (factionToInform == null || billDoer?.Faction != Faction.OfPlayer || service?.HasPassengerContract(pawn) != true) return true;
            // Preserve the native signal, replacing only this passenger's direct goodwill debit.
            QuestUtility.SendQuestTargetSignals(pawn.questTags, "SurgeryViolation", pawn.Named("SUBJECT"));
            return false;
        }
    }
    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberTookDamage))]
    internal static class Patch_MissionDamageGoodwill
    {
        // Native PreApplyDamage debits diplomacy before the same hit can emit the death outcome.
        static bool Prefix(Pawn member, DamageInfo dinfo) => dinfo.Instigator?.Faction != Faction.OfPlayer
            || Current.Game?.GetComponent<UniverseManager>()?.Missions.HasPassengerContract(member) != true;
    }
    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberCaptured))]
    internal static class Patch_MissionCaptured
    {
        static bool Prefix(Faction __instance, Pawn member)
        {
            if (Current.Game?.GetComponent<UniverseManager>()?.Missions.HasPassengerContract(member) != true) return true;
            __instance.ideos?.Notify_MemberGainedOrLost();
            return false;
        }
    }
    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberDied))]
    internal static class Patch_MissionDeathGoodwill
    {
        static bool Prefix(Faction __instance, Pawn member)
        {
            if (__instance.IsPlayer || Current.Game?.GetComponent<UniverseManager>()?.Missions.HasPassengerContract(member) != true) return true;
            __instance.ideos?.Notify_MemberGainedOrLost();
            return false;
        }
    }
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    internal static class Patch_MissionGuestStatus
    {
        static bool Prefix(Pawn ___pawn, Faction newHost) => newHost != null
            || Current.Game?.GetComponent<UniverseManager>()?.Missions.PreserveGuestStatus(___pawn) != true;
        static void Postfix(Pawn ___pawn, Faction newHost, GuestStatus guestStatus, ref Faction ___slaveFactionInt)
        {
            if (newHost != Faction.OfPlayer) return;
            var service = Current.Game?.GetComponent<UniverseManager>()?.Missions;
            Faction origin = service?.PassengerFaction(___pawn);
            if (origin == null) return;
            if (guestStatus == GuestStatus.Slave) ___slaveFactionInt = origin;
            if (guestStatus == GuestStatus.Prisoner || guestStatus == GuestStatus.Slave)
                service.FailPassenger(___pawn,
                    guestStatus == GuestStatus.Slave ? "Enslaved" : "Arrested");
        }
    }
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetFaction))]
    internal static class Patch_MissionShipFaction
    {
        static void Postfix(Thing __instance)
        {
            var console = __instance.TryGetComp<CompDeltaVPilotConsole>();
            if (console?.isMain == true)
                Current.Game?.GetComponent<UniverseManager>()?.Missions.ShipChanged(new DriveSubject(0, console.shipId));
        }
    }
}

using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Live view over manager-owned retention facts; this window stores no lifecycle state.</summary>
    internal sealed class Dialog_MapRetention : Window
    {
        private readonly UniverseManager universe;
        private readonly int objectId;

        internal Dialog_MapRetention(UniverseManager universe, SpaceObject target)
        {
            this.universe = universe;
            objectId = target.objectId;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            optionalTitle = "DeltaV_MapRetentionTitle".Translate(target.label);
        }

        public override Vector2 InitialSize => new Vector2(620f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            if (!DebugSettings.godMode || Current.Game?.GetComponent<UniverseManager>() != universe
                || universe.ObjectById(objectId) is not SpaceObject target)
            {
                Close();
                return;
            }

            SpaceMapRetentionInfo info = universe.MapStates.RetentionInfo(objectId);
            int now = Find.TickManager?.TicksGame ?? 0;
            string lastContact = info.lastMeaningfulPlayerContactTick < 0
                ? "DeltaV_MapRetentionNever".Translate()
                : "DeltaV_MapRetentionAgo".Translate(
                    Math.Max(0, now - info.lastMeaningfulPlayerContactTick).ToStringTicksToPeriod());
            string expiry = info.exactRetentionExpiryTick < 0
                ? "DeltaV_MapRetentionNotApplicable".Translate()
                : info.exactRetentionExpiryTick > now
                    ? "DeltaV_MapRetentionIn".Translate(
                        (info.exactRetentionExpiryTick - now).ToStringTicksToPeriod())
                    : "DeltaV_MapRetentionExpiredAgo".Translate(
                        Math.Max(0, now - info.exactRetentionExpiryTick).ToStringTicksToPeriod());
            string blocker = info.blockReason.NullOrEmpty()
                ? "DeltaV_MapRetentionNoBlocker".Translate()
                : TranslateBlocker(info.blockReason);

            var listing = new Listing_Standard();
            listing.Begin(new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 116f));
            string state = ("DeltaV_MapRetentionState_" + info.state).Translate();
            if (info.dormant) state += " (" + "DeltaV_MapRetentionStateDormant".Translate() + ")";
            if (info.coldRebuild) state += " (" + "DeltaV_MapRetentionStateCold".Translate() + ")";
            listing.Label("DeltaV_MapRetentionState".Translate(state));
            listing.Label("DeltaV_MapRetentionLastContact".Translate(lastContact));
            listing.Label("DeltaV_MapRetentionExpiry".Translate(expiry));
            listing.Label("DeltaV_MapRetentionArchiveSize".Translate(
                info.compressedBytes > 0 ? info.compressedBytes.ToStringBytes() : "—"));
            listing.Label("DeltaV_MapRetentionBlocker".Translate(blocker));
            listing.GapLine();
            listing.Label("DeltaV_MapRetentionExplanation".Translate());
            listing.End();

            float y = inRect.yMax - 104f;
            string keepLabel = info.manualKeepExact
                ? "DeltaV_MapRetentionResumeAutomatic".Translate()
                : "DeltaV_MapRetentionKeepExact".Translate();
            if (Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 30f), keepLabel))
                universe.MapStates.SetManualKeepExact(objectId, !info.manualKeepExact);

            y += 38f;
            float width = (inRect.width - 8f) * .5f;
            bool canCold = info.blockReason.NullOrEmpty()
                && info.state is SpaceMapStateKind.Loaded or SpaceMapStateKind.Archived;
            Rect coldRect = new Rect(inRect.x, y, width, 34f);
            if (Widgets.ButtonText(coldRect, "DeltaV_MapRetentionColdNow".Translate(), active: canCold))
                ConfirmCold(target.label);
            if (!canCold) TooltipHandler.TipRegion(coldRect, blocker);

            bool canRetire = info.blockReason.NullOrEmpty() && info.state != SpaceMapStateKind.Deleted;
            Rect retireRect = new Rect(coldRect.xMax + 8f, y, width, 34f);
            if (Widgets.ButtonText(retireRect, "DeltaV_MapRetentionAbandon".Translate(), active: canRetire))
                ConfirmPermanent(target.label);
            if (!canRetire) TooltipHandler.TipRegion(retireRect, blocker);
        }

        private void ConfirmCold(string label)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "DeltaV_MapRetentionColdConfirm".Translate(label), () =>
                {
                    SpaceObject current = universe.ObjectById(objectId);
                    if (current == null || !universe.MapStates.RequestColdRetirement(current, playerRequested: true))
                    {
                        Messages.Message("DeltaV_MapRetentionRequestRejected".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Close();
                }));
        }

        private void ConfirmPermanent(string label)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "DeltaV_MapRetentionAbandonConfirm".Translate(label), () =>
                {
                    SpaceObject current = universe.ObjectById(objectId);
                    if (current == null || !universe.MapStates.RequestPermanentRetirement(current,
                        "player abandoned site", playerRequested: true))
                    {
                        Messages.Message("DeltaV_MapRetentionRequestRejected".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Close();
                }));
        }

        private static string TranslateBlocker(string blocker)
        {
            string suffix = blocker switch
            {
                "current map" => "CurrentMap",
                "player pawn or prisoner" => "PlayerPawn",
                "player home" => "PlayerHome",
                "player building" => "PlayerBuilding",
                "player ship" => "PlayerShip",
                "active mission" => "ActiveMission",
                "map generation" => "Generation",
                "map transfer or docking" => "Transfer",
                "external map reference" => "ExternalReference",
                "transition in progress" => "Transition",
                "manual exact retention" => "ManualKeep",
                "map unavailable" => "Unavailable",
                _ => null
            };
            return suffix == null ? blocker : ("DeltaV_MapRetentionBlock_" + suffix).Translate();
        }
    }
}

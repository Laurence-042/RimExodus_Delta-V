using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public class CompSoftDockHackable : CompHackable
    {
        // This port Def has no timed lockouts. Hack/progress/save/completion stay vanilla;
        // the local pawn job advances work, without polling every idle door for expiry.
        public override void CompTick() { }
    }

    public partial class Building_DockingPort
    {
        internal Building_DockingPort SoftPeer(Pawn pawn, bool requireOpen = true)
            => Current.Game?.GetComponent<UniverseManager>()?.SoftDock.Peer(this, pawn, requireOpen);
        internal bool CanRemoteHack(Pawn pawn)
        {
            var peer = SoftPeer(pawn, false);
            var hack = peer?.GetComp<CompHackable>();
            return PawnCanOpen(pawn) && peer != null && !peer.PawnCanOpen(pawn)
                && hack != null && !hack.IsHacked && !hack.LockedOut && HackUtility.IsCapableOfHacking(pawn)
                && pawn.skills.GetSkill(SkillDefOf.Intellectual).Level >= hack.Props.intellectualSkillPrerequisite;
        }
        private IEnumerable<Gizmo> SoftDockGizmos()
        {
            var link = Current.Game?.GetComponent<UniverseManager>()?.SoftDock.Pair(this);
            if (link?.connected != true) yield break;
            SoftDockService service = Current.Game.GetComponent<UniverseManager>().SoftDock;
            var transferCommand = new Command_Action
            {
                defaultLabel = "DeltaV_SoftDockTransfer".Translate(),
                defaultDesc = "DeltaV_SoftDockTransferDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewCave"),
                action = () =>
                {
                    var portal = service.Portal(this);
                    if (portal?.IsEnterable(out _) == true) Find.WindowStack.Add(new Dialog_EnterPortal(portal));
                }
            };
            if (this.HostileTo(Faction.OfPlayer) || link.Other(this).HostileTo(Faction.OfPlayer))
                transferCommand.Disable("DeltaV_SoftDockTransferNoWorker".Translate());
            yield return transferCommand;
            var active = service.ExistingPortal(this);
            if (active?.LoadInProgress == true) yield return new Command_Action
            {
                defaultLabel = "DeltaV_SoftDockTransferCancel".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                action = active.CancelLoad
            };
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_SoftDockView".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewCave"),
                action = () => { var peer = link.Other(this); if (peer.Spawned) CameraJumper.TryJumpAndSelect(peer); }
            };
        }
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn pawn)
        {
            foreach (var option in base.GetFloatMenuOptions(pawn)) yield return option;
            if (!pawn.IsColonistPlayerControlled || !pawn.CanReach(this, PathEndMode.Touch, Danger.Deadly)) yield break;
            if (SoftPeer(pawn) != null)
            {
                yield return new FloatMenuOption("DeltaV_SoftDockEnter".Translate(),
                    () => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterPortal,
                        Current.Game.GetComponent<UniverseManager>().SoftDock.Portal(this)), JobTag.Misc));
                yield return new FloatMenuOption("DeltaV_SoftDockHaul".Translate(), () =>
                    Find.Targeter.BeginTargeting(new TargetingParameters
                    {
                        canTargetItems = true, canTargetPawns = true, canTargetBuildings = false, canTargetLocations = false,
                        validator = target => target.Thing != null && target.Thing.Map == Map
                            && (target.Thing.def.category == ThingCategory.Item || target.Thing is Pawn casualty && casualty.Downed)
                    }, target =>
                    {
                        var portal = Current.Game.GetComponent<UniverseManager>().SoftDock.Portal(this);
                        if (portal == null) return;
                        var row = new TransferableOneWay();
                        row.things.Add(target.Thing);
                        portal.AddToTheToLoadList(row, target.Thing.stackCount);
                        pawn.jobs.TryTakeOrderedJob(EnterPortalUtility.JobOnPortal(pawn, portal), JobTag.Misc);
                    }));
            }
            else if (CanRemoteHack(pawn))
                yield return new FloatMenuOption("DeltaV_SoftDockHack".Translate(),
                    () => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_HackSoftDock, this, SoftPeer(pawn, false)), JobTag.Misc));
        }
    }
}

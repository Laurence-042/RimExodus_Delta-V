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
                    () => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_EnterSoftDock, this), JobTag.Misc));
                yield return new FloatMenuOption("DeltaV_SoftDockHaul".Translate(), () =>
                    Find.Targeter.BeginTargeting(new TargetingParameters
                    {
                        canTargetItems = true, canTargetPawns = true, canTargetBuildings = false, canTargetLocations = false,
                        validator = target => target.Thing != null && target.Thing.Map == Map
                            && (target.Thing.def.category == ThingCategory.Item || target.Thing is Pawn casualty && casualty.Downed)
                    }, target =>
                    {
                        var job = JobMaker.MakeJob(DeltaVDefOf.DeltaV_HaulSoftDock, target.Thing, this);
                        job.count = target.Thing.stackCount;
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    }));
            }
            else if (CanRemoteHack(pawn))
                yield return new FloatMenuOption("DeltaV_SoftDockHack".Translate(),
                    () => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DeltaVDefOf.DeltaV_HackSoftDock, this, SoftPeer(pawn, false)), JobTag.Misc));
        }
        internal bool TryDestination(Pawn pawn, out Map map, out IntVec3 cell)
        {
            var peer = SoftPeer(pawn);
            map = peer?.Map; cell = IntVec3.Invalid;
            if (map == null) return false;
            // Exit onto the ship-facing side, never into the external visual passage.
            IntVec3 preferred = peer.Position - peer.Rotation.FacingCell;
            foreach (IntVec3 candidate in GenRadial.RadialCellsAround(preferred, 3f, true))
                if (candidate.InBounds(map) && candidate.Standable(map) && SpaceScatterService.Supported(map, candidate)
                    && map.reachability.CanReach(peer.Position, candidate, PathEndMode.OnCell,
                        TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly)))
                { cell = candidate; return true; }
            return false;
        }
    }
}

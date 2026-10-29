using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>战术层只选择参考轨迹，出力由现有自动驾驶和 MPC 计算。</summary>
    internal sealed partial class ShipAutoManeuverControl
    {
        private readonly UniverseManager universe;
        private readonly DriveSubject ownerId;
        private CombatTarget anchorTarget;

        internal ShipAutoManeuverControl(UniverseManager universe, DriveSubject ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        internal void DefaultCurveReference(ShipBase ship, out float clearance,
            out float fallbackDistance)
        {
            float ownRadius = ShipFireControl.HullRadius(ship);
            float enemyRadius = anchorTarget.TryResolve(universe, out var resolved)
                ? ShipFireControl.HullRadius(resolved.Ship) : ownRadius;
            clearance = ownRadius + enemyRadius + 8f;
            fallbackDistance = hasTactic ? attackPose.distance : anchorDistance > 0f ? anchorDistance : 100f;
        }

        internal void Decide(ShipBase ship, SpaceObject own, CombatTarget target,
            bool allowBoarding, bool playerInitiated)
        {
            if (ship?.IsValid != true || own == null) { Clear(ship); return; }
            if (!target.TryResolve(universe, out var resolved)
                || !ShipFireControl.CandidateValid(ship, own, resolved.Container, resolved.Ship))
            {
                Clear(ship); return;
            }

            if (anchorTarget != target)
            {
                nextTacticTick = 0;
                hasTactic = false;
                reposition.Reset(ownerId.key);
                turnDirection = 0;
                anchorDistance = OrbitSphere.ArcCells(ShipRegistry.ShipPose(ship).position,
                    resolved.Pose.position);
            }
            anchorTarget = target;
            offenseEnabled = !playerInitiated
                || ship.Combat?.fireControlMode == ShipFireControlMode.Automatic;
            Boarding = false;
            if (allowBoarding && universe.Shuttles.BoardingCraftOf(ship) != null
                && ship.CombatAI?.strategy?.npcBoardConditions is { Count: > 0 } board)
            {
                var facts = new CombatStrategyFacts(universe, ship, own);
                Boarding = ship.CombatAI.strategy.npcBoardMatchAny
                    ? board.Any(c => c?.Matches(facts.Context(resolved.Ship)) == true)
                    : board.All(c => c?.Matches(facts.Context(resolved.Ship)) == true);
            }
            float clearance = SpaceCollision.BoundsRadiusOf(own)
                + SpaceCollision.BoundsRadiusOf(resolved.Container) + 8f;
            universe.Autopilot.TrackOrbit(DriveSubject.Of(ship), SampleTarget,
                clearance, PlanTacticTrajectory, playerInitiated);
        }

        internal void Clear(ShipBase ship)
        {
            if (ship?.IsValid == true && universe.Autopilot.IsCombatOrbit(DriveSubject.Of(ship)))
                universe.Autopilot.Detach(DriveSubject.Of(ship), "combat target lost");
            anchorTarget = default;
            nextTacticTick = nextPoseTick = 0;
            hasTactic = false;
            routeShapes.Clear(); routeStructureVersion = -1;
            tacticIntent = default;
            tacticDriveVersion = tacticStructureVersion = -1;
            tacticDrive = null;
            activeRule = null;
            anchorDistance = 0f;
            turnDirection = 0;
            Boarding = false;
        }

        private bool SampleTarget(out SpacePose pose)
        {
            pose = default;
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (ship?.IsValid != true || !anchorTarget.TryResolve(universe, out var resolved)
                || !ShipFireControl.CandidateValid(ship, universe.FrameOf(ship.Map),
                    resolved.Container, resolved.Ship)) return false;
            pose = resolved.Pose;
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum NpcShipAiGate : byte
    {
        Ready,
        NoMap,
        NoMainConsole,
        NoNpcFaction,
        NotShipContainer,
        FrozenMap,
        OutsideActiveRadius,
        InvalidPilotTarget,
        NoUniverseFrame,
        GravityRestricted,
        ApproachConflict,
        NoControllerAuthority
    }

    /// <summary>实例级 NPC 战术决策。AI 只选择目标与任务；逐 tick 战斗由归属 controller 调度。</summary>
    public sealed class NpcShipAiService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<int, NpcShipAiGate> lastGateByShip = new Dictionary<int, NpcShipAiGate>();
        private readonly HashSet<int> noMainReportedObjects = new HashSet<int>();
        private readonly List<SpaceObject> objectBuffer = new List<SpaceObject>();
        private int lastDecisionTick = -1;
        private int lastCleanupTick = -1;

        public NpcShipAiService(UniverseManager universe) { this.universe = universe; }

        public void Tick()
        {
            int now = Find.TickManager.TicksGame;
            int interval = Mathf.Clamp(DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
            if (lastDecisionTick < 0 || now - lastDecisionTick >= interval)
            {
                lastDecisionTick = now;
                TickDecisions();
            }
        }

        private void TickDecisions()
        {
            objectBuffer.Clear();
            objectBuffer.AddRange(universe.objects);
            foreach (SpaceObject obj in objectBuffer)
            {
                if (obj?.map == null || obj.map.Disposed) continue;
                if (obj.Tag != SpaceObjectTag.Ship)
                {
                    LogUnclaimedConsoleEdge(obj);
                    continue;
                }
                noMainReportedObjects.Remove(obj.objectId);
                foreach (Ship ship in ShipRegistry.ShipsOnMap(obj.map))
                {
                    Faction faction = ShipRegistry.FactionOf(ship);
                    if (faction == null || faction == Faction.OfPlayer) continue;
                    NpcShipController controller = universe.Controls.NpcFor(ship);
                    NpcShipAiGate gate = GateFor(obj, ship, controller);
                    LogGateEdge(ship, obj, gate);
                    if (gate != NpcShipAiGate.Ready)
                    {
                        controller?.SetCombatReady(false);
                        continue;
                    }
                    if (!controller.Active || controller.target.ControlKey != ship.ownerId)
                        controller.Enter(PilotTarget.Of(ship));
                    controller.Ai.Tick(controller);
                }
            }
            int now = Find.TickManager.TicksGame;
            if (lastCleanupTick < 0 || now - lastCleanupTick >= 600)
            {
                lastCleanupTick = now;
                foreach (int id in lastGateByShip.Keys.ToList())
                    if (!ShipRegistry.ShipByOwner(id).IsValid)
                    {
                        lastGateByShip.Remove(id);
                    }
                noMainReportedObjects.RemoveWhere(id => universe.ObjectById(id) == null);
            }
        }

        private void LogUnclaimedConsoleEdge(SpaceObject obj)
        {
            if (!DeltaVLog.Enabled(DeltaVLogModule.Combat)
                || universe.Lifecycle.IsFrozen(obj.map)
                || noMainReportedObjects.Contains(obj.objectId)) return;
            if (!HasUnclaimedNpcConsole(obj)) return;
            noMainReportedObjects.Add(obj.objectId);
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"NPC console without recognized main ship on container #{obj.objectId}; "
                + $"AI gate {NpcShipAiGate.NoMainConsole}; {UniverseManager.MapTag(obj.map)}");
        }

        internal bool HasUnclaimedNpcConsole(SpaceObject obj)
        {
            return obj?.map != null && !obj.map.Disposed && obj.Tag != SpaceObjectTag.Ship
                && obj.map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole)
                    .Any(thing => thing.Faction != null && thing.Faction != Faction.OfPlayer);
        }

        internal NpcShipAiGate GateFor(SpaceObject obj, Ship ship)
        {
            NpcShipController controller = ship.IsValid ? universe.Controls.NpcFor(ship) : null;
            return GateFor(obj, ship, controller);
        }

        private NpcShipAiGate GateFor(SpaceObject obj, Ship ship, NpcShipController controller)
        {
            if (obj?.map == null || obj.map.Disposed) return NpcShipAiGate.NoMap;
            if (!ship.IsValid || ship.map != obj.map) return NpcShipAiGate.NoMainConsole;
            Faction faction = ShipRegistry.FactionOf(ship);
            if (faction == null || faction == Faction.OfPlayer) return NpcShipAiGate.NoNpcFaction;
            if (controller == null) return NpcShipAiGate.NoControllerAuthority;
            if (obj.Tag != SpaceObjectTag.Ship) return NpcShipAiGate.NotShipContainer;
            if (universe.Lifecycle.IsFrozen(obj.map) && !obj.npcRetreating)
                return NpcShipAiGate.FrozenMap;
            if (universe.Lifecycle.MinPlayerDistance(obj)
                > DeltaVMod.CurrentSettings.poiActiveRadius && !obj.npcRetreating)
                return NpcShipAiGate.OutsideActiveRadius;
            if (controller != null && (!controller.Active
                    || controller.target.ControlKey != ship.ownerId))
            {
                PilotEntryBlockReason block = controller.EntryBlockReason(PilotTarget.Of(ship));
                if (block != PilotEntryBlockReason.None)
                    return block switch
                    {
                        PilotEntryBlockReason.InvalidTarget => NpcShipAiGate.InvalidPilotTarget,
                        PilotEntryBlockReason.NoUniverseFrame => NpcShipAiGate.NoUniverseFrame,
                        PilotEntryBlockReason.GravityRestricted => NpcShipAiGate.GravityRestricted,
                        PilotEntryBlockReason.ApproachConflict => NpcShipAiGate.ApproachConflict,
                        _ => NpcShipAiGate.NoControllerAuthority
                    };
            }
            return NpcShipAiGate.Ready;
        }

        private void LogGateEdge(Ship ship, SpaceObject obj, NpcShipAiGate gate)
        {
            if (!DeltaVLog.Enabled(DeltaVLogModule.Combat)) return;
            if (lastGateByShip.TryGetValue(ship.ownerId, out NpcShipAiGate previous)
                && previous == gate) return;
            lastGateByShip[ship.ownerId] = gate;
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"NPC ship #{ship.ownerId} {UniverseManager.MapTag(obj.map)} "
                + $"AI gate {gate}; relation={ShipRegistry.StateOf(ship)} "
                + $"faction={ShipRegistry.FactionOf(ship)?.Name ?? "none"} "
                + $"playerDist={universe.Lifecycle.MinPlayerDistance(obj):F0}");
        }
    }

    internal sealed class NpcCombatAi : INpcShipAi
    {
        private readonly UniverseManager universe;
        private readonly int ownerId;
        private int nextBoardTick;
        private bool noThrustObserved;
        private bool lastNoThrust;

        public NpcCombatAi(UniverseManager universe, int ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        public bool HasAuthority(Ship ship) => ship.IsValid && ship.ownerId == ownerId
            && ShipRegistry.FactionOf(ship) != null
            && ShipRegistry.FactionOf(ship) != Faction.OfPlayer;

        public void ComposeCommand(Ship ship, ref PilotService.PilotCommand command) { }

        public void Tick(NpcShipController controller)
        {
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (!HasAuthority(ship)) return;
            SpaceObject own = universe.FrameOf(ship.map);
            if (own == null) return;
            int now = Find.TickManager.TicksGame;
            controller.AutoFire.Decide(ship, own);
            SpaceObject target = controller.AutoFire.Target;
            RecordBaseline(ship, own);
            string retreatReason = null;
            if (own.npcRetreating || IsDisabled(ship, own, out retreatReason))
            {
                controller.SetCombatReady(false);
                if (!own.npcRetreating)
                {
                    own.npcRetreating = true;
                    own.npcRetreatOrigin = own.pose.position;
                    SpaceObject home = NearestHome(own, ShipRegistry.FactionOf(ship));
                    own.npcHomeObjectId = home?.objectId ?? 0;
                    universe.Autopilot.Detach(ship, "NPC retreat");
                    if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"NPC ship #{ownerId} retreat triggered ({retreatReason}); home="
                            + (home == null ? "leave sector" : $"#{home.objectId} '{home.label}'")
                            + $"; {UniverseManager.MapTag(ship.map)}");
                }
                LogThrustEdge(ship);
                TickRetreat(ship, own, target);
                return;
            }
            LogThrustEdge(ship);
            if (target == null)
            {
                controller.SetCombatReady(false);
                universe.Autopilot.Detach(ship, "NPC target lost");
                return;
            }
            controller.SetCombatReady(true);
            controller.AutoManeuver.Decide(ship, own, target, own.npcDoctrines,
                allowBoarding: true, playerInitiated: false);
            if (controller.AutoManeuver.Doctrine == NpcDoctrine.Boarder && now >= nextBoardTick)
            {
                nextBoardTick = now + 600;
                TryBoard(ship, own, target);
            }
        }

        internal static int CountEligibleTargets(UniverseManager universe, Ship ship, SpaceObject own)
        {
            int count = 0;
            foreach (SpaceObject candidate in universe.objects)
                if (ShipAutoFireControl.CandidateValid(ship, own, candidate)) count++;
            return count;
        }


        private static float Thrust(Ship ship)
        {
            return ShipPhysics.AvailableThrust(ship.map);
        }

        private void LogThrustEdge(Ship ship)
        {
            if (!DeltaVLog.Enabled(DeltaVLogModule.Combat)) return;
            bool noThrust = Thrust(ship) <= 0f;
            if (noThrustObserved && noThrust == lastNoThrust) return;
            bool wasObserved = noThrustObserved;
            noThrustObserved = true;
            lastNoThrust = noThrust;
            if (noThrust)
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"NPC ship #{ownerId} has no operational map thrust; "
                    + $"{UniverseManager.MapTag(ship.map)}");
            else if (wasObserved)
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"NPC ship #{ownerId} operational map thrust restored; "
                    + $"{UniverseManager.MapTag(ship.map)}");
        }

        private void RecordBaseline(Ship ship, SpaceObject own)
        {
            if (own.npcInitialThrust <= 0f) own.npcInitialThrust = Thrust(ship);
            if (own.npcInitialFirepower <= 0f)
                own.npcInitialFirepower = universe.Weapons.EstimateFirepower(ship, 50f, 20f);
            if (own.npcInitialMainConsoleId <= 0)
                own.npcInitialMainConsoleId = ship.console.parent.thingIDNumber;
        }

        private bool IsDisabled(Ship ship, SpaceObject own, out string reason)
        {
            reason = null;
            if (own.npcInitialThrust > 0f && Thrust(ship) < own.npcInitialThrust * 0.3f)
            {
                reason = "thrust below 30% of initial";
                return true;
            }
            if (own.npcInitialFirepower > 0f
                && universe.Weapons.EstimateFirepower(ship, 50f, 20f)
                    < own.npcInitialFirepower * 0.3f)
            {
                reason = "firepower below 30% of initial";
                return true;
            }
            if (own.npcInitialMainConsoleId > 0
                && ship.console.parent.thingIDNumber != own.npcInitialMainConsoleId)
            {
                reason = "backup console succeeded main console";
                return true;
            }
            return false;
        }

        private SpaceObject NearestHome(SpaceObject own, Faction faction)
        {
            return universe.objects.Where(o => o != own && faction != null && o.faction == faction
                    && (o.contentTypeId == "TradeStation" || o.contentTypeId == "Shipyard"
                        || o.contentTypeId == "FactoryStation"))
                .OrderBy(o => OrbitSphere.ArcCells(own.pose.position, o.pose.position))
                .FirstOrDefault();
        }

        private void TickRetreat(Ship ship, SpaceObject own, SpaceObject threat)
        {
            if (Thrust(ship) <= 0f) return;
            SpaceObject home = universe.ObjectById(own.npcHomeObjectId);
            if (home == null && own.npcRetreatOrigin.sqrMagnitude > 0f
                && OrbitSphere.TileFromDirection(own.pose.position)
                    != OrbitSphere.TileFromDirection(own.npcRetreatOrigin))
            {
                universe.Autopilot.Detach(ship, "NPC left sector");
                universe.QueueFrameAction(() =>
                {
                    if (own.map != null) universe.Lifecycle.DestroyPocketMap(own.map);
                    universe.UnregisterObject(own);
                });
                return;
            }
            if (home != null)
            {
                if (OrbitSphere.ArcCells(own.pose.position, home.pose.position) <= 20f)
                {
                    universe.Autopilot.Detach(ship, "NPC reached home");
                    universe.QueueFrameAction(() =>
                    {
                        if (own.map != null) universe.Lifecycle.DestroyPocketMap(own.map);
                        universe.UnregisterObject(own);
                    });
                    return;
                }
                if (!universe.Autopilot.IsActive(ship.ownerId))
                    universe.Autopilot.TrackTo(ship, home, Vector2.zero, 0f,
                        allowVoidThrust: false, playerInitiated: false);
            }
            else if (!universe.Autopilot.IsActive(ship.ownerId))
            {
                Vector2 away = threat != null
                    ? -OrbitSphere.Offset(own.pose.position, threat.pose.position).normalized
                    : own.pose.velocity.normalized;
                if (away.sqrMagnitude < 1e-8f) away = Vector2.right;
                Vector3 waypoint = OrbitSphere.Move(own.pose.position, away * 1500f);
                universe.Autopilot.FlyTo(ship, waypoint, own.pose.angle, null,
                    allowVoidThrust: false, playerInitiated: false);
            }
        }

        private void TryBoard(Ship ship, SpaceObject own, SpaceObject enemy)
        {
            float range = OrbitSphere.ArcCells(own.pose.position, enemy.pose.position);
            if (range > 100f) return;
            Building body = ship.map.listerThings.AllThings.OfType<Building>()
                .FirstOrDefault(b => b.Faction == ShipRegistry.FactionOf(ship)
                    && b.TryGetComp<CompSpaceShuttle>() is { CanBoard: true } comp
                    && comp.Launchable != null && comp.Launchable.CanLaunch()
                    && b.TryGetComp<CompTransporter>()?.innerContainer?.Any(t => t is Pawn) == true);
            if (body == null) return;
            universe.Shuttles.Launch(body);
            if (body.Spawned && universe.FrameOf(body.Map)?.baseTag == SpaceObjectTag.Shuttle)
                universe.Shuttles.EngageIntrusion(body, enemy, playerInitiated: false);
        }
    }
}

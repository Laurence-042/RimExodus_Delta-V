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
        NoShip,
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
        private readonly Dictionary<DriveSubject, NpcShipAiGate> lastGateByShip = new Dictionary<DriveSubject, NpcShipAiGate>();
        private readonly HashSet<int> noMainReportedObjects = new HashSet<int>();
        private readonly List<SpaceObject> objectBuffer = new List<SpaceObject>();
        private int lastDecisionTick = -1;
        private int lastCleanupTick = -1;

        public NpcShipAiService(UniverseManager universe) { this.universe = universe; }

        internal const float ShipyardArrivalRadius = 20f;

        // 有图时以当前船的派系为准；无图时沿用归档前记录的撤退派系。
        internal SpaceObject ResolveRetreatHome(SpaceObject own, Faction faction = null)
        {
            if (faction != null) own.npcFaction = faction;
            faction = own.npcFaction;
            bool Valid(SpaceObject candidate)
            {
                if (candidate == null || candidate == own || faction == null
                    || candidate.contentTypeId != "Shipyard") return false;
                return ShipyardFaction(candidate) == faction;
            }
            SpaceObject home = universe.ObjectById(own.npcHomeObjectId);
            if (Valid(home)) return home;
            home = universe.objects.Where(Valid)
                .OrderBy(o => OrbitSphere.ArcCells(own.pose.position, o.pose.position))
                .ThenBy(o => o.objectId).FirstOrDefault();
            own.npcHomeObjectId = home?.objectId ?? 0;
            return home;
        }

        internal static Faction ShipyardFaction(SpaceObject candidate)
        {
            if (candidate == null || candidate.map?.Disposed == true) return null;
            // marker 可读取环境归属；已物化或归档对象必须仍承载同派系船体。
            if (candidate.map == null && !candidate.IsArchived) return candidate.faction;
            if (!candidate.HasTag(SpaceObjectTag.Ship)) return null;
            List<Faction> members = candidate.Factions.Distinct().ToList();
            return members.Count == 1 ? members[0] : null;
        }

        internal void NotifyMaterialized(SpaceObject marker, SpaceObject container)
        {
            ShipBase initial = container.Ships
                .FirstOrDefault(s => s.Faction != Faction.OfPlayer) ?? ShipBase.Invalid;
            if (initial.IsValid) RecordBaseline(initial, container);
            if (marker.contentTypeId != "Shipyard") return;
            foreach (SpaceObject obj in universe.objects)
                if (obj.npcHomeObjectId == marker.objectId)
                    obj.npcHomeObjectId = container.objectId;
        }

        internal static Vector3 RetreatWaypoint(SpaceObject own, SpaceObject threat = null)
        {
            Vector2 away = threat != null
                ? -OrbitSphere.Offset(own.pose.position, threat.pose.position).normalized
                : own.pose.velocity.normalized;
            if (away.sqrMagnitude < 1e-8f) away = Vector2.right;
            return OrbitSphere.Move(own.pose.position, away * 1500f);
        }

        private readonly HashSet<int> retirementQueued = new HashSet<int>();

        private void RecordBaseline(ShipBase ship, SpaceObject own)
        {
            if (own.npcBaselineRecorded || !ship.IsValid) return;
            own.npcInitialThrust = (universe.FrameOf(ship.Map)?.MotionInfo.AvailableThrust ?? 0f);
            own.npcInitialFirepower = universe.Weapons.EstimateFirepower(ship, 50f, 20f);
            own.npcInitialMainConsoleId = ship.Anchor.thingIDNumber;
            own.npcBaselineRecorded = true;
        }

        internal bool EvaluateRetreat(ShipBase ship, SpaceObject own, out string reason)
        {
            RecordBaseline(ship, own);
            return EvaluateRetreat(own, (universe.FrameOf(ship.Map)?.MotionInfo.AvailableThrust ?? 0f),
                universe.Weapons.EstimateFirepower(ship, 50f, 20f),
                ship.Anchor.thingIDNumber, out reason);
        }

        private bool EvaluateRetreat(SpaceObject own, float thrust, float firepower,
            int mainConsoleId, out string reason)
        {
            reason = null;
            if (mainConsoleId == 0) { own.npcRetreating = false; own.npcHomeObjectId = 0; return false; }
            if (own.npcInitialThrust > 0f && thrust < own.npcInitialThrust * 0.3f)
                reason = "thrust below 30% of initial";
            else if (own.npcInitialFirepower > 0f && firepower < own.npcInitialFirepower * 0.3f)
                reason = "firepower below 30% of initial";
            else if (own.npcInitialMainConsoleId > 0
                && mainConsoleId != own.npcInitialMainConsoleId)
                reason = "backup console succeeded main console";
            // 有图时始终重新读取真实战损。保存的结果只供无图阶段使用，不是永久撤退锁。
            own.npcRetreating = reason != null;
            if (!own.npcRetreating)
            {
                own.npcHomeObjectId = 0;
            }
            return own.npcRetreating;
        }

        internal void PrepareForArchive(SpaceObject obj, ArchivedSpaceObject archive)
        {
            // 只观测，不修改撤退、母港或导航任务。决策留给正常 AI 周期。
            archive.npcFaction = ShipyardFaction(obj);
            ShipBase ship = obj.Ships
                .FirstOrDefault(s => s.Faction != null
                    && s.Faction != Faction.OfPlayer) ?? ShipBase.Invalid;
            archive.observedThrust = ship.IsValid ? (universe.FrameOf(ship.Map)?.MotionInfo.AvailableThrust ?? 0f) : 0f;
            archive.observedFirepower = ship.IsValid ? universe.Weapons.EstimateFirepower(ship, 50f, 20f) : 0f;
            archive.observedMainConsoleId = ship.IsValid ? ship.Anchor.thingIDNumber : 0;
        }

        // 上次下令的目标只用于避免反复重建导航任务；无图/恢复/读档均可丢弃。
        private readonly Dictionary<int, int> navigationHomes = new Dictionary<int, int>();

        private void DecideArchivedRoute(ArchivedSpaceObject obj)
        {
            if (!EvaluateRetreat(obj, obj.observedThrust, obj.observedFirepower,
                obj.observedMainConsoleId, out _))
            {
                universe.Autopilot.Detach(obj);
                navigationHomes.Remove(obj.objectId);
                return;
            }
            SpaceObject home = ResolveRetreatHome(obj);
            if (obj.observedThrust <= 0f || !obj.TryGetDrive(obj.PrimaryDriver, out _))
            {
                universe.Autopilot.Detach(obj);
                navigationHomes.Remove(obj.objectId);
                return;
            }
            int homeId = home?.objectId ?? 0;
            if (universe.Autopilot.IsActive(obj.PrimaryDriver)
                && navigationHomes.TryGetValue(obj.objectId, out int previous) && previous == homeId) return;
            navigationHomes[obj.objectId] = homeId;
            if (home != null)
                universe.Autopilot.TrackTo(obj.PrimaryDriver, home, Vector2.zero,
                    SpaceTransform.WrapAngle(obj.pose.angle - home.pose.angle), allowVoidThrust: false, playerInitiated: false);
            else
                universe.Autopilot.FlyTo(obj.PrimaryDriver, RetreatWaypoint(obj), obj.pose.angle, allowVoidThrust: false, playerInitiated: false);
        }

        internal void NotifyArchivedMovement(ArchivedSpaceObject archived)
        {
            int id = archived.objectId, version = archived.RepresentationVersion;
            if (!archived.npcRetreating || archived.npcHomeObjectId == 0) return;
            SpaceObject home = universe.ObjectById(archived.npcHomeObjectId);
            if (home == null || OrbitSphere.ArcCells(archived.pose.position, home.pose.position)
                > ShipyardArrivalRadius || !retirementQueued.Add(archived.objectId)) return;
            universe.QueueFrameAction(() =>
            {
                var obj = universe.ObjectById(id) as ArchivedSpaceObject;
                try
                {
                    if (obj == null || obj.RepresentationVersion != version || obj.map != null
                        || !obj.npcRetreating || !obj.IsArchived) return;
                    if (!EvaluateRetreat(obj, obj.observedThrust, obj.observedFirepower,
                        obj.observedMainConsoleId, out _)) return;
                    SpaceObject currentHome = ResolveRetreatHome(obj);
                    if (currentHome == null || OrbitSphere.ArcCells(obj.pose.position, currentHome.pose.position)
                        > ShipyardArrivalRadius) return;
                    // AI 决定返厂退役；生命周期仅执行通用移除，恢复请求与玩家保护优先。
                    if (universe.Lifecycle.TryRemoveArchivedContainer(obj)
                        && DeltaVLog.Enabled(DeltaVLogModule.Combat))
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"NPC container #{obj.objectId} retired at shipyard #{currentHome.objectId}; crew passed to world");
                }
                catch (Exception ex)
                {
                    Log.Error($"[DeltaV] NPC retirement failed for #{id}: {ex}");
                }
                finally { retirementQueued.Remove(id); }
            });
        }

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
                if (obj == null) continue;
                if (obj.map == null)
                {
                    if (obj is ArchivedSpaceObject archive) DecideArchivedRoute(archive);
                    continue;
                }
                if (obj.map.Disposed) continue;
                navigationHomes.Remove(obj.objectId);
                if (obj.HasTag(SpaceObjectTag.Ship) != true)
                {
                    LogUnclaimedConsoleEdge(obj);
                    continue;
                }
                noMainReportedObjects.Remove(obj.objectId);
                foreach (ShipBase ship in obj.Ships)
                {
                    Faction faction = ship.Faction;
                    if (faction == null || faction == Faction.OfPlayer) continue;
                    NpcShipController controller = universe.Controls.NpcFor(ship);
                    NpcShipAiGate gate = GateFor(obj, ship, controller);
                    LogGateEdge(ship, obj, gate);
                    if (gate != NpcShipAiGate.Ready)
                    {
                        controller?.SetCombatReady(false);
                        continue;
                    }
                    if (!controller.Active || controller.target.Identity != ship.Identity)
                        controller.Enter(PilotTarget.Of(ship));
                    controller.Ai.Tick(controller);
                }
            }
            int now = Find.TickManager.TicksGame;
            if (lastCleanupTick < 0 || now - lastCleanupTick >= 600)
            {
                lastCleanupTick = now;
                foreach (DriveSubject id in lastGateByShip.Keys.ToList())
                    if (!ShipRegistry.ShipByIdentity(id).IsValid)
                    {
                        lastGateByShip.Remove(id);
                    }
                noMainReportedObjects.RemoveWhere(id => universe.ObjectById(id) == null);
                foreach (int id in navigationHomes.Keys.ToList())
                    if (universe.ObjectById(id) == null) navigationHomes.Remove(id);
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
                + $"AI gate {NpcShipAiGate.NoShip}; {UniverseManager.MapTag(obj.map)}");
        }

        internal bool HasUnclaimedNpcConsole(SpaceObject obj)
        {
            return obj?.map != null && !obj.map.Disposed && obj.HasTag(SpaceObjectTag.Ship) != true
                && obj.map.listerThings.ThingsOfDef(ThingDefOf.PilotConsole)
                    .Any(thing => thing.Faction != null && thing.Faction != Faction.OfPlayer);
        }

        internal NpcShipAiGate GateFor(SpaceObject obj, ShipBase ship)
        {
            NpcShipController controller = ship.IsValid ? universe.Controls.NpcFor(ship) : null;
            return GateFor(obj, ship, controller);
        }

        private NpcShipAiGate GateFor(SpaceObject obj, ShipBase ship, NpcShipController controller)
        {
            if (obj?.map == null || obj.map.Disposed) return NpcShipAiGate.NoMap;
            if (!ship.IsValid || ship.Map != obj.map) return NpcShipAiGate.NoShip;
            Faction faction = ship.Faction;
            if (faction == null || faction == Faction.OfPlayer) return NpcShipAiGate.NoNpcFaction;
            if (controller == null) return NpcShipAiGate.NoControllerAuthority;
            if (!ship.CanDriveIn(obj)) return NpcShipAiGate.InvalidPilotTarget;
            if (obj.projectileRoleOverride || universe.Approach.FlowTarget.Identity == ship.Identity)
                return NpcShipAiGate.ApproachConflict;
            if (obj.HasTag(SpaceObjectTag.Ship) != true) return NpcShipAiGate.NotShipContainer;
            if (universe.Lifecycle.IsFrozen(obj.map))
                return NpcShipAiGate.FrozenMap;
            if (universe.Lifecycle.MinPlayerDistance(obj)
                > DeltaVMod.CurrentSettings.poiActiveRadius)
                return NpcShipAiGate.OutsideActiveRadius;
            if (controller != null && (!controller.Active
                    || controller.target.Identity != ship.Identity))
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

        private void LogGateEdge(ShipBase ship, SpaceObject obj, NpcShipAiGate gate)
        {
            if (!DeltaVLog.Enabled(DeltaVLogModule.Combat)) return;
            if (lastGateByShip.TryGetValue(ship.Identity, out NpcShipAiGate previous)
                && previous == gate) return;
            lastGateByShip[ship.Identity] = gate;
            DeltaVLog.Message(DeltaVLogModule.Combat,
                $"NPC ship #{ship.Identity} {UniverseManager.MapTag(obj.map)} "
                + $"AI gate {gate}; relation={ship.State} "
                + $"faction={ship.Faction?.Name ?? "none"} "
                + $"playerDist={universe.Lifecycle.MinPlayerDistance(obj):F0}");
        }
    }

    internal sealed class NpcCombatAi : INpcShipAi
    {
        private readonly UniverseManager universe;
        private readonly DriveSubject ownerId;
        private int nextBoardTick;
        private bool noThrustObserved;
        private bool lastNoThrust;
        private int retreatHomeId;
        // AI 持有已下达的防守目标；决定何时改令，不修改导航任务内部状态。
        private Vector2 guardOffset;
        private float guardAngle;
        private const float GuardRetargetDistance = 1f;
        private const float GuardRetargetAngle = 5f * Mathf.Deg2Rad;

        public NpcCombatAi(UniverseManager universe, DriveSubject ownerId)
        {
            this.universe = universe;
            this.ownerId = ownerId;
        }

        public bool HasAuthority(ShipBase ship) => ship.IsValid && ship.Identity == ownerId
            && ship.Faction != null
            && ship.Faction != Faction.OfPlayer;

        public void ComposeCommand(ShipBase ship, ref PilotService.PilotCommand command) { }

        public void Tick(NpcShipController controller)
        {
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (!HasAuthority(ship)) return;
            SpaceObject own = universe.FrameOf(ship.Map);
            if (own == null) return;
            int now = Find.TickManager.TicksGame;
            controller.AutoFire.Decide(ship, own);
            SpaceObject target = controller.AutoFire.Target;
            bool wasRetreating = own.npcRetreating;
            if (universe.NpcShips.EvaluateRetreat(ship, own, out string retreatReason))
            {
                if (!wasRetreating)
                {
                    SpaceObject home = universe.NpcShips.ResolveRetreatHome(own, ship.Faction);
                    universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC retreat");
                    if (DeltaVLog.Enabled(DeltaVLogModule.Combat))
                        DeltaVLog.Message(DeltaVLogModule.Combat,
                            $"NPC ship #{ownerId} retreat triggered ({retreatReason}); home="
                            + (home == null ? "evade without home" : $"#{home.objectId} '{home.label}'")
                            + $"; {UniverseManager.MapTag(ship.Map)}");
                }
                LogThrustEdge(ship);
                TickRetreat(controller, ship, own, target);
                return;
            }
            if (wasRetreating)
            {
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC combat capability recovered");
                retreatHomeId = 0;
                guardOffset = Vector2.zero;
            }
            LogThrustEdge(ship);
            if (target == null)
            {
                controller.SetCombatReady(false);
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC target lost");
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

        internal static int CountEligibleTargets(UniverseManager universe, ShipBase ship, SpaceObject own)
        {
            int count = 0;
            foreach (SpaceObject candidate in universe.objects)
                if (ShipAutoFireControl.CandidateValid(ship, own, candidate)) count++;
            return count;
        }


        private float Thrust(ShipBase ship)
        {
            return (universe.FrameOf(ship.Map)?.MotionInfo.AvailableThrust ?? 0f);
        }

        private void LogThrustEdge(ShipBase ship)
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
                    + $"{UniverseManager.MapTag(ship.Map)}");
            else if (wasObserved)
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"NPC ship #{ownerId} operational map thrust restored; "
                    + $"{UniverseManager.MapTag(ship.Map)}");
        }

        private void TickRetreat(NpcShipController controller, ShipBase ship, SpaceObject own, SpaceObject threat)
        {
            SpaceObject home = universe.NpcShips.ResolveRetreatHome(own, ship.Faction);
            int homeId = home?.objectId ?? 0;
            bool changedHome = retreatHomeId != homeId;
            if (changedHome)
            {
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC retreat destination changed");
                retreatHomeId = homeId;
                guardOffset = Vector2.zero;
            }
            float radius = home == null ? 0f : SpaceCollision.OriginBoundsRadiusOf(home)
                + SpaceCollision.OriginBoundsRadiusOf(own) + NpcShipAiService.ShipyardArrivalRadius;
            bool defending = home?.map != null
                && OrbitSphere.ArcCells(own.pose.position, home.pose.position) <= radius + 20f;
            // 火控不以推进能力为门禁；保守站位从不进入环绕追击或跳帮。
            controller.AutoManeuver.Clear(ship);
            controller.SetCombatReady(defending && threat != null);
            if (Thrust(ship) <= 0f)
            {
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC retreat has no thrust");
                return;
            }
            if (home != null)
            {
                Vector2 desiredOffset = guardOffset;
                float desiredAngle = guardAngle;
                if (guardOffset.sqrMagnitude < 1e-8f || threat != null)
                {
                    Vector2 direction = threat != null
                        ? -SpaceTransform.UniverseToShipLocal(home, threat.pose.position).normalized
                        : SpaceTransform.UniverseToShipLocal(home, own.pose.position).normalized;
                    if (direction.sqrMagnitude < 1e-8f) direction = Vector2.right;
                    desiredOffset = direction * radius;
                    desiredAngle = SpaceTransform.WrapAngle(own.pose.angle - home.pose.angle);
                }
                desiredOffset = desiredOffset.normalized * radius;
                if (threat != null)
                {
                    Vector2 toThreat = OrbitSphere.Offset(own.pose.position, threat.pose.position);
                    desiredAngle = SpaceTransform.WrapAngle(Mathf.Atan2(toThreat.y, toThreat.x)
                        - PilotTarget.Of(ship).FrameAngle - home.pose.angle);
                }
                // 船厂自身移动由既有随动任务处理；仅当 AI 的站位意图明显变化时改令。
                if (!universe.Autopilot.IsActive(DriveSubject.Of(ship))
                    || (desiredOffset - guardOffset).sqrMagnitude >= GuardRetargetDistance * GuardRetargetDistance
                    || Mathf.Abs(SpaceTransform.WrapAngle(desiredAngle - guardAngle)) >= GuardRetargetAngle)
                {
                    guardOffset = desiredOffset;
                    guardAngle = desiredAngle;
                    universe.Autopilot.TrackTo(DriveSubject.Of(ship), home, guardOffset, guardAngle,
                        allowVoidThrust: false, playerInitiated: false);
                }
            }
            else if (!universe.Autopilot.IsActive(DriveSubject.Of(ship)))
            {
                Vector3 waypoint = NpcShipAiService.RetreatWaypoint(own, threat);
                universe.Autopilot.FlyTo(DriveSubject.Of(ship), waypoint, own.pose.angle, null,
                    allowVoidThrust: false, playerInitiated: false);
            }
        }

        private void TryBoard(ShipBase ship, SpaceObject own, SpaceObject enemy)
        {
            float range = OrbitSphere.ArcCells(own.pose.position, enemy.pose.position);
            if (range > 100f) return;
            universe.Shuttles.TryBoardFrom(ship, enemy);
        }
    }
}

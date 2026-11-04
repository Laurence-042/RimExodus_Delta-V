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

        internal static Vector3 RetreatWaypoint(SpaceObject own, SpacePose? threat = null)
        {
            Vector2 away = threat != null
                ? -OrbitSphere.Offset(own.pose.position, threat.Value.position).normalized
                : own.pose.velocity.normalized;
            if (away.sqrMagnitude < 1e-8f) away = Vector2.right;
            return OrbitSphere.Move(own.pose.position, away * 1500f);
        }

        private readonly HashSet<int> retirementQueued = new HashSet<int>();

        private void RecordBaseline(ShipBase ship, SpaceObject own)
        {
            if (!ship.IsValid || ship.CombatAI == null) return;
            own.npcFaction = ship.Faction;
            own.npcHadShip = true;
            float thrust = ShipThrust(ship);
            float firepower = universe.Weapons.EstimateFirepower(ship, 50f, 20f);
            List<CompShipCombatAI> consoles = ship.Map.listerThings.AllThings
                .Where(t => ship.Contains(t) && (t == ship.Anchor
                    || t.TryGetComp<CompDeltaVPilotConsole>() != null))
                .OfType<ThingWithComps>()
                .Select(t => t.TryGetComp<CompShipCombatAI>())
                .Where(c => c != null).ToList();
            foreach (CompShipCombatAI comp in consoles) comp.EnsureBaselineFor(ship);
            CompShipCombatAI established = consoles.FirstOrDefault(c => c.initialMainConsoleId > 0);
            foreach (CompShipCombatAI comp in consoles)
            {
                if (comp.initialMainConsoleId > 0) continue;
                comp.initialThrust = established?.initialThrust ?? thrust;
                comp.initialFirepower = established?.initialFirepower ?? firepower;
                comp.initialMainConsoleId = established?.initialMainConsoleId ?? ship.Anchor.thingIDNumber;
            }
        }

        internal bool EvaluateRetreat(ShipBase ship, SpaceObject own, out string reason)
        {
            RecordBaseline(ship, own);
            CompShipCombatAI comp = ship.CombatAI;
            if (comp == null) { reason = null; return false; }
            bool result = EvaluateRetreat(comp.initialThrust, comp.initialFirepower,
                comp.initialMainConsoleId, ShipThrust(ship),
                universe.Weapons.EstimateFirepower(ship, 50f, 20f),
                ship.Anchor.thingIDNumber, out reason);
            if (!result && comp.strategy.npcRetreatConditions.Count > 0)
            {
                var facts = new CombatStrategyFacts(universe, ship, own);
                bool matched = comp.strategy.npcRetreatMatchAny
                    ? comp.strategy.npcRetreatConditions.Any(c => c?.Matches(facts.Context()) == true)
                    : comp.strategy.npcRetreatConditions.All(c => c?.Matches(facts.Context()) == true);
                if (matched)
                { result = true; reason = "strategy retreat condition"; }
            }
            comp.retreating = result;
            own.npcRetreating = own.Ships.Any(s => s.CombatAI?.retreating == true);
            if (!own.npcRetreating) own.npcHomeObjectId = 0;
            return result;
        }

        private static bool EvaluateRetreat(float initialThrust, float initialFirepower,
            int initialMain, float thrust, float firepower, int main, out string reason)
        {
            reason = null;
            if (main == 0) return false;
            if (initialThrust > 0f && thrust < initialThrust * 0.3f)
                reason = "thrust below 30% of initial";
            else if (initialFirepower > 0f && firepower < initialFirepower * 0.3f)
                reason = "firepower below 30% of initial";
            else if (initialMain > 0 && main != initialMain)
                reason = "backup console succeeded main console";
            return reason != null;
        }

        private static bool EvaluateRetreat(ArchivedSpaceObject own, out string reason)
        {
            reason = null;
            foreach (NpcShipCombatSummary member in own.npcShips)
            {
                bool floor = EvaluateRetreat(member.initialThrust, member.initialFirepower,
                    member.initialMainConsoleId, member.observedThrust,
                    member.observedFirepower, member.mainConsoleId, out string memberReason);
                member.retreating |= floor;
                if (floor && reason == null) reason = memberReason;
            }
            own.npcRetreating = own.npcShips.Any(s => s.retreating);
            if (!own.npcRetreating) own.npcHomeObjectId = 0;
            return own.npcRetreating;
        }

        internal void PrepareForArchive(SpaceObject obj, ArchivedSpaceObject archive)
        {
            archive.npcFaction = ShipyardFaction(obj);
            archive.npcShips.Clear();
            foreach (ShipBase ship in obj.Ships.Where(s =>
                s.Faction != null && s.Faction != Faction.OfPlayer
                && s.CanDriveIn(obj)))
            {
                RecordBaseline(ship, obj);
                CompShipCombatAI comp = ship.CombatAI;
                archive.npcShips.Add(new NpcShipCombatSummary
                {
                    shipId = ship.Identity.key,
                    mainConsoleId = ship.Anchor.thingIDNumber,
                    initialMainConsoleId = comp?.initialMainConsoleId ?? 0,
                    initialThrust = comp?.initialThrust ?? 0f,
                    initialFirepower = comp?.initialFirepower ?? 0f,
                    observedThrust = ShipThrust(ship),
                    observedFirepower = universe.Weapons.EstimateFirepower(ship, 50f, 20f),
                    retreating = comp?.retreating ?? false
                });
            }
            NpcShipCombatSummary primary = archive.npcShips.FirstOrDefault(s =>
                s.shipId == archive.PrimaryDriver.key) ?? archive.npcShips.FirstOrDefault();
            archive.observedThrust = primary?.observedThrust ?? 0f;
            archive.observedFirepower = primary?.observedFirepower ?? 0f;
            archive.observedMainConsoleId = primary?.mainConsoleId ?? 0;
        }

        // 上次下令的目标只用于避免反复重建导航任务；无图/恢复/读档均可丢弃。
        private readonly Dictionary<int, int> navigationHomes = new Dictionary<int, int>();

        internal static float ShipThrust(ShipBase ship) => ship.BuildThrusters()
            .Where(t => t.operational).Sum(t => t.thrust);

        private void DecideArchivedRoute(ArchivedSpaceObject obj)
        {
            if (!EvaluateRetreat(obj, out _))
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
                    if (!EvaluateRetreat(obj, out _)) return;
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
            && !ship.PlayerMayOperate;

        public void ComposeCommand(ShipBase ship, ref PilotService.PilotCommand command) { }

        public void Tick(NpcShipController controller)
        {
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (!HasAuthority(ship)) return;
            SpaceObject own = universe.FrameOf(ship.Map);
            if (own == null) return;
            int now = Find.TickManager.TicksGame;
            controller.FireControl.Decide(ship, own);
            CombatTarget target = controller.FireControl.Target;
            bool wasRetreating = ship.CombatAI?.retreating == true;
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
                TickRetreat(controller, ship, own, target.TryResolve(universe, out var threat) ? threat.Pose : (SpacePose?)null);
                return;
            }
            if (wasRetreating)
            {
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC combat capability recovered");
                retreatHomeId = 0;
                guardOffset = Vector2.zero;
            }
            LogThrustEdge(ship);
            if (!target.HasLock)
            {
                controller.SetCombatReady(false);
                universe.Autopilot.Detach(DriveSubject.Of(ship), "NPC target lost");
                return;
            }
            controller.SetCombatReady(true);
            controller.AutoManeuver.Decide(ship, own, target,
                allowBoarding: true, playerInitiated: false);
            if (controller.AutoManeuver.Boarding && now >= nextBoardTick)
            {
                nextBoardTick = now + 600;
                TryBoard(ship, own, target);
            }
        }

        internal static int CountEligibleTargets(UniverseManager universe, ShipBase ship, SpaceObject own)
        {
            int count = 0;
            foreach (SpaceObject candidate in universe.objects)
                count += ShipFireControl.Candidates(ship, own, candidate).Count();
            return count;
        }


        private float Thrust(ShipBase ship)
        {
            return NpcShipAiService.ShipThrust(ship);
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

        private void TickRetreat(NpcShipController controller, ShipBase ship, SpaceObject own, SpacePose? threat)
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
                        ? -SpaceTransform.UniverseToShipLocal(home, threat.Value.position).normalized
                        : SpaceTransform.UniverseToShipLocal(home, own.pose.position).normalized;
                    if (direction.sqrMagnitude < 1e-8f) direction = Vector2.right;
                    desiredOffset = direction * radius;
                    desiredAngle = SpaceTransform.WrapAngle(own.pose.angle - home.pose.angle);
                }
                desiredOffset = desiredOffset.normalized * radius;
                if (threat != null)
                {
                    Vector2 toThreat = OrbitSphere.Offset(own.pose.position, threat.Value.position);
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

        private void TryBoard(ShipBase ship, SpaceObject own, CombatTarget enemy)
        {
            if (!enemy.TryResolve(universe, out var resolved)) return;
            float range = OrbitSphere.ArcCells(ShipRegistry.ShipPose(ship).position, resolved.Pose.position);
            if (range > 100f) return;
            universe.Shuttles.TryBoardFrom(ship, enemy);
        }
    }
}

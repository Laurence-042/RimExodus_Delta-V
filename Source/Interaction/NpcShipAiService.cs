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
        private SpaceObject target;
        private Thing preferredSystem;
        private NpcDoctrine doctrine;
        private int targetLockedUntil;
        private int lastDoctrineTick = -1;
        private int nextBoardTick;
        private bool targetObserved;
        private bool noThrustObserved;
        private bool lastNoThrust;
        private bool orbitTaskObserved;
        private float lastOrbitRadius;
        private NpcDoctrine lastOrbitDoctrine;
        private SpaceObject lastOrbitTarget;
        private int lastOrbitLogTick = int.MinValue;

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
            // 全对象目标评分只发生在外层设置周期的决策扫描中。
            SpaceObject selected = SelectTarget(ship, own);
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat)
                && (!targetObserved || selected != target))
                DeltaVLog.Message(DeltaVLogModule.Combat,
                    $"NPC ship #{ownerId} target "
                    + (selected == null ? "none (no loaded hostile in active radius)"
                        : $"#{selected.objectId} '{selected.label}'")
                    + $"; {UniverseManager.MapTag(ship.map)}");
            targetObserved = true;
            target = selected;
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
                TickRetreat(ship, own);
                return;
            }
            LogThrustEdge(ship);
            if (target == null)
            {
                controller.SetCombatReady(false);
                preferredSystem = null;
                orbitTaskObserved = false;
                universe.Autopilot.Detach(ship, "NPC target lost");
                return;
            }
            controller.SetCombatReady(true);
            int doctrineInterval = 2 * Mathf.Clamp(
                DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
            if (lastDoctrineTick < 0 || now - lastDoctrineTick >= doctrineInterval
                || !universe.Autopilot.IsActive(ship.ownerId))
            {
                lastDoctrineTick = now;
                PickDoctrine(ship, own, target, out doctrine, out float radius);
                // 同图多船或天然地面上的船也要先提出机动请求；首次真实出力由
                // RequestThrust 统一分离门禁处理，不能在任务创建前用派生状态拦掉。
                bool taskChanged = !orbitTaskObserved || lastOrbitTarget != target
                    || lastOrbitDoctrine != doctrine || Mathf.Abs(lastOrbitRadius - radius) > 0.5f
                    || !universe.Autopilot.IsActive(ship.ownerId);
                universe.Autopilot.TrackOrbit(ship, target, radius);
                if (taskChanged && DeltaVLog.Enabled(DeltaVLogModule.Combat)
                    && (lastOrbitLogTick == int.MinValue || now - lastOrbitLogTick >= 60))
                {
                    DeltaVLog.Message(DeltaVLogModule.Combat,
                        $"NPC ship #{ownerId} orbit task: target=#{target.objectId} "
                        + $"doctrine={doctrine} radius={radius:F1} "
                        + $"mapRelation={ShipRegistry.StateOf(ship)}; {UniverseManager.MapTag(ship.map)}");
                    lastOrbitLogTick = now;
                }
                orbitTaskObserved = true;
                lastOrbitTarget = target;
                lastOrbitDoctrine = doctrine;
                lastOrbitRadius = radius;
            }
            if (doctrine == NpcDoctrine.Boarder && now >= nextBoardTick)
            {
                nextBoardTick = now + 600;
                TryBoard(ship, own, target);
            }
            preferredSystem = doctrine == NpcDoctrine.Boarder ? SelectSystemTarget(target) : null;
        }

        public bool TryGetVolleyTarget(Ship ship, SpaceObject own,
            out SpaceObject victim, out Thing system, out bool suppressSystems)
        {
            victim = null;
            system = null;
            suppressSystems = false;
            if (!HasAuthority(ship) || target == null) return false;
            if (own == null || own.npcRetreating || universe.Lifecycle.IsFrozen(ship.map)
                || !CandidateValid(ship, own, target)) return false;
            victim = target;
            suppressSystems = doctrine == NpcDoctrine.Boarder;
            if (suppressSystems && preferredSystem != null
                && (!preferredSystem.Spawned || preferredSystem.Map != target.map))
                preferredSystem = SelectSystemTarget(target);
            system = preferredSystem != null && preferredSystem.Spawned
                && preferredSystem.Map == target.map ? preferredSystem : null;
            return true;
        }

        public void ClearCombatTarget()
        {
            target = null;
            preferredSystem = null;
        }

        private static Thing SelectSystemTarget(SpaceObject target)
        {
            if (target?.map == null) return null;
            Thing pointDefense = SpaceWeaponService.AllWeaponsOn(target.map)
                .Where(w => w.aimMode == SpaceWeaponAimMode.PointDefense
                    && w.parent.Spawned && !w.parent.Destroyed)
                .Select(w => w.parent).FirstOrDefault();
            return pointDefense ?? ShipPhysics.GetReport(target.map)?.thrusters
                .FirstOrDefault(t => t.operational && t.thing != null)?.thing;
        }

        internal static bool CandidateValid(Ship ship, SpaceObject own, SpaceObject candidate)
        {
            return ship.IsValid && own != null && candidate != null && candidate != own && candidate.map != null
                && !candidate.map.Disposed && candidate.baseTag != SpaceObjectTag.Projectile
                && candidate.baseTag != SpaceObjectTag.Shuttle
                && SpaceWeaponService.HostileTo(candidate, ShipRegistry.FactionOf(ship))
                && OrbitSphere.ArcCells(own.pose.position, candidate.pose.position)
                    <= DeltaVMod.CurrentSettings.poiActiveRadius;
        }

        internal static int CountEligibleTargets(UniverseManager universe, Ship ship, SpaceObject own)
        {
            int count = 0;
            foreach (SpaceObject candidate in universe.objects)
                if (CandidateValid(ship, own, candidate)) count++;
            return count;
        }

        private SpaceObject SelectTarget(Ship ship, SpaceObject own)
        {
            float best = float.NegativeInfinity;
            SpaceObject selected = null;
            foreach (SpaceObject candidate in universe.objects)
            {
                if (!CandidateValid(ship, own, candidate)) continue;
                float distance = OrbitSphere.ArcCells(own.pose.position, candidate.pose.position);
                Ship enemy = ShipRegistry.ShipsOnMap(candidate.map).FirstOrDefault();
                float enemyFire = universe.Weapons.EstimateFirepower(enemy, distance,
                    SpaceCollision.BoundsRadiusOf(own));
                float survivability = candidate.map.listerThings.AllThings
                    .OfType<Building>().Where(b => b.Spawned).Sum(b => (float)b.HitPoints);
                float ownFire = universe.Weapons.EstimateFirepower(ship, distance,
                    SpaceCollision.BoundsRadiusOf(candidate));
                float killability = ownFire / (1f + survivability / 1000f);
                float score = enemyFire * 2f + killability
                    - distance * 0.1f + (candidate == target ? 10f : 0f);
                if (candidate == target && Find.TickManager.TicksGame < targetLockedUntil)
                    score += 100000f;
                if (score <= best) continue;
                best = score;
                selected = candidate;
            }
            if (selected != target) targetLockedUntil = Find.TickManager.TicksGame + 180;
            return selected;
        }

        private void PickDoctrine(Ship ship, SpaceObject own, SpaceObject enemy,
            out NpcDoctrine selected, out float distance)
        {
            List<NpcDoctrine> priorities = own.npcDoctrines;
            if (priorities == null || priorities.Count == 0) priorities = NpcDoctrineDefaults.Create();
            Ship hostile = ShipRegistry.ShipsOnMap(enemy.map).FirstOrDefault();
            float clearance = SpaceCollision.BoundsRadiusOf(own)
                + SpaceCollision.BoundsRadiusOf(enemy) + 10f;
            float maxRange = SpaceWeaponService.WeaponsOn(ship.map, ship.ownerId)
                .Where(w => w.aimMode == SpaceWeaponAimMode.Volley)
                .Select(w => w.WeaponProps.range).DefaultIfEmpty(clearance + 30f).Max();
            float[] samples = { clearance, Mathf.Max(clearance, maxRange * 0.45f),
                Mathf.Max(clearance, maxRange * 0.7f), Mathf.Max(clearance, maxRange * 0.9f) };
            selected = NpcDoctrine.Standoff;
            distance = samples[0];
            float best = float.NegativeInfinity;
            for (int rank = 0; rank < priorities.Count; rank++)
            {
                NpcDoctrine option = priorities[rank];
                if (option == NpcDoctrine.Retreat) continue;
                for (int i = 0; i < samples.Length; i++)
                {
                    float at = samples[i];
                    float ownPower = universe.Weapons.EstimateFirepower(ship, at,
                        SpaceCollision.BoundsRadiusOf(enemy), option == NpcDoctrine.MissileBoat);
                    if (option == NpcDoctrine.MissileBoat && ownPower <= 0f) continue;
                    if (option == NpcDoctrine.MissileBoat)
                    {
                        float enemyPd = universe.Weapons.EstimatePointDefense(hostile);
                        ownPower *= ownPower / Mathf.Max(ownPower, ownPower + enemyPd);
                    }
                    if (option == NpcDoctrine.Boarder && !HasBoardingShuttle(ship)) continue;
                    float enemyPower = universe.Weapons.EstimateFirepower(hostile, at,
                        SpaceCollision.BoundsRadiusOf(own));
                    float preference = (priorities.Count - rank) * 0.1f
                        * Mathf.Max(1f, ownPower);
                    float posture = option switch
                    {
                        NpcDoctrine.Brawler => -at * 0.02f,
                        NpcDoctrine.Standoff => at * 0.02f,
                        NpcDoctrine.MissileBoat => at * 0.035f,
                        NpcDoctrine.Boarder => -at * 0.015f,
                        _ => 0f
                    };
                    float score = ownPower - enemyPower + preference + posture;
                    if (score <= best) continue;
                    best = score;
                    selected = option;
                    distance = at;
                }
            }
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

        private void TickRetreat(Ship ship, SpaceObject own)
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
                Vector2 away = target != null
                    ? -OrbitSphere.Offset(own.pose.position, target.pose.position).normalized
                    : own.pose.velocity.normalized;
                if (away.sqrMagnitude < 1e-8f) away = Vector2.right;
                Vector3 waypoint = OrbitSphere.Move(own.pose.position, away * 1500f);
                universe.Autopilot.FlyTo(ship, waypoint, own.pose.angle, null,
                    allowVoidThrust: false, playerInitiated: false);
            }
        }

        private static bool HasBoardingShuttle(Ship ship) => ship.map.listerThings.AllThings
            .OfType<Building>().Any(b => b.Faction == ShipRegistry.FactionOf(ship)
                && b.TryGetComp<CompSpaceShuttle>() is { CanBoard: true } comp
                && comp.Launchable != null && comp.Launchable.CanLaunch()
                && b.TryGetComp<CompTransporter>()?.innerContainer?.Any(t => t is Pawn) == true);

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

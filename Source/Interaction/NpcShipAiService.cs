using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>实例级 NPC 战术调度。AI 只选择目标与任务；移动和开火仍走现有服务。</summary>
    public sealed class NpcShipAiService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<int, NpcShipController> controllers = new Dictionary<int, NpcShipController>();
        private readonly List<SpaceObject> objectBuffer = new List<SpaceObject>();

        public NpcShipAiService(UniverseManager universe) { this.universe = universe; }

        public void Tick()
        {
            objectBuffer.Clear();
            objectBuffer.AddRange(universe.objects);
            foreach (SpaceObject obj in objectBuffer)
            {
                if (obj?.map == null || obj.map.Disposed || obj.Tag != SpaceObjectTag.Ship)
                    continue;
                if (universe.Lifecycle.IsFrozen(obj.map) && !obj.npcRetreating) continue;
                if (universe.Lifecycle.MinPlayerDistance(obj)
                    > DeltaVMod.CurrentSettings.poiActiveRadius && !obj.npcRetreating) continue;
                foreach (Ship ship in ShipRegistry.ShipsOnMap(obj.map))
                {
                    Faction faction = ShipRegistry.FactionOf(ship);
                    if (faction == null || faction == Faction.OfPlayer) continue;
                    if (!controllers.TryGetValue(ship.ownerId, out NpcShipController controller))
                    {
                        controller = new NpcShipController(universe);
                        controller.Ai = new NpcCombatAi(universe, ship.ownerId);
                        controllers.Add(ship.ownerId, controller);
                    }
                    if (!controller.Active || controller.target.ControlKey != ship.ownerId)
                    {
                        if (!controller.CanEnter(PilotTarget.Of(ship))) continue;
                        controller.Enter(PilotTarget.Of(ship));
                    }
                    controller.Ai.Tick(controller);
                }
            }
            if (Find.TickManager.TicksGame % 600 == 0)
            {
                foreach (int id in controllers.Keys.ToList())
                    if (!ShipRegistry.ShipByOwner(id).IsValid) controllers.Remove(id);
            }
        }
    }

    internal sealed class NpcCombatAi : INpcShipAi
    {
        private readonly UniverseManager universe;
        private readonly int ownerId;
        private SpaceObject target;
        private NpcDoctrine doctrine;
        private int targetLockedUntil;
        private int nextTargetTick;
        private int nextDoctrineTick;
        private int nextBoardTick;

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
            if (now >= nextTargetTick || !TargetValid(ship, own, target))
            {
                nextTargetTick = now + 60;
                target = SelectTarget(ship, own);
            }
            RecordBaseline(ship, own);
            if (own.npcRetreating || IsDisabled(ship, own))
            {
                if (!own.npcRetreating)
                {
                    own.npcRetreating = true;
                    own.npcRetreatOrigin = own.pose.position;
                    SpaceObject home = NearestHome(own, ShipRegistry.FactionOf(ship));
                    own.npcHomeObjectId = home?.objectId ?? 0;
                    universe.Autopilot.Detach(ship, "NPC retreat");
                }
                TickRetreat(ship, own);
                return;
            }
            if (target == null)
            {
                universe.Autopilot.Detach(ship, "NPC target lost");
                return;
            }
            if (now >= nextDoctrineTick)
            {
                nextDoctrineTick = now + 120;
                PickDoctrine(ship, own, target, out doctrine, out float radius);
                if (ShipRegistry.IsFreeFlight(ship))
                    universe.Autopilot.TrackOrbit(ship, target, radius);
            }
            universe.Weapons.TickNpcVolley(ship, target, doctrine == NpcDoctrine.Boarder);
            if (doctrine == NpcDoctrine.Boarder && now >= nextBoardTick)
            {
                nextBoardTick = now + 600;
                TryBoard(ship, own, target);
            }
        }

        private bool TargetValid(Ship ship, SpaceObject own, SpaceObject candidate)
        {
            return candidate != null && candidate != own && candidate.map != null
                && !candidate.map.Disposed && candidate.baseTag != SpaceObjectTag.Projectile
                && candidate.baseTag != SpaceObjectTag.Shuttle
                && SpaceWeaponService.HostileTo(candidate, ShipRegistry.FactionOf(ship))
                && OrbitSphere.ArcCells(own.pose.position, candidate.pose.position)
                    <= DeltaVMod.CurrentSettings.poiActiveRadius;
        }

        private SpaceObject SelectTarget(Ship ship, SpaceObject own)
        {
            float best = float.NegativeInfinity;
            SpaceObject selected = null;
            foreach (SpaceObject candidate in universe.objects)
            {
                if (!TargetValid(ship, own, candidate)) continue;
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

        private void RecordBaseline(Ship ship, SpaceObject own)
        {
            if (own.npcInitialThrust <= 0f) own.npcInitialThrust = Thrust(ship);
            if (own.npcInitialFirepower <= 0f)
                own.npcInitialFirepower = universe.Weapons.EstimateFirepower(ship, 50f, 20f);
            if (own.npcInitialMainConsoleId <= 0)
                own.npcInitialMainConsoleId = ship.console.parent.thingIDNumber;
        }

        private bool IsDisabled(Ship ship, SpaceObject own)
        {
            if (own.npcInitialThrust > 0f && Thrust(ship) < own.npcInitialThrust * 0.3f)
                return true;
            if (own.npcInitialFirepower > 0f
                && universe.Weapons.EstimateFirepower(ship, 50f, 20f)
                    < own.npcInitialFirepower * 0.3f) return true;
            return own.npcInitialMainConsoleId > 0
                && ship.console.parent.thingIDNumber != own.npcInitialMainConsoleId;
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
            if (!ShipRegistry.IsFreeFlight(ship) || Thrust(ship) <= 0f) return;
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

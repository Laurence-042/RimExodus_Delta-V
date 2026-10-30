using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class ShipFireControl
    {
        private readonly CombatStrategy fallbackStrategy = new CombatStrategy();
        private readonly Dictionary<int, CombatStrategyRule> automaticRules = new Dictionary<int, CombatStrategyRule>();
        private readonly Dictionary<(int weapon, DriveSubject enemy), CombatStrategyRule> fireRuleState = new Dictionary<(int, DriveSubject), CombatStrategyRule>();
        private readonly Dictionary<int, int> partRefreshTick = new Dictionary<int, int>();
        private readonly Dictionary<DriveSubject, CombatStrategyRule> targetRules = new Dictionary<DriveSubject, CombatStrategyRule>();
        private readonly Queue<CombatTarget> partQueue = new Queue<CombatTarget>();
        private CombatTarget commandedTarget;
        internal int QueueCount => partQueue.Count;
        internal int configurationVersion;
        private CombatStrategyFacts observation;
        private CombatStrategy observedStrategy;
        private ShipBase observedShip;
        private SpaceObject observedFrame;
        private int observationTick = -1, observationVersion = -1, targetSwitchTick;
        private readonly Dictionary<DriveSubject, List<Thing>> parts = new Dictionary<DriveSubject, List<Thing>>();
        private int partsVersion = -1;
        private readonly List<CombatWeaponThreat> threats = new List<CombatWeaponThreat>();
        private int threatTick = -100, threatVersion = -1, threatConfiguration = -1;
        private SpaceObject threatFrame;
        internal IReadOnlyList<CombatWeaponThreat> Threats(ShipBase ship, SpaceObject frame)
        {
            int now = Find.TickManager.TicksGame;
            if (now - threatTick >= 30 || frame != threatFrame || threatConfiguration != configurationVersion
                || SubstructureOwnership.CacheNeedsRefresh(threatVersion))
            {
                threats.Clear(); CombatWeaponThreat.CaptureRaw(ship, frame, 3f, threats);
                threatTick = now; threatFrame = frame; threatConfiguration = configurationVersion;
                threatVersion = SubstructureOwnership.GlobalStructureVersion;
            }
            return threats;
        }
        internal CombatStrategy Strategy(ShipBase ship) => ship.CombatAI?.strategy ?? fallbackStrategy;
        internal CombatStrategyFacts Facts(ShipBase ship, SpaceObject own)
        {
            int now = Find.TickManager.TicksGame;
            var strategy = Strategy(ship);
            if (observation == null || now - observationTick >= 30 || observedShip != ship
                || observedFrame != own || observedStrategy != strategy
                || SubstructureOwnership.CacheNeedsRefresh(observationVersion))
            {
                observation = new CombatStrategyFacts(universe, ship, own);
                observationTick = now; observedShip = ship; observedFrame = own; observedStrategy = strategy;
                observationVersion = SubstructureOwnership.GlobalStructureVersion;
            }
            return observation;
        }

        private void ClearStrategyState(bool commands = true)
        {
            observation = null; observedShip = null; observedFrame = null; observedStrategy = null;
            threats.Clear(); threatFrame = null; threatTick = -100;
            automaticRules.Clear(); fireRuleState.Clear(); partRefreshTick.Clear(); targetRules.Clear(); parts.Clear(); partsVersion = -1;
            if (commands) { partQueue.Clear(); commandedTarget = default; }
            targetSwitchTick = 0;
        }

        internal void CommandTarget(CombatTarget value, bool appendPart = false)
        {
            if (appendPart)
            { if (partQueue.Count < 16) partQueue.Enqueue(value); }
            else
            { commandedTarget = CombatTarget.ForShip(value.Ship); partQueue.Clear(); if (value.SystemId != 0) partQueue.Enqueue(value); }
            nextAutomaticAssignmentTick = 0;
        }
        internal void CancelCommands() { commandedTarget = default; partQueue.Clear(); nextAutomaticAssignmentTick = 0; }

        private bool TryQueuedPart(ShipBase ship, SpaceObject own, out CombatTarget queued)
        {
            while (partQueue.Count > 0)
            {
                queued = partQueue.Peek();
                if (TryResolveAutomatic(ship, own, queued, out _)) return true;
                partQueue.Dequeue();
            }
            queued = default; return false;
        }

        internal static bool Armed(ShipBase ship) => ship.Weapons.Any(w => w.SpaceModeEnabled
            && !w.WeaponProps.manualOnly && w.parent.Spawned && !w.parent.Destroyed);

        private bool TargetAllowed(ShipBase ship, ShipBase enemy, CombatStrategyContext context)
        {
            if (commandedTarget.Ship == enemy.Identity) return true;
            targetRules.TryGetValue(enemy.Identity, out var previous);
            var rule = Strategy(ship).Target(context, previous);
            targetRules[enemy.Identity] = rule;
            return rule.includeDisarmed || Armed(enemy);
        }

        private void DecideStrategyTarget(ShipBase ship, SpaceObject own)
        {
            if (commandedTarget.HasLock && TryResolveAutomatic(ship, own, commandedTarget, out _))
            { target = commandedTarget; return; }
            commandedTarget = default;
            var facts = Facts(ship, own);
            var strategy = Strategy(ship);
            var candidates = universe.objects.SelectMany(c => Candidates(ship, own, c))
                .OrderBy(s => OrbitSphere.ArcCells(own.pose.position, ShipRegistry.ShipPose(s).position))
                .ThenBy(s => s.Identity.key).Take(64).ToList();
            var live = new HashSet<DriveSubject>(candidates.Select(s => s.Identity));
            foreach (var id in targetRules.Keys.Where(k => !live.Contains(k)).ToList()) targetRules.Remove(id);
            float best = float.NegativeInfinity, currentScore = float.NegativeInfinity;
            CombatTarget selected = default;
            string reason = "-";
            foreach (var member in candidates)
            {
                var context = facts.Context(member);
                targetRules.TryGetValue(member.Identity, out var oldRule);
                var rule = strategy.Target(context, oldRule);
                targetRules[member.Identity] = rule;
                if (!rule.includeDisarmed && !Armed(member)) continue;
                float ownFire = universe.Weapons.EstimateFirepower(ship, context.distance, HullRadius(member));
                float health = Parts(member).Sum(t => (float)t.HitPoints);
                float finish = 1f / (1f + health / 1000f);
                float score = rule.targetOrder switch
                {
                    CombatTargetOrder.Nearest => 10000f / (1f + context.distance),
                    CombatTargetOrder.Threat => context.enemyFirepower,
                    CombatTargetOrder.Finish => ownFire * finish,
                    CombatTargetOrder.MainWeapons => PrimaryFirepower(ship, member, context.distance, rule.tactic.primaryWeapons),
                    _ => rule.targetThreatWeight * context.enemyFirepower * 2f
                        + rule.outgoingWeight * ownFire + rule.finishingWeight * ownFire * finish - context.distance * 0.1f
                };
                if (member.Identity == target.Ship) currentScore = score;
                if (score <= best) continue;
                best = score; selected = CombatTarget.ForShip(member.Identity); reason = rule.name;
            }
            int now = Find.TickManager.TicksGame;
            if (selected != target && !float.IsNegativeInfinity(currentScore)
                && (now < targetSwitchTick || best < currentScore + Mathf.Max(1f, Mathf.Abs(currentScore) * 0.2f))) return;
            if (selected != target) { preferredSystemId = 0; targetSwitchTick = now + 120; }
            if (DeltaVLog.Enabled(DeltaVLogModule.Combat) && (!targetObserved || selected != target))
                DeltaVLog.Message(DeltaVLogModule.Combat, $"ship #{ownerId} fire-control target {selected.Ship} rule={reason}");
            targetObserved = true; target = selected;
            if (ship.CombatAI != null) ship.CombatAI.lastTargetReason = reason + " → "
                + (selected.TryResolve(universe, out var chosen) ? chosen.Ship.Label : "-");
        }

        private List<Thing> Parts(ShipBase member)
        {
            if (SubstructureOwnership.CacheNeedsRefresh(partsVersion))
            { parts.Clear(); partsVersion = SubstructureOwnership.GlobalStructureVersion; }
            if (!parts.TryGetValue(member.Identity, out var result))
            {
                result = member.Map.listerThings.AllThings.Where(t => t is Building && t.Spawned
                    && !t.Destroyed && member.Contains(t)).OrderBy(t => t.thingIDNumber).ToList();
                parts[member.Identity] = result;
            }
            return result;
        }

        private float PrimaryFirepower(ShipBase ship, ShipBase enemy, float distance, CombatWeaponFilter filter)
        {
            float result = 0f;
            foreach (var weapon in ship.Weapons)
            {
                if (filter?.Includes(weapon) != true || distance > weapon.EffectiveRange
                    || distance < weapon.WeaponProps.minRange) continue;
                var projectile = weapon.SelectMagazine()?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
                result += (SpaceWeaponBallistics.ExpectedDamage(weapon, projectile))
                    * 60f / Mathf.Max(1, weapon.ShotInterval);
            }
            return result;
        }

        private CombatTarget SelectPart(ShipBase ship, SpaceObject own, CompSpaceWeapon weapon,
            ShipBase enemy, CombatPart preference)
        {
            var center = CombatTarget.ForShip(enemy.Identity);
            if (preference == CombatPart.Center) return center;
            WeaponAimInfo aim = SpaceWeaponService.AimInfo(weapon, own);
            SpaceObject frame = universe.FrameOf(enemy.Map);
            var candidates = Parts(enemy).Select(t => (thing: t, value: PartValue(t, preference)))
                .Where(p => p.value > 0f).OrderByDescending(p => p.value)
                .ThenBy(p => p.thing.thingIDNumber).Take(12);
            float best = float.NegativeInfinity;
            CombatTarget result = center;
            foreach (var candidate in candidates)
            {
                var handle = CombatTarget.ForShip(enemy.Identity, candidate.thing.thingIDNumber);
                if (!handle.TryResolve(universe, out var resolved)
                    || !SpaceWeaponBallistics.TryAim(aim, own.pose, resolved.Pose, out var lead)
                    || !weapon.Covers(own, lead, aim.Muzzle)
                    || !SpaceWeaponShot.HasClearAimPath(weapon, own, lead)) continue;
                Vector2 source = SpaceTransform.UniverseToMapPoint(frame, enemy.Map, aim.Muzzle);
                Vector2 end = new Vector2(candidate.thing.DrawPos.x, candidate.thing.DrawPos.z);
                float obstruction = SpaceCollision.TargetObstruction(enemy, source, end, candidate.thing);
                float score = candidate.value / (1f + obstruction) - 0.001f * (end - source).magnitude;
                if (score <= best) continue;
                best = score; result = handle;
            }
            return result;
        }

        private static float PartValue(Thing thing, CombatPart preference)
        {
            if (thing.TryGetComp<CompSpaceWeapon>() is { } w && !w.WeaponProps.manualOnly)
                return preference == CombatPart.Weapons || preference == CombatPart.Automatic ? 5f : 0f;
            if (thing.TryGetComp<CompSpaceShield>() != null)
                return preference == CombatPart.Shield || preference == CombatPart.Automatic ? 4f : 0f;
            if (thing.TryGetComp<CompDeltaVPilotConsole>() != null)
                return preference == CombatPart.Console || preference == CombatPart.Automatic ? 3f : 0f;
            if (thing.TryGetComp<CompPowerTrader>() is { } power && power.Props.PowerConsumption < 0f
                || thing.TryGetComp<CompPowerBattery>() != null)
                return preference == CombatPart.Power || preference == CombatPart.Automatic ? 3f : 0f;
            if (ThrusterPower.IsThruster(thing))
                return preference == CombatPart.Propulsion || preference == CombatPart.Automatic ? 4f : 0f;
            return 0f;
        }
    }
}

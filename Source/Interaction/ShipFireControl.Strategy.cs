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
        private readonly Dictionary<(long weapon, DriveSubject enemy), CombatStrategyRule> fireRuleState = new Dictionary<(long, DriveSubject), CombatStrategyRule>();
        private readonly Dictionary<DriveSubject, CombatStrategyRule> targetRules = new Dictionary<DriveSubject, CombatStrategyRule>();
        private readonly Queue<CombatTarget> partQueue = new Queue<CombatTarget>();
        private CombatTarget commandedTarget;
        internal int QueueCount => partQueue.Count;
        internal int configurationVersion;
        private CombatStrategyFacts observation;
        private CombatStrategy observedStrategy;
        private ShipBase observedShip;
        private SpaceObject observedFrame;
        private int observationTick = -1, targetSwitchTick;
        private readonly List<CombatWeaponThreat> threats = new List<CombatWeaponThreat>();
        private int threatTick = -100, threatConfiguration = -1;
        private SpaceObject threatFrame;
        internal IReadOnlyList<CombatWeaponThreat> Threats(ShipBase ship, SpaceObject frame)
        {
            int now = Find.TickManager.TicksGame;
            if (now - threatTick >= 30 || frame != threatFrame || threatConfiguration != configurationVersion)
            {
                threats.Clear(); CombatWeaponThreat.CaptureRaw(ship, frame, 3f, threats);
                threatTick = now; threatFrame = frame; threatConfiguration = configurationVersion;
            }
            return threats;
        }
        internal CombatStrategy Strategy(ShipBase ship) => ship.CombatAI?.strategy ?? fallbackStrategy;
        internal CombatStrategyFacts Facts(ShipBase ship, SpaceObject own, bool refresh = false)
        {
            int now = Find.TickManager.TicksGame;
            var strategy = Strategy(ship);
            if (refresh || observation == null || now - observationTick >= 30 || observedShip != ship
                || observedFrame != own || observedStrategy != strategy)
            {
                observation = new CombatStrategyFacts(universe, ship, own);
                observationTick = now; observedShip = ship; observedFrame = own; observedStrategy = strategy;
            }
            return observation;
        }

        private void ClearStrategyState(bool commands = true)
        {
            observation = null; observedShip = null; observedFrame = null; observedStrategy = null;
            threats.Clear(); threatFrame = null; threatTick = -100;
            fireRuleState.Clear(); targetRules.Clear();
            if (commands) { partQueue.Clear(); commandedTarget = default; }
            targetSwitchTick = 0;
        }

        internal void CommandTarget(CombatTarget value, bool appendPart = false)
        {
            if (value.SystemId != 0)
            {
                var ship = ShipRegistry.ShipByIdentity(ownerId);
                if (ship?.Map == null || !TryResolveAutomatic(ship, universe.FrameOf(ship.Map), value, out _)) return;
            }
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

        private bool TargetAllowed(ShipBase ship, ShipBase enemy, CombatStrategyRule rule)
            => universe.Controls.ExistingFor(ship) is NpcShipController || rule.includeDisarmed || Armed(enemy);

        private bool TargetAllowed(ShipBase ship, ShipBase enemy, CombatStrategyContext context)
        {
            if (commandedTarget.Ship == enemy.Identity) return true;
            targetRules.TryGetValue(enemy.Identity, out var previous);
            var rule = Strategy(ship).Target(context, previous);
            targetRules[enemy.Identity] = rule;
            return TargetAllowed(ship, enemy, rule);
        }

        private void DecideStrategyTarget(ShipBase ship, SpaceObject own)
        {
            if (commandedTarget.HasLock && TryResolveAutomatic(ship, own, commandedTarget, out _))
            {
                target = commandedTarget;
                if (ship.CombatAI != null) ship.CombatAI.lastTargetReason = "DeltaV_CombatDecisionOrderedTarget".Translate();
                return;
            }
            commandedTarget = default;
            var strategy = Strategy(ship);
            var candidates = universe.objects.SelectMany(c => Candidates(ship, own, c))
                .OrderBy(s => OrbitSphere.ArcCells(own.pose.position, ShipRegistry.ShipPose(s).position))
                .ThenBy(s => s.Identity.key).Take(64).ToList();
            // Keep the original first observation (including inherited baselines), even
            // without an enemy. An absent/zero baseline is not proof of initialization.
            var baseline = ship.CombatAI;
            bool baselineReady = baseline != null && baseline.baselineShipKey == ship.Identity.key
                && baseline.initialHullHp > 0f && baseline.initialThrust > 0f;
            var facts = candidates.Count > 0 || !baselineReady ? Facts(ship, own) : null;
            var live = new HashSet<DriveSubject>(candidates.Select(s => s.Identity));
            foreach (var id in targetRules.Keys.Where(k => !live.Contains(k)).ToList()) targetRules.Remove(id);
            float best = float.NegativeInfinity, currentScore = float.NegativeInfinity;
            CombatTarget selected = default;
            string reason = "-";
            string currentReason = null;
            foreach (var member in candidates)
            {
                var context = facts.Context(member);
                targetRules.TryGetValue(member.Identity, out var oldRule);
                var rule = strategy.Target(context, oldRule);
                targetRules[member.Identity] = rule;
                if (!TargetAllowed(ship, member, rule)) continue;
                float ownFire = universe.Weapons.EstimateFirepower(ship, context.distance, HullRadius(member));
                float health = HullHealth(member);
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
                if (member.Identity == target.Ship)
                {
                    currentScore = score;
                    currentReason = CombatDecisionText.Selection(strategy.targetRules, rule, oldRule);
                }
                if (score <= best) continue;
                best = score; selected = CombatTarget.ForShip(member.Identity);
                reason = CombatDecisionText.Selection(strategy.targetRules, rule, oldRule);
            }
            int now = Find.TickManager.TicksGame;
            if (selected != target && !float.IsNegativeInfinity(currentScore)
                && (now < targetSwitchTick || best < currentScore + Mathf.Max(1f, Mathf.Abs(currentScore) * 0.2f)))
            {
                if (ship.CombatAI != null) ship.CombatAI.lastTargetReason = currentReason + "\n"
                    + "DeltaV_CombatDecisionKeepTarget".Translate(currentScore.ToString("0.0"), best.ToString("0.0"),
                        (Mathf.Max(0, targetSwitchTick - now) / 60f).ToString("0.0"));
                return;
            }
            if (selected != target) targetSwitchTick = now + 120;
            if (DeltaVLog.Enabled(DeltaVLogModule.AI) && (!targetObserved || selected != target))
                DeltaVLog.Message(DeltaVLogModule.AI, $"target tick={Find.TickManager.TicksGame} ship={ownerId} "
                    + $"{UniverseManager.MapTag(ship.Map)} previous={target.Ship} selected={selected.Ship} rule='{reason}' score={best:F3}");
            targetObserved = true; target = selected;
            if (ship.CombatAI != null) ship.CombatAI.lastTargetReason = selected.TryResolve(universe, out var chosen)
                ? reason + " → " + chosen.Ship.Label + " · " + "DeltaV_CombatDecisionScore".Translate(best.ToString("0.0"))
                : "DeltaV_CombatDecisionNoCandidate".Translate(candidates.Count).ToString();
        }

        private static float HullHealth(ShipBase member)
        {
            double health = 0;
            foreach (Thing thing in member.Map.listerThings.AllThings)
                if (thing is Building && thing.Spawned && !thing.Destroyed && member.Contains(thing))
                    health += thing.HitPoints;
            return (float)health;
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

        internal static CombatPart? ExternalCombatPart(Thing thing)
        {
            if (thing is not Building || !thing.Spawned || thing.Destroyed) return null;
            var weapon = thing.TryGetComp<CompSpaceWeapon>();
            bool isWeapon = weapon != null && !weapon.WeaponProps.manualOnly;
            bool isThruster = ThrusterPower.IsThruster(thing);
            if (!isWeapon && !isThruster) return null;
            if (isWeapon && (weapon.WeaponProps.topTurret || weapon.WeaponProps.casemate)) return CombatPart.Weapons;
            foreach (IntVec3 cell in thing.OccupiedRect())
                if (cell.InBounds(thing.Map) && (thing.Map.roofGrid == null || !ShellHullVisuals.HasShellRoof(thing.Map, cell)))
                    return isWeapon ? CombatPart.Weapons : CombatPart.Propulsion;
            return null;
        }

        private static ResolvedCombatTarget AtExternalPart(ShipBase ship, SpaceObject frame, Thing part)
        {
            Vector3 draw = part.DrawPos;
            Vector2 local = SpaceTransform.MapToShipLocal(ship.Map, new Vector2(draw.x, draw.z));
            return new ResolvedCombatTarget(frame, ship, SpaceTransform.RigidPointPose(frame.pose, local, ship.FrameAngle));
        }

        internal sealed class ExternalTargets
        {
            internal readonly List<Thing> weapons = new List<Thing>();
            internal readonly List<Thing> propulsion = new List<Thing>();

            internal ExternalTargets() { }

            internal ExternalTargets(ShipBase ship) : this(ship.Map.listerThings.AllThings.Where(ship.Contains)) { }

            internal ExternalTargets(IEnumerable<Thing> observed)
            {
                foreach (Thing thing in observed) Add(thing);
                Sort();
            }

            internal CombatPart? Add(Thing thing)
            {
                var part = ExternalCombatPart(thing);
                if (part.HasValue) (part == CombatPart.Propulsion ? propulsion : weapons).Add(thing);
                return part;
            }

            internal void Sort()
            {
                weapons.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                propulsion.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            }

            internal bool TryGet(int id, out Thing part)
            {
                part = weapons.Find(t => t.thingIDNumber == id) ?? propulsion.Find(t => t.thingIDNumber == id);
                return part != null;
            }

            // Null means the hull center. A false return means even that point is unreachable.
            internal bool TrySelect(CombatPart priority, System.Func<Thing, bool> reachable)
            {
                using var timing = DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.CombatPartSelection);
                var first = priority == CombatPart.Propulsion ? propulsion : weapons;
                var second = priority == CombatPart.Propulsion ? weapons : propulsion;
                foreach (Thing part in first)
                    if (reachable(part)) return true;
                foreach (Thing part in second)
                    if (reachable(part)) return true;
                return reachable(null);
            }
        }
    }
}

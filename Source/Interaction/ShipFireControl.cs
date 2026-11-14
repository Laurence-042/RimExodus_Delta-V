using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>每船战术目标的唯一所有者；输入、机动和弹体只消费其身份与意图。</summary>
    internal sealed partial class ShipFireControl
    {
        private readonly UniverseManager universe;
        private readonly DriveSubject ownerId;
        private CombatTarget target;
        private int preferredSystemId;
        private bool targetObserved;
        private readonly Dictionary<long, PdLock> pointDefense = new Dictionary<long, PdLock>();
        private readonly Dictionary<long, CombatTarget> automaticTargets = new Dictionary<long, CombatTarget>();
        private readonly Dictionary<long, int> automaticRetryAt = new Dictionary<long, int>();
        private int nextAutomaticAssignmentTick;
        private CombatStrategy assignmentStrategy;
        private readonly List<long> staleWeapons = new List<long>();
        private readonly HashSet<long> validPointDefenseWeapons = new HashSet<long>();
        private int pointDefensePrunedVersion = -1;
        private readonly List<SpaceObject> interceptionCandidates = new List<SpaceObject>();
        private readonly Dictionary<int, float> pdReservedDamage = new Dictionary<int, float>();
        private struct PdLock { internal CombatTarget target; internal int retargetAt; }
        internal CombatTarget Target => target;
        internal ShipFireControl(UniverseManager universe, DriveSubject ownerId)
        { this.universe = universe; this.ownerId = ownerId; }

        internal void Clear(bool preserveCommands = false)
        { target = default; preferredSystemId = 0; automaticTargets.Clear(); automaticRetryAt.Clear(); nextAutomaticAssignmentTick = 0; ClearStrategyState(!preserveCommands); }
        internal void Dispose()
        { Clear(); pointDefense.Clear(); interceptionCandidates.Clear(); pdReservedDamage.Clear(); aimCapabilities.Clear(); leadPoints.Clear(); uniqueLeadPoints.Clear(); }
        internal void WeaponParametersChanged(CompSpaceWeapon weapon)
        {
            automaticRetryAt.Remove(weapon.Identity);
            observation = null; nextAutomaticAssignmentTick = 0; configurationVersion++;
        }
        internal void ForgetWeapon(CompSpaceWeapon weapon)
        {
            long id = weapon.Identity;
            bool hadLock = automaticTargets.Remove(id);
            pointDefense.Remove(id); automaticRetryAt.Remove(id);
            automaticRules.Remove(id); partRefreshTick.Remove(id);
            foreach (var key in fireRuleState.Keys.Where(k => k.weapon == id).ToList()) fireRuleState.Remove(key);
            if (hadLock || weapon.OwnerShip?.Identity == ownerId)
            { observation = null; nextAutomaticAssignmentTick = 0; configurationVersion++; }
        }

        internal void ForgetMap(Map map)
        {
            interceptionCandidates.Clear();
            automaticTargets.Clear(); automaticRetryAt.Clear(); nextAutomaticAssignmentTick = 0;
            observation = null; observedFrame = null; observedShip = null; parts.Clear();
            threats.Clear(); threatFrame = null; threatTick = -100;
            if (!target.TryResolve(universe, out var resolved) || resolved.Container?.map == map) Clear();
            staleWeapons.Clear();
            foreach (var pair in pointDefense)
                if (!pair.Value.target.TryResolve(universe, out var pd)
                    || pd.Container == null || pd.Container.map == map) staleWeapons.Add(pair.Key);
            foreach (long id in staleWeapons) pointDefense.Remove(id);
        }

        internal static bool CandidateValid(ShipBase attacker, SpaceObject own, SpaceObject container, ShipBase member)
            => CandidateValid(attacker, own, container, member, out _);

        private static bool CandidateValid(ShipBase attacker, SpaceObject own, SpaceObject container,
            ShipBase member, out float distance)
        {
            distance = 0f;
            if (attacker?.IsValid != true || attacker.Faction == null || own == null || container == null
                || container == own || container.map == null || container.map.Disposed
                || container.baseTag == SpaceObjectTag.Projectile || !container.HasTag(SpaceObjectTag.Ship)
                || member?.IsValid != true || member.Map != container.map || member.Faction == null
                || !member.Faction.HostileTo(attacker.Faction)) return false;
            distance = OrbitSphere.ArcCells(ShipRegistry.ShipPose(attacker).position, ShipRegistry.ShipPose(member).position);
            if (distance > DeltaVMod.CurrentSettings.poiActiveRadius) return false;
            return true;
        }

        internal static IEnumerable<ShipBase> Candidates(ShipBase ship, SpaceObject own, SpaceObject container)
        {
            if (container == null || container == own) yield break;
            foreach (ShipBase member in container.Ships)
                if (CandidateValid(ship, own, container, member)) yield return member;
        }

        internal bool TryResolveAutomatic(ShipBase ship, SpaceObject own, CombatTarget handle,
            out ResolvedCombatTarget resolved)
        {
            return handle.TryResolve(universe, out resolved)
                && CandidateValid(ship, own, resolved.Container, resolved.Ship);
        }

        internal static float HullRadius(ShipBase ship)
        {
            Vector2 center = ship.HullCenter;
            float radius = 0.5f;
            foreach (IntVec3 cell in ship.HullCells)
                radius = Mathf.Max(radius, (new Vector2(cell.x + 0.5f, cell.z + 0.5f) - center).magnitude + 0.71f);
            return radius;
        }

        internal void Decide(ShipBase ship, SpaceObject own)
        {
            DecideStrategyTarget(ship, own);
        }

        /// <summary>One target per weapon; locks are runtime-only and the weapon service remains the firing authority.</summary>
        internal void TickAutoVolley(ShipBase ship, SpaceObject own, bool suppressSystems)
        {
            if (universe.Lifecycle.IsFrozen(ship.Map)) return;
            int now = Find.TickManager.TicksGame;
            if (now >= nextAutomaticAssignmentTick || assignmentStrategy != Strategy(ship))
            {
                AssignAutomaticTargets(ship, own);
                assignmentStrategy = Strategy(ship);
                int interval = Mathf.Clamp(DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 30);
                nextAutomaticAssignmentTick = now + interval - ((now + ownerId.key % interval) % interval);
            }
            int mainMask = ship.Combat?.weaponGroupsMain ?? 0;
            int secondaryMask = ship.Combat?.weaponGroupsSecondary ?? 0;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!weapon.SpaceModeEnabled || weapon.WeaponProps.manualOnly
                    || weapon.aimMode != SpaceWeaponAimMode.Volley
                    || !weapon.GroupEnabled(weapon.salvoSet == SpaceWeaponSalvoSet.Main
                        ? mainMask : secondaryMask)
                    || !WeaponBelongsHere(weapon, own)) continue;
                long id = weapon.Identity;
                bool trigger = weapon.CooldownTicksLeft == 0
                    && (!automaticRetryAt.TryGetValue(id, out int retryAt) || now >= retryAt);
                if (!automaticTargets.TryGetValue(id, out CombatTarget handle)) continue;
                if (!trigger && weapon.Component<CompSpaceWeaponTraverse>() == null) continue;
                if (!TryResolveAutomatic(ship, own, handle, out ResolvedCombatTarget resolved))
                {
                    // Invalid handles stop firing immediately; reassign on the existing decision clock.
                    continue;
                }
                if (TryQueuedPart(ship, own, out var queued) && queued.Ship == handle.Ship)
                {
                    handle = queued;
                    if (!handle.TryResolve(universe, out resolved)) continue;
                }
                if (suppressSystems)
                {
                    Thing system = resolved.Ship.Weapons
                        .Where(w => w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.PointDefense
                            && w.parent.Spawned && !w.parent.Destroyed)
                        .Select(w => (Thing)w.parent).FirstOrDefault()
                        ?? resolved.Container.MotionInfo?.thrusters
                            .Where(t => t.operational && t.thing != null && resolved.Ship.Contains(t.thing))
                            .Select(t => t.thing).FirstOrDefault();
                    if (system != null)
                    {
                        handle = CombatTarget.ForShip(handle.Ship, system.thingIDNumber);
                        if (!handle.TryResolve(universe, out resolved)) continue;
                    }
                }
                bool fired = universe.Weapons.ApplyControl(weapon, own,
                    new WeaponControlCommand(handle, resolved, true, WeaponControlSource.Automatic, trigger));
                if (!trigger) continue;
                if (!fired)
                {
                    bool supplied = weapon.MagazineAvailable(weapon.SelectMagazine())
                        && SpaceWeaponBallistics.HasBaseShotEnergy(weapon);
                    automaticRetryAt[id] = now + (supplied ? 3 : 30);
                }
                else automaticRetryAt.Remove(id);
            }
        }

        private void AssignAutomaticTargets(ShipBase ship, SpaceObject own)
        {
            var previousTargets = new Dictionary<long, CombatTarget>(automaticTargets);
            automaticTargets.Clear();
            automaticRetryAt.Clear();
            var enemies = new List<ShipBase>();
            foreach (SpaceObject container in universe.objects)
                foreach (ShipBase member in Candidates(ship, own, container)) enemies.Add(member);
            // Assignment only reads the world. Share current poses within this call, never across ticks.
            var enemyPoses = new Dictionary<DriveSubject, SpacePose>();
            foreach (var enemy in enemies) enemyPoses[enemy.Identity] = ShipRegistry.ShipPose(enemy);
            enemies = enemies.OrderBy(e => OrbitSphere.ArcCells(own.pose.position, enemyPoses[e.Identity].position)).Take(64).ToList();
            var enemyIds = new HashSet<DriveSubject>(enemies.Select(e => e.Identity));
            var weaponIds = new HashSet<long>(ship.Weapons.Select(w => w.Identity));
            foreach (var key in fireRuleState.Keys.Where(k => !enemyIds.Contains(k.enemy) || !weaponIds.Contains(k.weapon)).ToList()) fireRuleState.Remove(key);
            foreach (var key in automaticRules.Keys.Where(k => !weaponIds.Contains(k)).ToList())
            { automaticRules.Remove(key); partRefreshTick.Remove(key); }
            if (enemies.Count == 0)
            {
                if (ship.CombatAI != null) ship.CombatAI.lastFireReason = "DeltaV_CombatDecisionNoTarget".Translate();
                return;
            }
            bool hasQueued = TryQueuedPart(ship, own, out var queuedTarget);
            var facts = Facts(ship, own);
            enemies.RemoveAll(e => !(hasQueued && queuedTarget.Ship == e.Identity)
                && !TargetAllowed(ship, e, facts.Context(e)));
            var reservedDamage = new Dictionary<DriveSubject, float>();
            var reasons = new List<string>();
            var enemyRadius = enemies.ToDictionary(e => e.Identity, HullRadius);
            var enemyHealth = enemies.ToDictionary(e => e.Identity,
                e => Parts(e).Sum(b => (float)b.HitPoints));
            foreach (CompSpaceWeapon weapon in ship.Weapons.OrderBy(w => w.Identity))
            {
                if (!weapon.SpaceModeEnabled || weapon.WeaponProps.manualOnly
                    || weapon.aimMode != SpaceWeaponAimMode.Volley || !weapon.GroupActive
                    || !WeaponBelongsHere(weapon, own)) continue;
                float best = float.NegativeInfinity;
                ShipBase selected = null;
                CombatStrategyRule selectedRule = null;
                CombatStrategyRule selectedPrevious = null;
                string holdReason = null;
                float damage = SpaceWeaponBallistics.ExpectedDamage(weapon,
                    weapon.SelectMagazine()?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef);
                WeaponAimInfo aim = SpaceWeaponService.AimInfo(weapon, own);
                foreach (ShipBase member in enemies)
                {
                    SpacePose enemyPose = enemyPoses[member.Identity];
                    if (hasQueued && queuedTarget.Ship == member.Identity && queuedTarget.TryResolve(universe, out var queuedPart))
                        enemyPose = queuedPart.Pose;
                    CombatStrategyContext context = facts.Context(member, weapon);
                    var ruleKey = (weapon.Identity, member.Identity);
                    fireRuleState.TryGetValue(ruleKey, out var lastRule);
                    CombatStrategyRule rule = Strategy(ship).Fire(context, lastRule);
                    fireRuleState[ruleKey] = rule;
                    if (rule.fireAction == CombatFireAction.Hold)
                    { holdReason = CombatDecisionText.Selection(Strategy(ship).fireRules, rule, lastRule); continue; }
                    if (rule.anchorOnly && member.Identity != target.Ship) continue;
                    if (!SpaceWeaponBallistics.TryAim(aim, own.pose, enemyPose, out var lead)
                        || !weapon.Covers(own, lead, aim.Muzzle)
                        || !SpaceWeaponShot.HasClearAimPath(weapon, own, lead)) continue;
                    float radius = enemyRadius[member.Identity];
                    float spread = weapon.WeaponProps.baseSpreadDeg * Mathf.Deg2Rad;
                    float hit = Mathf.Clamp01(radius /
                        Mathf.Max(radius, context.distance * Mathf.Tan(spread)));
                    float rate = damage * hit
                        * 60f / Mathf.Max(1, weapon.ShotInterval);
                    float targetHealth = enemyHealth[member.Identity];
                    reservedDamage.TryGetValue(member.Identity, out float reserved);
                    float finishing = 1f / (1f + targetHealth / 1000f);
                    float score = rule.outgoingWeight * rate
                        + rule.targetThreatWeight * context.enemyFirepower
                        + rule.finishingWeight * rate * finishing
                        - reserved / Mathf.Max(1f, targetHealth) * rate;
                    if (hasQueued && queuedTarget.Ship == member.Identity) score += 100000f;
                    if (score <= best) continue;
                    best = score; selected = member; selectedRule = rule; selectedPrevious = lastRule;
                }
                if (selected == null)
                {
                    if (holdReason != null && ship.CombatAI != null)
                        reasons.Add(weapon.parent.LabelCap + " · " + holdReason + " · " + "DeltaV_CombatFireAction_Hold".Translate());
                    continue;
                }
                long weaponId = weapon.Identity;
                bool sameRule = automaticRules.TryGetValue(weaponId, out var oldSelected) && oldSelected == selectedRule;
                automaticRules[weaponId] = selectedRule;
                int now = Find.TickManager.TicksGame;
                bool retainPart = sameRule && previousTargets.TryGetValue(weaponId, out var oldTarget)
                    && oldTarget.Ship == selected.Identity && oldTarget.TryResolve(universe, out _)
                    && partRefreshTick.TryGetValue(weaponId, out int refresh) && now < refresh;
                automaticTargets[weaponId] = retainPart ? previousTargets[weaponId] : SelectPart(ship, own, weapon, selected, selectedRule.part);
                if (!retainPart) partRefreshTick[weaponId] = now + 120;
                if (hasQueued && queuedTarget.Ship == selected.Identity) automaticTargets[weaponId] = queuedTarget;
                reservedDamage.TryGetValue(selected.Identity, out float previous);
                reservedDamage[selected.Identity] = previous + Mathf.Max(1f,
                    damage);
                if (ship.CombatAI != null)
                    reasons.Add(weapon.parent.LabelCap + " → " + selected.Label + " ("
                        + CombatDecisionText.Selection(Strategy(ship).fireRules, selectedRule, selectedPrevious) + ")");
            }
            if (ship.CombatAI != null)
                ship.CombatAI.lastFireReason = reasons.Count == 0 ? "DeltaV_CombatNoFireChoice".Translate().ToString()
                    : string.Join("; ", reasons);
        }

        private static int CompareIdentity(DriveSubject a, DriveSubject b)
        { int kind = a.shuttle.CompareTo(b.shuttle); return kind != 0 ? kind : a.key.CompareTo(b.key); }

        internal bool TryGetIntent(ShipBase ship, SpaceObject own, bool suppressSystems, out ShipVolleyIntent intent)
        {
            intent = default;
            if (universe.Lifecycle.IsFrozen(ship.Map)) return false;
            if (!TryResolveAutomatic(ship, own, target, out var victim))
            {
                if (!target.HasLock) return false;
                Clear();
                Decide(ship, own);
                if (!TryResolveAutomatic(ship, own, target, out victim)) return false;
            }
            if (suppressSystems && preferredSystemId == 0)
            {
                Thing system = victim.Ship.Weapons.Where(w => w.SpaceModeEnabled && w.aimMode == SpaceWeaponAimMode.PointDefense
                        && w.parent.Spawned && !w.parent.Destroyed).Select(w => (Thing)w.parent).FirstOrDefault()
                    ?? victim.Container.MotionInfo?.thrusters.Where(t => t.operational && t.thing != null
                        && victim.Ship.Contains(t.thing)).Select(t => t.thing).FirstOrDefault();
                preferredSystemId = system?.thingIDNumber ?? 0;
            }
            CombatTarget handle = CombatTarget.ForShip(target.Ship, suppressSystems ? preferredSystemId : 0);
            // A system lock changes the rigid point; resolve only that variant once here.
            if (suppressSystems && preferredSystemId != 0
                && !handle.TryResolve(universe, out victim)) return false;
            intent = new ShipVolleyIntent(handle, !suppressSystems, victim, true);
            return true;
        }

        internal void TickVolley(ShipBase ship, SpaceObject container, ShipVolleyIntent intent)
        {
            ResolvedCombatTarget resolved = intent.resolvedTarget;
            if (!intent.localPoint.HasValue && !intent.hasResolvedTarget
                && !intent.target.TryResolve(universe, out resolved)) return;
            var combat = ship.Combat;
            int mainMask = combat?.WeaponGroupMask(SpaceWeaponSalvoSet.Main) ?? 0;
            int secondaryMask = combat?.WeaponGroupMask(SpaceWeaponSalvoSet.Secondary) ?? 0;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                long dispatchStart = universe.Performance.Start();
                bool included = weapon.SpaceModeEnabled && !(weapon.WeaponProps.manualOnly && intent.fireAll)
                    && weapon.aimMode == SpaceWeaponAimMode.Volley
                    && weapon.GroupEnabled(weapon.salvoSet == SpaceWeaponSalvoSet.Main ? mainMask : secondaryMask)
                    && WeaponBelongsHere(weapon, container);
                bool fire = intent.fireAll || intent.main && weapon.salvoSet == SpaceWeaponSalvoSet.Main
                    || intent.secondary && weapon.salvoSet == SpaceWeaponSalvoSet.Secondary;
                universe.Performance.Record(PerformanceStage.WeaponDispatch, dispatchStart);
                if (!included) continue;
                WeaponControlCommand command = intent.localPoint.HasValue
                    ? new WeaponControlCommand(intent.localPoint.Value, fire)
                    : new WeaponControlCommand(intent.target, resolved, intent.predictIntercept,
                        intent.fireAll ? WeaponControlSource.Automatic : WeaponControlSource.Manual, fire);
                universe.Weapons.ApplyControl(weapon, container, command);
            }
        }

        internal void RefreshPointDefenseFilter(CompSpaceWeapon weapon)
        {
            if (pointDefense.TryGetValue(weapon.Identity, out var state)
                && (!state.target.TryResolve(universe, out var target)
                    || !weapon.AcceptsPointDefenseTarget(target.Container)))
                pointDefense.Remove(weapon.Identity);
        }

        internal void PrunePointDefense(ShipBase ship, bool force = false)
        {
            if (pointDefense.Count == 0) return;
            int structureVersion = SubstructureOwnership.StructureVersion(ship.Map);
            if (!force && ship.IsValid && pointDefensePrunedVersion == structureVersion) return;
            // 归属、模式与移除事件可能在下一次 tick 前发生；每次消费前仍复核。
            validPointDefenseWeapons.Clear();
            if (ship.IsValid)
                foreach (CompSpaceWeapon weapon in ship.Weapons)
                    if (weapon.parent.Spawned && !weapon.parent.Destroyed && weapon.SpaceModeEnabled
                        && weapon.aimMode == SpaceWeaponAimMode.PointDefense && weapon.OwnerShip.Identity == ownerId)
                        validPointDefenseWeapons.Add(weapon.Identity);
            staleWeapons.Clear();
            foreach (long id in pointDefense.Keys)
                if (!validPointDefenseWeapons.Contains(id)) staleWeapons.Add(id);
            foreach (long id in staleWeapons) pointDefense.Remove(id);
            pointDefensePrunedVersion = structureVersion;
        }

        private bool WeaponBelongsHere(CompSpaceWeapon weapon, SpaceObject container) =>
            weapon.parent.Spawned && !weapon.parent.Destroyed
            && weapon.OwnerShip.Identity == ownerId && weapon.Container == container;

        internal IReadOnlyList<SpaceObject> CollectInterceptionCandidates(ShipBase ship, SpaceObject container)
        {
            interceptionCandidates.Clear();
            pdReservedDamage.Clear();
            Faction faction = ship.Faction;
            if (faction != null)
                foreach (SpaceObject obj in universe.objects)
                    if (obj != null && obj != container
                        && obj.InterceptionEligible && obj.HostileTo(faction))
                        interceptionCandidates.Add(obj);
            return interceptionCandidates;
        }

        internal bool TickPointDefense(ShipBase ship, SpaceObject container, bool hasScatterTargets)
        {
            if (ship.EffectiveFireControlMode == ShipFireControlMode.Disabled)
            {
                pointDefense.Clear();
                foreach (CompSpaceWeapon weapon in ship.Weapons) universe.Scatter.ReleaseAutomatic(weapon);
                return false;
            }
            long candidatesStart = universe.Performance.Start();
            CollectInterceptionCandidates(ship, container);
            universe.Performance.Record(PerformanceStage.PointDefenseCandidates, candidatesStart);
            bool hasInterceptionTargets = interceptionCandidates.Count > 0;
            try
            {
                if (hasInterceptionTargets || hasScatterTargets)
                    TickPointDefenseWeapons(ship, container, hasInterceptionTargets, hasScatterTargets);
                else pointDefense.Clear();
                return hasInterceptionTargets;
            }
            finally { interceptionCandidates.Clear(); } // No object references or positions survive this combat call.
        }

        private void TickPointDefenseWeapons(ShipBase ship, SpaceObject container,
            bool hasInterceptionTargets, bool hasScatterTargets)
        {
            long pruneStart = universe.Performance.Start();
            PrunePointDefense(ship);
            universe.Performance.Record(PerformanceStage.PointDefensePrune, pruneStart);
            int now = Find.TickManager.TicksGame;
            var combat = ship.Combat;
            int mainMask = combat?.WeaponGroupMask(SpaceWeaponSalvoSet.Main) ?? 0;
            int secondaryMask = combat?.WeaponGroupMask(SpaceWeaponSalvoSet.Secondary) ?? 0;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                if (!weapon.SpaceModeEnabled || weapon.aimMode != SpaceWeaponAimMode.PointDefense
                    || !weapon.AllowsPointDefense
                    || (weapon.WeaponProps.tractor ? !hasScatterTargets : !hasInterceptionTargets || weapon.pdTargets == 0)
                    || !weapon.GroupEnabled(weapon.salvoSet == SpaceWeaponSalvoSet.Main ? mainMask : secondaryMask)
                    || !WeaponBelongsHere(weapon, container)) continue;
                pointDefense.TryGetValue(weapon.Identity, out PdLock state);
                if (!state.target.HasLock && now < state.retargetAt) continue;
                if (!SpaceWeaponService.CanTrack(weapon)) continue;
                Vector3 muzzle = SpaceWeaponService.TargetingMuzzle(weapon, container);
                ResolvedCombatTarget victim = default;
                bool valid = PdTargetValid(weapon, state.target, muzzle, out victim);
                if (valid && !weapon.WeaponProps.tractor && PdCoverageFull(victim.Container, pdReservedDamage))
                {
                    SpaceObject alternative = FindPdTarget(weapon, muzzle);
                    if (alternative != null && alternative != victim.Container)
                    {
                        state.target = CombatTarget.ForContainer(alternative);
                        pointDefense[weapon.Identity] = state;
                        valid = state.target.TryResolve(universe, out victim);
                    }
                }
                if (!valid)
                {
                    // Empty searches keep their cadence; lost/contested locks release and reselect immediately.
                    if (weapon.WeaponProps.tractor && state.target.HasLock)
                        universe.Scatter.ReleaseAutomatic(weapon);
                    if (weapon.WeaponProps.tractor && !state.target.HasLock && now < state.retargetAt) continue;
                    if (!SpaceWeaponService.CanAcquire(weapon))
                    {
                        state.target = default; state.retargetAt = now + 30;
                        pointDefense[weapon.Identity] = state;
                        continue;
                    }
                    long searchStart = universe.Performance.Start();
                    state.target = CombatTarget.ForContainer(FindPdTarget(weapon, muzzle)); state.retargetAt = now + 30;
                    universe.Performance.Record(PerformanceStage.PointDefenseSearch, searchStart);
                    pointDefense[weapon.Identity] = state;
                    valid = state.target.TryResolve(universe, out victim);
                }
                if (!valid) continue;
                if (weapon.WeaponProps.tractor && !universe.Scatter.ClaimAutomatic(victim.Container, weapon))
                {
                    state.target = CombatTarget.ForContainer(FindPdTarget(weapon, muzzle));
                    pointDefense[weapon.Identity] = state;
                    if (!state.target.TryResolve(universe, out victim)
                        || !universe.Scatter.ClaimAutomatic(victim.Container, weapon)) continue;
                }
                bool fired = universe.Weapons.ApplyControl(weapon, container,
                    new WeaponControlCommand(state.target, victim, true, WeaponControlSource.PointDefense));
                if (!fired && weapon.WeaponProps.tractor && !SpaceWeaponService.CanAcquire(weapon))
                {
                    universe.Scatter.ReleaseAutomatic(weapon);
                    state.target = default; state.retargetAt = now + 30;
                    pointDefense[weapon.Identity] = state;
                }
                if (fired && !weapon.WeaponProps.tractor)
                {
                    float damage = PdDamageAgainst(weapon, victim.Container);
                    if (damage > 0f)
                    {
                        int id = victim.Container.objectId;
                        pdReservedDamage.TryGetValue(id, out float reserved);
                        pdReservedDamage[id] = reserved + damage;
                    }
                }
            }
        }

        private readonly List<(CompSpaceWeapon weapon, WeaponAimInfo aim)> aimCapabilities = new List<(CompSpaceWeapon, WeaponAimInfo)>();
        private readonly List<Vector3> leadPoints = new List<Vector3>();
        private readonly HashSet<Vector3> uniqueLeadPoints = new HashSet<Vector3>();

        internal IReadOnlyList<Vector3> LeadPoints(ShipBase ship)
        {
            leadPoints.Clear(); uniqueLeadPoints.Clear(); aimCapabilities.Clear();
            SpaceObject own = ship.IsValid ? universe.FrameOf(ship.Map) : null;
            if (own == null) return leadPoints;
            foreach (CompSpaceWeapon weapon in ship.Weapons)
                if (WeaponBelongsHere(weapon, own) && weapon.SpaceModeEnabled
                    && weapon.GroupActive && weapon.aimMode == SpaceWeaponAimMode.Volley)
                    aimCapabilities.Add((weapon, SpaceWeaponService.AimInfo(weapon, own)));
            foreach (SpaceObject container in universe.objects)
            foreach (ShipBase member in Candidates(ship, own, container))
            {
                if (!CombatTarget.ForShip(member.Identity).TryResolve(universe, out var resolved)) continue;
                foreach (var capability in aimCapabilities)
                {
                    WeaponAimInfo info = capability.aim;
                    Vector3 point = resolved.Pose.position;
                    if (!SpaceWeaponBallistics.TryAim(info, own.pose, resolved.Pose, out point)) continue;
                    if (!capability.weapon.Covers(own, point, info.Muzzle)) continue;
                    if (uniqueLeadPoints.Add(point)) leadPoints.Add(point);
                }
            }
            return leadPoints;
        }

        internal static CombatTarget ManualTargetAlongRay(UniverseManager universe, SpaceObject source,
            Vector3 origin, Vector2 direction, Faction faction)
        {
            SpaceObject container = TargetAlongRay(universe, source, origin, direction, faction);
            if (container == null) return default;
            ShipBase selected = null;
            float best = float.MaxValue;
            foreach (ShipBase member in container.Ships)
            {
                if (!member.IsValid || member.Faction?.HostileTo(faction) != true) continue;
                Vector2 offset = OrbitSphere.Offset(origin, ShipRegistry.ShipPose(member).position);
                float along = Vector2.Dot(offset, direction);
                if (along <= 0f || (offset - direction * along).magnitude > HullRadius(member)) continue;
                if (along < best || along == best && CompareIdentity(member.Identity, selected.Identity) < 0)
                { best = along; selected = member; }
            }
            return selected != null ? CombatTarget.ForShip(selected.Identity) : CombatTarget.ForContainer(container);
        }

        internal bool PdTargetValid(CompSpaceWeapon weapon, CombatTarget handle, Vector3 muzzle,
            out ResolvedCombatTarget resolved)
        {
            return handle.TryResolve(universe, out resolved) && weapon.Container != null
                && (weapon.WeaponProps.tractor || resolved.Container.HostileTo(weapon.parent.Faction))
                && PdCandidateValid(weapon, resolved.Container, muzzle, out _);
        }

        private bool PdCandidateValid(CompSpaceWeapon weapon, SpaceObject target, Vector3 muzzle, out float distance)
        {
            distance = 0f;
            if (weapon.WeaponProps.tractor)
            {
                if (!universe.Scatter.AutoAvailable(target, weapon)) return false;
                distance = OrbitSphere.Offset(muzzle, target.pose.position).magnitude;
                return distance <= weapon.EffectiveRange && weapon.Covers(weapon.Container, target.pose.position, muzzle);
            }
            if (target == null || target == weapon.Container
                || !weapon.AcceptsPointDefenseTarget(target)) return false;
            distance = OrbitSphere.Offset(muzzle, target.pose.position).magnitude - SpaceCollision.BoundsRadiusOf(target);
            if (distance < weapon.WeaponProps.minRange || distance > weapon.EffectiveRange) return false;
            if (weapon.WeaponProps.casemate || weapon.coverage.fixedDirection
                || weapon.coverage.arc < 360f || weapon.coverage.direction != 0f)
            {
                var info = SpaceWeaponService.AimInfo(weapon, weapon.Container);
                return SpaceWeaponBallistics.TryAim(info, weapon.Container.pose, target.pose, out var lead)
                    && weapon.Covers(weapon.Container, lead, info.Muzzle)
                    && (!weapon.WeaponProps.casemate || SpaceWeaponShot.HasClearAimPath(weapon, weapon.Container, lead));
            }
            return true;
        }

        /// <summary>
        /// PD 索敌（§4.2）：射程内敌对的可拦截容器；优先级 =「预计命中己方的最近
        /// 容器优先」（来袭弹逼近自身容器 → 按预计命中时间升序），兜底最近。
        /// </summary>
        internal SpaceObject FindPdTarget(CompSpaceWeapon weapon, Vector3 muzzle)
        {
            SpaceObject own = weapon.Container;
            if (own == null) return null;
            SpaceObject best = null;
            float bestScore = float.MaxValue;
            SpaceObject fallback = null;
            float fallbackScore = float.MaxValue;
            IEnumerable<SpaceObject> candidates = weapon.WeaponProps.tractor
                ? universe.Scatter.Candidates : interceptionCandidates;
            foreach (SpaceObject obj in candidates)
            {
                if (universe.ObjectById(obj.objectId) != obj) continue;
                if (!PdCandidateValid(weapon, obj, muzzle, out float distance)) continue;
                if (weapon.WeaponProps.tractor)
                {
                    if (distance < bestScore) { bestScore = distance; best = obj; }
                    continue;
                }
                Vector2 offset = OrbitSphere.Offset(own.pose.position, obj.pose.position);
                // 来袭判定：目标相对速度朝己方逼近 → 预计命中时间（秒）作优先分；
                // 非来袭（掠过/远离）兜底排在所有来袭之后、按距离。
                Vector2 targetVel = SpaceTransform.TransportVelocity(obj.pose, own.pose.position)
                    - own.pose.velocity;
                float closing = offset.sqrMagnitude > 1e-6f
                    ? -Vector2.Dot(targetVel, offset.normalized) : 0f;
                float score = closing > 1f
                    ? Mathf.Max(0f, distance) / closing
                    : 10000f + distance;
                if (score < fallbackScore) { fallbackScore = score; fallback = obj; }
                if (PdCoverageFull(obj, pdReservedDamage)) continue;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = obj;
                }
            }
            return best ?? fallback;
        }

        private static bool PdCoverageFull(SpaceObject target, Dictionary<int, float> reservedDamage)
        {
            if (reservedDamage == null || !reservedDamage.TryGetValue(target.objectId, out float damage)) return false;
            return target.InterceptionBody is Building building && damage >= building.HitPoints;
        }

        private static float PdDamageAgainst(CompSpaceWeapon weapon, SpaceObject target)
        {
            ThingDef projectile = weapon.SelectMagazine()?.SelectedProjectileDef ?? weapon.WeaponProps.projectileDef;
            if (projectile == null || target.InterceptionBody is not Building building) return 0f;
            return weapon.WeaponProps.projectilesPerShot > 1 ? EstimateFanHits(weapon, target, projectile)
                : SpaceWeaponBallistics.ExpectedDamage(weapon, projectile, building.def);
        }

        private static float EstimateFanHits(CompSpaceWeapon weapon, SpaceObject target, ThingDef projectile)
        {
            var source = weapon.Container;
            var aim = SpaceWeaponService.AimInfo(weapon, source);
            var query = target.map == null ? null : new SpaceCollision.RayQuery(target);
            var passed = new HashSet<Thing>();
            Vector2 from = query == null ? SpaceTransform.UniverseToShipLocal(target.pose, aim.Muzzle)
                : SpaceTransform.UniverseToMapPoint(target, target.map, aim.Muzzle);
            Vector2 relative = SpaceTransform.VelocityAtUniversePoint(source.pose, aim.Muzzle)
                - SpaceTransform.VelocityAtUniversePoint(target.pose, aim.Muzzle);
            var props = projectile.GetCompProperties<CompProperties_SpaceProjectile>();
            int count = weapon.WeaponProps.projectilesPerShot;
            float mass = SpaceWeaponBallistics.ProjectileMass(weapon, weapon.SelectMagazine()) / count;
            float damage = 0;
            for (int i = 0; i < count; i++)
            {
                var mapDirection = SpaceTransform.Rotate(Vector2.right,
                    weapon.FacingAngle + SpaceWeaponShot.FanOffset(weapon.WeaponProps, i));
                var velocity = relative + aim.Speed * SpaceTransform.MapDirectionToUniverseTangent(
                    source, weapon.parent.Map, aim.MuzzleMap, mapDirection);
                Vector2 local = query == null
                    ? SpaceTransform.Rotate(SpaceTransform.TransportTangent(velocity, aim.Muzzle, target.pose.position),
                        -target.pose.angle).normalized
                    : SpaceTransform.UniverseTangentToMapDirection(target, target.map, aim.Muzzle, velocity);
                float length = velocity.magnitude * props.timeoutTicks / 60f;
                float radius = SpaceProjectileService.FootprintRadius(projectile);
                SpaceCollision.RayContact hit;
                bool found = query == null
                    ? SpaceCollision.TryMissileFootprint((AbstractMissile)target.Payload, from, local, length, radius, out hit)
                    : query.Next(from, local, length, projectile, radius, passed, null,
                        _ => false, _ => false, out hit, skipSubstructure: true);
                if (!found) continue;
                var effect = SpaceImpactEffects.Resolve(projectile, hit.building.def);
                if (effect?.defenderEffect == null) continue;
                float amount = SpaceImpactEffects.ComputeDamage(effect.defenderEffect, velocity.magnitude,
                    SpaceTransform.IncidenceAngle(local, hit.normal), 1f, mass);
                damage += SpaceImpactEffects.HitChance(projectile, hit.kind, hit.building, effect)
                    * SpaceImpactEffects.ExpectedBuildingDamage(hit.building, effect?.defenderEffect?.damageDef, amount, out _);
            }
            return damage;
        }

        private static SpaceObject TargetAlongRay(UniverseManager universe, SpaceObject source,
            Vector3 origin, Vector2 dirTangent, Faction faction)
        {
            SpaceObject best = null;
            float bestAlong = float.MaxValue;
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj == source || obj.map == null || obj.map.Disposed) continue;
                if (!obj.HostileTo(faction)) continue;
                Vector2 offset = OrbitSphere.Offset(origin, obj.pose.position);
                float along = Vector2.Dot(offset, dirTangent);
                if (along <= 0f) continue;
                float miss = (offset - dirTangent * along).magnitude;
                if (miss > SpaceCollision.BoundsRadiusOf(obj)) continue;
                if (along < bestAlong)
                {
                    bestAlong = along;
                    best = obj;
                }
            }
            return best;
        }

    }
}

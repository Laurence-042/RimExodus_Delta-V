using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DeltaV
{
    /// <summary>一艘船一个运行时 controller。身份使用带种类的 DriveSubject，地图只在使用时重查。</summary>
    public sealed class ShipControlService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<DriveSubject, ShipController> controllers = new Dictionary<DriveSubject, ShipController>();
        private readonly Dictionary<DriveSubject, (ShipStructure State, System.IDisposable Handle)> loadoutSubscriptions =
            new Dictionary<DriveSubject, (ShipStructure, System.IDisposable)>();
        private readonly HashSet<Map> pendingGunnerMaps = new HashSet<Map>();
        private readonly List<ShipController> tickBuffer = new List<ShipController>();
        private DriveSubject activeAimOwnerId;
        private int lastPlayerDecisionTick = -1;

        internal ShipBase ResolveShip(DriveSubject identity) => universe.ShipStructure.Resolve(identity);

        internal ShipControlService(UniverseManager universe) { this.universe = universe; }

        internal ShipController ExistingFor(ShipBase ship) => ship?.IsValid == true
            && controllers.TryGetValue(ship.Identity, out ShipController controller)
                ? controller : null;

        internal bool CollisionAvoidanceEnabled(ShipBase ship) =>
            ExistingFor(ship)?.CollisionAvoidanceEnabled ?? true;

        internal void NotifyHullAvoidanceChanged(DriveSubject subject)
        {
            universe.Projectiles.RefreshHullSource(subject);
            if (controllers.TryGetValue(subject, out var controller))
                controller.AutoManeuver.InvalidateHullAvoidance();
        }

        public ShipController For(ShipBase ship, bool debugTakeover = false)
        {
            if (!ship.IsValid || universe.FrameOf(ship.Map)?.HasTag(SpaceObjectTag.Ship) != true) return null;
            universe.ShipStructure.BindIdentity(ship);
            Faction faction = ship.Faction;
            if (faction == null) return null;
            bool playerAuthority = ship.PlayerMayOperate
                || DebugSettings.godMode && (debugTakeover
                    || controllers.TryGetValue(ship.Identity, out ShipController current)
                    && current is PlayerShipController
                    && (activeAimOwnerId == ship.Identity
                        || universe.Pilot.PilotedTarget(universe).Identity.Equals(ship.Identity)));
            if (controllers.TryGetValue(ship.Identity, out ShipController existing))
            {
                if (playerAuthority && existing is PlayerShipController
                    || !playerAuthority && existing is NpcShipController)
                {
                    existing.BindShip(ship);
                    BindLoadout(ship, existing);
                    return existing;
                }
                Remove(ship.Identity);
            }
            ShipController controller = playerAuthority
                ? new PlayerShipController(universe, ship.Identity)
                : new NpcShipController(universe, ship.Identity)
                {
                    Ai = new NpcCombatAi(universe, ship.Identity)
                };
            controllers.Add(ship.Identity, controller);
            controller.BindShip(ship);
            BindLoadout(ship, controller);
            return controller;
        }

        private void BindLoadout(ShipBase ship, ShipController controller)
        {
            if (ship.StructuralState is not { } state) return;
            if (loadoutSubscriptions.TryGetValue(ship.Identity, out var previous))
            {
                if (previous.State == state) return;
                previous.Handle.Dispose();
            }
            loadoutSubscriptions[ship.Identity] = (state, state.Watch(ShipStructureAspect.Loadout, batch =>
            {
                foreach (ShipStructureChange change in batch.Changes)
                    if (change.After is CompSpaceWeapon weapon)
                        controller.FireControl.WeaponParametersChanged(weapon);
            }));
        }

        internal PlayerShipController PlayerFor(ShipBase ship, bool debugTakeover = false)
            => For(ship, debugTakeover) as PlayerShipController;
        internal NpcShipController NpcFor(ShipBase ship) => For(ship) as NpcShipController;

        internal void ReleaseDebugControl(DriveSubject ownerId)
        {
            if (AimActiveFor(ShipRegistry.ShipByIdentity(ownerId))
                || universe.Pilot.PilotedTarget(universe).Identity.Equals(ownerId)) return;
            ReconcileOwner(ownerId);
        }

        internal void RegisterMap(Map map)
        {
            if (map == null || map.Disposed || universe.FrameOf(map)?.HasTag(SpaceObjectTag.Ship) != true)
                return;
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map)) For(ship);
            NotifyGunnerAvailabilityChanged(map);
        }

        internal void ReconcileOwner(int ownerId, Map changedMap = null) => ReconcileOwner(new DriveSubject(0, ownerId), changedMap);

        internal void ReconcileOwner(DriveSubject ownerId, Map changedMap = null)
        {
            if (ownerId.key <= 0) return;
            universe.Missions.ShipChanged(ownerId);
            if (changedMap != null && !changedMap.Disposed) universe.ShipStructure.For(changedMap);
            ShipBase ship = ResolveShip(ownerId);
            changedMap ??= ship.Map;
            if (ship.IsValid && ship.Faction != null) For(ship);
            else Remove(ownerId);
            universe.Weapons.QueuePower(changedMap);
        }

        private void Remove(DriveSubject ownerId)
        {
            if (!controllers.TryGetValue(ownerId, out ShipController controller)) return;
            if (loadoutSubscriptions.Remove(ownerId, out var subscription)) subscription.Handle.Dispose();
            controller.FireControl.Dispose();
            universe.ForgetPilotController(controller);
            universe.SoftDock.StopSteeringFor(ownerId);
            if (controller is PlayerShipController player) player.EndAim("ship lost");
            controller.Exit();
            universe.Autopilot.Detach(ownerId, "ship controller released");
            WorldShipMarkerRenderer.ForgetShip(ownerId);
            controllers.Remove(ownerId);

            if (activeAimOwnerId == ownerId) activeAimOwnerId = default;
        }

        internal void ForgetWeaponTarget(CompSpaceWeapon weapon)
        {
            foreach (ShipController controller in controllers.Values) controller.FireControl.ForgetWeapon(weapon);
        }

        internal void RefreshPointDefenseFilter(CompSpaceWeapon weapon)
        {
            if (controllers.TryGetValue(weapon.OwnerShip.Identity, out var controller))
                controller.FireControl.RefreshPointDefenseFilter(weapon);
        }

        internal void ForgetMap(Map map)
        {
            foreach (ShipController controller in controllers.Values) controller.FireControl.ForgetMap(map);
            (universe.FrameOf(map) as MapSpaceObject)?.ClearMemberCaches();
            pendingGunnerMaps.Remove(map);
            foreach (DriveSubject ownerId in new List<DriveSubject>(controllers.Keys))
            {
                ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
                if (!ship.IsValid || ship.Map == map) Remove(ownerId);
            }
        }

        internal void Rebuild()
        {
            Reset();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj?.map == null || obj.map.Disposed) continue;
                if (obj.HasTag(SpaceObjectTag.Ship))
                    foreach (ShipBase ship in obj.Ships) For(ship);
                NotifyGunnerAvailabilityChanged(obj.map);
            }
        }

        internal void Reset()
        {
            foreach (DriveSubject ownerId in new List<DriveSubject>(controllers.Keys)) Remove(ownerId);
            pendingGunnerMaps.Clear();
            activeAimOwnerId = default;
            lastPlayerDecisionTick = -1;
            tickBuffer.Clear();
        }

        public void Tick()
        {
            long stageStart = universe.Performance.Start();
            if (pendingGunnerMaps.Count > 0) ProcessGunnerEvents();
            SyncManualAimSelection();
            universe.Performance.Record(PerformanceStage.ControlSelection, stageStart);
            tickBuffer.Clear();
            tickBuffer.AddRange(controllers.Values);
            int now = Find.TickManager.TicksGame;
            int interval = UnityEngine.Mathf.Clamp(
                DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
            stageStart = universe.Performance.Start();
            if (lastPlayerDecisionTick < 0 || now - lastPlayerDecisionTick >= interval)
            {
                lastPlayerDecisionTick = now;
                foreach (ShipController controller in tickBuffer)
                    if (controller is PlayerShipController player && player.OwnerId.key > 0)
                    {
                        ShipBase ship = ShipRegistry.ShipByIdentity(player.OwnerId);
                        if (ship.IsValid) player.DecideAutoCombat(ship);
                    }
            }
            universe.Performance.Record(PerformanceStage.ControlDecision, stageStart);
            stageStart = universe.Performance.Start();
            bool hasScatterTargets = universe.Scatter.HasCandidates;
            bool hasInterceptionTargets = false;
            foreach (ShipController controller in tickBuffer)
                controller.TickCombat(ref hasInterceptionTargets, hasScatterTargets);
            universe.Performance.RecordPointDefenseTargets(hasInterceptionTargets, hasScatterTargets);
            universe.Performance.Record(PerformanceStage.ControlCombat, stageStart);
            tickBuffer.Clear();
        }

        internal void NotifyGunnerAvailabilityChanged(Map map)
        {
            // A destroyed console removes the Ship tag, but its crew still need
            // a defense duty and its remaining weapons still produce events.
            if (map != null && !map.Disposed && map.IsPocketMap
                && universe.FrameOf(map) is MapSpaceObject)
                pendingGunnerMaps.Add(map);
        }

        internal static void NotifyGunnerChange(Map map) =>
            Current.Game?.GetComponent<UniverseManager>()?.Controls
                .NotifyGunnerAvailabilityChanged(map);

        private void ProcessGunnerEvents()
        {
            var maps = new List<Map>(pendingGunnerMaps);
            pendingGunnerMaps.Clear();
            foreach (Map map in maps)
            {
                if (map == null || map.Disposed) continue;
                var playerOwners = new HashSet<DriveSubject>();
                foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
                {
                    ShipController controller = For(ship);
                    if (controller == null || !controller.GunnersEnabled) continue;
                    if (controller is PlayerShipController) playerOwners.Add(ship.Identity);
                    else universe.Weapons.DispatchGunners(map, ship.Faction, ship.Identity);
                }
                if (playerOwners.Count > 0)
                    universe.Weapons.DispatchGunners(map, Faction.OfPlayer, playerOwners);
                EnsureNpcDefenders(map);
            }
        }

        private void EnsureNpcDefenders(Map map)
        {
            if (!(universe.FrameOf(map) is MapSpaceObject frame)) return;
            Faction fallbackFaction = null;
            bool hasValidShip = false;
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
            {
                hasValidShip = true;
                if (ship.Faction == null || ship.Faction == Faction.OfPlayer) continue;
                if (fallbackFaction != null && fallbackFaction != ship.Faction)
                { fallbackFaction = null; break; }
                fallbackFaction = ship.Faction;
            }
            fallbackFaction ??= frame.npcFaction ?? frame.faction;
            if (fallbackFaction == Faction.OfPlayer) fallbackFaction = null;
            if (fallbackFaction == null && frame.npcHadShip)
            {
                // Existing wrecks can have lost both their console and recorded
                // faction. Recover only an unambiguous hostile crew faction.
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.Faction?.HostileTo(Faction.OfPlayer) != true) continue;
                    if (fallbackFaction != null && fallbackFaction != pawn.Faction)
                    { fallbackFaction = null; break; }
                    fallbackFaction = pawn.Faction;
                }
            }

            var groups = new Dictionary<DriveSubject, List<Pawn>>();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn?.Spawned != true || pawn.Downed || !pawn.RaceProps.Humanlike
                    || pawn.Faction?.HostileTo(Faction.OfPlayer) != true
                    || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony
                    || pawn.GetLord() != null || pawn.mindState?.duty != null
                    || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.AnyPlayerForced
                    || SpaceWeaponManning.WeaponFor(pawn.CurJob) != null) continue;

                ShipBase ship = ShipRegistry.ShipOf(pawn);
                DriveSubject group;
                if (ship.IsValid)
                {
                    if (ship.Faction != pawn.Faction) continue;
                    group = ship.Identity;
                }
                else
                {
                    if (pawn.Faction != fallbackFaction
                        || hasValidShip && frame.npcFaction != pawn.Faction
                            && frame.faction != pawn.Faction) continue;
                    group = default;
                }
                if (!groups.TryGetValue(group, out List<Pawn> crew))
                    groups[group] = crew = new List<Pawn>();
                crew.Add(pawn);
            }
            foreach (var entry in groups)
            {
                List<Pawn> crew = entry.Value;
                Faction faction = crew[0].Faction;
                Lord existing = map.lordManager.lords.Find(lord => lord.faction == faction
                    && lord.LordJob is LordJob_NpcShipDefenders defenders && defenders.Ship == entry.Key);
                if (existing != null) existing.AddPawns(crew);
                else LordMaker.MakeNewLord(faction, new LordJob_NpcShipDefenders(entry.Key), map, crew);
                foreach (Pawn pawn in crew)
                    if (pawn.CurJob?.def == JobDefOf.EnterPortal
                        && pawn.CurJob.targetA.Thing is SoftDockPortal)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
        }

        public bool AimActive => activeAimOwnerId.key > 0
            && controllers.TryGetValue(activeAimOwnerId, out ShipController controller)
            && controller is PlayerShipController player && player.AimActive;

        public bool AimActiveFor(ShipBase ship) => ship.IsValid && AimActive
            && activeAimOwnerId == ship.Identity;

        internal bool ManualAimOwner(DriveSubject ownerId) => activeAimOwnerId == ownerId && AimActive;

        public ShipFireControlMode FireControlModeFor(ShipBase ship) => ship.IsValid
            ? ship.EffectiveFireControlMode : ShipFireControlMode.Automatic;

        public bool AimSessionOnFocusedMap
        {
            get
            {
                SyncManualAimSelection();
                return AimActive
                    && ((PlayerShipController)controllers[activeAimOwnerId]).AimSessionOnFocusedMap;
            }
        }

        private void SyncManualAimSelection()
        {
            Map map = Find.CurrentMap;
            ShipBase selected = map != null ? universe.Pilot.SelectedShipOnMap(map) : ShipBase.Invalid;
            DriveSubject wanted = universe.Pilot.CanPlayerControl(selected)
                && selected.Combat?.weaponsArmed == true
                && selected.EffectiveFireControlMode == ShipFireControlMode.Manual
                ? selected.Identity : default;
            if (wanted == activeAimOwnerId && (wanted.key == 0 || AimActive)) return;
            DriveSubject oldOwnerId = activeAimOwnerId;
            bool endedOldSession = false;
            if (oldOwnerId.key > 0 && controllers.TryGetValue(oldOwnerId, out ShipController old)
                && old is PlayerShipController previous)
            {
                ShipBase priorShip = ShipRegistry.ShipByIdentity(oldOwnerId);
                // 切走聚焦图时保留会话；同图改选或切回自动火控时结束。
                if (!priorShip.IsValid || priorShip.Map == map)
                {
                    previous.EndAim("selection changed");
                    endedOldSession = true;
                }
            }
            activeAimOwnerId = default;
            if (endedOldSession) ReleaseDebugControl(oldOwnerId);
            if (wanted.key == 0) return;
            PlayerShipController player = PlayerFor(selected, debugTakeover: true);
            if (player == null) return;
            if (!player.AimActive) player.BeginAim();
            activeAimOwnerId = wanted;
        }

        public void SetFireControlMode(ShipBase ship, ShipFireControlMode mode)
        {
            if (!ship.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlayerShipController player = PlayerFor(ship, debugTakeover: true);
            if (player == null || !WeaponsArmed(ship) || !ship.SupportsFireControlMode(mode)) return;
            ship.Combat.fireControlMode = mode;
            if (ship.Combat.fireControlMode == ShipFireControlMode.Disabled)
            {
                player.EndAim("fire control disabled");
                foreach (var weapon in ship.Weapons)
                {
                    ForgetWeaponTarget(weapon);
                    universe.Scatter.ReleaseAutomatic(weapon);
                }
            }
            SyncManualAimSelection();
            if (ship.Combat.fireControlMode == ShipFireControlMode.Automatic)
                player.DecideAutoCombat(ship);
        }

        public void ToggleFireControl(ShipBase ship)
        {
            if (!WeaponsArmed(ship)) return;
            int next = (int)ship.EffectiveFireControlMode;
            do { next = (next + 1) % 3; }
            while (!ship.SupportsFireControlMode((ShipFireControlMode)next));
            SetFireControlMode(ship, (ShipFireControlMode)next);
        }

        internal bool SelectPlayerAutoTask(ShipBase ship, PlayerAutoTaskKind task,
            PlayerNavigationTarget navigation = default, bool activate = true)
        {
            if (!ship.IsValid) return false;
            PlayerShipController player = PlayerFor(ship);
            if (player == null || player.EntryBlockReason(PilotTarget.Of(ship)) != PilotEntryBlockReason.None
                || universe.FrameOf(ship.Map)?.projectileRoleOverride == true) return false;
            if (player.ManualControl) universe.Pilot.Exit(universe, resumeAutoTask: false);
            player.ReplaceAutoTask(task, navigation, activate);
            return true;
        }

        internal int EmitAimMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            SyncManualAimSelection();
            if (!AimActive) return baseSlot;
            return ((PlayerShipController)controllers[activeAimOwnerId])
                .EmitAimMarkers(frame, frameMap, layer, baseSlot);
        }

        internal void ProcessAimInputEvent()
        {
            SyncManualAimSelection();
            if (AimActive) ((PlayerShipController)controllers[activeAimOwnerId]).ProcessAimInputEvent();
        }

        public bool WeaponsArmed(ShipBase ship) => ship.IsValid
            && ship.Combat?.weaponsArmed == true;

        internal void SetWeaponGroupMask(ShipBase ship, SpaceWeaponSalvoSet set, int mask)
        {
            if (ship?.IsValid != true || ship.Combat == null) return;
            int changed = ship.Combat.WeaponGroupMask(set) ^ mask;
            if (changed == 0) return;
            ship.Combat.SetWeaponGroupMask(set, mask);
            universe.Weapons.SyncShipPowerRequests(ship, set, changed);
        }

        public void ArmWeapons(ShipBase ship)
        {
            if (!ship.IsValid || PlayerFor(ship, debugTakeover: true) is not PlayerShipController player) return;
            if (ship.Combat.weaponsArmed) return;
            ship.Combat.weaponsArmed = true;
            universe.Weapons.SyncShipPowerRequests(ship);
            int manned = universe.Weapons.DispatchGunners(ship.Map, Faction.OfPlayer, ship.Identity);
            SyncManualAimSelection();
            if (ship.EffectiveFireControlMode == ShipFireControlMode.Automatic)
                player.DecideAutoCombat(ship);
            Messages.Message("DeltaV_WeaponsArmed".Translate(manned), MessageTypeDefOf.NeutralEvent, false);
        }

        public void ReassignGunners(ShipBase ship)
        {
            if (!WeaponsArmed(ship) || PlayerFor(ship) == null) return;
            var owner = new HashSet<DriveSubject> { ship.Identity };
            universe.Weapons.EndBattleStationJobs(ship.Map, Faction.OfPlayer, owner);
            int manned = universe.Weapons.DispatchGunners(ship.Map, Faction.OfPlayer, owner);
            Messages.Message("DeltaV_GunnersReassigned".Translate(manned), MessageTypeDefOf.NeutralEvent, false);
        }

        public void DisarmWeapons(ShipBase ship)
        {
            if (!ship.IsValid || PlayerFor(ship) is not PlayerShipController player) return;
            if (!ship.Combat.weaponsArmed) return;
            ship.Combat.weaponsArmed = false;
            player.EndAim("weapons disarmed");
            player.FireControl.Clear();
            player.AutoManeuver.Clear(ship);
            foreach (CompSpaceWeapon weapon in ship.Weapons)
            {
                ForgetWeaponTarget(weapon);
                universe.Scatter.ReleaseAutomatic(weapon);
                weapon.SyncPowerRequest();
            }
            int released = universe.Weapons.EndBattleStationJobs(ship.Map, Faction.OfPlayer,
                new HashSet<DriveSubject> { ship.Identity });
            SyncManualAimSelection();
            Messages.Message("DeltaV_WeaponsDisarmed".Translate(released), MessageTypeDefOf.NeutralEvent, false);
        }

    }
}

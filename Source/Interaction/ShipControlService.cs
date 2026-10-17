using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>一艘船一个运行时 controller。身份使用带种类的 DriveSubject，地图只在使用时重查。</summary>
    public sealed class ShipControlService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<DriveSubject, ShipController> controllers = new Dictionary<DriveSubject, ShipController>();
        private readonly HashSet<Map> pendingGunnerMaps = new HashSet<Map>();
        private readonly List<ShipController> tickBuffer = new List<ShipController>();
        private DriveSubject activeAimOwnerId;
        private int lastPlayerDecisionTick = -1;

        private readonly Dictionary<DriveSubject, ShipBase> shipsByIdentity = new Dictionary<DriveSubject, ShipBase>();
        private int identityVersion = -1, identityMeasuredAt = -1;
        internal ShipBase ResolveShip(DriveSubject identity)
        {
            if (identity.key <= 0) return ShipBase.Invalid;
            if (SubstructureOwnership.CacheNeedsRefresh(identityVersion, identityMeasuredAt))
            {
                shipsByIdentity.Clear();
                foreach (Map map in Find.Maps)
                    foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map)) shipsByIdentity[ship.Identity] = ship;
                identityVersion = SubstructureOwnership.GlobalStructureVersion;
                identityMeasuredAt = Find.TickManager.TicksGame;
            }
            return shipsByIdentity.TryGetValue(identity, out ShipBase found) && found.IsValid ? found : ShipBase.Invalid;
        }

        internal ShipControlService(UniverseManager universe) { this.universe = universe; }

        public ShipController For(ShipBase ship, bool debugTakeover = false)
        {
            if (!ship.IsValid || universe.FrameOf(ship.Map)?.HasTag(SpaceObjectTag.Ship) != true) return null;
            shipsByIdentity[ship.Identity] = ship;
            Faction faction = ship.Faction;
            if (faction == null) return null;
            bool playerAuthority = faction == Faction.OfPlayer
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
            return controller;
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

        internal void ReconcileOwner(int ownerId) => ReconcileOwner(new DriveSubject(0, ownerId));

        internal void ReconcileOwner(DriveSubject ownerId)
        {
            if (ownerId.key <= 0) return;
            SubstructureOwnership.BumpStructureVersion();
            ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
            if (ship.IsValid && ship.Faction != null) For(ship);
            else Remove(ownerId);
        }

        private void Remove(DriveSubject ownerId)
        {
            if (!controllers.TryGetValue(ownerId, out ShipController controller)) return;
            universe.ForgetPilotController(controller);
            if (controller is PlayerShipController player) player.EndAim("ship lost");
            controller.Exit();
            universe.Autopilot.Detach(ownerId, "ship controller released");
            WorldShipMarkerRenderer.ForgetShip(ownerId);
            controllers.Remove(ownerId);

            if (activeAimOwnerId == ownerId) activeAimOwnerId = default;
        }

        internal void ForgetMap(Map map)
        {
            shipsByIdentity.Clear();
            identityVersion = -1;
            (universe.FrameOf(map) as MapSpaceObject)?.ClearMemberCaches();
            pendingGunnerMaps.Remove(map);
            foreach (DriveSubject ownerId in new List<DriveSubject>(controllers.Keys))
            {
                ShipBase ship = ShipRegistry.ShipByIdentity(ownerId);
                if (!ship.IsValid || ship.Map == map) Remove(ownerId);
            }
            shipsByIdentity.Clear();
            identityVersion = -1;
        }

        internal void Rebuild()
        {
            Reset();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj?.map == null || obj.map.Disposed || obj.HasTag(SpaceObjectTag.Ship) != true) continue;
                foreach (ShipBase ship in obj.Ships) For(ship);
            }
        }

        internal void Reset()
        {
            shipsByIdentity.Clear();
            identityVersion = -1;
            foreach (DriveSubject ownerId in new List<DriveSubject>(controllers.Keys)) Remove(ownerId);
            shipsByIdentity.Clear();
            identityVersion = -1;
            pendingGunnerMaps.Clear();
            activeAimOwnerId = default;
            lastPlayerDecisionTick = -1;
            tickBuffer.Clear();
        }

        public void Tick()
        {
            if (pendingGunnerMaps.Count > 0) ProcessGunnerEvents();
            SyncManualAimSelection();
            tickBuffer.Clear();
            tickBuffer.AddRange(controllers.Values);
            int now = Find.TickManager.TicksGame;
            int interval = UnityEngine.Mathf.Clamp(
                DeltaVMod.CurrentSettings?.npcDecisionIntervalTicks ?? 60, 1, 600);
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
            foreach (ShipController controller in tickBuffer)
                controller.TickCombat();
            tickBuffer.Clear();
        }

        internal void NotifyGunnerAvailabilityChanged(Map map)
        {
            if (map != null && !map.Disposed && universe.FrameOf(map)?.HasTag(SpaceObjectTag.Ship) == true)
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
            }
        }

        public bool AimActive => activeAimOwnerId.key > 0
            && controllers.TryGetValue(activeAimOwnerId, out ShipController controller)
            && controller is PlayerShipController player && player.AimActive;

        public bool AimActiveFor(ShipBase ship) => ship.IsValid && AimActive
            && activeAimOwnerId == ship.Identity;

        internal bool ManualAimOwner(DriveSubject ownerId) => activeAimOwnerId == ownerId && AimActive;

        public ShipFireControlMode FireControlModeFor(ShipBase ship) => ship.IsValid
            ? ship.Combat.fireControlMode : ShipFireControlMode.Automatic;

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
            DriveSubject wanted = selected.IsValid && (selected.Faction == Faction.OfPlayer
                || DebugSettings.godMode)
                && selected.Combat.fireControlMode == ShipFireControlMode.Manual
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

        public void ToggleFireControl(ShipBase ship)
        {
            if (!ship.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlayerShipController player = PlayerFor(ship, debugTakeover: true);
            if (player == null) return;
            ship.Combat.fireControlMode = ship.Combat.fireControlMode == ShipFireControlMode.Automatic
                ? ShipFireControlMode.Manual : ShipFireControlMode.Automatic;
            SyncManualAimSelection();
            if (ship.Combat.fireControlMode == ShipFireControlMode.Automatic)
                player.DecideAutoCombat(ship);
        }

        public bool AutoManeuverActiveFor(ShipBase ship) => ship.IsValid
            && controllers.TryGetValue(ship.Identity, out ShipController controller)
            && controller is PlayerShipController player && player.AutoManeuverEnabled;

        public void ToggleAutoManeuver(ShipBase ship)
        {
            if (!ship.IsValid) return;
            PlayerShipController player = PlayerFor(ship);
            if (player == null) return;
            bool enable = !player.AutoManeuverEnabled;
            if (enable)
            {
                if (universe.FrameOf(ship.Map)?.projectileRoleOverride == true) return;
                if (universe.Pilot.PilotedShip(universe).Identity == ship.Identity)
                    universe.Pilot.Exit(universe);
                universe.Autopilot.Detach(DriveSubject.Of(ship), "combat maneuver engaged");
                player.SetAutoManeuver(true);
                universe.Pilot.ArmAutopilotInput(ship);
                player.DecideAutoCombat(ship);
            }
            else
            {
                player.SetAutoManeuver(false);
                universe.Pilot.DisarmAutopilotInput(ship);
            }
        }

        internal int EmitAimMarkers(SpaceObject frame, Map frameMap, ShellLayer layer, int baseSlot)
        {
            SyncManualAimSelection();
            if (!AimActive) return baseSlot;
            return ((PlayerShipController)controllers[activeAimOwnerId])
                .EmitAimMarkers(frame, frameMap, layer, baseSlot);
        }

        public bool BattleStationsActive(Map map)
        {
            if (map == null) return false;
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
                if (controllers.TryGetValue(ship.Identity, out ShipController controller)
                    && controller is PlayerShipController player && player.GunnersEnabled) return true;
            return false;
        }

        public void EnterBattleStations(Map map)
        {
            if (map == null) return;
            var owners = new HashSet<DriveSubject>();
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
            {
                PlayerShipController player = PlayerFor(ship);
                if (player == null) continue;
                player.SetBattleStations(true);
                owners.Add(ship.Identity);
            }
            universe.Weapons.EndBattleStationJobs(map, Faction.OfPlayer, owners);
            int manned = owners.Count > 0
                ? universe.Weapons.DispatchGunners(map, Faction.OfPlayer, owners) : 0;
            Messages.Message("DeltaV_BattleStationsOn".Translate(manned), MessageTypeDefOf.NeutralEvent, false);
        }

        public void ExitBattleStations(Map map)
        {
            if (map == null) return;
            var owners = new HashSet<DriveSubject>();
            foreach (ShipBase ship in ShipRegistry.AllShipsOnMap(map))
                if (controllers.TryGetValue(ship.Identity, out ShipController controller)
                    && controller is PlayerShipController player)
                {
                    player.SetBattleStations(false);
                    owners.Add(ship.Identity);
                }
            int released = universe.Weapons.EndBattleStationJobs(map, Faction.OfPlayer, owners);
            Messages.Message("DeltaV_BattleStationsOff".Translate(released), MessageTypeDefOf.NeutralEvent, false);
        }

    }
}

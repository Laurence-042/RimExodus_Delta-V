using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>一艘船一个运行时 controller。身份是结构归属 ID，地图只在使用时重查。</summary>
    public sealed class ShipControlService
    {
        private readonly UniverseManager universe;
        private readonly Dictionary<int, ShipController> controllers = new Dictionary<int, ShipController>();
        private readonly HashSet<Map> pendingGunnerMaps = new HashSet<Map>();
        private readonly List<ShipController> tickBuffer = new List<ShipController>();
        private int activeAimOwnerId;
        private int lastPlayerDecisionTick = -1;

        internal ShipControlService(UniverseManager universe) { this.universe = universe; }

        public ShipController For(Ship ship, bool debugTakeover = false)
        {
            if (!ship.IsValid || universe.FrameOf(ship.map)?.Tag != SpaceObjectTag.Ship) return null;
            Faction faction = ShipRegistry.FactionOf(ship);
            if (faction == null) return null;
            bool playerAuthority = faction == Faction.OfPlayer
                || DebugSettings.godMode && (debugTakeover
                    || controllers.TryGetValue(ship.ownerId, out ShipController current)
                    && current is PlayerShipController
                    && (activeAimOwnerId == ship.ownerId
                        || universe.IsPilotControllingOwner(ship.ownerId)));
            if (controllers.TryGetValue(ship.ownerId, out ShipController existing))
            {
                if (playerAuthority && existing is PlayerShipController
                    || !playerAuthority && existing is NpcShipController)
                {
                    existing.BindShip(ship);
                    return existing;
                }
                Remove(ship.ownerId);
            }
            ShipController controller = playerAuthority
                ? new PlayerShipController(universe, ship.ownerId)
                : new NpcShipController(universe, ship.ownerId)
                {
                    Ai = new NpcCombatAi(universe, ship.ownerId)
                };
            controllers.Add(ship.ownerId, controller);
            controller.BindShip(ship);
            return controller;
        }

        internal PlayerShipController PlayerFor(Ship ship, bool debugTakeover = false)
            => For(ship, debugTakeover) as PlayerShipController;
        internal NpcShipController NpcFor(Ship ship) => For(ship) as NpcShipController;

        internal void ReleaseDebugControl(int ownerId)
        {
            if (AimActiveFor(ShipRegistry.ShipByOwner(ownerId))
                || universe.IsPilotControllingOwner(ownerId)) return;
            ReconcileOwner(ownerId);
        }

        internal void RegisterMap(Map map)
        {
            if (map == null || map.Disposed || universe.FrameOf(map)?.Tag != SpaceObjectTag.Ship)
                return;
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map)) For(ship);
            NotifyGunnerAvailabilityChanged(map);
        }

        internal void ReconcileOwner(int ownerId)
        {
            if (ownerId <= 0) return;
            Ship ship = ShipRegistry.ShipByOwner(ownerId);
            if (ship.IsValid) For(ship);
            else Remove(ownerId);
        }

        private void Remove(int ownerId)
        {
            if (!controllers.TryGetValue(ownerId, out ShipController controller)) return;
            universe.ForgetPilotController(controller);
            if (controller is PlayerShipController player) player.EndAim("ship lost");
            controller.Exit();
            controllers.Remove(ownerId);
            if (activeAimOwnerId == ownerId) activeAimOwnerId = 0;
        }

        internal void ForgetMap(Map map)
        {
            pendingGunnerMaps.Remove(map);
            foreach (int ownerId in new List<int>(controllers.Keys))
            {
                Ship ship = ShipRegistry.ShipByOwner(ownerId);
                if (!ship.IsValid || ship.map == map) Remove(ownerId);
            }
        }

        internal void Rebuild()
        {
            Reset();
            foreach (SpaceObject obj in universe.objects)
            {
                if (obj?.map == null || obj.map.Disposed || obj.Tag != SpaceObjectTag.Ship) continue;
                foreach (Ship ship in ShipRegistry.ShipsOnMap(obj.map)) For(ship);
            }
        }

        internal void Reset()
        {
            foreach (int ownerId in new List<int>(controllers.Keys)) Remove(ownerId);
            pendingGunnerMaps.Clear();
            activeAimOwnerId = 0;
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
                    if (controller is PlayerShipController player && player.OwnerId > 0)
                    {
                        Ship ship = ShipRegistry.ShipByOwner(player.OwnerId);
                        if (ship.IsValid) player.DecideAutoCombat(ship);
                    }
            }
            foreach (ShipController controller in tickBuffer)
                controller.TickCombat();
            tickBuffer.Clear();
        }

        internal void NotifyGunnerAvailabilityChanged(Map map)
        {
            if (map != null && !map.Disposed && universe.FrameOf(map)?.Tag == SpaceObjectTag.Ship)
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
                var playerOwners = new HashSet<int>();
                foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                {
                    ShipController controller = For(ship);
                    if (controller == null || !controller.GunnersEnabled) continue;
                    if (controller is PlayerShipController) playerOwners.Add(ship.ownerId);
                    else universe.Weapons.DispatchGunners(map, ShipRegistry.FactionOf(ship), ship.ownerId);
                }
                if (playerOwners.Count > 0)
                    universe.Weapons.DispatchGunners(map, Faction.OfPlayer, playerOwners);
            }
        }

        public bool AimActive => activeAimOwnerId > 0
            && controllers.TryGetValue(activeAimOwnerId, out ShipController controller)
            && controller is PlayerShipController player && player.AimActive;

        public bool AimActiveFor(Ship ship) => ship.IsValid && AimActive
            && activeAimOwnerId == ship.ownerId;

        internal bool ManualAimOwner(int ownerId) => activeAimOwnerId == ownerId && AimActive;

        public ShipFireControlMode FireControlModeFor(Ship ship) => ship.IsValid
            ? ship.console.fireControlMode : ShipFireControlMode.Automatic;

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
            Ship selected = map != null ? universe.Pilot.SelectedShipOnMap(map) : Ship.Invalid;
            int wanted = selected.IsValid && (ShipRegistry.FactionOf(selected) == Faction.OfPlayer
                || DebugSettings.godMode)
                && selected.console.fireControlMode == ShipFireControlMode.Manual
                ? selected.ownerId : 0;
            if (wanted == activeAimOwnerId && (wanted == 0 || AimActive)) return;
            int oldOwnerId = activeAimOwnerId;
            bool endedOldSession = false;
            if (oldOwnerId > 0 && controllers.TryGetValue(oldOwnerId, out ShipController old)
                && old is PlayerShipController previous)
            {
                Ship priorShip = ShipRegistry.ShipByOwner(oldOwnerId);
                // 切走聚焦图时保留会话；同图改选或切回自动火控时结束。
                if (!priorShip.IsValid || priorShip.map == map)
                {
                    previous.EndAim("selection changed");
                    endedOldSession = true;
                }
            }
            activeAimOwnerId = 0;
            if (endedOldSession) ReleaseDebugControl(oldOwnerId);
            if (wanted == 0) return;
            PlayerShipController player = PlayerFor(selected, debugTakeover: true);
            if (player == null) return;
            if (!player.AimActive) player.BeginAim();
            activeAimOwnerId = wanted;
        }

        public void ToggleFireControl(Ship ship)
        {
            if (!ship.IsValid)
            {
                Messages.Message("DeltaV_PilotNeedShipFocus".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlayerShipController player = PlayerFor(ship, debugTakeover: true);
            if (player == null) return;
            ship.console.fireControlMode = ship.console.fireControlMode == ShipFireControlMode.Automatic
                ? ShipFireControlMode.Manual : ShipFireControlMode.Automatic;
            SyncManualAimSelection();
            if (ship.console.fireControlMode == ShipFireControlMode.Automatic)
                player.DecideAutoCombat(ship);
        }

        public bool AutoManeuverActiveFor(Ship ship) => ship.IsValid
            && controllers.TryGetValue(ship.ownerId, out ShipController controller)
            && controller is PlayerShipController player && player.AutoManeuverEnabled;

        public void ToggleAutoManeuver(Ship ship)
        {
            if (!ship.IsValid) return;
            PlayerShipController player = PlayerFor(ship);
            if (player == null) return;
            bool enable = !player.AutoManeuverEnabled;
            if (enable)
            {
                if (universe.Pilot.PilotedShip(universe).ownerId == ship.ownerId)
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
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                if (controllers.TryGetValue(ship.ownerId, out ShipController controller)
                    && controller is PlayerShipController player && player.GunnersEnabled) return true;
            return false;
        }

        public void EnterBattleStations(Map map)
        {
            if (map == null) return;
            var owners = new HashSet<int>();
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
            {
                PlayerShipController player = PlayerFor(ship);
                if (player == null) continue;
                player.SetBattleStations(true);
                owners.Add(ship.ownerId);
            }
            universe.Weapons.EndBattleStationJobs(map, Faction.OfPlayer, owners);
            int manned = owners.Count > 0
                ? universe.Weapons.DispatchGunners(map, Faction.OfPlayer, owners) : 0;
            Messages.Message("DeltaV_BattleStationsOn".Translate(manned), MessageTypeDefOf.NeutralEvent, false);
        }

        public void ExitBattleStations(Map map)
        {
            if (map == null) return;
            var owners = new HashSet<int>();
            foreach (Ship ship in ShipRegistry.ShipsOnMap(map))
                if (controllers.TryGetValue(ship.ownerId, out ShipController controller)
                    && controller is PlayerShipController player)
                {
                    player.SetBattleStations(false);
                    owners.Add(ship.ownerId);
                }
            int released = universe.Weapons.EndBattleStationJobs(map, Faction.OfPlayer, owners);
            Messages.Message("DeltaV_BattleStationsOff".Translate(released), MessageTypeDefOf.NeutralEvent, false);
        }

    }
}

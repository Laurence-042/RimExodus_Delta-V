using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    [Flags]
    internal enum ShipStructureAspect
    {
        Foundation = 1, Terrain = 2, Roof = 4, Membership = 8, Footprint = 16,
        Health = 32, Faction = 64, Cargo = 128, Shield = 256, Primary = 512,
        Items = 1024, Representation = 2048, Ownership = 4096, ShieldGeometry = 8192,
        Propulsion = 16384, HullBoundary = 32768, Loadout = 65536
    }

    internal readonly struct ShipStructureChange
    {
        internal readonly ShipStructureAspect Aspect;
        internal readonly Thing Thing;
        internal readonly IntVec3? Cell;
        internal readonly object Before, After;
        internal ShipStructureChange(ShipStructureAspect aspect, Thing thing, IntVec3? cell, object before, object after)
        { Aspect = aspect; Thing = thing; Cell = cell; Before = before; After = after; }
    }

    internal readonly struct ShipStructureBatch
    {
        internal readonly int Revision;
        internal readonly IReadOnlyList<ShipStructureChange> Changes;
        internal ShipStructureBatch(int revision, IReadOnlyList<ShipStructureChange> changes)
        { Revision = revision; Changes = changes; }
    }

    internal sealed class ShipStructureStream
    {
        private readonly List<Subscription> subscriptions = new List<Subscription>();
        private List<ShipStructureChange> pending;
        private bool publishing;
        internal int Revision { get; private set; }
        internal int GeometryRevision { get; private set; }
        internal int TransferRevision { get; private set; }
        private readonly int[] aspects = new int[17];

        internal IDisposable Watch(ShipStructureAspect aspects, Action<ShipStructureBatch> callback,
            Predicate<IntVec3> area = null)
        {
            var subscription = new Subscription(this, aspects, area, callback);
            subscriptions.Add(subscription);
            return subscription;
        }

        internal int RevisionOf(ShipStructureAspect mask)
        {
            int revision = 0;
            for (int i = 0; i < aspects.Length && (int)mask >> i != 0; i++)
                if (((int)mask & 1 << i) != 0) revision = Math.Max(revision, aspects[i]);
            return revision;
        }

        internal void Add(ShipStructureChange change)
        {
            Revision++;
            for (int i = 0; i < aspects.Length && (int)change.Aspect >> i != 0; i++)
                if (((int)change.Aspect & 1 << i) != 0) aspects[i] = Revision;
            const ShipStructureAspect geometry = ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                | ShipStructureAspect.Primary | ShipStructureAspect.Footprint | ShipStructureAspect.Membership
                | ShipStructureAspect.Representation | ShipStructureAspect.ShieldGeometry | ShipStructureAspect.Roof;
            if ((change.Aspect & geometry) != 0) GeometryRevision++;
            const ShipStructureAspect transfer = ShipStructureAspect.Foundation | ShipStructureAspect.Ownership
                | ShipStructureAspect.Primary | ShipStructureAspect.Footprint | ShipStructureAspect.Membership
                | ShipStructureAspect.Representation;
            if ((change.Aspect & transfer) != 0) TransferRevision++;
            if (subscriptions.Count != 0)
                (pending ??= new List<ShipStructureChange>()).Add(change);
        }

        internal void Publish()
        {
            if (publishing) return;
            publishing = true;
            try
            {
                while (pending != null)
                {
                    var changes = pending;
                    int revision = Revision;
                    pending = null;
                    foreach (Subscription subscription in subscriptions.ToArray())
                    {
                        if (subscription.Owner == null) continue;
                        var matches = changes.FindAll(subscription.Accepts);
                        if (matches.Count != 0) subscription.Callback(new ShipStructureBatch(revision, matches.AsReadOnly()));
                    }
                }
            }
            finally { publishing = false; }
        }

        internal void Release()
        {
            foreach (Subscription subscription in subscriptions) subscription.Owner = null;
            subscriptions.Clear();
            pending = null;
        }

        private sealed class Subscription : IDisposable
        {
            internal ShipStructureStream Owner;
            internal readonly Action<ShipStructureBatch> Callback;
            private readonly ShipStructureAspect aspects;
            private readonly Predicate<IntVec3> area;
            internal Subscription(ShipStructureStream owner, ShipStructureAspect aspects, Predicate<IntVec3> area,
                Action<ShipStructureBatch> callback)
            { Owner = owner; this.aspects = aspects; this.area = area; Callback = callback; }

            internal bool Accepts(ShipStructureChange change)
            {
                if ((aspects & change.Aspect) == 0) return false;
                if (area == null || change.Aspect == ShipStructureAspect.Representation) return true;
                if (change.Cell is IntVec3 cell && area(cell)) return true;
                return (change.Before is CellRect previous && previous.Any(position => area(position)))
                    || (change.After is CellRect current && current.Any(position => area(position)));
            }

            public void Dispose()
            {
                Owner?.subscriptions.Remove(this);
                Owner = null;
            }
        }
    }

    /// <summary>The runtime view of the ship's current representation, keyed by its drive identity.</summary>
    internal sealed class ShipStructure
    {
        private readonly ShipStructureStream stream = new ShipStructureStream();
        private readonly ShipBase ship;
        private ShipStructureIndex.MapView map;
        private ShipStructureIndex.Members members = new ShipStructureIndex.Members();
        private readonly Dictionary<Building, ShipPhysics.Thruster> installed = new Dictionary<Building, ShipPhysics.Thruster>();
        internal ShipPhysics.MassIndex Mass { get; private set; }
        internal HashSet<IntVec3> Foundation { get; set; }
        internal ShipBase Vessel => ship;
        internal Map Map => map?.Map;
        internal Thing Anchor => map == null ? null : ship.Anchor;
        internal int Revision => stream.Revision;
        internal int GeometryRevision => stream.GeometryRevision;
        internal int TransferRevision => stream.TransferRevision;
        internal bool HasTurret => members.HasTurret;
        internal IEnumerable<Building> Buildings => members.Buildings;
        internal IEnumerable<Thing> Thrusters => installed.Keys;
        internal IEnumerable<CompSpaceWeapon> InstalledWeapons => members.InstalledWeapons;
        internal IEnumerable<CompSpaceShield> Shields => members.Shields;
        internal (double Available, double Maximum) CombatShieldTotals => members.CombatShieldTotals;
        internal IReadOnlyList<CompSpaceShield> WholeFootprintShields => members.WholeFootprintShields;
        internal double SharedGuardCapacity => members.SharedGuardCapacity;
        internal int GuardOutlineRevision => members.GuardOutlineRevision;
        internal IEnumerable<CompDeltaVPilotConsole> Consoles => members.Consoles;
        internal IEnumerable<Thing> ExposedWeapons => members.ExposedWeapons;
        internal IEnumerable<Thing> ExposedThrusters => members.ExposedThrusters;
        internal float HullHealth => members.HullHealth;
        internal float MaximumHullHealth => members.MaximumHullHealth;
        internal IEnumerable<CompShipCombatAI> CombatConsoles => ship is ShuttleShip
            ? Enumerable.Repeat(ship.CombatAI, 1).Where(comp => comp != null)
            : Consoles.Select(console => console.parent.TryGetComp<CompShipCombatAI>()).Where(comp => comp != null);
        internal Thing FindBuilding(int id) => members.BuildingById(id);
        internal bool Contains(IntVec3 cell) => ship is ShuttleShip ? Anchor?.OccupiedRect().Contains(cell) == true
            : Foundation?.Contains(cell) == true;
        internal IEnumerable<ShipPhysics.Thruster> InstalledThrusters => installed.Values;
        internal IReadOnlyList<ShipPhysics.Thruster> VirtualThrusters { get; private set; } = Array.Empty<ShipPhysics.Thruster>();

        internal void RefreshVirtualThrusters()
        {
            VirtualThrusters = ship.CreateVirtualThrusters().ToArray();
        }
        internal ShipPhysics.Thruster InstalledThruster(Building building) =>
            installed.TryGetValue(building, out var entry) ? entry : null;

        internal ShipStructure(ShipBase ship) => this.ship = ship;

        internal void Bind(ShipStructureIndex.MapView next)
        {
            map = next;
            Mass = new ShipPhysics.MassIndex(next.Map);
            RefreshVirtualThrusters();
        }

        internal void Add(ShipStructureIndex.BuildingRecord record)
        {
            members.Add(record);
            if (record.WholeDomain) members.SetWholeFootprintMembers(record, true);
            if (record.Shuttle != null) members.SetShuttleWeapons(record, true);
            Mass.SetBuilding(record.Building, record.Footprint, record.Weight);
            if (record.Thruster)
                installed.Add(record.Building, ShipPhysics.BuildInstalledThruster(record.Building, readOperational: false));
        }

        internal void Remove(ShipStructureIndex.BuildingRecord record)
        {
            members.Remove(record);
            if (record.Shuttle != null) members.SetShuttleWeapons(record, false);
            Mass.RemoveBuilding(record.Building);
            installed.Remove(record.Building);
        }

        internal bool RefreshThruster(Building building)
        {
            if (!installed.TryGetValue(building, out var entry)) return false;
            var next = ShipPhysics.BuildInstalledThruster(building, readOperational: false);
            if (entry.thrust == next.thrust && entry.dir == next.dir && entry.localPos == next.localPos) return false;
            entry.localPos = next.localPos;
            entry.dir = next.dir;
            entry.thrust = next.thrust;
            entry.visuals = next.visuals;
            entry.normalVisuals = next.normalVisuals;
            return true;
        }

        internal void SetWholeFootprint(ShipStructureIndex.BuildingRecord record, bool whole) =>
            members.SetWholeFootprintMembers(record, whole);
        internal void SetHealth(int before, int after) => members.SetHealth(before, after);
        internal void SetExposure(ShipStructureIndex.BuildingRecord record, CombatPart? before, CombatPart? after) =>
            members.SetExposure(record, before, after);

        internal void ShieldChanged(ShipStructureIndex.BuildingRecord record, SpaceShieldMode before, double previous) =>
            members.ChangeCombatShield(record.Shield, before, previous, record.Geometry.Mode,
                record.Capacity.Regular + record.Capacity.Emergency);
        internal void InvalidateGuardOutline() => members.GuardOutlineRevision++;
        internal void UpdateGuardCapacity(double delta) => members.UpdateGuardCapacity(delta);

        internal IDisposable Watch(ShipStructureAspect aspects, Action<ShipStructureBatch> callback,
            Predicate<IntVec3> area = null) => stream.Watch(aspects, callback, area);

        internal ShipStructureStream Stream => stream;

        internal void Unbind()
        {
            Foundation = null;
            map = null;
            Mass = null;
            VirtualThrusters = Array.Empty<ShipPhysics.Thruster>();
        }

        internal void Release()
        {
            stream.Release();
            installed.Clear();
            members = new ShipStructureIndex.Members();
            Unbind();
        }
    }

    /// <summary>Runtime reverse indices for Things in managed maps and the views that consume them.</summary>
    internal sealed class ShipStructureIndex
    {
        internal static (SpaceShieldMode Mode, float Radius, float Projection, float Length, float Angle) ShieldGeometry(CompSpaceShield shield)
            => (shield.mode, shield.groundRadius, shield.projection, shield.length, shield.angleDeg);

        internal static (double Regular, double Emergency, bool FlickedOn, GuardEmergencyReason Reason) ShieldCapacity(CompSpaceShield shield)
            => (shield.RegularCapacity, shield.EmergencyCapacity, shield.FlickedOn, shield.EmergencyReason);

        internal sealed class BuildingRecord
        {
            internal readonly Building Building;
            internal readonly CompSpaceShield Shield;
            internal (SpaceShieldMode Mode, float Radius, float Projection, float Length, float Angle) Geometry;
            internal (double Regular, double Emergency, bool FlickedOn, GuardEmergencyReason Reason) Capacity;
            internal double GuardContribution;
            internal readonly CompProjectileInterceptor Interceptor;
            internal readonly CompSpaceWeaponMagazine MissileChamber;
            internal readonly CompSpaceWeapon[] Weapons;
            internal readonly CompSpaceShuttle Shuttle;
            internal readonly CompDeltaVPilotConsole Console;
            internal readonly bool Thruster;
            internal readonly bool GlobeEquipment, GlobeArmed;
            internal int HitPoints;
            internal readonly int MaxHitPoints;
            internal CombatPart? ExposedPart;
            internal CellRect Footprint;
            internal HashSet<IntVec3> Domain;
            internal bool WholeDomain;
            internal ShipStructure Structure;
            internal double Weight;
            internal int FootprintOwner, OwnerBinding;
            internal (Faction Faction, int Owner)? ConsoleWarningGroup;

            internal BuildingRecord(Building building)
            {
                Building = building;
                Shield = building.TryGetComp<CompSpaceShield>();
                if (Shield != null)
                {
                    Geometry = ShieldGeometry(Shield);
                    Capacity = ShieldCapacity(Shield);
                    GuardContribution = Shield.RegularBankContribution;
                }
                Interceptor = building.TryGetComp<CompProjectileInterceptor>();
                Shuttle = building.TryGetComp<CompSpaceShuttle>();
                Weapons = building.AllComps.Count != 0
                    ? CompSpaceWeapon.On(building).ToArray() : Array.Empty<CompSpaceWeapon>();
                Console = building.TryGetComp<CompDeltaVPilotConsole>();
                if (building.TryGetComp<CompSpaceWeaponMagazine>() is { IsMissileChamber: true } magazine)
                    MissileChamber = magazine;
                Thruster = ThrusterPower.IsThruster(building);
                GlobeEquipment = GlobeDisplayRules.IsEquipment(building.def);
                GlobeArmed = building is Building_Turret || Weapons.FirstOrDefault() is { } weapon
                    && GlobeDisplayRules.IsWeapon(weapon);
                HitPoints = building.HitPoints;
                MaxHitPoints = Math.Max(1, building.MaxHitPoints);
                Footprint = building.OccupiedRect();
                Weight = ShipPhysics.BuildingWeight(building);
                ExposedPart = ShipFireControl.ExternalCombatPart(building);
            }
        }

        internal sealed class Members
        {
            private readonly Dictionary<int, Building> buildings = new Dictionary<int, Building>();
            private readonly HashSet<Thing> thrusters = new HashSet<Thing>();
            private readonly HashSet<CompSpaceWeapon> installedWeapons = new HashSet<CompSpaceWeapon>();
            private readonly HashSet<CompSpaceShield> shields = new HashSet<CompSpaceShield>();
            private readonly HashSet<CompSpaceShield> lineShields = new HashSet<CompSpaceShield>();
            private double guardAvailable, guardMaximum;
            private readonly List<CompSpaceShield> wholeFootprintShields = new List<CompSpaceShield>();
            private readonly HashSet<CompProjectileInterceptor> interceptors = new HashSet<CompProjectileInterceptor>();
            private readonly HashSet<CompDeltaVPilotConsole> consoles = new HashSet<CompDeltaVPilotConsole>();
            private readonly SortedDictionary<int, Thing> exposedWeapons = new SortedDictionary<int, Thing>();
            private readonly SortedDictionary<int, Thing> exposedThrusters = new SortedDictionary<int, Thing>();
            internal IEnumerable<Building> Buildings => buildings.Values;
            internal IEnumerable<Thing> Thrusters => thrusters;
            internal IEnumerable<CompSpaceWeapon> InstalledWeapons => installedWeapons;
            internal IEnumerable<CompSpaceShield> Shields => shields;
            internal (double Available, double Maximum) CombatShieldTotals
            {
                get
                {
                    double available = guardAvailable;
                    foreach (CompSpaceShield shield in lineShields)
                        if (shield.State == SpaceShieldState.LineActive) available++;
                    return (Math.Max(0, available), guardMaximum + lineShields.Count);
                }
            }
            internal IReadOnlyList<CompSpaceShield> WholeFootprintShields => wholeFootprintShields;
            internal double SharedGuardCapacity { get; private set; }
            internal int GuardOutlineRevision;
            internal IEnumerable<CompProjectileInterceptor> Interceptors => interceptors;
            internal IEnumerable<CompDeltaVPilotConsole> Consoles => consoles;
            internal IEnumerable<Thing> ExposedWeapons => exposedWeapons.Values;
            internal IEnumerable<Thing> ExposedThrusters => exposedThrusters.Values;
            internal long HullHealth { get; private set; }
            internal long MaximumHullHealth { get; private set; }
            private int globeEquipment, turrets;
            private readonly bool discoveryOnly;
            internal Members(bool discoveryOnly = false) => this.discoveryOnly = discoveryOnly;
            internal bool HasGlobeEquipment => globeEquipment != 0;
            internal bool HasTurret => turrets != 0;
            internal int Count => buildings.Count;
            internal Thing BuildingById(int id) => buildings.TryGetValue(id, out var building) ? building : null;

            internal void Add(BuildingRecord record)
            {
                Building building = record.Building;
                buildings.Add(building.thingIDNumber, building);
                if (record.GlobeEquipment) globeEquipment++;
                if (record.Interceptor != null) interceptors.Add(record.Interceptor);
                if (record.Console != null) consoles.Add(record.Console);
                if (discoveryOnly) return;
                if (building is Building_Turret) turrets++;
                HullHealth += record.HitPoints;
                MaximumHullHealth += record.MaxHitPoints;
                if (record.Thruster) thrusters.Add(building);
                if (record.Shield != null)
                {
                    shields.Add(record.Shield);
                    ChangeCombatShield(record.Shield, null, 0, record.Geometry.Mode, record.Capacity.Regular + record.Capacity.Emergency);
                }
                SetExposure(record, null, record.ExposedPart);
            }

            internal void Remove(BuildingRecord record)
            {
                Building building = record.Building;
                buildings.Remove(building.thingIDNumber);
                if (record.GlobeEquipment) globeEquipment--;
                if (record.Interceptor != null) interceptors.Remove(record.Interceptor);
                if (record.Console != null) consoles.Remove(record.Console);
                if (discoveryOnly) return;
                if (building is Building_Turret) turrets--;
                HullHealth -= record.HitPoints;
                MaximumHullHealth -= record.MaxHitPoints;
                thrusters.Remove(building);
                if (record.Shield != null)
                {
                    shields.Remove(record.Shield);
                    ChangeCombatShield(record.Shield, record.Geometry.Mode, record.Capacity.Regular + record.Capacity.Emergency, null, 0);
                }
                SetWholeFootprintMembers(record, false);
                SetExposure(record, record.ExposedPart, null);
            }

            internal void ChangeCombatShield(CompSpaceShield shield, SpaceShieldMode? before, double previous,
                SpaceShieldMode? after, double current)
            {
                int maximum = Math.Max(1, shield.ShieldProps.regularCapacity + shield.ShieldProps.emergencyCapacity);
                if (before == SpaceShieldMode.Guard)
                {
                    guardAvailable -= previous;
                    guardMaximum -= maximum;
                }
                else if (before.HasValue && before != after) lineShields.Remove(shield);
                if (after == SpaceShieldMode.Guard)
                {
                    guardAvailable += current;
                    guardMaximum += maximum;
                }
                else if (after.HasValue && before != after) lineShields.Add(shield);
            }

            internal void SetWholeFootprintMembers(BuildingRecord record, bool whole)
            {
                if (record.Shuttle == null)
                    foreach (CompSpaceWeapon weapon in record.Weapons)
                    {
                        if (whole) installedWeapons.Add(weapon);
                        else installedWeapons.Remove(weapon);
                    }
                if (record.Shield == null) return;
                if (whole)
                {
                    if (wholeFootprintShields.Contains(record.Shield)) return;
                    wholeFootprintShields.Add(record.Shield);
                    SharedGuardCapacity += record.GuardContribution;
                    GuardOutlineRevision++;
                }
                else if (wholeFootprintShields.Remove(record.Shield))
                {
                    SharedGuardCapacity -= record.GuardContribution;
                    GuardOutlineRevision++;
                }
            }

            internal void SetShuttleWeapons(BuildingRecord record, bool present)
            {
                foreach (CompSpaceWeapon weapon in record.Weapons)
                {
                    if (present) installedWeapons.Add(weapon);
                    else installedWeapons.Remove(weapon);
                }
            }

            internal void UpdateGuardCapacity(double delta) => SharedGuardCapacity += delta;

            internal void SetHealth(int before, int after) => HullHealth += (long)after - before;

            internal void SetExposure(BuildingRecord record, CombatPart? before, CombatPart? after)
            {
                int id = record.Building.thingIDNumber;
                if (before == CombatPart.Weapons) exposedWeapons.Remove(id);
                if (before == CombatPart.Propulsion) exposedThrusters.Remove(id);
                if (after == CombatPart.Weapons) exposedWeapons[id] = record.Building;
                if (after == CombatPart.Propulsion) exposedThrusters[id] = record.Building;
            }
        }

        internal sealed class MapView
        {
            private readonly Map map;
            private readonly FoundationTopologyService topology;
            internal Map Map => map;
            internal HashSet<IntVec3> PhysicalDomainAt(IntVec3 cell) => topology.PhysicalDomainAt(map, cell);
            private readonly Dictionary<int, Thing> things = new Dictionary<int, Thing>();
            private readonly Dictionary<Building, BuildingRecord> records = new Dictionary<Building, BuildingRecord>();
            private readonly Dictionary<HashSet<IntVec3>, HashSet<BuildingRecord>> domainBuildings =
                new Dictionary<HashSet<IntVec3>, HashSet<BuildingRecord>>();
            private readonly Dictionary<HashSet<IntVec3>, ShipStructure> domainRoutes =
                new Dictionary<HashSet<IntVec3>, ShipStructure>();
            private readonly Dictionary<Thing, ShipStructure> shuttleRoutes = new Dictionary<Thing, ShipStructure>();
            private readonly Dictionary<IntVec3, ShipStructure> cellRoutes = new Dictionary<IntVec3, ShipStructure>();
            private readonly HashSet<ShipStructure> boundShips = new HashSet<ShipStructure>();
            private readonly HashSet<BuildingRecord> dirtyRoutes = new HashSet<BuildingRecord>();
            private readonly ShipStructureIndex index;
            private readonly Dictionary<IntVec3, HashSet<BuildingRecord>> byCell = new Dictionary<IntVec3, HashSet<BuildingRecord>>();
            private readonly Dictionary<int, HashSet<CompSpaceWeaponMagazine>> missileChambers = new Dictionary<int, HashSet<CompSpaceWeaponMagazine>>();
            private readonly HashSet<CompSpaceWeaponMagazine> allMissileChambers = new HashSet<CompSpaceWeaponMagazine>();
            private readonly Dictionary<Type, object> buildingTypes = new Dictionary<Type, object>();
            private readonly HashSet<CompSpaceShuttle> shuttles = new HashSet<CompSpaceShuttle>();
            private readonly Dictionary<IntVec3, HashSet<Building_DockingPort>> channelsByCell = new Dictionary<IntVec3, HashSet<Building_DockingPort>>();
            private readonly Dictionary<Faction, Dictionary<int, HashSet<Building>>> consoleWarningGroups =
                new Dictionary<Faction, Dictionary<int, HashSet<Building>>>();
            private readonly HashSet<CompSpaceWeapon> mapWeapons = new HashSet<CompSpaceWeapon>();
            private readonly HashSet<Building> interceptionBodies = new HashSet<Building>();
            internal Building InterceptionBody => interceptionBodies.FirstOrDefault();
            internal IEnumerable<CompSpaceWeapon> Weapons => mapWeapons;
            private readonly Dictionary<int, CompDeltaVPilotConsole> primaryByOwner = new Dictionary<int, CompDeltaVPilotConsole>();
            private readonly Dictionary<CompDeltaVPilotConsole, int> ownerByPrimary = new Dictionary<CompDeltaVPilotConsole, int>();
            private readonly Dictionary<int, int> primaryBindings = new Dictionary<int, int>();
            private int ownerBindingSerial;
            internal IEnumerable<CompDeltaVPilotConsole> Primaries => primaryByOwner.Values;
            internal int PrimaryOwner(CompDeltaVPilotConsole console) => ownerByPrimary.TryGetValue(console, out int owner) ? owner : 0;
            internal CompDeltaVPilotConsole Primary(int owner) => primaryByOwner.TryGetValue(owner, out var console) ? console : null;

            internal void UpdatePrimary(CompDeltaVPilotConsole console, bool remove = false)
            {
                if (console == null) return;
                if (ownerByPrimary.Remove(console, out int owner) && Primary(owner) == console)
                {
                    primaryByOwner.Remove(owner);
                    primaryBindings[owner] = ++ownerBindingSerial;
                }
                if (remove || console.parent.Map != map || !console.Ship.IsValid) return;
                ownerByPrimary[console] = console.shipId;
                primaryByOwner[console.shipId] = console;
                primaryBindings[console.shipId] = ++ownerBindingSerial;
            }
            internal int WeaponOwnerBinding(Building building)
            {
                // Synchronize queued foundation edges before reading the indexed membership.
                topology.BuildingDomain(map, building);
                var record = Record(building);
                if (record == null) return -1;
                primaryBindings.TryGetValue(record.FootprintOwner, out int primaryRevision);
                return Math.Max(record.OwnerBinding, primaryRevision);
            }

            internal IEnumerable<CompSpaceWeaponMagazine> MissileChambers(int owner) => owner <= 0
                ? allMissileChambers : missileChambers.TryGetValue(owner, out var group)
                    ? group : Enumerable.Empty<CompSpaceWeaponMagazine>();

            private void MoveMissileChamber(CompSpaceWeaponMagazine chamber, int before, int after)
            {
                if (before == after) return;
                if (before > 0 && missileChambers.TryGetValue(before, out var former))
                {
                    former.Remove(chamber);
                    if (former.Count == 0) missileChambers.Remove(before);
                }
                if (after <= 0) return;
                if (!missileChambers.TryGetValue(after, out var group))
                    missileChambers.Add(after, group = new HashSet<CompSpaceWeaponMagazine>());
                group.Add(chamber);
            }

            internal IEnumerable<Building> MissingPrimaryTargets(Faction faction)
            {
                if (!consoleWarningGroups.TryGetValue(faction, out var groups)) yield break;
                foreach (var pair in groups)
                    if (Primary(pair.Key)?.Ship.IsValid != true) yield return pair.Value.First();
            }

            internal void UpdateConsoleWarning(BuildingRecord record, bool remove = false)
            {
                var building = record.Building;
                (Faction Faction, int Owner)? next = null;
                if (!remove && map.terrainGrid != null && record.Shuttle == null && building.Faction is Faction faction
                    && SubstructureOwnership.HasFoundation(map, building.Position))
                    next = (faction, SubstructureOwnership.TryGet(map)?.OwnerAt(building.Position) ?? 0);
                if (record.ConsoleWarningGroup == next) return;
                if (record.ConsoleWarningGroup is { } previous
                    && consoleWarningGroups.TryGetValue(previous.Faction, out var oldOwners)
                    && oldOwners.TryGetValue(previous.Owner, out var oldMembers))
                {
                    oldMembers.Remove(building);
                    if (oldMembers.Count == 0) oldOwners.Remove(previous.Owner);
                    if (oldOwners.Count == 0) consoleWarningGroups.Remove(previous.Faction);
                }
                record.ConsoleWarningGroup = next;
                if (next is not { } added) return;
                if (!consoleWarningGroups.TryGetValue(added.Faction, out var owners))
                    consoleWarningGroups.Add(added.Faction, owners = new Dictionary<int, HashSet<Building>>());
                if (!owners.TryGetValue(added.Owner, out var members))
                    owners.Add(added.Owner, members = new HashSet<Building>());
                members.Add(building);
            }

            internal void UpdateFootprintOwners(IntVec3 cell)
            {
                if (!byCell.TryGetValue(cell, out var affected)) return;
                var ownership = SubstructureOwnership.TryGet(map);
                foreach (BuildingRecord record in affected)
                {
                    int owner = OwnerOfFootprint(ownership, record.Footprint);
                    if (record.MissileChamber != null)
                        MoveMissileChamber(record.MissileChamber, record.FootprintOwner, owner);
                    record.FootprintOwner = owner;
                    if (record.Building.Position == cell) UpdateConsoleWarning(record);
                }
            }

            internal readonly Members All = new Members(discoveryOnly: true);
            internal ShipPhysics.CachedReport PhysicsReport { get; set; }
            internal void ForgetPhysics()
            {
                PhysicsReport?.Dispose();
                PhysicsReport = null;
            }
            private ShipPhysics.MassIndex residualMass;
            private readonly List<(ShipStructureChange Change, ShipStructure Former)> cellChanges =
                new List<(ShipStructureChange, ShipStructure)>();
            internal IEnumerable<ShipStructure> BoundShips => boundShips;
            internal int MassRevision { get; private set; }

            internal ShipPhysics.MassMoments TotalMass()
            {
                if (residualMass == null)
                {
                    residualMass = new ShipPhysics.MassIndex(map);
                    foreach (IntVec3 cell in SubstructureOwnership.CellsOnMap(map))
                        if (!cellRoutes.ContainsKey(cell)) residualMass.SetFoundation(cell, true);
                    foreach (var record in records.Values)
                        if (record.Structure == null)
                            residualMass.SetBuilding(record.Building, record.Footprint, record.Weight);
                }
                var total = residualMass.Moments;
                foreach (var ship in boundShips) total.Add(ship.Mass.Moments);
                return total;
            }

            internal void UpdateMass(Building building, double delta)
            {
                if (!records.TryGetValue(building, out var record)) return;
                record.Weight += delta;
                (record.Structure?.Mass ?? residualMass)?.SetBuilding(building, record.Footprint, record.Weight);
                MassRevision++;
            }

            internal void FoundationChanged(IntVec3 cell)
            {
                UpdateTerrain(cell);
                if (map.thingGrid == null) return;
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                    if (thing.Position == cell && things.TryGetValue(thing.thingIDNumber, out var registered) && registered == thing)
                        UpdateLooseThing(thing);
            }
            internal IEnumerable<Thing> Things => things.Values;
            private int canvasContents;
            private readonly HashSet<Thing> cargo = new HashSet<Thing>();
            internal IReadOnlyCollection<Thing> Cargo => cargo;
            private readonly HashSet<Thing> looseCargo = new HashSet<Thing>();
            internal IReadOnlyCollection<Thing> LooseCargo => looseCargo;
            internal int BuildingCount => All.Count;

            [Flags]
            private enum CellFacts : byte { Support = 1, Natural = 2, Loose = 4, Visible = 8 }
            private CellFacts[] cellFacts;
            private int supportCount, naturalCount, looseTerrainCount;
            private readonly HashSet<IntVec3> visibleTerrain = new HashSet<IntVec3>();
            private readonly Dictionary<IntVec3, int> rockCells = new Dictionary<IntVec3, int>();
            internal int SupportCount { get { EnsureCellFacts(); return supportCount; } }
            internal bool HasNaturalGround { get { EnsureCellFacts(); return naturalCount != 0; } }
            internal bool HasLooseTerrain { get { EnsureCellFacts(); return looseTerrainCount != 0; } }
            internal IEnumerable<IntVec3> VisibleTerrain { get { EnsureCellFacts(); return visibleTerrain; } }
            internal IEnumerable<IntVec3> RockCells => rockCells.Keys;
            internal bool IsGlobeCell(IntVec3 cell)
            {
                EnsureCellFacts();
                return visibleTerrain.Contains(cell) || rockCells.ContainsKey(cell);
            }
            internal bool IsGlobeRock(IntVec3 cell) => rockCells.ContainsKey(cell);
            internal IEnumerable<IntVec3> OccupiedBuildingCells => byCell.Keys;
            private readonly HashSet<Thing> looseThings = new HashSet<Thing>();
            internal bool HasLooseThings => looseThings.Count != 0;

            private void UpdateLooseThing(Thing thing)
            {
                ThingCategory category = thing.def.category;
                if (category == ThingCategory.Filth || category == ThingCategory.Mote || category == ThingCategory.Projectile
                    || category == ThingCategory.Gas || category == ThingCategory.Ethereal && thing is not Blueprint && thing is not Frame) return;
                if (SubstructureOwnership.HasFoundation(map, thing.Position)) looseThings.Remove(thing);
                else looseThings.Add(thing);
            }

            private void EnsureCellFacts()
            {
                if (cellFacts != null || map.terrainGrid == null) return;
                long start = performance?.Start() ?? 0;
                cellFacts = new CellFacts[map.cellIndices.NumGridCells];
                foreach (IntVec3 cell in map.AllCells) UpdateTerrain(cell);
                performance?.RecordStructureBootstrap(start, cellFacts.Length, 0);
            }

            internal void UpdateTerrain(IntVec3 cell)
            {
                if (cellFacts == null || !cell.InBounds(map)) return;
                int index = map.cellIndices.CellToIndex(cell);
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                TerrainDef ground = map.terrainGrid.BaseTerrainAt(cell);
                TerrainDef foundation = SubstructureOwnership.FoundationAt(map, cell);
                bool supports = SubstructureOwnership.IsSupportTerrain(terrain);
                CellFacts previous = cellFacts[index];
                CellFacts next = 0;
                if (foundation != null || supports) next |= CellFacts.Support;
                if (SubstructureOwnership.IsSupportTerrain(ground) && ground.natural && !ground.isFoundation) next |= CellFacts.Natural;
                if (foundation == null && (supports || map.roofGrid?.RoofAt(cell) != null)) next |= CellFacts.Loose;
                if (GlobeDisplayRules.IsTerrain(terrain) || GlobeDisplayRules.IsTerrain(map.terrainGrid.FoundationAt(cell))) next |= CellFacts.Visible;
                if (previous == next) return;
                supportCount += Flag(next, CellFacts.Support) - Flag(previous, CellFacts.Support);
                naturalCount += Flag(next, CellFacts.Natural) - Flag(previous, CellFacts.Natural);
                looseTerrainCount += Flag(next, CellFacts.Loose) - Flag(previous, CellFacts.Loose);
                if ((next & CellFacts.Visible) != 0) visibleTerrain.Add(cell);
                else visibleTerrain.Remove(cell);
                cellFacts[index] = next;
            }

            private static int Flag(CellFacts flags, CellFacts flag) => (flags & flag) == 0 ? 0 : 1;

            private void UpdateRock(BuildingRecord record, int delta)
            {
                if (!GlobeDisplayRules.IsNaturalRock(record.Building.def)) return;
                foreach (IntVec3 cell in record.Footprint)
                {
                    rockCells.TryGetValue(cell, out int count);
                    count += delta;
                    if (count == 0) rockCells.Remove(cell);
                    else rockCells[cell] = count;
                }
            }

            internal void UpdateLooseCargo(Thing thing, bool supported)
            {
                if (!SpaceScatterService.IsCargo(thing)) return;
                if (supported) looseCargo.Remove(thing);
                else looseCargo.Add(thing);
            }

            internal void SupportChanged(IntVec3 cell)
            {
                if (map.thingGrid == null || !cell.InBounds(map)) return;
                bool supported = SubstructureOwnership.SupportsAt(map, cell);
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(cell))
                    if (thing.Position == cell && thing.Spawned && !thing.Destroyed) UpdateLooseCargo(thing, supported);
            }
            internal bool IsBlankCanvas => canvasContents == 0;
            private static bool CountsInCanvas(Thing thing) => thing.def.category != ThingCategory.Filth
                && thing.def.category != ThingCategory.Gas && thing.def.category != ThingCategory.Mote;
            internal IEnumerable<CompSpaceShuttle> Shuttles => shuttles;
            internal Thing Find(int id) => things.TryGetValue(id, out Thing thing) ? thing : null;

            private readonly DeltaVPerformanceProbe performance;

            internal MapView(Map map, ShipStructureIndex index, FoundationTopologyService topology, DeltaVPerformanceProbe performance)
            {
                this.index = index;
                this.map = map;
                this.topology = topology;
                this.performance = performance;
            }

            internal void Initialize()
            {
                if (map.listerThings == null) return;
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    if (!thing.Spawned || thing.Map != map || thing.Destroyed) continue;
                    if (thing is Building building) RegisterBuilding(building, bindDomain: false);
                    else RegisterItem(thing);
                }
                topology.DomainCount(map);
                foreach (Building building in All.Buildings) DomainChanged(building, topology.BuildingDomain(map, building));
            }

            internal HashSet<IntVec3> DomainOf(Building building) => Record(building)?.Domain;
            internal ShipStructure StructureAt(IntVec3 cell) =>
                cellRoutes.TryGetValue(cell, out var structure) ? structure : null;

            internal void Attach(ShipStructure structure)
            {
                if (!boundShips.Add(structure)) return;
                structure.Bind(this);
                MassRevision++;
                if (structure.Vessel is ShuttleShip && structure.Anchor is { } anchor)
                {
                    shuttleRoutes[anchor] = structure;
                    if (anchor is Building building && Record(building) is { } record) RouteBuilding(record);
                    return;
                }
                RefreshBindings();
                FlushBuildingRoutes();
                if (IsCustomFoundation(structure))
                    foreach (var record in records.Values)
                        if (structure.Vessel.Contains(record.Building)) RouteBuilding(record);
            }

            private static bool IsCustomFoundation(ShipStructure structure) =>
                structure.Vessel is FoundationShip && structure.Vessel.GetType() != typeof(FoundationShip);

            internal void Detach(ShipStructure structure, bool release)
            {
                if (!boundShips.Remove(structure)) return;
                if (structure.Foundation is { } domain && domainRoutes.TryGetValue(domain, out var owner) && owner == structure)
                    domainRoutes.Remove(domain);
                if (structure.Anchor is { } anchor) shuttleRoutes.Remove(anchor);
                foreach (IntVec3 cell in structure.Mass.FoundationCells.ToArray()) RouteCell(cell);
                foreach (Building building in structure.Buildings.ToArray())
                    if (Record(building) is { } record) RouteBuilding(record);
                MassRevision++;
                if (release) structure.Release();
                else structure.Unbind();
            }

            internal void RefreshBindings()
            {
                HashSet<HashSet<IntVec3>> changed = null;
                foreach (var structure in boundShips)
                {
                    if (structure.Vessel is not FoundationShip foundation) continue;
                    HashSet<IntVec3> next = foundation.IsValid && foundation.Map == map
                        && SubstructureOwnership.TryGet(map)?.OwnerAt(foundation.Anchor.Position) == foundation.Identity.key
                        ? topology.DomainAt(map, foundation.Anchor.Position) : null;
                    if (next == structure.Foundation) continue;
                    if (structure.Foundation is { } previous)
                    {
                        if (domainRoutes.TryGetValue(previous, out var route) && route == structure) domainRoutes.Remove(previous);
                        (changed ??= new HashSet<HashSet<IntVec3>>()).Add(previous);
                    }
                    structure.Foundation = next;
                    if (next != null)
                    {
                        domainRoutes[next] = structure;
                        (changed ??= new HashSet<HashSet<IntVec3>>()).Add(next);
                    }
                }
                if (changed == null) return;
                foreach (var domain in changed)
                {
                    foreach (IntVec3 cell in domain) RouteCell(cell);
                    if (domainBuildings.TryGetValue(domain, out var buildings))
                        dirtyRoutes.UnionWith(buildings);
                }
            }

            internal void SynchronizeRoutes(IEnumerable<IntVec3> cells)
            {
                RefreshBindings();
                foreach (IntVec3 cell in cells) RouteCell(cell);
                FlushBuildingRoutes();
            }

            internal void FlushBuildingRoutes()
            {
                foreach (var record in dirtyRoutes) RouteBuilding(record);
                dirtyRoutes.Clear();
            }

            internal void QueueCellChange(ShipStructureChange change, IntVec3 cell)
            {
                cellChanges.Add((change, StructureAt(cell)));
            }

            internal void PublishCellChanges()
            {
                foreach (var (change, former) in cellChanges)
                {
                    ShipStructure current = StructureAt(change.Cell.Value);
                    if (former != null && former != current) index.Record(former.Stream, change);
                    if (current != null) index.Record(current.Stream, change);
                }
                cellChanges.Clear();
            }

            internal void ReleaseShips()
            {
                foreach (var structure in boundShips) structure.Release();
                boundShips.Clear();
            }

            internal void RefreshExplicitRepresentation()
            {
                RefreshBindings();
                if (boundShips.Any(IsCustomFoundation))
                    foreach (var record in records.Values) RouteBuilding(record);
            }

            private void RouteCell(IntVec3 cell)
            {
                bool present = SubstructureOwnership.HasFoundation(map, cell);
                ShipStructure before = StructureAt(cell);
                ShipStructure after = null;
                if (present && topology.DomainAt(map, cell) is { } domain) domainRoutes.TryGetValue(domain, out after);
                if (before == after)
                {
                    if (after == null && residualMass?.SetFoundation(cell, present) == true) MassRevision++;
                    return;
                }
                if (before != null) before.Mass.SetFoundation(cell, false);
                else residualMass?.SetFoundation(cell, false);
                if (after == null)
                {
                    cellRoutes.Remove(cell);
                    residualMass?.SetFoundation(cell, present);
                }
                else
                {
                    cellRoutes[cell] = after;
                    after.Mass.SetFoundation(cell, true);
                }
                MassRevision++;
                var range = CellRect.SingleCell(cell);
                index.RecordRoute(map, before, after, null, cell, range, range, ShipStructureAspect.Membership);
            }

            internal void RouteBuilding(BuildingRecord record, bool publish = true)
            {
                var before = record.Structure;
                ShipStructure after = null;
                if (record.Building.Spawned && record.Building.Map == map)
                {
                    if (record.Shuttle != null) shuttleRoutes.TryGetValue(record.Building, out after);
                    else
                    {
                        if (record.Domain != null) domainRoutes.TryGetValue(record.Domain, out after);
                        if (after == null)
                            foreach (var custom in boundShips)
                                if (IsCustomFoundation(custom) && custom.Vessel.IsValid && custom.Vessel.Contains(record.Building))
                                {
                                    after = custom;
                                    break;
                                }
                    }
                }
                if (after == before) return;
                MoveBuilding(record, after);
                if (publish) index.RecordRoute(map, before, after, record.Building, record.Building.Position,
                    record.Footprint, record.Footprint, ShipStructureAspect.Membership);
            }

            internal void MoveBuilding(BuildingRecord record, ShipStructure next)
            {
                if (record.Structure is { } previous) previous.Remove(record);
                else residualMass?.RemoveBuilding(record.Building);
                record.Structure = next;
                record.OwnerBinding = ++ownerBindingSerial;
                if (next != null) next.Add(record);
                else if (record.Building.Spawned && record.Building.Map == map)
                    residualMass?.SetBuilding(record.Building, record.Footprint, record.Weight);
                MassRevision++;
            }

            internal IEnumerable<T> BuildingsOf<T>() where T : class
            {
                if (!buildingTypes.TryGetValue(typeof(T), out var found))
                {
                    var list = new TypedBuildings<T>(All.Buildings);
                    buildingTypes.Add(typeof(T), found = list);
                }
                return ((TypedBuildings<T>)found).Buildings;
            }

            private interface IBuildingType { void Update(Building building, bool add); }
            private sealed class TypedBuildings<T> : IBuildingType where T : class
            {
                internal readonly HashSet<T> Buildings;
                internal TypedBuildings(IEnumerable<Building> buildings) { Buildings = new HashSet<T>(buildings.OfType<T>()); }
                public void Update(Building building, bool add)
                {
                    if (building is not T typed) return;
                    if (add) Buildings.Add(typed);
                    else Buildings.Remove(typed);
                }
            }

            internal bool RegisterItem(Thing thing)
            {
                if (things.ContainsKey(thing.thingIDNumber)) return false;
                things.Add(thing.thingIDNumber, thing);
                if (CountsInCanvas(thing)) canvasContents++;
                if (map.terrainGrid != null)
                {
                    UpdateLooseThing(thing);
                    if (SpaceScatterService.IsCargo(thing))
                    {
                        cargo.Add(thing);
                        UpdateLooseCargo(thing, SubstructureOwnership.SupportsAt(map, thing.Position));
                    }
                }
                return true;
            }

            internal bool RemoveItem(Thing thing)
            {
                if (!things.TryGetValue(thing.thingIDNumber, out Thing registered) || registered != thing) return false;
                things.Remove(thing.thingIDNumber);
                if (CountsInCanvas(thing)) canvasContents--;
                cargo.Remove(thing);
                looseCargo.Remove(thing);
                looseThings.Remove(thing);
                return true;
            }
            internal IEnumerable<Building_DockingPort> ChannelsAt(IntVec3 cell) => channelsByCell.TryGetValue(cell, out var ports)
                ? ports : Enumerable.Empty<Building_DockingPort>();

            internal void UpdateChannel(Building_DockingPort port, bool add)
            {
                foreach (IntVec3 cell in port.ChannelDependencies)
                {
                    if (!channelsByCell.TryGetValue(cell, out var ports))
                    {
                        if (!add) continue;
                        channelsByCell.Add(cell, ports = new HashSet<Building_DockingPort>());
                    }
                    if (add) ports.Add(port);
                    else if (ports.Remove(port) && ports.Count == 0) channelsByCell.Remove(cell);
                }
            }

            internal IEnumerable<BuildingRecord> RecordsAt(IntVec3 cell) => byCell.TryGetValue(cell, out var recordsInCell)
                ? recordsInCell : Enumerable.Empty<BuildingRecord>();
            internal BuildingRecord Record(Building building) => records.TryGetValue(building, out var record) ? record : null;

            internal void RegisterBuilding(Building building, bool bindDomain = true)
            {
                if (records.ContainsKey(building)) return;
                var record = new BuildingRecord(building);
                records.Add(building, record);
                record.OwnerBinding = ++ownerBindingSerial;
                UpdateConsoleWarning(record);
                record.FootprintOwner = OwnerOfFootprint(SubstructureOwnership.TryGet(map), record.Footprint);
                if (record.MissileChamber != null)
                {
                    allMissileChambers.Add(record.MissileChamber);
                    MoveMissileChamber(record.MissileChamber, 0, record.FootprintOwner);
                }
                RegisterItem(building);
                UpdateRock(record, 1);
                if (building is Building_DockingPort port) UpdateChannel(port, true);
                All.Add(record);
                UpdatePrimary(record.Console);
                residualMass?.SetBuilding(building, record.Footprint, record.Weight);
                MassRevision++;
                foreach (object type in buildingTypes.Values) ((IBuildingType)type).Update(building, true);
                mapWeapons.UnionWith(record.Weapons);
                if (building.def.GetCompProperties<CompProperties_SpaceShuttle>() != null
                    || building.def.GetCompProperties<CompProperties_SpaceProjectile>() != null)
                    interceptionBodies.Add(building);
                if (record.Shuttle is { } shuttle) shuttles.Add(shuttle);
                foreach (IntVec3 cell in record.Footprint)
                {
                    if (!byCell.TryGetValue(cell, out var recordsInCell)) byCell.Add(cell, recordsInCell = new HashSet<BuildingRecord>());
                    recordsInCell.Add(record);
                }
                if (bindDomain) DomainChanged(building, topology.BuildingDomain(map, building));
            }

            internal void RemoveBuilding(Building building)
            {
                if (!records.TryGetValue(building, out var record)) return;
                if (record.Structure is { } structure) structure.Remove(record);
                record.Structure = null;
                residualMass?.RemoveBuilding(building);
                MassRevision++;
                DomainChanged(building, null);
                dirtyRoutes.Remove(record);
                records.Remove(building);
                UpdateConsoleWarning(record, remove: true);
                if (record.MissileChamber != null)
                {
                    allMissileChambers.Remove(record.MissileChamber);
                    MoveMissileChamber(record.MissileChamber, record.FootprintOwner, 0);
                }
                UpdateRock(record, -1);
                if (building is Building_DockingPort port) UpdateChannel(port, false);
                All.Remove(record);
                UpdatePrimary(record.Console, remove: true);
                RemoveItem(building);
                foreach (object type in buildingTypes.Values) ((IBuildingType)type).Update(building, false);
                mapWeapons.ExceptWith(record.Weapons);
                interceptionBodies.Remove(building);
                if (record.Shuttle is { } shuttle) shuttles.Remove(shuttle);
                foreach (IntVec3 cell in record.Footprint)
                {
                    var recordsInCell = byCell[cell];
                    recordsInCell.Remove(record);
                    if (recordsInCell.Count == 0) byCell.Remove(cell);
                }
            }

            internal void DomainChanged(Building building, HashSet<IntVec3> next)
            {
                if (!records.TryGetValue(building, out var record)) return;
                bool whole = next != null && record.Footprint.All(next.Contains);
                if (next == record.Domain && whole == record.WholeDomain) return;
                if (record.Domain != null && domainBuildings.TryGetValue(record.Domain, out var previous))
                {
                    previous.Remove(record);
                    if (previous.Count == 0) domainBuildings.Remove(record.Domain);
                }
                if (record.Structure is { } structure && whole != record.WholeDomain)
                {
                    structure.SetWholeFootprint(record, whole);
                    index.Record(structure.Stream, new ShipStructureChange(ShipStructureAspect.Footprint,
                        building, building.Position, record.Footprint, record.Footprint));
                }
                record.Domain = next;
                record.OwnerBinding = ++ownerBindingSerial;
                record.WholeDomain = whole;
                if (next != null)
                {
                    if (!domainBuildings.TryGetValue(next, out var members))
                        domainBuildings.Add(next, members = new HashSet<BuildingRecord>());
                    members.Add(record);
                }
                dirtyRoutes.Add(record);
            }

            internal void UpdateCombatShield(BuildingRecord record, SpaceShieldMode before, double previous)
            {
                var current = record.Capacity.Regular + record.Capacity.Emergency;
                if (before == record.Geometry.Mode && previous == current) return;
                record.Structure?.ShieldChanged(record, before, previous);
            }

            internal void InvalidateGuardOutline(BuildingRecord record)
            {
                if (record.WholeDomain) record.Structure?.InvalidateGuardOutline();
            }

            internal void UpdateGuardCapacity(BuildingRecord record)
            {
                double next = record.Shield.RegularBankContribution;
                double delta = next - record.GuardContribution;
                if (delta != 0 && record.WholeDomain) record.Structure?.UpdateGuardCapacity(delta);
                record.GuardContribution = next;
            }

            internal bool ChangeHealth(Building building)
            {
                if (!records.TryGetValue(building, out var record)) return false;
                int next = building.HitPoints;
                if (next == record.HitPoints) return false;
                record.Structure?.SetHealth(record.HitPoints, next);
                record.HitPoints = next;
                return true;
            }

            internal void ChangeRoof(IntVec3 cell)
            {
                UpdateTerrain(cell);
                if (!byCell.TryGetValue(cell, out var recordsInCell)) return;
                foreach (BuildingRecord record in recordsInCell)
                {
                    CombatPart? next = ShipFireControl.ExternalCombatPart(record.Building);
                    if (next == record.ExposedPart) continue;
                    record.Structure?.SetExposure(record, record.ExposedPart, next);
                    record.ExposedPart = next;
                }
            }
        }

        private sealed class MapEvents
        {
            internal readonly Map Map;
            internal MapEvents(Map map) => Map = map;
            internal readonly ShipStructureStream Stream = new ShipStructureStream();
            internal bool Changed;
            internal int ShipRevision;
        }

        private readonly Dictionary<Map, MapEvents> mapEvents = new Dictionary<Map, MapEvents>();
        private readonly HashSet<ShipStructureStream> pending = new HashSet<ShipStructureStream>();
        private readonly HashSet<MapEvents> pendingMaps = new HashSet<MapEvents>();
        private readonly Action<Map> forgetProtection;
        private readonly Action<Map, CellRect?> hullChanged;
        private readonly DeltaVPerformanceProbe performance;
        private int depth, batchEvents;
        private long batchStart;
        private bool publishing;
        internal int GlobalVersion { get; private set; }
        internal long Events { get; private set; }
        internal long Publications { get; private set; }
        internal int MapCount => mapEvents.Count;

        private MapEvents EventsFor(Map map)
        {
            if (!mapEvents.TryGetValue(map, out var events)) mapEvents.Add(map, events = new MapEvents(map));
            return events;
        }

        internal int ShipRevision(Map map) => map == null ? -1 : EventsFor(map).ShipRevision;
        internal int RevisionOf(Map map, ShipStructureAspect aspects) => map == null ? -1 : EventsFor(map).Stream.RevisionOf(aspects);
        internal int TransferRevision(Map map) => map == null ? -1 : EventsFor(map).Stream.TransferRevision;

        internal IDisposable WatchMap(Map map, ShipStructureAspect aspects, Action<ShipStructureBatch> callback,
            Predicate<IntVec3> area = null) => EventsFor(map).Stream.Watch(aspects, callback, area);

        internal IDisposable BeginChanges() => new ChangeScope(this);

        private sealed class ChangeScope : IDisposable
        {
            private ShipStructureIndex owner;
            internal ChangeScope(ShipStructureIndex owner)
            {
                this.owner = owner;
                if (owner.depth++ == 0 && !owner.publishing)
                {
                    owner.batchEvents = 0;
                    owner.batchStart = owner.performance?.Start() ?? 0;
                }
            }
            public void Dispose()
            {
                if (owner == null) return;
                var index = owner;
                owner = null;
                index.depth--;
                index.Commit();
            }
        }

        private void Record(ShipStructureStream stream, ShipStructureChange change)
        {
            stream.Add(change);
            pending.Add(stream);
        }

        private void Commit()
        {
            if (depth != 0 || publishing) return;
            publishing = true;
            try
            {
                while (pending.Count != 0 || pendingRoutes.Count != 0)
                {
                    foreach (var view in pendingRoutes.ToArray())
                    {
                        pendingRoutes.Remove(view);
                        topology.DomainCount(view.Map);
                        view.RefreshBindings();
                        view.FlushBuildingRoutes();
                        view.PublishCellChanges();
                    }
                    var streams = pending.ToArray();
                    pending.Clear();
                    foreach (var stream in streams) stream.Publish();
                }
            }
            finally
            {
                publishing = false;
                Publications += pendingMaps.Count;
                pendingMaps.Clear();
                performance?.RecordStructureCommit(batchStart, batchEvents);
                batchStart = 0;
            }
        }

        internal void RecordRoute(Map map, ShipStructure before, ShipStructure after, Thing thing, IntVec3 cell,
            CellRect? oldRange, CellRect? newRange, ShipStructureAspect aspect)
        {
            if (before == after)
            {
                if (before != null && oldRange.HasValue && newRange.HasValue)
                    Record(before.Stream, new ShipStructureChange(ShipStructureAspect.Footprint, thing, cell, oldRange, newRange));
                return;
            }
            if (before != null)
                Record(before.Stream, new ShipStructureChange(aspect, thing, cell, oldRange, null));
            if (after != null)
                Record(after.Stream, new ShipStructureChange(aspect, thing, cell, null, newRange));
        }

        private void RecordMap(Map map, ShipStructureChange change, bool structure = false)
        {
            if (batchStart != 0) batchEvents++;
            var events = EventsFor(map);
            if (structure)
            {
                Events++;
                GlobalVersion++;
                events.Changed = true;
                pendingMaps.Add(events);
            }
            if (change.Aspect == ShipStructureAspect.Primary || change.Aspect == ShipStructureAspect.Representation
                || change.Aspect == ShipStructureAspect.Membership && (change.Thing.TryGetComp<CompDeltaVPilotConsole>() != null
                    || change.Thing.TryGetComp<CompSpaceShuttle>() != null)) events.ShipRevision++;
            Record(events.Stream, change);
        }

        internal void Changed(Map map, CellRect? range = null)
        {
            if (map == null) return;
            using var changes = BeginChanges();
            if (maps.TryGetValue(map, out var view))
            {
                pendingRoutes.Add(view);
                if (!range.HasValue)
                {
                    view.RefreshExplicitRepresentation();
                    foreach (var ship in view.BoundShips)
                    {
                        ship.RefreshVirtualThrusters();
                        Record(ship.Stream, new ShipStructureChange(ShipStructureAspect.Representation, null, null, null, null));
                    }
                }
                else
                    foreach (var record in range.Value.SelectMany(view.RecordsAt).Distinct())
                        RecordLoadout(record.Building);
            }
            RecordMap(map, new ShipStructureChange(range.HasValue ? ShipStructureAspect.Footprint
                : ShipStructureAspect.Representation, null, null, null, range), structure: true);
            hullChanged?.Invoke(map, range);
        }

        internal void InvalidateAll()
        {
            using var changes = BeginChanges();
            foreach (Map map in mapEvents.Keys.ToArray()) Changed(map);
        }

        private readonly FoundationTopologyService topology;
        internal FoundationTopologyService Topology => topology;
        private readonly HashSet<MapView> pendingRoutes = new HashSet<MapView>();
        private readonly Dictionary<Map, MapView> maps = new Dictionary<Map, MapView>();
        private readonly Dictionary<DriveSubject, (Map Map, ShipBase Ship)> knownShips =
            new Dictionary<DriveSubject, (Map, ShipBase)>();
        private bool initialMapsRead;
        private readonly SortedDictionary<int, HashSet<CompStructureMass>> massDue =
            new SortedDictionary<int, HashSet<CompStructureMass>>();
        private int nextMassDue = int.MaxValue;

        internal void QueueMass(CompStructureMass mass)
        {
            if (!massDue.TryGetValue(mass.DueTick, out var due))
                massDue.Add(mass.DueTick, due = new HashSet<CompStructureMass>());
            due.Add(mass);
            nextMassDue = Math.Min(nextMassDue, mass.DueTick);
        }

        internal void CancelMass(CompStructureMass mass)
        {
            if (!massDue.TryGetValue(mass.DueTick, out var due)) return;
            due.Remove(mass);
            if (due.Count != 0) return;
            massDue.Remove(mass.DueTick);
            if (mass.DueTick == nextMassDue) RefreshMassDeadline();
        }

        private void RefreshMassDeadline() => nextMassDue = massDue.Count == 0 ? int.MaxValue : massDue.First().Key;

        internal void PublishDueMass(int tick)
        {
            while (massDue.Count != 0 && tick >= nextMassDue)
            {
                using var cursor = massDue.GetEnumerator();
                cursor.MoveNext();
                var entry = cursor.Current;
                massDue.Remove(entry.Key);
                RefreshMassDeadline();
                using var changes = BeginChanges();
                foreach (CompStructureMass mass in entry.Value)
                    if (mass.DueTick == entry.Key && mass.parent.Spawned && !mass.parent.Map.Disposed)
                        mass.Publish();
            }
        }

        internal void PublishMass(CompStructureMass mass, double before, double after)
        {
            if (before == after || mass.parent is not Building building || building.Map is not Map map
                || !maps.TryGetValue(map, out var view)) return;
            view.UpdateMass(building, after - before);
            NotifyProperty(building, ShipStructureAspect.Cargo, before, after);
        }

        internal ShipStructureIndex(FoundationTopologyService topology, Action<Map> forgetProtection = null,
            Action<Map, CellRect?> hullChanged = null, DeltaVPerformanceProbe performance = null)
        {
            this.topology = topology;
            this.forgetProtection = forgetProtection;
            this.hullChanged = hullChanged;
            this.performance = performance;
            topology.BuildingDomainChanged += OnBuildingDomainChanged;
            topology.HullBoundaryChanged += OnHullBoundaryChanged;
            topology.TopologyChanged += OnTopologyChanged;
        }

        private void OnHullBoundaryChanged(Map map, HashSet<IntVec3> cells)
        {
            if (!mapEvents.ContainsKey(map)) return;
            using var changes = BeginChanges();
            foreach (IntVec3 cell in cells)
                RecordMap(map, new ShipStructureChange(ShipStructureAspect.HullBoundary, null, cell, null, null));
        }

        private void OnTopologyChanged(Map map, HashSet<IntVec3> cells)
        {
            if (!maps.TryGetValue(map, out var view)) return;
            using var changes = BeginChanges();
            view.SynchronizeRoutes(cells);
        }

        private void OnBuildingDomainChanged(Map map, Building building, HashSet<IntVec3> previous, HashSet<IntVec3> next)
        {
            if (!maps.TryGetValue(map, out var view)) return;
            using var changes = BeginChanges();
            view.DomainChanged(building, next);
            pendingRoutes.Add(view);
        }

        internal static int ReadRevision(Map map, ShipStructureAspect aspects) =>
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.RevisionOf(map, aspects) ?? 0;
        internal static int UniverseRevision => Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.GlobalVersion ?? 0;
        internal static MapView Read(Map map) => Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.For(map);

        internal bool TryFootprintOwner(Thing thing, out int owner)
        {
            owner = 0;
            if (thing is not Building building || thing.Map is not Map map
                || !maps.TryGetValue(map, out var view) || view.Record(building) is not BuildingRecord record)
                return false;
            owner = record.FootprintOwner;
            return true;
        }

        internal static int OwnerOfFootprint(SubstructureOwnership ownership, CellRect footprint)
        {
            if (ownership == null) return 0;
            int owner = 0;
            foreach (IntVec3 cell in footprint)
            {
                int at = ownership.OwnerAt(cell);
                if (at <= 0 || owner != 0 && at != owner) return 0;
                owner = at;
            }
            return owner;
        }

        internal void NotifyChannel(Map map, Building_DockingPort port, bool add)
        {
            if (maps.TryGetValue(map, out var view)) view.UpdateChannel(port, add);
        }

        internal bool Tracks(Map map) => map != null && maps.ContainsKey(map);

        internal MapView ExistingView(Map map) => map != null && maps.TryGetValue(map, out var view) ? view : null;

        internal void ClearPhysicsReports()
        {
            foreach (MapView view in maps.Values) view.ForgetPhysics();
        }

        internal MapView For(Map map)
        {
            if (map == null || map.Disposed) return null;
            if (!maps.TryGetValue(map, out var view))
            {
                long start = performance?.Start() ?? 0;
                maps.Add(map, view = new MapView(map, this, topology, performance));
                view.Initialize();
                Patch_ShipHullRendering.Watch(this, map);
                performance?.RecordStructureBootstrap(start, 0, map.listerThings?.AllThings.Count ?? 0);
                foreach (CompDeltaVPilotConsole console in view.Primaries) BindIdentity(console.Vessel);
                foreach (CompSpaceShuttle shuttle in view.Shuttles) BindIdentity(shuttle.Vessel);
            }
            return view;
        }

        internal ShipBase Resolve(DriveSubject identity)
        {
            if (identity.key <= 0) return ShipBase.Invalid;
            if (!initialMapsRead)
            {
                initialMapsRead = true;
                foreach (Map map in Find.Maps)
                    if (map?.listerThings != null && (map.Parent is ShipPocketParent_DeltaV
                        || Current.Game?.GetComponent<UniverseManager>()?.SpaceObjectOnMap(map) is { baseTag: not SpaceObjectTag.Projectile }))
                        For(map);
            }
            return knownShips.TryGetValue(identity, out var entry) && entry.Ship.IsValid && entry.Ship.Map == entry.Map
                ? entry.Ship : ShipBase.Invalid;
        }

        internal void BindIdentity(ShipBase ship)
        {
            if (ship?.IsValid != true) return;
            Map map = ship.Map;
            MapView view = For(map);
            var structure = ship.StructureInstance;
            if (structure.Map == map && knownShips.TryGetValue(ship.Identity, out var current) && current.Ship == ship) return;
            using var changes = BeginChanges();
            Map previous = structure.Map;
            if (previous != null && previous != map && maps.TryGetValue(previous, out var oldView))
                oldView.Detach(structure, release: false);
            if (knownShips.TryGetValue(ship.Identity, out var existing) && existing.Ship != ship
                && maps.TryGetValue(existing.Map, out var existingView))
                existingView.Detach(existing.Ship.StructureInstance, release: true);
            knownShips[ship.Identity] = (map, ship);
            view.Attach(structure);
            Record(structure.Stream, new ShipStructureChange(ShipStructureAspect.Representation, ship.Anchor, null, previous, map));
        }

        private void RemoveIdentity(Map map, DriveSubject identity, Thing anchor, bool release = false)
        {
            if (!knownShips.TryGetValue(identity, out var entry) || entry.Map != map || entry.Ship.Anchor != anchor) return;
            knownShips.Remove(identity);
            if (maps.TryGetValue(map, out var view)) view.Detach(entry.Ship.StructureInstance, release);
        }

        internal ShipStructure StructureOf(Building building) =>
            building?.Map is Map map && maps.TryGetValue(map, out var view) ? view.Record(building)?.Structure : null;

        internal void NotifyConsole(CompDeltaVPilotConsole console)
        {
            Map map = console?.parent?.Map;
            if (map == null || !maps.TryGetValue(map, out var view)) return;
            using var changes = BeginChanges();
            int before = view.PrimaryOwner(console);
            view.UpdatePrimary(console);
            int after = view.PrimaryOwner(console);
            if (before == after) return;
            ShipStructure former = before > 0 && knownShips.TryGetValue(new DriveSubject(0, before), out var entry)
                ? entry.Ship.StructureInstance : null;
            if (before > 0) RemoveIdentity(map, new DriveSubject(0, before), console.parent);
            if (after > 0) BindIdentity(console.Vessel);
            pendingRoutes.Add(view);
            var change = new ShipStructureChange(ShipStructureAspect.Primary, console.parent, console.parent.Position, before, after);
            RecordMap(map, change, structure: true);
            var current = after > 0 ? console.Vessel.StructuralState : null;
            if (former != null && former != current) Record(former.Stream, change);
            if (current != null) Record(current.Stream, change);
        }

        internal void NotifyTerrain(Map map, IntVec3 cell)
        {
            if (map != null && maps.TryGetValue(map, out var view)) view.UpdateTerrain(cell);
        }

        internal void NotifySupport(Map map, IntVec3 cell)
        {
            if (map != null && maps.TryGetValue(map, out var view)) view.SupportChanged(cell);
        }

        internal void NotifyItem(Map map, Thing thing, bool added)
        {
            if (map == null || thing is Building || !maps.TryGetValue(map, out var view)) return;
            if (!(added ? view.RegisterItem(thing) : view.RemoveItem(thing))) return;
            using var changes = BeginChanges();
            var change = new ShipStructureChange(ShipStructureAspect.Items, thing, thing.Position, !added, added);
            RecordMap(map, change);
            if (view.StructureAt(thing.Position) is { } structure)
                Record(structure.Stream, change);
        }

        internal void NotifyBuilding(Map map, Building building, CellRect? previous = null)
        {
            if (map == null) return;
            if (!maps.TryGetValue(map, out var view))
            {
                topology.NotifyBuilding(map, building);
                return;
            }
            var beforeRecord = view.Record(building);
            bool before = beforeRecord != null;
            bool after = building.Spawned && building.Map == map;
            if (before == after && (!after || !previous.HasValue)) return;
            using var changes = BeginChanges();
            ShipStructure former = beforeRecord?.Structure;
            var console = building.TryGetComp<CompDeltaVPilotConsole>();
            var shuttle = building.TryGetComp<CompSpaceShuttle>();
            if (before)
            {
                view.RemoveBuilding(building);
                if (!after)
                {
                    if (console != null && console.shipId > 0) RemoveIdentity(map, new DriveSubject(0, console.shipId), building);
                    if (shuttle != null) RemoveIdentity(map, shuttle.Vessel.Identity, building);
                }
            }
            topology.NotifyBuilding(map, building);
            if (after)
            {
                view.RegisterBuilding(building);
                if (console?.Ship.IsValid == true) BindIdentity(console.Vessel);
                if (shuttle != null) BindIdentity(shuttle.Vessel);
            }
            var change = new ShipStructureChange(before == after ? ShipStructureAspect.Footprint : ShipStructureAspect.Membership,
                building, building.Position, beforeRecord?.Footprint, view.Record(building)?.Footprint);
            RecordMap(map, change, structure: true);
            if (change.Before is CellRect oldFootprint) hullChanged?.Invoke(map, oldFootprint);
            if (change.After is CellRect newFootprint && !(change.Before is CellRect unchanged && unchanged == newFootprint))
                hullChanged?.Invoke(map, newFootprint);
            if (after && view.Record(building) is { } record)
                view.RouteBuilding(record, publish: former == null);
            ShipStructure current = view.Record(building)?.Structure;
            if (before && after && shuttle != null) current?.RefreshVirtualThrusters();
            if (former != null)
                RecordRoute(map, former, current, building, building.Position, beforeRecord.Footprint,
                    view.Record(building)?.Footprint, ShipStructureAspect.Membership);
            pendingRoutes.Add(view);
        }

        internal void NotifyFoundation(Map map, IntVec3 cell)
        {
            if (map == null) return;
            topology.NotifyChanged(map, cell);
            if (maps.TryGetValue(map, out var view))
            {
                view.FoundationChanged(cell);
                pendingRoutes.Add(view);
            }
        }

        internal void NotifyCell(Map map, IntVec3 cell, ShipStructureAspect aspect, object before, object after,
            int formerOwner = 0, int nextOwner = 0)
        {
            if (map == null || Equals(before, after)) return;
            using var scope = BeginChanges();
            var change = new ShipStructureChange(aspect, null, cell, before, after);
            RecordMap(map, change, structure: true);
            if (aspect != ShipStructureAspect.Ownership) hullChanged?.Invoke(map, CellRect.SingleCell(cell));
            if (!maps.TryGetValue(map, out var view)) return;
            if (aspect == ShipStructureAspect.Foundation || aspect == ShipStructureAspect.Ownership)
            {
                view.QueueCellChange(change, cell);
                view.UpdateFootprintOwners(cell);
                pendingRoutes.Add(view);
            }
            else if (view.StructureAt(cell) is { } structure) Record(structure.Stream, change);
        }

        internal void NotifyHealth(Map map, Building building, int before)
        {
            if (maps.TryGetValue(map, out var view) && view.ChangeHealth(building))
                NotifyProperty(building, ShipStructureAspect.Health, before, building.HitPoints);
        }

        internal void NotifyRoof(Map map, IntVec3 cell, RoofDef before, RoofDef after)
        {
            if (map == null || before == after || !maps.TryGetValue(map, out var view)) return;
            view.ChangeRoof(cell);
            using var changes = BeginChanges();
            var change = new ShipStructureChange(ShipStructureAspect.Roof, null, cell, before, after);
            RecordMap(map, change);
            if (view.StructureAt(cell) is { } foundation)
                Record(foundation.Stream, change);
            foreach (BuildingRecord record in view.RecordsAt(cell))
                if (record.Shuttle != null && record.Structure is { } shuttle)
                    Record(shuttle.Stream, change);
        }

        internal bool NotifyShield(CompSpaceShield shield)
        {
            if (shield?.parent is not Building building || building.Map is not Map map
                || !maps.TryGetValue(map, out var view) || view.Record(building) is not BuildingRecord record
                || record.Shield != shield) return false;
            using var changes = BeginChanges();
            var previousGeometry = record.Geometry;
            var previousCapacity = record.Capacity;
            bool contributed = record.GuardContribution > 0;
            record.Geometry = ShieldGeometry(shield);
            record.Capacity = ShieldCapacity(shield);
            view.UpdateGuardCapacity(record);
            if (contributed != (record.GuardContribution > 0)) view.InvalidateGuardOutline(record);
            view.UpdateCombatShield(record, previousGeometry.Mode, previousCapacity.Regular + previousCapacity.Emergency);
            if (previousGeometry != record.Geometry)
            {
                view.InvalidateGuardOutline(record);
                NotifyProperty(building, ShipStructureAspect.ShieldGeometry, previousGeometry, record.Geometry, structural: true);
            }
            if (previousCapacity != record.Capacity)
                NotifyProperty(building, ShipStructureAspect.Shield, previousCapacity, record.Capacity);
            return previousGeometry != record.Geometry || previousCapacity != record.Capacity;
        }

        internal void InvalidateGuardOutline(CompSpaceShield shield)
        {
            if (shield.parent is Building building && building.Map is Map map && maps.TryGetValue(map, out var view)
                && view.Record(building) is { } record && record.Shield == shield) view.InvalidateGuardOutline(record);
        }

        internal void NotifyProperty(Thing thing, ShipStructureAspect aspect, object before, object after, bool structural = false)
        {
            Map map = thing?.Map;
            if (map == null || !maps.ContainsKey(map) || Equals(before, after)) return;
            using var changes = BeginChanges();
            if (aspect == ShipStructureAspect.Faction && thing is Building member
                && maps[map].Record(member) is { } record) maps[map].UpdateConsoleWarning(record);
            var change = new ShipStructureChange(aspect, thing, thing.Position, before, after);
            RecordMap(map, change, structural);
            if (thing is Building building && maps[map].Record(building)?.Structure is { } structure)
            {
                if (aspect == ShipStructureAspect.Propulsion) structure.RefreshThruster(building);
                Record(structure.Stream, change);
            }
            else if (thing is not Building && maps[map].StructureAt(thing.Position) is { } foundation)
                Record(foundation.Stream, change);
        }

        internal void RecordLoadout(Thing thing, object before = null, object after = null)
        {
            if (thing is not Building building || building.Map is not Map map
                || !maps.TryGetValue(map, out var view) || view.Record(building) is not { } record) return;
            using var changes = BeginChanges();
            var change = new ShipStructureChange(ShipStructureAspect.Loadout, building, building.Position, before, after);
            RecordMap(map, change);
            if (record.Structure is { } ship) Record(ship.Stream, change);
        }

        internal void Forget(Map map)
        {
            if (map == null) return;
            if (mapEvents.TryGetValue(map, out var events))
            {
                events.Stream.Release();
                mapEvents.Remove(map);
                pendingMaps.Remove(events);
                if (events.Changed) GlobalVersion++;
            }
            hullChanged?.Invoke(map, null);
            forgetProtection?.Invoke(map);
            if (maps.Remove(map, out MapView view))
            {
                pendingRoutes.Remove(view);
                view.ForgetPhysics();
                view.ReleaseShips();
            }
            topology.Forget(map);
            foreach (var entry in massDue.ToArray())
            {
                entry.Value.RemoveWhere(mass => mass.parent.Map == map);
                if (entry.Value.Count == 0) massDue.Remove(entry.Key);
            }
            RefreshMassDeadline();
            foreach (var entry in knownShips.Where(entry => entry.Value.Map == map).ToArray()) knownShips.Remove(entry.Key);
        }

        internal void Clear()
        {
            foreach (var view in maps.Values) view.ReleaseShips();
            pendingRoutes.Clear();
            topology.Clear();
            knownShips.Clear();
            initialMapsRead = false;
            ClearPhysicsReports();
            maps.Clear();
            massDue.Clear();
            nextMassDue = int.MaxValue;
            foreach (var events in mapEvents.Values) events.Stream.Release();
            mapEvents.Clear();
            pendingMaps.Clear();
            pending.Clear();
            hullChanged?.Invoke(null, null);
            forgetProtection?.Invoke(null);
            GlobalVersion++;
        }
    }
}

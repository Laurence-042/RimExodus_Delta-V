using System.Collections.Generic;
using DeltaV;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void CheckShipStructureChanges(UniverseManager universe, Faction faction)
    {
        CheckOwnedHandoffAndTransfer(universe, faction);
        Map map = TestMap(121300);
        Map other = TestMap(121400);
        var hull = AddTestHull(map, faction, 121301);
        var neighbor = AddTestHull(other, faction, 121401);
        Building building = (Building)hull.parent;
        var index = universe.ShipStructure;
        var structure = hull.Vessel.StructuralState;
        var shield = new CompSpaceShield { parent = neighbor.parent, props = new CompProperties_SpaceShield() };
        shield.InitializeGuard();
        AddFixtureComponents(neighbor.parent, shield);
        var remoteStructure = neighbor.Vessel.StructuralState;
        IntVec3 root = building.Position;
        var batches = new List<ShipStructureBatch>();
        var remoteBatches = new List<ShipStructureBatch>();
        using var local = structure.Watch(ShipStructureAspect.Health | ShipStructureAspect.Membership, batches.Add,
            cell => cell == root);
        using var remote = remoteStructure.Watch(ShipStructureAspect.Health | ShipStructureAspect.Membership,
            remoteBatches.Add);
        try
        {
            double maximum = shield.ShieldProps.regularCapacity + shield.ShieldProps.emergencyCapacity;
            Check(remoteStructure.CombatShieldTotals == (shield.RegularCapacity, maximum),
                "guard totals initialize from the registered shield");
            shield.DebitRegular(50);
            Check(remoteStructure.CombatShieldTotals == (shield.RegularCapacity, maximum),
                "guard capacity changes update combat totals in the same tick");
            shield.mode = SpaceShieldMode.Kinetic;
            index.NotifyShield(shield);
            Check(remoteStructure.CombatShieldTotals.Maximum == 1
                && structure.CombatShieldTotals.Maximum == 0,
                "changing shield mode updates its own combat aggregate only");

            float originalHealth = structure.HullHealth;
            int before = building.HitPoints;
            using (index.BeginChanges())
            {
                SetFixtureHitPoints(building, before - 1);
                Check(structure.HullHealth == originalHealth - 1 && batches.Count == 0,
                    "ship health totals change before the callback batch is delivered");
                RemoveFixtureBuilding(building, map);
            }
            Check(batches.Count == 1 && batches[0].Changes.Count == 2 && remoteBatches.Count == 0,
                "the former ship receives the despawn in order and the other ship receives nothing");
            Check(batches[0].Changes[0].Aspect == ShipStructureAspect.Health
                && (int)batches[0].Changes[0].Before == before
                && (int)batches[0].Changes[0].After == before - 1
                && batches[0].Changes[1].Before is CellRect && batches[0].Changes[1].After == null,
                "normalized ship deltas preserve exact values and the pre-despawn footprint");
            index.Forget(map);
            Check(structure.Map == null && structure.Anchor == null, "forget releases the live ship index and its subscription");
        }
        finally
        {
            RetireFixtureMap(map);
            RetireFixtureMap(other);
        }
    }

    static void CheckOwnedHandoffAndTransfer(UniverseManager universe, Faction faction)
    {
        var first = TestMap(121250, 8);
        var next = TestMap(121260, 8);
        var index = universe.ShipStructure;
        using var odyssey = new FixtureOdyssey();
        try
        {
            var former = AddTestConsole(first, faction, 121251);
            var successor = new CompDeltaVPilotConsole { shipId = former.shipId, isMain = false };
            AddTestBuilding(first, faction, 121252, ThingDefOf.PilotConsole, successor);
            MoveCombatFixture(successor.parent, new IntVec3(2, 0, 2));
            var deck = FixtureFoundation("handoffDeck");
            void Lay(Map map, IntVec3 cell) => SetFixtureFoundation(map, cell, deck, former.shipId);
            Lay(first, former.parent.Position);
            Lay(first, successor.parent.Position);
            Lay(next, successor.parent.Position);
            ShipBase vessel = former.Vessel;
            ShipStructure instance = vessel.StructuralState;
            Check(ReferenceEquals(instance, vessel.StructureInstance), "the canonical vessel owns its runtime structure instance");
            var foundationChanges = new List<ShipStructureBatch>();
            using var foundationWatch = instance.Watch(ShipStructureAspect.Foundation, foundationChanges.Add);
            var replacementDeck = FixtureFoundation("handoffReplacementDeck");
            IntVec3 consoleCell = former.parent.Position;
            SetFixtureFoundation(first, consoleCell, replacementDeck, former.shipId);
            Check(foundationChanges.Count == 1 && foundationChanges[0].Changes.Count == 1
                && ReferenceEquals(foundationChanges[0].Changes[0].Before, deck)
                && ReferenceEquals(foundationChanges[0].Changes[0].After, replacementDeck),
                "the owner receives exact foundation facts even when the control domain stays the same");
            var changes = new List<ShipStructureBatch>();
            using var watch = instance.Watch(ShipStructureAspect.Loadout, changes.Add);
            using (index.BeginChanges())
            {
                successor.isMain = true;
                Call(successor, "TakeVesselFrom", former);
                former.isMain = false;
                index.NotifyConsole(former);
                index.NotifyConsole(successor);
            }
            Check(ReferenceEquals(successor.Vessel, vessel) && ReferenceEquals(vessel.StructuralState, instance)
                && instance.Anchor == successor.parent && !former.Vessel.IsValid,
                "primary handoff transfers the same ship object and its structure instance");
            var load = new CompStructureMass { parent = successor.parent };
            AddFixtureComponents(successor.parent, load);
            var firstView = index.For(first);
            double originalShipMass = instance.Mass.Moments.mass;
            double originalMapMass = firstView.TotalMass().mass;
            var massChanges = new List<ShipStructureBatch>();
            using var massWatch = instance.Watch(ShipStructureAspect.Cargo, massChanges.Add);
            load.RestorePublication((25d, -1));
            Check(System.Math.Abs(instance.Mass.Moments.mass - originalShipMass - 25d) < 0.001
                && System.Math.Abs(firstView.TotalMass().mass - originalMapMass - 25d) < 0.001
                && massChanges.Count == 1 && (double)massChanges[0].Changes[0].After == 25d,
                "a published load changes the owner ledger and the host mass exactly once");
            var body = (Building)successor.parent;
            index.For(next);
            TransferFixtureBuilding(body, next);
            var weapon = new CompSpaceWeapon { parent = body, props = new CompProperties_SpaceWeapon() };
            AddFixtureComponents(body, weapon);
            weapon.NotifyParametersChanged();
            Check(ReferenceEquals(successor.Vessel, vessel) && ReferenceEquals(vessel.StructuralState, instance)
                && vessel.Map == next && index.ExistingView(first).Primary(former.shipId) == null,
                "cross-map transfer binds the same ship and structure in the destination only");
            Check(changes.Count == 1 && ReferenceEquals(changes[0].Changes[0].After, weapon),
                "the original subscription receives equipment changes on the destination");
        }
        finally
        {
            RetireFixtureMap(first);
            RetireFixtureMap(next);
        }
    }

}

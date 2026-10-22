using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DeltaV
{
    public sealed partial class SoftDockService
    {
        private readonly Dictionary<Building_DockingPort, SoftDockPortal> portals = new Dictionary<Building_DockingPort, SoftDockPortal>();

        internal SoftDockPortal Portal(Building_DockingPort port)
        {
            if (Pair(port)?.connected != true) return null;
            if (portals.TryGetValue(port, out var portal) && portal.Spawned) return portal;
            portal = (SoftDockPortal)ThingMaker.MakeThing(DeltaVDefOf.DeltaV_SoftDockPortal);
            portal.port = port;
            portal.peer = Pair(port).Other(port);
            portal.SetFaction(port.Faction);
            GenSpawn.Spawn(portal, port.Position, port.Map);
            portals[port] = portal;
            return portal;
        }
        internal SoftDockPortal ExistingPortal(Building_DockingPort port)
            => portals.TryGetValue(port, out var portal) && portal.Spawned ? portal : null;

        private void CancelTransfersAt(Building_DockingPort port)
        {
            foreach (var entry in portals.Where(p => p.Key == port || p.Value.peer == port).ToList())
            { portals.Remove(entry.Key); entry.Value.Close(); }
        }
        private void CancelAllTransfers()
        {
            foreach (var portal in portals.Values.ToList()) portal.Close();
            portals.Clear();
        }
    }

    // The door keeps vanilla door behavior; this non-edifice endpoint supplies the
    // actual MapPortal contract required by vanilla UI, lords, jobs and hauling.
    public sealed class SoftDockPortal : MapPortal
    {
        internal Building_DockingPort port, peer;
        public override string EnterString => "DeltaV_SoftDockTransfer".Translate();
        public override bool IsEnterable(out string reason)
        {
            var service = Current.Game?.GetComponent<UniverseManager>()?.SoftDock;
            bool connected = Spawned && port?.Spawned == true && peer?.Spawned == true
                && port.Map == Map && service?.Peer(port, null, requireOpen: false) == peer
                && !port.HostileTo(Faction.OfPlayer) && !peer.HostileTo(Faction.OfPlayer);
            reason = connected ? "" : "DeltaV_SoftDockTransferDisconnected".Translate().ToString();
            return connected;
        }
        public override Map GetOtherMap() => peer.Map;
        public override IntVec3 GetDestinationLocation() => peer.Position - peer.Rotation.FacingCell;
        public override void OnEntered(Pawn pawn)
        { Notify_ThingAdded(pawn); pawn.PostSwapMap(); }
        internal void Close()
        {
            if (Spawned && leftToLoad != null) CancelLoad();
            if (!Destroyed) Destroy(DestroyMode.Vanish);
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref port, "port");
            Scribe_References.Look(ref peer, "peer");
        }
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // Soft links are off on load. Keep native references resolvable until
            // vanilla lords/jobs have loaded, then cancel through the native API.
            if (respawningAfterLoad) Current.Game.GetComponent<UniverseManager>().QueueFrameAction(Close);
        }
    }
}

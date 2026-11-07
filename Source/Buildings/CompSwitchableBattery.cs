using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_SwitchableBattery : CompProperties_Battery
    {
        public CompProperties_SwitchableBattery()
        {
            compClass = typeof(CompSwitchableBattery);
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            foreach (var entry in base.SpecialDisplayStats(req)) yield return entry;
            yield return SpaceCombatStats.Entry("DeltaV_StatCapCapacity",
                SpaceCombatStats.WattDays(storedEnergyMax), 5100);
        }
    }

    /// <summary>储能与结算沿用原版电池；隔离只改变公共电网连接，专属设备仍可直取。</summary>
    public class CompSwitchableBattery : CompPowerBattery
    {
        private bool gridConnected = true;

        public bool GridConnected => gridConnected;
        internal bool WantsGridConnection => gridConnected
            && (parent.TryGetComp<CompSpaceWeaponAttachment>()?.SwitchedOn ?? true);

        public bool CanSupply(float wattDays) => wattDays > 0f && !StunnedByEMP
            && !parent.IsBrokenDown() && StoredEnergy + 0.0001f >= wattDays;

        public void Consume(float wattDays) => DrawPower(UnityEngine.Mathf.Min(wattDays, StoredEnergy));

        public void SetGridConnected(bool connected)
        {
            gridConnected = connected;
            RefreshConnection();
        }

        internal void RefreshConnection()
        {
            if (!parent.Spawned) return;
            if (!WantsGridConnection)
                PowerConnectionMaker.DisconnectFromPowerNet(this);
            else if (connectParent == null)
                parent.Map.powerNetManager.Notify_ConnectorWantsConnect(this);
            parent.Map.powerNetManager.NotifyDrawersForWireUpdate(parent.Position);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            RefreshConnection();
        }

        public override void PostExposeData()
        {
            // Read the switch before CompPower restores its connection in PostLoadInit.
            Scribe_Values.Look(ref gridConnected, "gridConnected", true);
            base.PostExposeData();
        }

        public override string CompInspectStringExtra() => base.CompInspectStringExtra()
            + "\n" + (WantsGridConnection ? "DeltaV_BatteryGridConnected" : "DeltaV_BatteryGridIsolated").Translate()
            + "\n" + "DeltaV_BatteryShortCircuitSafe".Translate();

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) yield return gizmo;
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            yield return new Command_BatchToggle<CompSwitchableBattery>("BatteryGridConnected",
                this, battery => battery.GridConnected, (battery, connected) => battery.SetGridConnected(connected))
            {
                defaultLabel = "DeltaV_BatteryGridConnection".Translate(),
                defaultDesc = "DeltaV_BatteryGridConnectionTip".Translate(),
                icon = TexCommand.Install,
            };
        }
    }
}

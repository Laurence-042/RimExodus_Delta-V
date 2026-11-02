using System;
using System.Collections.Generic;
using Verse;

namespace DeltaV
{
    public partial class CompSpaceShield
    {
        private IEnumerable<Gizmo> DevGizmos()
        {
            if (ShieldProps.regularCapacity > 0)
            {
                yield return DevAction("RegularFull", s => s.DevSetRegular(true));
                yield return DevAction("RegularEmpty", s => s.DevSetRegular(false));
                yield return DevAction("SkipRecoveryWait", s => s.recoveryWaitTicks = 0);
            }
            yield return DevAction("EmergencyFull", s => s.DevSetEmergency(true));
            yield return DevAction("EmergencyEmpty", s => s.DevSetEmergency(false));
            if (mode == SpaceShieldMode.Guard)
            {
                yield return DevAction("Hit100", s => s.ConsumeGuard(100f));
                yield return DevAction("Hit1000", s => s.ConsumeGuard(1000f));
            }
            else
            {
                yield return DevAction("LineFailure", s => s.disabledUntilTick =
                    Find.TickManager.TicksGame + s.ShieldProps.pulseFailureTicks);
                yield return DevAction("LineReset", s => s.disabledUntilTick = 0);
            }
        }

        private Gizmo DevAction(string action, Action<CompSpaceShield> apply) =>
            new Command_BatchSetting<CompSpaceShield>("ShieldDev" + action, this, shields =>
            {
                if (!DebugSettings.ShowDevGizmos) return;
                foreach (var shield in shields)
                {
                    apply(shield);
                    shield.SettingsChanged();
                }
            })
            {
                defaultLabel = ("DeltaV_ShieldDev" + action).Translate(),
                defaultDesc = ("DeltaV_ShieldDev" + action + "Tip").Translate(),
            };

        private void DevSetRegular(bool full)
        {
            regularCapacity = full ? ShieldProps.regularCapacity : 0;
            recoveryWaitTicks = full ? 0 : ShieldProps.recoveryDelayTicks;
            emergencyReason = full && ShieldProps.regularCapacity > 0
                ? GuardEmergencyReason.None : GuardEmergencyReason.Automatic;
        }

        private void DevSetEmergency(bool full)
        {
            var fuel = EmergencyFuel;
            if (fuel == null) return;
            if (full) fuel.Refuel((fuel.Props.fuelCapacity - fuel.Fuel) / fuel.Props.FuelMultiplierCurrentDifficulty);
            else fuel.ConsumeFuel(fuel.Fuel);
        }
    }
}

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>CutoutComplex 的红色蒙版从 ThingComp.ForceColor 取主色。</summary>
    internal static class RailgunStatusAppearance
    {
        public static readonly Color ReadyTint = new Color(0.25f, 0.65f, 1f);
        public static readonly Color CoolingTint = new Color(1f, 0.20f, 0.20f);
        public static readonly Color UnavailableTint = new Color(0.45f, 0.45f, 0.45f);
    }

    public class CompProperties_RailgunReadinessAppearance : CompProperties
    {
        public CompProperties_RailgunReadinessAppearance()
        {
            compClass = typeof(CompRailgunReadinessAppearance);
        }
    }

    /// <summary>The loading unit changes the masked control-screen color at state edges.</summary>
    public class CompRailgunReadinessAppearance : ThingComp, ISpaceWeaponFireCommit
    {
        private enum State : byte { Unknown, Unavailable, Cooling, Ready }
        private State lastState;
        private bool wasCooling;
        private Effecter progressBarEffecter;

        public Color Tint => lastState == State.Ready ? RailgunStatusAppearance.ReadyTint
            : lastState == State.Cooling ? RailgunStatusAppearance.CoolingTint
            : RailgunStatusAppearance.UnavailableTint;

        public override Color? ForceColor() => Tint;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            lastState = State.Unknown;
            Rebind();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            CleanupProgressBar();
            base.PostDeSpawn(map, mode);
        }

        public override void CompTick()
        {
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon?.CombatPowerEnabled != true)
            {
                CleanupProgressBar();
                return;
            }
            int remaining = weapon?.CooldownTicksLeft ?? 0;
            if (wasCooling != (remaining > 0) || parent.IsHashIntervalTick(60)) Refresh();
            if (remaining <= 0 || !parent.Spawned)
            {
                CleanupProgressBar();
                return;
            }
            // Same effect and placement as Building_TurretGun's mortar cooldown.
            progressBarEffecter ??= EffecterDefOf.ProgressBar.Spawn();
            progressBarEffecter.EffectTick(parent, TargetInfo.Invalid);
            MoteProgressBar mote = ((SubEffecter_ProgressBar)progressBarEffecter.children[0]).mote;
            mote.progress = 1f - Mathf.Clamp01((float)remaining / Mathf.Max(1, weapon.CommittedCooldownTicks));
            mote.offsetZ = -0.8f;
        }

        private void CleanupProgressBar()
        {
            progressBarEffecter?.Cleanup();
            progressBarEffecter = null;
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff"
                || signal == "Refueled" || signal == "RanOutOfFuel") Refresh();
        }

        public void NotifyShotCommitted(SpaceWeaponShot shot) => Refresh();

        public override string CompInspectStringExtra()
        {
            string key = lastState == State.Ready ? "DeltaV_RailReady"
                : lastState == State.Cooling ? "DeltaV_RailCooling" : "DeltaV_RailUnavailable";
            string status = "DeltaV_RailReadiness".Translate(key.Translate());
            var weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return status;
            status += "\n" + "DeltaV_StatCooldown".Translate() + ": " + SpaceCombatStats.Seconds(weapon.ShotInterval);
            int remaining = weapon.CooldownTicksLeft;
            if (remaining > 0)
                status += "\n" + "CanFireIn".Translate() + ": " + remaining.ToStringSecondsFromTicks();
            return status;
        }

        public void Rebind()
        {
            Refresh();
        }

        public void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return;
            SpaceWeaponLayout layout = weapon.Layout;
            List<CompSpaceRailgun> active = null;
            bool shotEnergy = weapon.Operational
                && SpaceWeaponBallistics.TryPlanEnergy(weapon,
                    out active);
            wasCooling = weapon.CooldownTicksLeft > 0;
            foreach (var segment in layout.acceleration.segments)
                segment.parent.TryGetComp<CompRailgunAcceleratorAppearance>()?
                    .SetStatus(shotEnergy && active.Contains(segment), wasCooling);
            State state = !shotEnergy ? State.Unavailable
                : wasCooling ? State.Cooling : State.Ready;
            if (state == lastState) return;
            lastState = state;
            parent.Notify_ColorChanged();
        }
    }

    public class CompProperties_RailgunAcceleratorAppearance : CompProperties
    {
        public CompProperties_RailgunAcceleratorAppearance()
        {
            compClass = typeof(CompRailgunAcceleratorAppearance);
        }
    }

    /// <summary>Coil color uses the same sequential energy plan as the actual shot.</summary>
    public class CompRailgunAcceleratorAppearance : ThingComp
    {
        private bool? lastActive;
        private bool cooling;

        public CompSpaceRailgun Segment => parent.TryGetComp<CompSpaceRailgun>();

        public Color Tint => lastActive != true ? RailgunStatusAppearance.UnavailableTint
            : cooling ? RailgunStatusAppearance.CoolingTint : RailgunStatusAppearance.ReadyTint;

        public override Color? ForceColor() => Tint;

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff")
                Segment?.BoundWeapon?.parent.TryGetComp<CompRailgunReadinessAppearance>()?.Refresh();
        }

        public override string CompInspectStringExtra() =>
            "DeltaV_RailAcceleratorStatus".Translate((lastActive == true
                ? cooling ? "DeltaV_RailCooling" : "DeltaV_RailAcceleratorActive"
                : "DeltaV_RailAcceleratorSkipped").Translate());

        public void SetStatus(bool active, bool isCooling)
        {
            isCooling &= active;
            if (lastActive == active && cooling == isCooling) return;
            lastActive = active;
            cooling = isCooling;
            if (parent.Spawned) parent.Notify_ColorChanged();
        }
    }
}

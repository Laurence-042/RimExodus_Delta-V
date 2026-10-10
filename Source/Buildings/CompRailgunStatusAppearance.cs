using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>CutoutComplex 的红色蒙版从 ThingComp.ForceColor 取主色。</summary>
    internal static class RailgunStatusAppearance
    {
        public static readonly Color ReadyTint = new Color(0.66f, 0.92f, 1f);
        public static readonly Color CoolingTint = new Color(1f, 0.62f, 0.20f);
        public static readonly Color UnavailableTint = new Color(0.72f, 0.19f, 0.22f);
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
        private readonly List<CompRailgunAcceleratorAppearance> registered = new List<CompRailgunAcceleratorAppearance>();

        public Color Tint => lastState == State.Ready ? RailgunStatusAppearance.ReadyTint
            : lastState == State.Cooling ? RailgunStatusAppearance.CoolingTint
            : RailgunStatusAppearance.UnavailableTint;

        public override Color? ForceColor() => Tint;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            lastState = State.Unknown;
            Refresh();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            foreach (CompRailgunAcceleratorAppearance appearance in registered)
                appearance.SetLoader(null);
            registered.Clear();
            base.PostDeSpawn(map, mode);
        }

        public override void CompTick()
        {
            if (parent.IsHashIntervalTick(60)) Refresh();
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
            return "DeltaV_RailReadiness".Translate(key.Translate());
        }

        public void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return;
            SpaceWeaponAccelerationSolution acceleration = CompSpaceRailgun.Resolve(weapon);
            foreach (CompRailgunAcceleratorAppearance appearance in registered)
                if (!acceleration.segments.Contains(appearance.Segment)) appearance.SetLoader(null);
            registered.Clear();
            foreach (CompSpaceRailgun segment in acceleration.segments)
            {
                CompRailgunAcceleratorAppearance appearance = segment.parent.TryGetComp<CompRailgunAcceleratorAppearance>();
                if (appearance == null) continue;
                appearance.SetLoader(this);
                registered.Add(appearance);
            }
            bool powerOn = parent.TryGetComp<CompPowerTrader>()?.PowerOn != false;
            bool ammo = weapon.MagazineAvailable(weapon.SelectMagazine());
            List<CompSpaceRailgun> active = null;
            bool shotEnergy = powerOn && ammo
                && SpaceWeaponShot.TryPlanEnergy(weapon, acceleration, out active);
            foreach (CompRailgunAcceleratorAppearance appearance in registered)
                appearance.SetActive(shotEnergy && active.Contains(appearance.Segment));
            bool resources = shotEnergy && weapon.TryGetSkillLevel(out _)
                && SpaceWeaponShot.HasClearFixedPath(weapon, acceleration);
            State state = !resources ? State.Unavailable
                : Find.TickManager.TicksGame < weapon.cooldownUntilTick ? State.Cooling : State.Ready;
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
        private CompRailgunReadinessAppearance loader;

        public CompSpaceRailgun Segment => parent.TryGetComp<CompSpaceRailgun>();

        public Color Tint => lastActive == true ? RailgunStatusAppearance.ReadyTint
            : RailgunStatusAppearance.UnavailableTint;

        public override Color? ForceColor() => Tint;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            Segment?.ConnectedWeapon()?.parent.TryGetComp<CompRailgunReadinessAppearance>()?.Refresh();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            CompRailgunReadinessAppearance previous = loader;
            SetLoader(null);
            base.PostDeSpawn(map, mode);
            // 帧边界重解链，避免在移除回调中读到尚未清理的 ThingGrid。
            if (previous != null)
                Current.Game?.GetComponent<UniverseManager>()?.QueueFrameAction(previous.Refresh);
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff") loader?.Refresh();
        }

        public override string CompInspectStringExtra() =>
            "DeltaV_RailAcceleratorStatus".Translate((lastActive == true
                ? "DeltaV_RailAcceleratorActive" : "DeltaV_RailAcceleratorSkipped").Translate());

        public void SetLoader(CompRailgunReadinessAppearance value)
        {
            loader = value;
            if (value == null) SetActive(false);
        }

        public void SetActive(bool active)
        {
            if (lastActive == active) return;
            lastActive = active;
            if (parent.Spawned) parent.Notify_ColorChanged();
        }
    }
}

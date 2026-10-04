using RimWorld;
using Verse;

namespace DeltaV
{
    public class CompProperties_RailgunReadinessGlow : CompProperties
    {
        public ColorInt readyColor = new ColorInt(75, 195, 205, 0);
        public ColorInt coolingColor = new ColorInt(220, 165, 65, 0);
        public ColorInt unavailableColor = new ColorInt(190, 65, 55, 0);

        public CompProperties_RailgunReadinessGlow()
        {
            compClass = typeof(CompRailgunReadinessGlow);
        }
    }

    /// <summary>仅在状态边沿改原版 CompGlower 的实例颜色；无目标相关射界判断。</summary>
    public class CompRailgunReadinessGlow : ThingComp, ISpaceWeaponFireCommit
    {
        private enum State : byte { Unknown, Unavailable, Cooling, Ready }
        private State lastState;
        private CompProperties_RailgunReadinessGlow GlowProps =>
            (CompProperties_RailgunReadinessGlow)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            lastState = State.Unknown;
            Refresh();
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

        private void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            CompGlower glower = parent.TryGetComp<CompGlower>();
            if (weapon == null || glower == null) return;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            bool resources = (power == null || power.PowerOn)
                && weapon.TryGetSkillLevel(out _)
                && (parent.TryGetComp<CompSpaceWeaponMagazine>() == null
                    || weapon.SelectMagazine() != null)
                && SpaceWeaponShot.HasShotEnergy(weapon)
                && SpaceWeaponShot.HasClearFixedPath(weapon);
            State state = !resources ? State.Unavailable
                : Find.TickManager.TicksGame < weapon.cooldownUntilTick ? State.Cooling : State.Ready;
            if (state == lastState) return;
            lastState = state;
            glower.GlowColor = state == State.Ready ? GlowProps.readyColor
                : state == State.Cooling ? GlowProps.coolingColor : GlowProps.unavailableColor;
        }
    }

    public class CompProperties_RailgunAcceleratorGlow : CompProperties
    {
        public ColorInt activeColor = new ColorInt(75, 195, 205, 0);
        public ColorInt skippedColor = new ColorInt(190, 65, 55, 0);

        public CompProperties_RailgunAcceleratorGlow()
        {
            compClass = typeof(CompRailgunAcceleratorGlow);
        }
    }

    /// <summary>逐段显示本发是否能完成加速；判定复用射击事务的同一能量规划。</summary>
    public class CompRailgunAcceleratorGlow : ThingComp
    {
        private bool? lastActive;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            Refresh();
        }

        public override void CompTick()
        {
            if (parent.IsHashIntervalTick(60)) Refresh();
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff") Refresh();
        }

        public override string CompInspectStringExtra() =>
            "DeltaV_RailAcceleratorStatus".Translate((lastActive == true
                ? "DeltaV_RailAcceleratorActive" : "DeltaV_RailAcceleratorSkipped").Translate());

        public void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceRailgun segment = parent.TryGetComp<CompSpaceRailgun>();
            CompGlower glower = parent.TryGetComp<CompGlower>();
            if (segment == null || glower == null) return;
            CompSpaceWeapon weapon = segment.ConnectedWeapon();
            bool active = SpaceWeaponShot.WillAccelerate(weapon, segment);
            if (lastActive == active) return;
            lastActive = active;
            var glowProps = (CompProperties_RailgunAcceleratorGlow)props;
            glower.GlowColor = active ? glowProps.activeColor : glowProps.skippedColor;
        }
    }
}

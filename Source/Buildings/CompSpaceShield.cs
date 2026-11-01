using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum SpaceShieldMode : byte
    {
        Guard,
        Kinetic,
        Electromagnetic,
    }

    public enum SpaceShieldState : byte
    {
        Guard,
        LineInactive,
        LineActive,
    }

    public class CompProperties_SpaceShield : CompProperties
    {
        public float groundRadius = 24.9f;
        public int minGroundRadius = 8;
        public int maxGroundRadius = 40;
        public int minProjection = 2;
        public int maxProjection = 32;
        public int minLength = 4;
        public int maxLength = 32;
        public float guardPowerWatts = 10f;
        public float lineBasePowerWatts = 10f;
        public float spaceProjectionPowerWatts = 1f;
        public float spaceLengthPowerWatts = 1.5f;
        public int pulseFailureTicks = 180;
        public int guardRecoveryTicks = 2500;
        public int regularCapacity = 1000, emergencyCapacity = 2000;
        public int recoveryDelayTicks = 600;
        public float recoveryPowerWatts = 1000f;

        public CompProperties_SpaceShield()
        {
            compClass = typeof(CompSpaceShield);
        }
    }

    /// <summary>Vanilla intersection, EMP and visuals; capacities belong to CompSpaceShield.</summary>
    public sealed class CompDeltaVShieldInterceptor : CompProjectileInterceptor
    {
        protected override int NumInactiveDots => 0;
        protected override int HitPointsPerInterval => 0;
    }

    /// <summary>Physical silver hauling without displaying an empty fuel tank as shield health.</summary>
    public sealed class CompDeltaVShieldSilverFuel : CompRefuelable
    {
        public override string CompInspectStringExtra() => null;
        public override IEnumerable<Gizmo> CompGetGizmosExtra() { yield break; }
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats() { yield break; }
    }

    /// <summary>
    /// Three mutually exclusive modes. This component owns both guard capacities;
    /// SpaceCollision consumes the two projected line modes.
    /// </summary>
    public partial class CompSpaceShield : ThingComp, IShellTurretTopSource
    {
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public SpaceShieldMode mode = SpaceShieldMode.Guard;
        public float groundRadius = 24.9f;
        public float projection = 12f;
        public float length = 18f;
        public float angleDeg;
        public int kineticStrengthPercent;
        public int electromagneticStrengthPercent;
        private int disabledUntilTick;
        private CompPowerTrader power;
        private CompFlickable flick;
        private CompProjectileInterceptor interceptor;
        private CompRefuelable silverFuel;
        private CompProperties_ProjectileInterceptor instanceInterceptorProps;
        private Material topMaterial;

        public Material ShellTopMaterial => topMaterial ??= MaterialPool.MatFrom(
            parent.def.GetModExtension<DefModExtension_SpaceTurretArt>().topTexPath);
        // Existing shield art faces north; the shared turret convention faces east.
        public float ShellTopAngle => parent.Rotation.AsAngle
            - (mode == SpaceShieldMode.Guard ? 0f : angleDeg) - TurretTop.ArtworkRotation;

        public override void PostDraw()
        {
            base.PostDraw();
            SpaceTurretTopRenderer.Draw(parent, this, parent.DrawPos, Vector3.zero, 0f);
        }

        public CompProperties_SpaceShield ShieldProps => (CompProperties_SpaceShield)props;
        public bool GuardRecoveryPowered => parent.Spawned
            && (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn == true
            && power.PowerNet != null && FlickedOn;
        public bool FlickedOn => (flick ??= parent.TryGetComp<CompFlickable>())?.SwitchIsOn != false;

        public SpaceShieldState State
        {
            get
            {
                if (mode == SpaceShieldMode.Guard) return SpaceShieldState.Guard;
                if (!parent.Spawned || Find.TickManager.TicksGame < disabledUntilTick
                    || (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn != true
                    || !FlickedOn) return SpaceShieldState.LineInactive;
                return SpaceShieldState.LineActive;
            }
        }

        public override void PostPostMake()
        {
            base.PostPostMake();
            groundRadius = ShieldProps.groundRadius;
            EnsureInstanceInterceptorProps();
            InitializeGuard();
            projection = Mathf.Clamp(projection, ShieldProps.minProjection, ShieldProps.maxProjection);
            length = Mathf.Clamp(length, ShieldProps.minLength, ShieldProps.maxLength);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureInstanceInterceptorProps();

            UpdateSilverTarget();
            UpdatePowerDraw();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref mode, "DeltaV_shieldMode", SpaceShieldMode.Guard);
            Scribe_Values.Look(ref groundRadius, "DeltaV_shieldGroundRadius", ShieldProps.groundRadius);
            Scribe_Values.Look(ref projection, "DeltaV_shieldProjection", 12f);
            Scribe_Values.Look(ref length, "DeltaV_shieldLength", 18f);
            Scribe_Values.Look(ref angleDeg, "DeltaV_shieldAngle", 0f);
            Scribe_Values.Look(ref kineticStrengthPercent, "DeltaV_kineticStrengthPercent", 0);
            Scribe_Values.Look(ref electromagneticStrengthPercent, "DeltaV_electromagneticStrengthPercent", 0);
            Scribe_Values.Look(ref disabledUntilTick, "DeltaV_shieldDisabledUntilTick", 0);
            ExposeGuardData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                groundRadius = Mathf.Clamp(groundRadius, ShieldProps.minGroundRadius,
                    ShieldProps.maxGroundRadius);
                projection = Mathf.Clamp(projection, ShieldProps.minProjection, ShieldProps.maxProjection);
                length = Mathf.Clamp(length, ShieldProps.minLength, ShieldProps.maxLength);
                angleDeg = Mathf.Clamp(angleDeg, -90f, 90f);
                kineticStrengthPercent = Mathf.Clamp(kineticStrengthPercent, 0, 100);
                electromagneticStrengthPercent = Mathf.Clamp(electromagneticStrengthPercent, 0, 100);
                EnsureInstanceInterceptorProps();
                UpdateSilverTarget();
                UpdatePowerDraw();
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            AdvanceGuardTick(GuardRecoveryPowered);
            ApplySilverFuel();
            UpdateSilverTarget();
            UpdatePowerDraw();
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == CompRefuelable.RefueledSignal) ApplySilverFuel();
            UpdatePowerDraw();
        }

        private void UpdateSilverTarget()
        {
            silverFuel ??= parent.TryGetComp<CompRefuelable>();
            if (silverFuel == null) return;
            float missing = (float)(ShieldProps.emergencyCapacity - emergencyCapacity);
            if (Mathf.Abs(silverFuel.TargetFuelLevel - missing) > 0.001f)
                silverFuel.TargetFuelLevel = missing;
        }

        private void ApplySilverFuel()
        {
            silverFuel ??= parent.TryGetComp<CompRefuelable>();
            if (silverFuel == null) return;
            float amount = (float)Math.Min(silverFuel.Fuel, ShieldProps.emergencyCapacity - emergencyCapacity);
            if (amount <= 0f) return;
            emergencyCapacity = Math.Min(ShieldProps.emergencyCapacity, emergencyCapacity + amount);
            silverFuel.ConsumeFuel(amount);
            UpdateSilverTarget();
        }

        private void EnsureInstanceInterceptorProps()
        {
            interceptor ??= parent.TryGetComp<CompProjectileInterceptor>();
            if (interceptor == null) return;
            if (instanceInterceptorProps == null || interceptor.props != instanceInterceptorProps)
            {
                var source = interceptor.props as CompProperties_ProjectileInterceptor;
                if (source == null || CloneMethod == null) return;
                instanceInterceptorProps = (CompProperties_ProjectileInterceptor)CloneMethod.Invoke(source, null);
                interceptor.props = instanceInterceptorProps;
            }
            instanceInterceptorProps.radius = groundRadius;
        }

        public float WantedPowerWatts => (WantsRegularRecovery ? ShieldProps.recoveryPowerWatts : 0f)
            + (mode == SpaceShieldMode.Guard ? ShieldProps.guardPowerWatts
            : ShieldProps.lineBasePowerWatts
                + ShieldProps.spaceProjectionPowerWatts
                    * Mathf.Max(0f, projection - ShieldProps.minProjection)
                + ShieldProps.spaceLengthPowerWatts
                    * Mathf.Max(0f, length - ShieldProps.minLength));

        private void UpdatePowerDraw()
        {
            power ??= parent.TryGetComp<CompPowerTrader>();
            if (power == null) return;
            float output = -WantedPowerWatts;
            if (Mathf.Abs(power.PowerOutput - output) > 0.5f) power.PowerOutput = output;
        }

        internal void SettingsChanged()
        {
            EnsureInstanceInterceptorProps();
            UpdateSilverTarget();
            UpdatePowerDraw();
            SpaceCollision.InvalidateShield(parent.Map);
        }

        public void GetSegment(out Vector2 start, out Vector2 end, out Vector2 frontNormal)
        {
            Vector3 trueCenter = parent.TrueCenter();
            Vector2 center = new Vector2(trueCenter.x, trueCenter.z);
            Vector2 facing = SpaceTransform.RotatePlacementOffset(Vector2.up, parent.Rotation);
            frontNormal = SpaceTransform.Rotate(facing, angleDeg * Mathf.Deg2Rad);
            Vector2 tangent = new Vector2(frontNormal.y, -frontNormal.x);
            center += frontNormal * projection;
            start = center - tangent * (length * 0.5f);
            end = center + tangent * (length * 0.5f);
        }

        /// <summary>Pay once from the shared net and return the actually funded fraction.</summary>
        public float ConsumePulseFraction(float wattDays)
        {
            if (State != SpaceShieldState.LineActive) return 0f;
            if (wattDays <= 0f) return 1f;
            PowerNet net = power?.PowerNet;
            float available = 0f;
            if (net != null)
                foreach (CompPowerBattery battery in net.batteryComps)
                    if (!battery.StunnedByEMP) available += Mathf.Max(0f, battery.StoredEnergy);
            float paid = Mathf.Min(available, wattDays);
            float remaining = paid;
            if (net != null)
                foreach (CompPowerBattery battery in net.batteryComps)
                {
                    if (battery.StunnedByEMP || remaining <= 0f) continue;
                    float drawn = Mathf.Min(remaining, battery.StoredEnergy);
                    if (drawn <= 0f) continue;
                    battery.DrawPower(drawn);
                    remaining -= drawn;
                }
            if (paid + 0.0001f < wattDays)
                disabledUntilTick = Find.TickManager.TicksGame + ShieldProps.pulseFailureTicks;
            return Mathf.Clamp01(paid / wattDays);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer || !parent.Spawned) yield break;
            if (ShieldProps.regularCapacity > 0)
            {
                yield return new Gizmo_ShieldRegular(this);
                if (mode == SpaceShieldMode.Guard && emergencyReason != GuardEmergencyReason.Manual)
                    yield return new Command_BatchSetting<CompSpaceShield>("ShieldManualEmergency", this,
                        shields => { foreach (var shield in shields) shield.EnterManualEmergency(); })
                    {
                        defaultLabel = "DeltaV_ShieldManualEmergency".Translate(),
                        defaultDesc = "DeltaV_ShieldManualEmergencyTip".Translate(), icon = parent.def.uiIcon
                    };
            }
            yield return new Gizmo_ShieldEmergency(this);
            foreach (SpaceShieldMode next in new[]
                { SpaceShieldMode.Guard, SpaceShieldMode.Kinetic, SpaceShieldMode.Electromagnetic })
            {
                if (next == mode) continue;
                var command = new Command_BatchSetting<CompSpaceShield>("ShieldMode" + next,
                    this, shields =>
                    {
                        foreach (CompSpaceShield shield in shields)
                        {
                            if (!shield.parent.Spawned || shield.parent.Destroyed) continue;
                            shield.mode = next;
                            shield.TryRejoinRegular();
                            shield.SettingsChanged();
                        }
                    })
                {
                    defaultLabel = (next == SpaceShieldMode.Guard ? "DeltaV_ShieldModeGuard"
                        : next == SpaceShieldMode.Kinetic ? "DeltaV_ShieldModeKinetic"
                        : "DeltaV_ShieldModeElectromagnetic").Translate(),
                    defaultDesc = "DeltaV_ShieldModeDesc".Translate(),
                    icon = parent.def.uiIcon,
                };
                yield return command;
            }
            if (mode == SpaceShieldMode.Guard)
            {
                yield return Slider("DeltaV_ShieldRadius", ShieldProps.minGroundRadius,
                    ShieldProps.maxGroundRadius, s => Mathf.RoundToInt(s.groundRadius),
                    (s, value) => s.groundRadius = Mathf.Clamp(value, s.ShieldProps.minGroundRadius, s.ShieldProps.maxGroundRadius));
                yield break;
            }
            yield return Slider("DeltaV_ShieldProjection", ShieldProps.minProjection,
                ShieldProps.maxProjection, s => Mathf.RoundToInt(s.projection),
                (s, value) => s.projection = Mathf.Clamp(value, s.ShieldProps.minProjection, s.ShieldProps.maxProjection));
            yield return Slider("DeltaV_ShieldLength", ShieldProps.minLength,
                ShieldProps.maxLength, s => Mathf.RoundToInt(s.length),
                (s, value) => s.length = Mathf.Clamp(value, s.ShieldProps.minLength, s.ShieldProps.maxLength));
            yield return Slider("DeltaV_ShieldAngle", -90, 90,
                s => Mathf.RoundToInt(s.angleDeg), (s, value) => s.angleDeg = value);
            if (mode == SpaceShieldMode.Kinetic)
                yield return Slider("DeltaV_ShieldKineticStrength", 0, 100,
                    s => s.kineticStrengthPercent, (s, value) => s.kineticStrengthPercent = value);
            else
                yield return Slider("DeltaV_ShieldElectromagneticStrength", 0, 100,
                    s => s.electromagneticStrengthPercent,
                    (s, value) => s.electromagneticStrengthPercent = value);
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            if (State != SpaceShieldState.LineActive) return;
            GetSegment(out Vector2 start, out Vector2 end, out Vector2 normal);
            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
            Vector3 a = new Vector3(start.x, altitude, start.y);
            Vector3 b = new Vector3(end.x, altitude, end.y);
            Vector3 center = (a + b) * 0.5f;
            Vector3 front = center + new Vector3(normal.x, 0f, normal.y) * 1.5f;
            SimpleColor color = mode == SpaceShieldMode.Electromagnetic
                ? SimpleColor.Magenta : SimpleColor.Cyan;
            GenDraw.DrawLineBetween(a, b, color, 0.15f);
            GenDraw.DrawLineBetween(center, front, color, 0.12f);
        }

        private Command_BatchSetting<CompSpaceShield> Slider(string key, int min, int max,
            Func<CompSpaceShield, int> current, Action<CompSpaceShield, int> assign)
        {
            return new Command_BatchSetting<CompSpaceShield>(key, this, shields =>
                Find.WindowStack.Add(new Dialog_SpaceShieldSetting(shields, key, min, max,
                    current, assign)))
            {
                defaultLabel = key.Translate() + ": " + current(this),
                icon = TexCommand.Install,
            };
        }

        public override string CompInspectStringExtra()
        {
            string state = mode == SpaceShieldMode.Guard
                ? (UsesRegular ? "DeltaV_ShieldStateRegular" : "DeltaV_ShieldStateEmergency").Translate().ToString()
                : (mode == SpaceShieldMode.Kinetic ? "DeltaV_ShieldStateKinetic" : "DeltaV_ShieldStateElectromagnetic").Translate().ToString();
            string status = "DeltaV_ShieldCapacities".Translate(RegularCapacity.ToString("0.#"), ShieldProps.regularCapacity,
                EmergencyCapacity.ToString("0.#"), ShieldProps.emergencyCapacity);
            if (UsesRegular) status += "\n" + "DeltaV_ShieldSharedRegular".Translate(SharedRegularCapacity.ToString("0.#"));
            if (RegularCapacity < ShieldProps.regularCapacity)
                status += "\n" + (recoveryWaitTicks > 0
                    ? "DeltaV_ShieldRecoveryWait".Translate((recoveryWaitTicks / 60f).ToString("0.0"))
                    : (GuardRecoveryPowered ? "DeltaV_ShieldRecovering" : "DeltaV_ShieldGuardRecoveryPaused").Translate());
            if (mode != SpaceShieldMode.Guard) status += "\n" + SpaceShieldReference.Build(this);
            return state + "\n" + status;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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
        public string projectorTexPath = "Things/Building/SpaceWeapons/ShieldProjector";
        public float projectorDrawSize = 2.8f;

        public CompProperties_SpaceShield()
        {
            compClass = typeof(CompSpaceShield);
        }
    }

    /// <summary>Vanilla interception and hitpoint storage; Delta-V owns replenishment.</summary>
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
    /// Three mutually exclusive modes. The vanilla interceptor stores guard hitpoints;
    /// SpaceCollision consumes the two projected line modes.
    /// </summary>
    public class CompSpaceShield : ThingComp
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
        private int guardRecoveryTicksRemaining = -1;
        private bool guardUnlocked;
        private CompPowerTrader power;
        private CompFlickable flick;
        private CompProjectileInterceptor interceptor;
        private CompRefuelable silverFuel;
        private CompProperties_ProjectileInterceptor instanceInterceptorProps;
        private Material projectorMaterial;

        public CompProperties_SpaceShield ShieldProps => (CompProperties_SpaceShield)props;
        public bool GuardLocked => mode == SpaceShieldMode.Guard && !guardUnlocked;
        public int GuardTicksRemaining => mode == SpaceShieldMode.Guard
            ? Mathf.Max(0, guardRecoveryTicksRemaining) : 0;
        public bool GuardRecoveryPowered => parent.Spawned
            && (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn == true
            && power.PowerNet != null && FlickedOn;
        public int GuardHitPoints => (interceptor ??= parent.TryGetComp<CompProjectileInterceptor>())?.currentHitPoints ?? 0;
        public int GuardHitPointsMax => (interceptor ??= parent.TryGetComp<CompProjectileInterceptor>())?.HitPointsMax ?? 0;
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
            if (mode == SpaceShieldMode.Guard) StartGuardInterval(lockSwitching: false);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureInstanceInterceptorProps();
            if (mode == SpaceShieldMode.Guard && guardRecoveryTicksRemaining <= 0)
                StartGuardInterval(lockSwitching: false);
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
            Scribe_Values.Look(ref guardRecoveryTicksRemaining, "DeltaV_guardRecoveryTicksRemaining", -1);
            Scribe_Values.Look(ref guardUnlocked, "DeltaV_guardUnlocked", false);
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
            if (mode == SpaceShieldMode.Guard && silverFuel?.Fuel >= 1f
                && GuardHitPoints < GuardHitPointsMax) ApplySilverFuel();
            // Only ticks actually supplied by the local power net count toward recovery.
            if (mode == SpaceShieldMode.Guard && GuardRecoveryPowered
                && --guardRecoveryTicksRemaining <= 0)
            {
                guardUnlocked = true;
                (interceptor ??= parent.TryGetComp<CompProjectileInterceptor>()).currentHitPoints = GuardHitPointsMax;
                guardRecoveryTicksRemaining = Mathf.Max(1, ShieldProps.guardRecoveryTicks);
                UpdateSilverTarget();
            }
            else if (mode == SpaceShieldMode.Guard) UpdateSilverTarget();
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == CompRefuelable.RefueledSignal) ApplySilverFuel();
        }

        private void StartGuardInterval(bool lockSwitching)
        {
            guardUnlocked = !lockSwitching;
            guardRecoveryTicksRemaining = Mathf.Max(1, ShieldProps.guardRecoveryTicks);
        }

        private void UpdateSilverTarget()
        {
            silverFuel ??= parent.TryGetComp<CompRefuelable>();
            if (silverFuel == null) return;
            int missing = mode == SpaceShieldMode.Guard
                ? Mathf.Max(0, GuardHitPointsMax - GuardHitPoints) : 0;
            if (Mathf.Abs(silverFuel.TargetFuelLevel - missing) > 0.01f)
                silverFuel.TargetFuelLevel = missing;
        }

        private void ApplySilverFuel()
        {
            silverFuel ??= parent.TryGetComp<CompRefuelable>();
            if (silverFuel == null || mode != SpaceShieldMode.Guard) return;
            interceptor ??= parent.TryGetComp<CompProjectileInterceptor>();
            if (interceptor == null) return;
            int amount = Mathf.Min(Mathf.FloorToInt(silverFuel.Fuel),
                Mathf.Max(0, interceptor.HitPointsMax - interceptor.currentHitPoints));
            if (amount <= 0) return;
            interceptor.currentHitPoints += amount;
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

        public float WantedPowerWatts => mode == SpaceShieldMode.Guard ? ShieldProps.guardPowerWatts
            : ShieldProps.lineBasePowerWatts
                + ShieldProps.spaceProjectionPowerWatts
                    * Mathf.Max(0f, projection - ShieldProps.minProjection)
                + ShieldProps.spaceLengthPowerWatts
                    * Mathf.Max(0f, length - ShieldProps.minLength);

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

        public override void PostDraw()
        {
            base.PostDraw();
            projectorMaterial ??= MaterialPool.MatFrom(ShieldProps.projectorTexPath,
                ShaderDatabase.Cutout);
            Vector3 center = parent.DrawPos;
            center.y = parent.def.Altitude + 0.02f;
            // SpaceTransform.Rotate uses counterclockwise XY angles, while Unity's
            // positive yaw turns north toward east on the XZ plane.
            float facing = parent.Rotation.AsAngle - (mode == SpaceShieldMode.Guard ? 0f : angleDeg);
            Graphics.DrawMesh(MeshPool.plane10,
                Matrix4x4.TRS(center, Quaternion.Euler(0f, facing, 0f),
                    new Vector3(ShieldProps.projectorDrawSize, 1f, ShieldProps.projectorDrawSize)),
                projectorMaterial, 0);
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
            foreach (SpaceShieldMode next in new[]
                { SpaceShieldMode.Guard, SpaceShieldMode.Kinetic, SpaceShieldMode.Electromagnetic })
            {
                if (next == mode) continue;
                var command = new Command_BatchSetting<CompSpaceShield>("ShieldMode" + next,
                    this, shields =>
                    {
                        foreach (CompSpaceShield shield in shields)
                        {
                            if (!shield.parent.Spawned || shield.parent.Destroyed
                                || shield.GuardLocked) continue;
                            shield.mode = next;
                            if (next == SpaceShieldMode.Guard)
                                shield.StartGuardInterval(lockSwitching: true);
                            else shield.guardRecoveryTicksRemaining = 0;
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
                if (GuardLocked)
                {
                    command.groupable = false;
                    command.Disable("DeltaV_ShieldGuardLocked"
                        .Translate(GuardTicksRemaining.ToStringTicksToPeriod()));
                }
                yield return command;
            }
            if (mode == SpaceShieldMode.Guard)
            {
                yield return Slider("DeltaV_ShieldRadius", ShieldProps.minGroundRadius,
                    ShieldProps.maxGroundRadius, s => Mathf.RoundToInt(s.groundRadius),
                    (s, value) => s.groundRadius = value);
                yield break;
            }
            yield return Slider("DeltaV_ShieldProjection", ShieldProps.minProjection,
                ShieldProps.maxProjection, s => Mathf.RoundToInt(s.projection),
                (s, value) => s.projection = value);
            yield return Slider("DeltaV_ShieldLength", ShieldProps.minLength,
                ShieldProps.maxLength, s => Mathf.RoundToInt(s.length),
                (s, value) => s.length = value);
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
            if (mode == SpaceShieldMode.Guard)
                return "DeltaV_ShieldStateGuard".Translate() + "\n"
                    + "DeltaV_ShieldGuardStatus".Translate(GuardHitPoints, GuardHitPointsMax,
                        GuardTicksRemaining.ToStringTicksToPeriod())
                    + (GuardRecoveryPowered ? "" : "\n" + "DeltaV_ShieldGuardRecoveryPaused".Translate().ToString());
            string state = (mode == SpaceShieldMode.Kinetic
                ? "DeltaV_ShieldStateKinetic" : "DeltaV_ShieldStateElectromagnetic").Translate();
            return state + "\n" + SpaceShieldReference.Build(this);
        }
    }
}

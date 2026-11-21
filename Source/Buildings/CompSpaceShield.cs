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
        public int guardRecoveryTicks = 7500;
        public int regularCapacity = 750, emergencyCapacity = 750;
        public int recoveryDelayTicks = 180;
        public float recoveryPowerWatts = 1000f;

        public CompProperties_SpaceShield()
        {
            compClass = typeof(CompSpaceShield);
        }
    }

    /// <summary>Vanilla intersection and EMP; Delta-V owns capacity and guard outline rendering.</summary>
    public sealed class CompDeltaVShieldInterceptor : CompProjectileInterceptor
    {
        protected override int NumInactiveDots => 0;
        protected override int HitPointsPerInterval => 0;
    }

    /// <summary>
    /// Three mutually exclusive modes. Emergency capacity uses native CompRefuelable;
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
        private bool outlineActive;
        private CompRefuelable silverFuel;
        private CompProperties_ProjectileInterceptor instanceInterceptorProps;
        private Material[] topMaterials;
        private Texture2D topTexture, topMask;

        public Material ShellTopMaterial
        {
            get
            {
                int index = mode == SpaceShieldMode.Kinetic ? 0
                    : mode == SpaceShieldMode.Electromagnetic ? 1 : UsesRegular ? 2 : 3;
                topMaterials ??= new Material[4];
                if (topMaterials[index] == null)
                {
                    string path = parent.def.GetModExtension<DefModExtension_SpaceTurretArt>().topTexPath;
                    topTexture ??= ContentFinder<Texture2D>.Get(path);
                    topMask ??= ContentFinder<Texture2D>.Get(path + "_m");
                    topMaterials[index] = MaterialPool.MatFrom(new MaterialRequest(
                        topTexture, ShaderDatabase.CutoutComplex, FieldColor)
                    {
                        maskTex = topMask,
                        colorTwo = Color.white,
                    });
                }
                return topMaterials[index];
            }
        }
        // Existing shield art faces north; the shared turret convention faces east.
        public float ShellTopAngle => parent.Rotation.AsAngle
            - (mode == SpaceShieldMode.Guard ? 0f : angleDeg) - TurretTop.ArtworkRotation;

        public override void PostDraw()
        {
            base.PostDraw();
            SpaceTurretTopRenderer.Draw(parent, this, parent.DrawPos, Vector3.zero, 0f);
        }

        public CompProperties_SpaceShield ShieldProps => (CompProperties_SpaceShield)props;
        // Match vanilla CompProjectileInterceptor.CheckIntercept, independent of the footprint size.
        internal Vector2 GuardCenter
        {
            get
            {
                Vector3 center = parent.Position.ToVector3Shifted();
                return new Vector2(center.x, center.z);
            }
        }
        public static Color KineticColor => new Color(0.25f, 0.55f, 1f);
        public static Color ElectromagneticColor => new Color(0.7f, 0.35f, 1f);
        public static Color EmergencyColor => new Color(1f, 0.15f, 0.15f);
        public Color FieldColor => mode == SpaceShieldMode.Kinetic ? KineticColor
            : mode == SpaceShieldMode.Electromagnetic ? ElectromagneticColor
            : UsesRegular ? Color.white : EmergencyColor;

        private static string ModeLabel(SpaceShieldMode value) =>
            (value == SpaceShieldMode.Guard ? "DeltaV_ShieldModeGuard"
                : value == SpaceShieldMode.Kinetic ? "DeltaV_ShieldModeKinetic"
                : "DeltaV_ShieldModeElectromagnetic").Translate();

        internal void UpdateFieldColor()
        {
            EnsureInstanceInterceptorProps();
            if (instanceInterceptorProps != null) instanceInterceptorProps.color = FieldColor;
        }
        public bool GuardRecoveryPowered => parent.Spawned
            && (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn == true
            && power.PowerNet != null && FlickedOn;
        public bool FlickedOn => (flick ??= parent.TryGetComp<CompFlickable>())?.SwitchIsOn != false;

        public SpaceShieldState State
        {
            get
            {
                if (mode == SpaceShieldMode.Guard) return SpaceShieldState.Guard;
                if (!LineAvailableAt(Find.TickManager.TicksGame)) return SpaceShieldState.LineInactive;
                return SpaceShieldState.LineActive;
            }
        }

        internal bool LineAvailableAt(int tick) => parent.Spawned && tick >= disabledUntilTick
            && (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn == true && FlickedOn;

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

            UpdatePowerDraw();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            GuardShieldRenderer.Release(this);
            base.PostDeSpawn(map, mode);
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
                UpdatePowerDraw();
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            AdvanceGuardTick(GuardRecoveryPowered);
            UpdatePowerDraw();
            ObserveGuardOutline(UsesRegular && (interceptor ??= parent.TryGetComp<CompProjectileInterceptor>())?.Active == true);
        }

        internal void ObserveGuardOutline(bool regularActive)
        {
            if (outlineActive == regularActive) return;
            outlineActive = regularActive;
            Current.Game?.GetComponent<UniverseManager>()?.ShipStructure.InvalidateGuardOutline(this);
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            UpdatePowerDraw();
            NotifyProtectionGuard();
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
            UpdatePowerDraw();
            NotifyProtectionGuard();
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

        private float AvailablePulseEnergy()
        {
            float available = 0f;
            if (power?.PowerNet != null)
                foreach (CompPowerBattery battery in power.PowerNet.batteryComps)
                    if (!battery.StunnedByEMP) available += Mathf.Max(0f, battery.StoredEnergy);
            return available;
        }

        public float ConsumeKineticImpulse(float mass, float normalSpeed, float requested, float baseCost)
        {
            if (State != SpaceShieldState.LineActive) return 0f;
            float desired = SpaceKinetics.ShieldCost(mass, normalSpeed, requested, baseCost);
            float available = AvailablePulseEnergy();
            float impulse = SpaceKinetics.FundedShieldImpulse(mass, normalSpeed, requested, baseCost, available);
            ConsumePulseFraction(SpaceKinetics.ShieldCost(mass, normalSpeed, impulse, baseCost));
            if (available < desired)
                disabledUntilTick = Find.TickManager.TicksGame + ShieldProps.pulseFailureTicks;
            return impulse;
        }

        /// <summary>Pay once from the shared net and return the actually funded fraction.</summary>
        public float ConsumePulseFraction(float wattDays)
        {
            if (State != SpaceShieldState.LineActive) return 0f;
            if (wattDays <= 0f) return 1f;
            PowerNet net = power?.PowerNet;
            float available = AvailablePulseEnergy();
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
            if (!parent.Spawned) yield break;
            if (DebugSettings.ShowDevGizmos)
                foreach (Gizmo gizmo in DevGizmos()) yield return gizmo;
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            if (ShieldProps.regularCapacity > 0)
            {
                yield return new Gizmo_ShieldRegular(this);
                if (mode == SpaceShieldMode.Guard && emergencyReason != GuardEmergencyReason.Manual)
                {
                    var emergency = new Command_BatchSetting<CompSpaceShield>("ShieldManualEmergency", this,
                        shields => { foreach (var shield in shields) shield.EnterManualEmergency(); })
                    {
                        defaultLabel = "DeltaV_ShieldManualEmergency".Translate(),
                        defaultDesc = "DeltaV_ShieldManualEmergencyTip".Translate(), icon = parent.def.uiIcon
                    };
                    if (EmergencyCapacity <= 0) emergency.Disable("DeltaV_ShieldNoEmergency".Translate());
                    yield return emergency;
                }
                if (mode == SpaceShieldMode.Guard && emergencyReason != GuardEmergencyReason.None)
                {
                    var regular = new Command_BatchSetting<CompSpaceShield>("ShieldUseRegular", this,
                        shields => { foreach (var shield in shields) shield.UseRegularGuard(); })
                    {
                        defaultLabel = "DeltaV_ShieldUseRegular".Translate(),
                        defaultDesc = "DeltaV_ShieldUseRegularTip".Translate(), icon = parent.def.uiIcon
                    };
                    if (RegularCapacity <= 0) regular.Disable("DeltaV_ShieldNoRegular".Translate());
                    yield return regular;
                }
            }
            yield return new Command_BatchSetting<CompSpaceShield>("ShieldMode", this, shields =>
            {
                var options = new List<FloatMenuOption>();
                foreach (SpaceShieldMode next in new[]
                    { SpaceShieldMode.Guard, SpaceShieldMode.Kinetic, SpaceShieldMode.Electromagnetic })
                    options.Add(new FloatMenuOption(ModeLabel(next), () =>
                    {
                        foreach (CompSpaceShield shield in shields)
                        {
                            if (!shield.parent.Spawned || shield.parent.Destroyed) continue;
                            shield.mode = next;
                            shield.TryRejoinRegular();
                            shield.SettingsChanged();
                        }
                    }));
                Find.WindowStack.Add(new FloatMenu(options));
            })
            {
                defaultLabel = "DeltaV_ShieldCurrentMode".Translate(
                    (mode == SpaceShieldMode.Guard ? "DeltaV_ShieldStateGuard"
                        : mode == SpaceShieldMode.Kinetic ? "DeltaV_ShieldStateKinetic"
                        : "DeltaV_ShieldStateElectromagnetic").Translate()),
                defaultDesc = "DeltaV_ShieldModeDesc".Translate(),
                icon = parent.def.uiIcon,
                defaultIconColor = FieldColor,
            };
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
                ? SimpleColor.Magenta : SimpleColor.Blue;
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
                defaultDesc = SpaceShieldReference.SettingDescription(this, key),
                icon = TexCommand.Install,
            };
        }

        public override string CompInspectStringExtra()
        {
            string state = mode == SpaceShieldMode.Guard
                ? (UsesRegular ? "DeltaV_ShieldStateRegular" : "DeltaV_ShieldStateEmergency").Translate().ToString()
                : (mode == SpaceShieldMode.Kinetic ? "DeltaV_ShieldStateKinetic" : "DeltaV_ShieldStateElectromagnetic").Translate().ToString();
            string status = state;
            if (ShieldProps.regularCapacity > 0)
                status += "\n" + "DeltaV_ShieldRegularCapacity".Translate(RegularCapacity.ToString("0.#"), ShieldProps.regularCapacity);
            if (UsesRegular) status += "\n" + "DeltaV_ShieldSharedRegular".Translate(SharedRegularCapacity.ToString("0.#"));
            if (RegularCapacity < ShieldProps.regularCapacity)
                status += "\n" + (recoveryWaitTicks > 0
                    ? "DeltaV_ShieldRecoveryWait".Translate((recoveryWaitTicks / 60f).ToString("0.0"))
                    : (GuardRecoveryPowered ? "DeltaV_ShieldRecovering" : "DeltaV_ShieldGuardRecoveryPaused").Translate());
            if (mode != SpaceShieldMode.Guard) status += "\n" + SpaceShieldReference.Build(this);
            return status;
        }
    }
}

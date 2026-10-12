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
        Ground,
        Space,
    }

    public enum SpaceShieldState : byte
    {
        Ground,
        SpaceInactive,
        SpaceActive,
    }

    public class CompProperties_SpaceShield : CompProperties
    {
        public float groundRadius = 24.9f;
        public int minGroundRadius = 8;
        public int maxGroundRadius = 40;
        public int minProjection = 2;
        public int maxProjection = 50;
        public int minLength = 4;
        public int maxLength = 60;
        public float basePowerWatts = 200f;
        public float groundRadiusPowerWatts = 4f;
        public float spaceAreaPowerWatts = 4f;
        public int pulseFailureTicks = 180;
        public string projectorTexPath = "Things/Building/SpaceWeapons/ShieldProjector";
        public float projectorDrawSize = 2.8f;

        public CompProperties_SpaceShield()
        {
            compClass = typeof(CompSpaceShield);
        }
    }

    /// <summary>Odyssey interceptor timing and capacity, charged from this building's
    /// own power connection rather than from a grav engine facility link.</summary>
    public sealed class CompDeltaVShieldInterceptor : CompProjectileInterceptor
    {
        private CompPowerTrader power;
        protected override int NumInactiveDots => 0;

        protected override int HitPointsPerInterval
        {
            get
            {
                if (base.Active) return 0;
                power ??= parent.TryGetComp<CompPowerTrader>();
                return power?.PowerOn == true ? 2 : -1;
            }
        }
    }

    /// <summary>
    /// 原版逆重护盾的双模式控制。地面模式仅给此实例的拦截器换一份 Props 浅拷贝，
    /// 原版拦截/容量/绘制仍直接消费它；太空模式由 SpaceCollision 消费本 Comp 的线段。
    /// </summary>
    public class CompSpaceShield : ThingComp
    {
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public SpaceShieldMode mode = SpaceShieldMode.Ground;
        public float groundRadius = 24.9f;
        public float projection = 12f;
        public float length = 18f;
        public float angleDeg;
        public int refractionStrength = 2;
        private int disabledUntilTick;
        private CompPowerTrader power;
        private CompFlickable flick;
        private CompProjectileInterceptor interceptor;
        private CompProperties_ProjectileInterceptor instanceInterceptorProps;
        private Material projectorMaterial;

        public CompProperties_SpaceShield ShieldProps => (CompProperties_SpaceShield)props;
        // One operational result for space collision, field drawing and pulse payment.
        // Ground mode delegates charge and active state to the original interceptor.
        public SpaceShieldState State
        {
            get
            {
                if (mode == SpaceShieldMode.Ground) return SpaceShieldState.Ground;
                if (!parent.Spawned || Find.TickManager.TicksGame < disabledUntilTick
                    || (power ??= parent.TryGetComp<CompPowerTrader>())?.PowerOn != true
                    || (flick ??= parent.TryGetComp<CompFlickable>())?.SwitchIsOn == false
                    || !HasPulseEnergy()) return SpaceShieldState.SpaceInactive;
                return SpaceShieldState.SpaceActive;
            }
        }

        private bool HasPulseEnergy()
        {
            PowerNet net = power?.PowerNet;
            if (net == null) return false;
            foreach (CompPowerBattery battery in net.batteryComps)
                if (!battery.StunnedByEMP && battery.StoredEnergy > 0.0001f) return true;
            return false;
        }

        public override void PostPostMake()
        {
            base.PostPostMake();
            groundRadius = ShieldProps.groundRadius;
            EnsureInstanceInterceptorProps();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureInstanceInterceptorProps();
            UpdatePowerDraw();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref mode, "DeltaV_shieldMode", SpaceShieldMode.Ground);
            Scribe_Values.Look(ref groundRadius, "DeltaV_shieldGroundRadius", ShieldProps.groundRadius);
            Scribe_Values.Look(ref projection, "DeltaV_shieldProjection", 12f);
            Scribe_Values.Look(ref length, "DeltaV_shieldLength", 18f);
            Scribe_Values.Look(ref angleDeg, "DeltaV_shieldAngle", 0f);
            Scribe_Values.Look(ref refractionStrength, "DeltaV_shieldStrength", 2);
            Scribe_Values.Look(ref disabledUntilTick, "DeltaV_shieldDisabledUntilTick", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                groundRadius = Mathf.Clamp(groundRadius, ShieldProps.minGroundRadius,
                    ShieldProps.maxGroundRadius);
                projection = Mathf.Clamp(projection, ShieldProps.minProjection, ShieldProps.maxProjection);
                length = Mathf.Clamp(length, ShieldProps.minLength, ShieldProps.maxLength);
                angleDeg = Mathf.Clamp(angleDeg, -90f, 90f);
                refractionStrength = Mathf.Clamp(refractionStrength, 1, 5);
                EnsureInstanceInterceptorProps();
                UpdatePowerDraw();
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (Find.TickManager.TicksGame % 60 == parent.thingIDNumber % 60)
                UpdatePowerDraw();
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

        public float WantedPowerWatts => mode == SpaceShieldMode.Ground
            ? ShieldProps.basePowerWatts + ShieldProps.groundRadiusPowerWatts
                * Mathf.Max(0f, groundRadius * groundRadius
                    - ShieldProps.minGroundRadius * ShieldProps.minGroundRadius)
            : ShieldProps.basePowerWatts + ShieldProps.spaceAreaPowerWatts * projection * length;

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
            float facing = parent.Rotation.AsAngle - (mode == SpaceShieldMode.Space ? angleDeg : 0f);
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

        /// <summary>每层单独从实际电池扣脉冲；欠额仍耗尽可用蓄电并进入短暂失效。</summary>
        public bool TryConsumePulse(float wattDays)
        {
            if (State != SpaceShieldState.SpaceActive) return false;
            if (wattDays <= 0f) return true;
            PowerNet net = power?.PowerNet;
            float available = 0f;
            if (net != null)
                foreach (CompPowerBattery battery in net.batteryComps)
                    if (!battery.StunnedByEMP) available += Mathf.Max(0f, battery.StoredEnergy);
            float remaining = Mathf.Min(available, wattDays);
            if (net != null)
                foreach (CompPowerBattery battery in net.batteryComps)
                {
                    if (battery.StunnedByEMP || remaining <= 0f) continue;
                    float drawn = Mathf.Min(remaining, battery.StoredEnergy);
                    if (drawn <= 0f) continue;
                    battery.DrawPower(drawn);
                    remaining -= drawn;
                }
            if (available + 0.0001f >= wattDays) return true;
            disabledUntilTick = Find.TickManager.TicksGame + ShieldProps.pulseFailureTicks;
            return false;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer || !parent.Spawned) yield break;
            yield return new Command_Action
            {
                defaultLabel = (mode == SpaceShieldMode.Ground
                    ? "DeltaV_ShieldModeGround" : "DeltaV_ShieldModeSpace").Translate(),
                defaultDesc = "DeltaV_ShieldModeDesc".Translate(),
                icon = parent.def.uiIcon,
                action = delegate
                {
                    mode = mode == SpaceShieldMode.Ground ? SpaceShieldMode.Space : SpaceShieldMode.Ground;
                    SettingsChanged();
                },
            };
            if (mode == SpaceShieldMode.Ground)
            {
                interceptor ??= parent.TryGetComp<CompProjectileInterceptor>();
                if (interceptor != null)
                {
                    var activation = new Command_Action
                    {
                        defaultLabel = (interceptor.Active
                            ? "DeltaV_ShieldDeactivate" : "DeltaV_ShieldActivate").Translate(),
                        icon = parent.def.uiIcon,
                        action = () =>
                        {
                            if (interceptor.Active) interceptor.Deactivate();
                            else interceptor.Activate();
                        },
                    };
                    if (!interceptor.Active && (interceptor.Charging || interceptor.currentHitPoints < 100))
                        activation.Disable("DeltaV_ShieldNeedCharge".Translate());
                    yield return activation;
                }
                yield return Slider("DeltaV_ShieldRadius", ShieldProps.minGroundRadius,
                    ShieldProps.maxGroundRadius, Mathf.RoundToInt(groundRadius),
                    value => groundRadius = value);
                yield break;
            }
            yield return Slider("DeltaV_ShieldProjection", ShieldProps.minProjection,
                ShieldProps.maxProjection, Mathf.RoundToInt(projection), value => projection = value);
            yield return Slider("DeltaV_ShieldLength", ShieldProps.minLength,
                ShieldProps.maxLength, Mathf.RoundToInt(length), value => length = value);
            yield return Slider("DeltaV_ShieldAngle", -90, 90,
                Mathf.RoundToInt(angleDeg), value => angleDeg = value);
            yield return Slider("DeltaV_ShieldStrength", 1, 5,
                refractionStrength, value => refractionStrength = value);
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            if (State != SpaceShieldState.SpaceActive) return;
            GetSegment(out Vector2 start, out Vector2 end, out Vector2 normal);
            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
            Vector3 a = new Vector3(start.x, altitude, start.y);
            Vector3 b = new Vector3(end.x, altitude, end.y);
            Vector3 center = (a + b) * 0.5f;
            Vector3 front = center + new Vector3(normal.x, 0f, normal.y) * 1.5f;
            GenDraw.DrawLineBetween(a, b, SimpleColor.Cyan, 0.15f);
            GenDraw.DrawLineBetween(center, front, SimpleColor.Cyan, 0.12f);
        }

        private Command_Action Slider(string key, int min, int max, int initial, Action<int> assign)
        {
            return new Command_Action
            {
                defaultLabel = key.Translate() + ": " + initial,
                icon = parent.def.uiIcon,
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_SpaceShieldSetting(this, key, min, max,
                        initial, assign));
                },
            };
        }

        public override string CompInspectStringExtra()
        {
            string state = (mode == SpaceShieldMode.Ground
                ? "DeltaV_ShieldStateGround" : "DeltaV_ShieldStateSpace").Translate();
            if (mode != SpaceShieldMode.Space) return state;
            return state + "\n" + SpaceShieldReference.Build(refractionStrength, false);
        }
    }
}

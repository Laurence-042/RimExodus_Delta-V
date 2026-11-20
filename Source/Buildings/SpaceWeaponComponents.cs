using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>武器局部几何的唯一入口。偏移均以朝北 Def 坐标记录，再随 Rot4 旋转。</summary>
    public static class SpaceWeaponGeometry
    {
        public static bool WithinFireDeviation(float targetMapAngle, float muzzleMapAngle,
            float allowedDegrees)
        {
            return Mathf.Abs(Mathf.DeltaAngle(targetMapAngle * Mathf.Rad2Deg,
                muzzleMapAngle * Mathf.Rad2Deg)) <= Mathf.Clamp(allowedDegrees, 0f, 180f) + 0.001f;
        }

        public static IntVec3 RotatedCell(Thing thing, IntVec3 northOffset)
        {
            return thing.Position + northOffset.RotatedBy(thing.Rotation);
        }

        public static Vector2 FootprintCenter(Thing thing)
        {
            CellRect rect = thing.OccupiedRect();
            return new Vector2((rect.minX + rect.maxX + 1) * 0.5f,
                (rect.minZ + rect.maxZ + 1) * 0.5f);
        }

        public static Vector2 MuzzleMapPoint(CompSpaceWeapon weapon)
            => MuzzleMapPointAtAngle(weapon, null);

        internal static Vector2 CasematePivotOffset(Thing thing)
            => SpaceTransform.RotatePlacementOffset(thing.def.building.turretTopOffset, thing.Rotation);

        internal static Vector2 MuzzleMapPointAtAngle(CompSpaceWeapon weapon, float? turretAngle)
        {
            Vector2 center = FootprintCenter(weapon.parent) + SpaceTransform.RotatePlacementOffset(weapon.Mount.Offset, weapon.parent.Rotation);
            if ((weapon.WeaponProps.topTurret || weapon.WeaponProps.casemate) && weapon.WeaponProps.turretMuzzleDistance > 0f
                && weapon.Component<CompSpaceWeaponTraverse>() != null)
            {
                if (weapon.WeaponProps.casemate) center += CasematePivotOffset(weapon.parent);
                return center + SpaceTransform.Rotate(Vector2.right, turretAngle ?? weapon.FacingAngle)
                    * weapon.WeaponProps.turretMuzzleDistance;
            }
            Vector2 local = weapon.WeaponProps.muzzleOffset;
            // muzzleOffset 与 IntVec3 槽位同样以朝北 Def 坐标记录；不要混用
            // East=0 的航向角，否则 North 建筑的 +Z 偏移会被错误转到 -X。
            return center + SpaceTransform.RotatePlacementOffset(local, weapon.parent.Rotation);
        }

        public static int OwnerIdOf(Thing thing)
        {
            if (thing?.Map == null) return 0;
            SubstructureOwnership ownership = SubstructureOwnership.TryGet(thing.Map);
            if (ownership == null) return 0;
            int owner = 0;
            foreach (IntVec3 cell in thing.OccupiedRect())
            {
                int at = ownership.OwnerAt(cell);
                if (at <= 0 || (owner != 0 && owner != at)) return 0;
                owner = at;
            }
            return owner;
        }
    }

    public class CompProperties_SpaceWeaponTraverse : CompProperties
    {
        public float turnRateDegPerSec = 60f;
        public float fireToleranceDeg = 2f;
        public float mechanicalArcDeg = 360f;

        public CompProperties_SpaceWeaponTraverse()
        {
            compClass = typeof(CompSpaceWeaponTraverse);
        }
    }

    /// <summary>可转炮塔的权威角度。角度采用地图帧弧度，与 SpaceWeapon 的发射方向同口径。</summary>
    public class CompSpaceWeaponTraverse : ThingComp
    {
        private float currentAngle;
        [Unsaved] private float angleAtTickStart;
        [Unsaved] private int rotationTick = -1;
        [Unsaved] private int spaceCommandTick = -1;

        public CompProperties_SpaceWeaponTraverse TraverseProps =>
            (CompProperties_SpaceWeaponTraverse)props;
        public float CurrentAngle => currentAngle;

        public void RotateForPrefab(Rot4 rotation)
        {
            currentAngle = SpaceTransform.WrapAngle(currentAngle
                + ShipOrientation.AngleFromRot4(rotation)
                - ShipOrientation.AngleFromRot4(Rot4.North));
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad) currentAngle = ShipOrientation.AngleFromRot4(parent.Rotation);
            ApplyToVanillaTop();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref currentAngle, "currentAngle", 0f);
        }

        public void AimAt(float mapAngle)
        {
            spaceCommandTick = Find.TickManager.TicksGame;
            AdvanceToward(mapAngle);
        }

        public void AimAtVanilla(float mapAngle)
        {
            if (spaceCommandTick == Find.TickManager.TicksGame) return;
            AdvanceToward(mapAngle);
        }

        public bool AlignedTo(float mapAngle)
        {
            return Mathf.Abs(SpaceTransform.WrapAngle(currentAngle - mapAngle))
                <= TraverseProps.fireToleranceDeg * Mathf.Deg2Rad;
        }

        public static float MapAngleTo(LocalTargetInfo target, Thing turret)
        {
            Vector3 delta = target.Cell.ToVector3Shifted() - turret.DrawPos;
            return Mathf.Atan2(delta.z, delta.x);
        }

        private void AdvanceToward(float mapAngle)
        {
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon != null ? !weapon.Operational
                : parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                    || parent.TryGetComp<CompFlickable>() is { SwitchIsOn: false }) return;
            int now = Find.TickManager.TicksGame;
            if (rotationTick != now)
            {
                rotationTick = now;
                angleAtTickStart = currentAngle;
            }
            currentAngle = AngleAfter(angleAtTickStart, mapAngle, 1f / 60f);
            ApplyToVanillaTop();
        }

        internal float AngleAfter(float start, float desired, float seconds)
        {
            float delta = SpaceTransform.WrapAngle(ClampToMechanicalArc(desired) - start);
            float max = TraverseProps.turnRateDegPerSec * Mathf.Deg2Rad * Mathf.Max(0, seconds);
            return start + Mathf.Clamp(delta, -max, max);
        }

        private float ClampToMechanicalArc(float angle)
        {
            float half = TraverseProps.mechanicalArcDeg * 0.5f * Mathf.Deg2Rad;
            if (half >= Mathf.PI - 0.001f) return angle;
            float baseAngle = ShipOrientation.AngleFromRot4(parent.Rotation);
            return baseAngle + Mathf.Clamp(SpaceTransform.WrapAngle(angle - baseAngle), -half, half);
        }

        private void ApplyToVanillaTop()
        {
            if (parent is Building_TurretGun turret)
            {
                // 原版 AngleFlat 以 North=0、顺时针为正；Delta-V 以 East=0、逆时针为正。
                turret.Top.CurRotation = 90f - currentAngle * Mathf.Rad2Deg;
            }
        }
    }

    public class DefModExtension_SpaceMissileAmmo : DefModExtension
    {
        public ThingDef spaceProjectileDef;
    }

    public class CompProperties_SpaceWeaponMagazine : CompProperties
    {
        public ThingDef projectileDef;
        public bool armorPiercing;
        public float ammoPerShot = 1f;
        public float unitMass = 1f;
        public int defaultPriority;
        public bool attachment;
        public float armorPenetrationScale = 1f;

        public CompProperties_SpaceWeaponMagazine()
        {
            compClass = typeof(CompSpaceWeaponMagazine);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (ammoPerShot <= 0f || unitMass <= 0f)
                yield return $"{parentDef.defName}: invalid ammunition stock configuration";
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            yield return SpaceCombatStats.Entry("DeltaV_StatAmmoPerShot",
                (req.Thing?.TryGetComp<CompWeaponTuning>()?.mass ?? ammoPerShot).ToString("0.##"), 5060);
            if (armorPenetrationScale > 1f)
                yield return SpaceCombatStats.Entry("DeltaV_StatArmorPenetrationScale",
                    armorPenetrationScale.ToString("0.##") + "×", 5050);
        }
    }

    /// <summary>通过原版 Refuelable 读取弹头/装药；导弹适配原版可换弹组件。</summary>
    public partial class CompSpaceWeaponMagazine : ThingComp
    {
        private int priority;
        private bool initialized;

        public CompProperties_SpaceWeaponMagazine MagazineProps =>
            (CompProperties_SpaceWeaponMagazine)props;
        public CompRefuelable Refuelable => Supply(GunSupplyKind.Heads) ?? parent.TryGetComp<CompRefuelable>();
        public CompChangeableProjectile ChangeableProjectile => parent.TryGetComp<CompChangeableProjectile>();
        public int Priority => priority;
        public bool HasShot => ChangeableProjectile != null
            ? ChangeableProjectile.Loaded && ChangeableProjectile.Projectile != null
            : Refuelable != null && Refuelable.Fuel + 0.0001f >= MagazineProps.ammoPerShot;
        public ThingDef SelectedProjectileDef => ChangeableProjectile?.LoadedShell
            ?.GetModExtension<DefModExtension_SpaceMissileAmmo>()?.spaceProjectileDef
            ?? ChangeableProjectile?.Projectile ?? MagazineProps.projectileDef
            ?? (MagazineProps.armorPiercing
                ? parent.TryGetComp<CompSpaceWeaponAttachment>()?.HostWeapon?.WeaponProps.armorPiercingProjectileDef
                : null);

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!initialized)
            {
                priority = MagazineProps.defaultPriority;
                initialized = true;
            }
            if (!respawningAfterLoad) ShipControlService.NotifyGunnerChange(parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            ShipControlService.NotifyGunnerChange(map);
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "Refueled" || signal == "RanOutOfFuel")
            {
                ShipControlService.NotifyGunnerChange(parent.Map);
                NotifySupplyChanged();
            }
        }

        public void ConsumeShot()
        {
            if (ChangeableProjectile != null)
                ChangeableProjectile.Notify_ProjectileLaunched();
            else
                Refuelable?.ConsumeFuel(MagazineProps.ammoPerShot);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref priority, "priority", MagazineProps.defaultPriority);
            Scribe_Values.Look(ref initialized, "priorityInitialized", true);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (ChangeableProjectile != null) yield break;
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            yield return new Command_BatchSetting<CompSpaceWeaponMagazine>("MagazinePriority", this,
                magazines =>
                {
                    var options = new List<FloatMenuOption>();
                    for (int value = -2; value <= 5; value++)
                    {
                        int captured = value;
                        options.Add(new FloatMenuOption(captured.ToString(), () =>
                        {
                            foreach (CompSpaceWeaponMagazine magazine in magazines)
                                if (magazine.parent.Spawned && !magazine.parent.Destroyed)
                                { magazine.priority = captured; magazine.NotifySupplyChanged(); }
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                })
            {
                defaultLabel = "DeltaV_MagazinePriority".Translate(priority),
                defaultDesc = "DeltaV_MagazinePriorityTip".Translate(),
                icon = TexCommand.Install,
            };
        }
    }

    public class CompProperties_SpaceWeaponAttachment : CompProperties
    {
        public bool enabledByDefault = true;
        public bool requiresHostPower;
        public List<ThingDef> allowedHostDefs = new List<ThingDef>();

        public CompProperties_SpaceWeaponAttachment()
        {
            compClass = typeof(CompSpaceWeaponGenericAttachment);
        }
    }

    public abstract class CompSpaceWeaponAttachment : ThingComp
    {
        private bool enabled;
        private bool initialized;
        internal bool SwitchedOn => initialized ? enabled : AttachmentProps.enabledByDefault;
        public bool Enabled => enabled && Powered;
        public CompProperties_SpaceWeaponAttachment AttachmentProps =>
            (CompProperties_SpaceWeaponAttachment)props;
        public bool CompatibleWith(ThingDef hostDef) =>
            AttachmentProps.allowedHostDefs.NullOrEmpty()
            || AttachmentProps.allowedHostDefs.Contains(hostDef);
        public virtual float AvailableShotEnergy => 0f;
        public virtual CompPowerBattery ShotEnergyBattery => null;
        public virtual bool CanSupplyShotEnergy(float wattDays) => false;
        public virtual void ConsumeShotEnergy(float wattDays) { }
        public virtual int? AimSkillFor(CompSpaceWeapon weapon) => null;
        public virtual float SegmentEnergyReduction => 0f;
        public virtual float ShotIntervalMultiplier => 1f;

        public CompSpaceCargo HostCargo
        {
            get
            {
                if (!parent.Spawned) return null;
                foreach (Thing thing in parent.Position.GetThingList(parent.Map))
                    if (thing.TryGetComp<CompSpaceCargo>() is { } cargo
                        && cargo.AttachmentSlotAt(parent.Position) != null) return cargo;
                return null;
            }
        }

        /// <summary>
        /// 覆盖式挂件不是 edifice；同格 EdificeGrid 中留下的建筑就是宿主。
        /// 这与原版 ThingGrid 允许普通 Thing 覆盖 edifice 的语义一致，不需要放置补丁。
        /// </summary>
        public CompSpaceWeapon HostWeapon
        {
            get
            {
                if (!parent.Spawned || parent.Map == null) return null;
                return parent.Position.GetEdifice(parent.Map)?.TryGetComp<CompSpaceWeapon>();
            }
        }

        public CompSpaceRailgun HostRailgun
        {
            get
            {
                if (!parent.Spawned || parent.Map == null) return null;
                foreach (Thing thing in parent.Map.thingGrid.ThingsListAtFast(parent.Position))
                {
                    if (thing == parent || thing is not ThingWithComps host || thing.Destroyed) continue;
                    CompSpaceRailgun rail = host.TryGetComp<CompSpaceRailgun>();
                    if (rail != null && rail.AttachmentSlotAt(parent.Position) != null) return rail;
                }
                return null;
            }
        }

        public bool Powered
        {
            get
            {
                if (!AttachmentProps.requiresHostPower) return true;
                ThingWithComps host = HostWeapon?.parent ?? HostRailgun?.parent ?? HostCargo?.parent;
                if (host == null) return false;
                return host.TryGetComp<CompPowerTrader>()?.PowerOn ?? true;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!initialized)
            {
                enabled = AttachmentProps.enabledByDefault;
                initialized = true;
            }
            parent.TryGetComp<CompSwitchableBattery>()?.RefreshConnection();
            if (!respawningAfterLoad) ShipControlService.NotifyGunnerChange(parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            ShipControlService.NotifyGunnerChange(map);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref initialized, "enabledInitialized", true);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            yield return new Command_BatchToggle<CompSpaceWeaponAttachment>("AttachmentEnabled",
                this, attachment => attachment.enabled, (attachment, active) =>
                {
                    attachment.enabled = active;
                    attachment.parent.TryGetComp<CompSwitchableBattery>()?.RefreshConnection();
                    attachment.parent.TryGetComp<CompCargoTemperature>()?.RefreshPower();
                    attachment.HostWeapon?.NotifyParametersChanged();
                    ShipControlService.NotifyGunnerChange(attachment.parent.Map);
                })
            {
                defaultLabel = "DeltaV_AttachmentEnabled".Translate(),
                defaultDesc = "DeltaV_AttachmentEnabledTip".Translate(),
                icon = TexCommand.ForbidOff,
            };
        }
    }

    public class CompSpaceWeaponGenericAttachment : CompSpaceWeaponAttachment { }
}

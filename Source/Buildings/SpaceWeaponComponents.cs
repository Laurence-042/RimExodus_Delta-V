using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    public static class SpaceWeaponCrewUtility
    {
        public static IntVec3 GunnerCell(Thing thing, CompProperties_SpaceWeaponCrew props)
        {
            return SpaceWeaponGeometry.RotatedCell(thing, props.gunnerOffset);
        }

        public static IntVec3 GunnerCellAt(IntVec3 root, Rot4 rotation,
            CompProperties_SpaceWeaponCrew props)
        {
            return root + props.gunnerOffset.RotatedBy(rotation);
        }

        public static bool RequiresClearCell(CompSpaceWeapon weapon)
        {
            return weapon?.Crew != null && !weapon.AutoAimEffective;
        }
    }

    /// <summary>武器局部几何的唯一入口。偏移均以朝北 Def 坐标记录，再随 Rot4 旋转。</summary>
    public static class SpaceWeaponGeometry
    {
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
        {
            Vector2 center = FootprintCenter(weapon.parent);
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

    public class CompProperties_SpaceWeaponCrew : CompProperties
    {
        public IntVec3 gunnerOffset = new IntVec3(0, 0, -1);
        public WorkTags requiredWorkTags = WorkTags.Violent;

        public CompProperties_SpaceWeaponCrew()
        {
            compClass = typeof(CompSpaceWeaponCrew);
        }
    }

    /// <summary>载人需求与固定炮手位。原版 CompMannable 仅作为双模炮塔适配器。</summary>
    public class CompSpaceWeaponCrew : ThingComp
    {
        private int lastManTick = -1;
        private Pawn lastManPawn;
        public Pawn reservedGunner;

        public CompProperties_SpaceWeaponCrew CrewProps => (CompProperties_SpaceWeaponCrew)props;
        public IntVec3 GunnerCell => SpaceWeaponCrewUtility.GunnerCell(parent, CrewProps);
        public bool MannedNow => Find.TickManager.TicksGame - lastManTick <= 1
            && lastManPawn != null && lastManPawn.Spawned;
        public Pawn ManningPawn => MannedNow ? lastManPawn : null;

        public void ManForATick(Pawn pawn)
        {
            lastManTick = Find.TickManager.TicksGame;
            lastManPawn = pawn;
            pawn.mindState.lastMannedThing = parent;
            parent.TryGetComp<CompMannable>()?.ManForATick(pawn);
        }

        public void NotifyVanillaManned(Pawn pawn)
        {
            if (pawn == null || pawn.Position != GunnerCell) return;
            lastManTick = Find.TickManager.TicksGame;
            lastManPawn = pawn;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref reservedGunner, "reservedGunner");
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn pawn)
        {
            if (!pawn.RaceProps.ToolUser || pawn.Map != parent.Map
                || !pawn.CanReserveAndReach(parent, PathEndMode.Touch, Danger.Deadly))
            {
                yield break;
            }
            if (CrewProps.requiredWorkTags != WorkTags.None
                && pawn.WorkTagIsDisabled(CrewProps.requiredWorkTags))
            {
                yield return new FloatMenuOption("CannotManThing".Translate(parent.LabelShort, parent), null);
                yield break;
            }
            if (!GunnerCell.Standable(parent.Map)
                || !pawn.CanReach(GunnerCell, PathEndMode.OnCell, Danger.Deadly))
            {
                yield break;
            }
            yield return new FloatMenuOption("OrderManThing".Translate(parent.LabelShort, parent), delegate
            {
                Job job = JobMaker.MakeJob(DeltaVDefOf.DeltaV_OperateSpaceWeapon);
                job.targetB = parent;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            });
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
        [Unsaved] private float desiredAngle;
        [Unsaved] private bool hasDesired;

        public CompProperties_SpaceWeaponTraverse TraverseProps =>
            (CompProperties_SpaceWeaponTraverse)props;
        public float CurrentAngle => currentAngle;

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
            desiredAngle = ClampToMechanicalArc(mapAngle);
            hasDesired = true;
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

        public override void CompTick()
        {
            base.CompTick();
            if (parent is Building_TurretGun turret && turret.CurrentTarget.IsValid)
                AimAt(MapAngleTo(turret.CurrentTarget, turret));
            if (hasDesired)
            {
                float delta = SpaceTransform.WrapAngle(desiredAngle - currentAngle);
                float max = TraverseProps.turnRateDegPerSec * Mathf.Deg2Rad / 60f;
                currentAngle += Mathf.Clamp(delta, -max, max);
            }
            ApplyToVanillaTop();
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

    public class CompProperties_SpaceWeaponMagazine : CompProperties
    {
        public ThingDef projectileDef;
        public float ammoPerShot = 1f;
        public int defaultPriority;
        public bool attachment;

        public CompProperties_SpaceWeaponMagazine()
        {
            compClass = typeof(CompSpaceWeaponMagazine);
        }
    }

    /// <summary>对原版 CompRefuelable 的武器语义适配；搬运和持久计数继续复用原版。</summary>
    public class CompSpaceWeaponMagazine : ThingComp
    {
        private int priority;
        private bool initialized;

        public CompProperties_SpaceWeaponMagazine MagazineProps =>
            (CompProperties_SpaceWeaponMagazine)props;
        public CompRefuelable Refuelable => parent.TryGetComp<CompRefuelable>();
        public int Priority => priority;
        public bool HasShot => Refuelable != null && Refuelable.Fuel + 0.0001f >= MagazineProps.ammoPerShot;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!initialized)
            {
                priority = MagazineProps.defaultPriority;
                initialized = true;
            }
        }

        public void ConsumeShot()
        {
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
            if (parent.Faction != Faction.OfPlayer && !DebugSettings.godMode) yield break;
            yield return new Command_Action
            {
                defaultLabel = "DeltaV_MagazinePriority".Translate(priority),
                defaultDesc = "DeltaV_MagazinePriorityTip".Translate(),
                icon = TexCommand.Install,
                action = delegate
                {
                    var options = new List<FloatMenuOption>();
                    for (int value = -2; value <= 5; value++)
                    {
                        int captured = value;
                        options.Add(new FloatMenuOption(captured.ToString(), delegate { priority = captured; }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }
    }

    public class CompProperties_SpaceWeaponAttachment : CompProperties
    {
        public bool enabledByDefault = true;
        public bool requiresHostPower;

        public CompProperties_SpaceWeaponAttachment()
        {
            compClass = typeof(CompSpaceWeaponAttachment);
        }
    }

    public class CompSpaceWeaponAttachment : ThingComp
    {
        private bool enabled;
        private bool initialized;
        public bool Enabled => enabled && Powered;
        public CompProperties_SpaceWeaponAttachment AttachmentProps =>
            (CompProperties_SpaceWeaponAttachment)props;

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

        public bool Powered
        {
            get
            {
                if (!AttachmentProps.requiresHostPower) return true;
                CompSpaceWeapon host = HostWeapon;
                if (host == null) return false;
                return host.parent.TryGetComp<CompPowerTrader>()?.PowerOn ?? true;
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
            yield return new Command_Toggle
            {
                defaultLabel = "DeltaV_AttachmentEnabled".Translate(),
                defaultDesc = "DeltaV_AttachmentEnabledTip".Translate(),
                icon = TexCommand.ForbidOff,
                isActive = () => enabled,
                toggleAction = delegate { enabled = !enabled; }
            };
        }
    }
}

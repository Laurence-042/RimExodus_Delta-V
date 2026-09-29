using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>保留旧 XML 名称；重构后仅 Accelerator 有效，装弹机本身是完整武器。</summary>
    public enum RailgunPartRole : byte
    {
        Loader = 0,
        Accelerator = 1,
        Muzzle = 2
    }

    public class CompProperties_SpaceRailgun : CompProperties
    {
        public RailgunPartRole role = RailgunPartRole.Accelerator;
        public float speedBonus = 20f;
        public bool movesMuzzle = true;
        public IntVec3 outputOffset = IntVec3.Zero;
        public IntVec3 nextRootOffset = new IntVec3(0, 0, 1);

        public CompProperties_SpaceRailgun()
        {
            compClass = typeof(CompSpaceRailgun);
        }
    }

    public readonly struct SpaceWeaponAccelerationSolution
    {
        public readonly float speedBonus;
        public readonly Vector2? muzzleMap;
        public readonly List<CompSpaceRailgun> segments;

        public SpaceWeaponAccelerationSolution(float speedBonus, Vector2? muzzleMap,
            List<CompSpaceRailgun> segments)
        {
            this.speedBonus = speedBonus;
            this.muzzleMap = muzzleMap;
            this.segments = segments;
        }
    }

    /// <summary>通用串联加速件；只修改速度和可选出膛位置。</summary>
    public class CompSpaceRailgun : ThingComp
    {
        public CompProperties_SpaceRailgun RailProps => (CompProperties_SpaceRailgun)props;
        public RailgunPartRole Role => RailProps.role;

        public static SpaceWeaponAccelerationSolution Resolve(CompSpaceWeapon weapon)
        {
            var used = new List<CompSpaceRailgun>();
            if (weapon?.parent?.Map == null || weapon.WeaponProps.acceleratorStarts.NullOrEmpty())
            {
                return new SpaceWeaponAccelerationSolution(0f, null, used);
            }
            Map map = weapon.parent.Map;
            int owner = SpaceWeaponGeometry.OwnerIdOf(weapon.parent);
            float bonus = 0f;
            Vector2? muzzle = null;
            var seen = new HashSet<int>();
            foreach (IntVec3 start in weapon.WeaponProps.acceleratorStarts)
            {
                IntVec3 root = SpaceWeaponGeometry.RotatedCell(weapon.parent, start);
                int guard = 0;
                while (guard++ < 64 && root.InBounds(map))
                {
                    CompSpaceRailgun segment = SegmentAtRoot(map, root, weapon.parent.Rotation);
                    if (segment == null || !seen.Add(segment.parent.thingIDNumber)) break;
                    if (!weapon.WeaponProps.allowedAcceleratorDefs.NullOrEmpty()
                        && !weapon.WeaponProps.allowedAcceleratorDefs.Contains(segment.parent.def)) break;
                    if (owner <= 0 || SpaceWeaponGeometry.OwnerIdOf(segment.parent) != owner) break;
                    if (segment.parent.TryGetComp<CompPowerTrader>() is { PowerOn: false }) break;
                    used.Add(segment);
                    bonus += segment.RailProps.speedBonus;
                    if (segment.RailProps.movesMuzzle)
                    {
                        IntVec3 output = SpaceWeaponGeometry.RotatedCell(segment.parent,
                            segment.RailProps.outputOffset);
                        muzzle = new Vector2(output.x + 0.5f, output.z + 0.5f);
                    }
                    root = SpaceWeaponGeometry.RotatedCell(segment.parent,
                        segment.RailProps.nextRootOffset);
                }
            }
            return new SpaceWeaponAccelerationSolution(bonus, muzzle, used);
        }

        public static CompSpaceRailgun SegmentAtRoot(Map map, IntVec3 root, Rot4 rotation)
        {
            if (map == null || !root.InBounds(map)) return null;
            foreach (Thing thing in map.thingGrid.ThingsListAtFast(root))
            {
                if (thing.Position != root || thing.Rotation != rotation || thing.Destroyed) continue;
                CompSpaceRailgun comp = (thing as ThingWithComps)?.TryGetComp<CompSpaceRailgun>();
                if (comp != null && comp.Role == RailgunPartRole.Accelerator) return comp;
            }
            return null;
        }

        public static string ChainSummary(ThingWithComps weaponThing)
        {
            CompSpaceWeapon weapon = weaponThing?.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return "not a space weapon";
            SpaceWeaponAccelerationSolution solution = Resolve(weapon);
            return $"accelerators={solution.segments.Count} speed+={solution.speedBonus:F1}";
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            if (parent.Map == null) return;
            IntVec3 root = SpaceWeaponGeometry.RotatedCell(parent, RailProps.nextRootOffset);
            var cells = new List<IntVec3>();
            ThingDef def = parent.def;
            foreach (IntVec3 cell in GenAdj.OccupiedRect(root, parent.Rotation, def.Size)) cells.Add(cell);
            GenDraw.DrawFieldEdges(cells,
                SegmentAtRoot(parent.Map, root, parent.Rotation) != null ? Color.green : Color.yellow);
            GenDraw.DrawLineBetween(parent.TrueCenter(), root.ToVector3Shifted());
        }
    }
}

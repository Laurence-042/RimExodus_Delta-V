using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>装弹机与加速段的建造关联只读 Def 连接声明。</summary>
    public static class SpaceRailgunConstruction
    {
        private static Dictionary<ThingDef, List<ThingDef>> loadersByAccelerator;
        private static int cachedDefCount = -1;

        public static IEnumerable<Gizmo> BuildGizmos(ThingWithComps host)
        {
            if (!host.Spawned || (host.Faction != Faction.OfPlayer && !DebugSettings.godMode))
                yield break;

            var related = new List<ThingDef>();
            var seen = new HashSet<ThingDef>();
            CompSpaceWeapon weapon = host.TryGetComp<CompSpaceWeapon>();
            if (weapon != null && !weapon.WeaponProps.acceleratorStarts.NullOrEmpty())
            {
                foreach (ThingDef def in weapon.WeaponProps.allowedAcceleratorDefs)
                    if (def != null && seen.Add(def)) related.Add(def);
            }

            CompSpaceRailgun rail = host.TryGetComp<CompSpaceRailgun>();
            if (rail != null && rail.Role == RailgunPartRole.Accelerator)
            {
                if (seen.Add(host.def)) related.Add(host.def); // 串联下一段
                EnsureReverseIndex();
                if (loadersByAccelerator.TryGetValue(host.def, out List<ThingDef> loaders))
                {
                    foreach (ThingDef def in loaders)
                        if (seen.Add(def)) related.Add(def);
                }
            }

            if (TryBarrierCell(host, out _))
            {
                ThingDef barrier = DefDatabase<ThingDef>.GetNamed("DeltaV_VacuumBarrier");
                Designator_Build designator = BuildCopyCommandUtility.FindAllowedDesignator(barrier);
                if (designator != null) yield return designator;
            }

            foreach (ThingDef def in related)
            {
                Designator_Build designator = BuildCopyCommandUtility.FindAllowedDesignator(def);
                if (designator != null) yield return designator;
            }
        }

        internal static void DrawBarrierCell(ThingWithComps host)
        {
            if (TryBarrierCell(host, out IntVec3 cell)
                && DefDatabase<ResearchProjectDef>.GetNamed("DeltaV_VacuumBarrier").IsFinished)
                GenDraw.DrawFieldEdges(new List<IntVec3> { cell }, UnityEngine.Color.cyan);
        }

        private static bool TryBarrierCell(ThingWithComps host, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (!host.Spawned || host.Faction != Faction.OfPlayer) return false;
            var weapon = host.TryGetComp<CompSpaceWeapon>();
            var segment = host.TryGetComp<CompSpaceRailgun>();
            if (weapon == null || weapon.WeaponProps.acceleratorStarts.NullOrEmpty())
                weapon = segment?.ConnectedWeapon();
            if (weapon == null) return false;

            var segments = weapon.Layout.acceleration.segments;
            var last = segments.Count > 0 ? segments[segments.Count - 1] : null;
            if (segment != null && last != segment) return false;
            IntVec3 offset = last?.RailProps.nextRootOffset ?? weapon.WeaponProps.acceleratorStarts[0];
            cell = SpaceWeaponGeometry.RotatedCell(last?.parent ?? weapon.parent, offset);
            return cell.InBounds(host.Map) && SubstructureOwnership.HasFoundation(host.Map, cell);
        }

        public static IReadOnlyList<ThingDef> LoaderDefsFor(ThingDef accelerator)
        {
            EnsureReverseIndex();
            return accelerator != null
                && loadersByAccelerator.TryGetValue(accelerator, out List<ThingDef> loaders)
                    ? loaders : (IReadOnlyList<ThingDef>)System.Array.Empty<ThingDef>();
        }

        private static void EnsureReverseIndex()
        {
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            if (loadersByAccelerator != null && cachedDefCount == defs.Count) return;
            cachedDefCount = defs.Count;
            loadersByAccelerator = new Dictionary<ThingDef, List<ThingDef>>();
            foreach (ThingDef def in defs)
            {
                CompProperties_SpaceWeapon props = def.GetCompProperties<CompProperties_SpaceWeapon>();
                if (props == null || props.acceleratorStarts.NullOrEmpty()) continue;
                foreach (ThingDef accelerator in props.allowedAcceleratorDefs)
                {
                    if (accelerator == null) continue;
                    if (!loadersByAccelerator.TryGetValue(accelerator, out List<ThingDef> loaders))
                        loadersByAccelerator[accelerator] = loaders = new List<ThingDef>();
                    loaders.Add(def);
                }
            }
        }
    }
}

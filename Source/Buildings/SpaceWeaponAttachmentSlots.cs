using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Def 槽位是建造关系与选中提示的共同事实源。</summary>
    public static class SpaceWeaponAttachmentSlots
    {
        public static IEnumerable<Gizmo> BuildGizmos(ThingWithComps host,
            List<SpaceWeaponAttachmentSlot> slots, List<ThingDef> allowed)
        {
            if (slots.NullOrEmpty() || !host.Spawned
                || (host.Faction != Faction.OfPlayer && !DebugSettings.godMode)) yield break;
            IEnumerable<ThingDef> candidates = !allowed.NullOrEmpty()
                ? allowed : DefDatabase<ThingDef>.AllDefsListForReading;
            foreach (ThingDef def in candidates)
            {
                CompProperties_SpaceWeaponAttachment attachment =
                    def.GetCompProperties<CompProperties_SpaceWeaponAttachment>();
                if (attachment == null || (!attachment.allowedHostDefs.NullOrEmpty()
                    && !attachment.allowedHostDefs.Contains(host.def))) continue;
                Designator_Build designator = BuildCopyCommandUtility.FindAllowedDesignator(def);
                if (designator != null) yield return designator;
            }
        }

        public static void DrawSelection(ThingWithComps host,
            List<SpaceWeaponAttachmentSlot> slots, IEnumerable<ThingWithComps> attached)
        {
            if (host.Map == null || slots.NullOrEmpty()) return;
            var occupied = new HashSet<IntVec3>();
            foreach (ThingWithComps thing in attached)
                foreach (IntVec3 cell in thing.OccupiedRect()) occupied.Add(cell);
            foreach (SpaceWeaponAttachmentSlot slot in slots)
            {
                IntVec3 cell = SpaceWeaponGeometry.RotatedCell(host, slot.cell);
                if (!host.OccupiedRect().Contains(cell)) continue;
                GenDraw.DrawFieldEdges(new List<IntVec3> { cell },
                    occupied.Contains(cell) ? Color.green : Color.yellow);
            }
        }
    }
}

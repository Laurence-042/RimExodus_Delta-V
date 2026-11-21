using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Def 槽位是建造关系与选中提示的共同事实源。</summary>
    public static class SpaceWeaponAttachmentSlots
    {
        internal static bool Fits(ThingDef host, IntVec3 root, Rot4 rotation, SpaceWeaponAttachmentSlot slot,
            ThingDef attachment, IntVec3 attachmentRoot, Rot4 attachmentRotation)
        {
            if (root + slot.cell.RotatedBy(rotation) != attachmentRoot) return false;
            CellRect hostRect = GenAdj.OccupiedRect(root, rotation, host.Size);
            if (slot.external)
                return rotation == attachmentRotation && !hostRect.Overlaps(GenAdj.OccupiedRect(attachmentRoot, attachmentRotation, attachment.Size));
            foreach (IntVec3 cell in GenAdj.OccupiedRect(attachmentRoot, attachmentRotation, attachment.Size))
                if (!hostRect.Contains(cell)) return false;
            return true;
        }

        internal static IEnumerable<Thing> ExteriorHosts(ThingDef attachment, IntVec3 root, Rot4 rotation, Map map)
        {
            var plugin = attachment.GetCompProperties<CompProperties_SpaceWeaponAttachment>();
            if (plugin == null || !plugin.external) yield break;
            var seen = new HashSet<Thing>();
            foreach (ThingDef host in plugin.allowedHostDefs)
            {
                var weapon = host.GetCompProperties<CompProperties_SpaceWeapon>();
                if (weapon == null || !weapon.allowedAttachmentDefs.NullOrEmpty()
                    && !weapon.allowedAttachmentDefs.Contains(attachment)) continue;
                foreach (var slot in weapon.attachmentSlots)
                {
                    if (!slot.external) continue;
                    IntVec3 position = root - slot.cell.RotatedBy(rotation);
                    if (!position.InBounds(map)
                        || !Fits(host, position, rotation, slot, attachment, root, rotation)) continue;
                    foreach (Thing candidate in map.thingGrid.ThingsListAtFast(position))
                    {
                        if (candidate.Position != position || candidate.Rotation != rotation || candidate.Destroyed) continue;
                        ThingDef candidateDef = candidate.def.IsBlueprint || candidate.def.IsFrame
                            ? candidate.def.entityDefToBuild as ThingDef : candidate.def;
                        if (candidateDef == host && seen.Add(candidate)) yield return candidate;
                    }
                }
            }
        }

        internal static IEnumerable<ThingWithComps> Discover(ThingWithComps host,
            List<SpaceWeaponAttachmentSlot> slots, List<ThingDef> allowed,
            Predicate<CompSpaceWeaponAttachment> ownsOverlay, HashSet<IntVec3> dependencies)
        {
            Map map = host.Map;
            if (map == null || slots.NullOrEmpty()) yield break;
            int owner = SpaceWeaponGeometry.OwnerIdOf(host);
            var seen = new HashSet<Thing>();
            foreach (SpaceWeaponAttachmentSlot slot in slots)
            {
                IntVec3 root = SpaceWeaponGeometry.RotatedCell(host, slot.cell);
                dependencies?.Add(root);
                if (!root.InBounds(map)) continue;
                foreach (Thing thing in map.thingGrid.ThingsListAtFast(root))
                {
                    if (thing == host || thing is not ThingWithComps attached || thing.Destroyed) continue;
                    var attachment = attached.TryGetComp<CompSpaceWeaponAttachment>();
                    if (attachment == null || attachment.AttachmentProps.external != slot.external
                        || !attachment.CompatibleWith(host.def)
                        || !allowed.NullOrEmpty() && !allowed.Contains(thing.def)) continue;
                    if (!Fits(host.def, host.Position, host.Rotation, slot, thing.def, thing.Position, thing.Rotation)) continue;
                    dependencies?.UnionWith(thing.OccupiedRect());
                    if (slot.external ? owner <= 0 || thing.Faction != host.Faction : !ownsOverlay(attachment)) continue;
                    if (owner > 0 && SpaceWeaponGeometry.OwnerIdOf(thing) != owner) continue;
                    if (seen.Add(thing)) yield return attached;
                }
            }
        }

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
                if (!slot.external && !host.OccupiedRect().Contains(cell)) continue;
                GenDraw.DrawFieldEdges(new List<IntVec3> { cell },
                    occupied.Contains(cell) ? Color.green : Color.yellow);
            }
        }
    }
}

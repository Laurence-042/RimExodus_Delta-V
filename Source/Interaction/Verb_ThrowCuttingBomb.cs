using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class Verb_ThrowCuttingBomb : Verb_LaunchProjectileStatic
    {
        public override bool CanHitTargetFrom(IntVec3 root, LocalTargetInfo target) =>
            target.IsValid && root != target.Cell && Projectile_CuttingBomb.CanCutOn(Caster?.Map)
            && base.CanHitTargetFrom(root, target);

        public override bool ValidateTarget(LocalTargetInfo target, bool showMessages = true)
        {
            if (Projectile_CuttingBomb.CanCutOn(Caster?.Map))
                return CanHitTarget(target) && base.ValidateTarget(target, showMessages);

            if (showMessages)
                Messages.Message("DeltaV_CuttingBombRequiresSpace".Translate(), MessageTypeDefOf.RejectInput);
            return false;
        }

        public override void DrawHighlight(LocalTargetInfo target)
        {
            base.DrawHighlight(target);
            if (!target.IsValid) return;

            var cells = new List<IntVec3>(Projectile_CuttingBomb.CutLength);
            foreach (IntVec3 cell in Projectile_CuttingBomb.CutCells(caster.Position, target.Cell))
            {
                if (cell.InBounds(caster.Map)) cells.Add(cell);
            }
            GenDraw.DrawFieldEdges(cells, verbProps.explosionRadiusRingColor);
        }
    }
}

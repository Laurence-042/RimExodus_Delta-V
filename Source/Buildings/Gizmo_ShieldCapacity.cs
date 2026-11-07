using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class Gizmo_ShieldRegular : Gizmo_Slider
    {
        private readonly CompSpaceShield shield;
        private readonly List<CompSpaceShield> shields = new List<CompSpaceShield>();
        public Gizmo_ShieldRegular(CompSpaceShield shield) { this.shield = shield; shields.Add(shield); }
        public override bool GroupsWith(Gizmo other) => other is Gizmo_ShieldRegular;
        public override void MergeWith(Gizmo other)
        {
            foreach (CompSpaceShield item in ((Gizmo_ShieldRegular)other).shields)
                if (!shields.Contains(item)) shields.Add(item);
        }
        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            // Deselecting during a drag can hide the MouseUp event from this bar.
            if (!Input.GetMouseButton(0)) DraggingBar = false;
            return base.GizmoOnGUI(topLeft, maxWidth, parms);
        }
        protected override float Target
        {
            get => shield.AutoRejoinPercent / 100f;
            set
            {
                // Gizmo_Slider writes Target even on idle repaint; merely selecting must not unify settings.
                if (!DraggingBar) return;
                int percent = Mathf.RoundToInt(value * 100f);
                foreach (CompSpaceShield item in shields)
                    if (item.parent?.Spawned == true && !item.parent.Destroyed)
                        item.AutoRejoinPercent = percent;
            }
        }
        private double Capacity => shields.Sum(s => (double)s.ShieldProps.regularCapacity);
        private double Stored => shields.Sum(s => s.RegularCapacity);
        protected override float ValuePercent => (float)(Stored / Capacity);
        protected override string BarLabel => Stored.ToString("0.#") + " / " + Capacity.ToString("0.#");
        protected override string Title => shields.Any(s => s.AutoRejoinPercent != shield.AutoRejoinPercent)
            ? "DeltaV_ShieldRegularBarMixed".Translate()
            : "DeltaV_ShieldRegularBar".Translate(shield.AutoRejoinPercent);
        protected override float Width => 220f;
        protected override Color BarColor => Color.white;
        protected override bool IsDraggable => true;
        protected override FloatRange DragRange => new FloatRange(.01f, 1f);
        protected override int Increments => 100;
        // Gizmos are recreated on GUI events; keep the drag alive on the component, like the ammunition bars.
        protected override bool DraggingBar { get => shield.draggingRegularBar; set => shield.draggingRegularBar = value; }
        protected override string GetTooltip() => "DeltaV_ShieldRegularBarTip".Translate();
    }

}

using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class Gizmo_ShieldRegular : Gizmo_Slider
    {
        private readonly CompSpaceShield shield;
        private bool dragging;
        public Gizmo_ShieldRegular(CompSpaceShield shield) { this.shield = shield; }
        protected override float Target
        {
            get => shield.AutoRejoinPercent / 100f;
            set => shield.AutoRejoinPercent = Mathf.RoundToInt(value * 100f);
        }
        protected override float ValuePercent => (float)(shield.RegularCapacity / shield.ShieldProps.regularCapacity);
        protected override string BarLabel => shield.RegularCapacity.ToString("0.#") + " / " + shield.ShieldProps.regularCapacity;
        protected override string Title => "DeltaV_ShieldRegularBar".Translate(shield.AutoRejoinPercent);
        protected override float Width => 220f;
        protected override Color BarColor => Color.white;
        protected override bool IsDraggable => true;
        protected override FloatRange DragRange => new FloatRange(.01f, 1f);
        protected override int Increments => 100;
        protected override bool DraggingBar { get => dragging; set => dragging = value; }
        protected override string GetTooltip() => "DeltaV_ShieldRegularBarTip".Translate();
    }

}

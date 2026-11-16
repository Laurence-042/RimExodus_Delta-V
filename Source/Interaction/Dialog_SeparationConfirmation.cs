using System;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_SeparationConfirmation : Dialog_MessageBox
    {
        private readonly UniverseManager universe;

        internal Dialog_SeparationConfirmation(UniverseManager universe, string text, Action onConfirm, Action onCancel)
            : base(text, "Confirm".Translate(), onConfirm, "GoBack".Translate(), onCancel,
                buttonADestructive: true, acceptAction: onConfirm, cancelAction: onCancel)
        {
            this.universe = universe;
        }

        public override void DoWindowContents(Rect rect)
        {
            const float spacing = 6f;
            Text.Font = GameFont.Small;
            string hint = "DeltaV_SeparationWarningReenableHint".Translate();
            float hintHeight = Text.CalcHeight(hint, rect.width);
            Rect standardContents = rect;
            standardContents.height -= ButtonHeight + hintHeight + spacing * 2;
            base.DoWindowContents(standardContents);

            var button = new Rect(rect.x, standardContents.yMax + spacing, rect.width, ButtonHeight);
            Widgets.Label(new Rect(rect.x, button.yMax + spacing, rect.width, hintHeight), hint);
            if (Widgets.ButtonText(button, "DeltaV_ConfirmAndDisableSeparationWarnings".Translate()))
            {
                universe.ConfirmSplitWarnings = false;
                buttonAAction();
                Close();
            }
        }
    }
}

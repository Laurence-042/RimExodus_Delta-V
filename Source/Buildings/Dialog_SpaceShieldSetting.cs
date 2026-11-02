using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>Live setting preview; closing without confirmation restores the instance value.</summary>
    public sealed class Dialog_SpaceShieldSetting : Window
    {
        private readonly CompSpaceShield[] shields;
        private readonly string key;
        private readonly int min;
        private readonly int max;
        private readonly int[] originals;
        private readonly Action<CompSpaceShield, int> assign;
        private int value;
        private bool confirmed;

        public override Vector2 InitialSize => new Vector2(430f, 220f);
        protected override float Margin => 12f;

        public Dialog_SpaceShieldSetting(CompSpaceShield[] shields, string key, int min, int max,
            Func<CompSpaceShield, int> current, Action<CompSpaceShield, int> assign)
        {
            this.shields = shields;
            this.key = key;
            this.min = min;
            this.max = max;
            originals = new int[shields.Length];
            for (int i = 0; i < shields.Length; i++) originals[i] = current(shields[i]);
            value = originals[0];
            this.assign = assign;
            forcePause = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 0f, rect.width, 28f), key.Translate() + ": " + value);
            int next = Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(0f, 32f, rect.width, 30f), value, min, max,
                middleAlignment: true, null, null, null, 1f));
            if (next != value)
            {
                value = next;
                for (int i = 0; i < shields.Length; i++)
                {
                    if (!shields[i].parent.Spawned || shields[i].parent.Destroyed) continue;
                    assign(shields[i], value);
                    shields[i].SettingsChanged();
                }
            }
            Widgets.Label(new Rect(0f, 76f, rect.width, 70f),
                SpaceShieldReference.Build(shields[0]));
            float buttonWidth = (rect.width - 10f) * 0.5f;
            if (Widgets.ButtonText(new Rect(0f, rect.height - 30f, buttonWidth, 30f),
                "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(buttonWidth + 10f, rect.height - 30f,
                buttonWidth, 30f), "OK".Translate()))
            {
                for (int i = 0; i < shields.Length; i++)
                {
                    if (!shields[i].parent.Spawned || shields[i].parent.Destroyed) continue;
                    assign(shields[i], value);
                    shields[i].SettingsChanged();
                }
                confirmed = true;
                Close();
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (confirmed) return;
            for (int i = 0; i < shields.Length; i++)
            {
                if (!shields[i].parent.Spawned || shields[i].parent.Destroyed) continue;
                assign(shields[i], originals[i]);
                shields[i].SettingsChanged();
            }
        }
    }

    public static class SpaceShieldReference
    {
        public static string Build(CompSpaceShield shield)
        {
            if (shield.mode == SpaceShieldMode.Guard)
                return "DeltaV_ShieldGuardReference".Translate(shield.ShieldProps.regularCapacity, shield.ShieldProps.emergencyCapacity);
            if (shield.mode == SpaceShieldMode.Kinetic)
            {
                float energy = SpaceKinetics.ShieldNormalEnergy(shield.kineticStrengthPercent);
                return "DeltaV_ShieldKineticEnergyReference".Translate(energy.ToString("F0"));
            }
            Vector2 incoming = new Vector2(0.5f, -0.8660254f);
            SpaceShieldRefraction.Plan plan = SpaceShieldRefraction.Calculate(incoming, Vector2.up,
                shield.electromagneticStrengthPercent);
            return "DeltaV_ShieldElectromagneticReference".Translate(plan.addedDeg.ToString("F1"));
        }
    }
}

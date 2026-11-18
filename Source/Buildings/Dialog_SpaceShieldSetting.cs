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
        private Vector2 descriptionScroll;

        public override Vector2 InitialSize => new Vector2(520f, 420f);
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
            string description = SpaceShieldReference.SettingDescription(shields[0], key);
            var viewport = new Rect(0f, 76f, rect.width, rect.height - 118f);
            var content = new Rect(0f, 0f, rect.width - 16f,
                Mathf.Max(viewport.height, Text.CalcHeight(description, rect.width - 16f)));
            Widgets.BeginScrollView(viewport, ref descriptionScroll, content);
            Widgets.Label(content, description);
            Widgets.EndScrollView();
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
        public static string SettingDescription(CompSpaceShield shield, string key)
        {
            switch (key)
            {
                case "DeltaV_ShieldRadius":
                    return "DeltaV_ShieldRadiusTip".Translate() + "\n\n" + Build(shield);
                case "DeltaV_ShieldProjection":
                    return "DeltaV_ShieldProjectionTip".Translate(
                        shield.ShieldProps.spaceProjectionPowerWatts.ToString("0.##") + " W")
                        + "\n\n" + PowerReference(shield);
                case "DeltaV_ShieldLength":
                    return "DeltaV_ShieldLengthTip".Translate(
                        shield.ShieldProps.spaceLengthPowerWatts.ToString("0.##") + " W")
                        + "\n\n" + PowerReference(shield);
                case "DeltaV_ShieldAngle":
                    return "DeltaV_ShieldAngleTip".Translate();
                case "DeltaV_ShieldKineticStrength":
                    var round = DefDatabase<ThingDef>.GetNamed("DeltaV_Projectile_Rapid")
                        .GetCompProperties<CompProperties_SpaceProjectile>();
                    float impulse = SpaceKinetics.ShieldImpulse(shield.kineticStrengthPercent);
                    return "DeltaV_ShieldKineticStrengthTip".Translate() + "\n\n" + Build(shield)
                        + "\n\n" + "DeltaV_ShieldKineticCostReference".Translate(
                            round.mass.ToString("0.##"), round.speed.ToString("0.##"),
                            PulseEnergy(SpaceKinetics.ShieldCost(
                                round.mass, -round.speed, impulse, round.shieldFragmentCost)));
                case "DeltaV_ShieldElectromagneticStrength":
                    var plan = SpaceShieldRefraction.Calculate(new Vector2(0.5f, -0.8660254f),
                        Vector2.up, shield.electromagneticStrengthPercent);
                    var normal = SpaceShieldRefraction.Calculate(Vector2.down, Vector2.up,
                        shield.electromagneticStrengthPercent);
                    float small = DefDatabase<ThingDef>.GetNamed("DeltaV_Projectile_Laser")
                        .GetCompProperties<CompProperties_SpaceProjectile>().shieldLaserTurnEnergy;
                    float medium = DefDatabase<ThingDef>.GetNamed("DeltaV_Projectile_LaserHeavy")
                        .GetCompProperties<CompProperties_SpaceProjectile>().shieldLaserTurnEnergy;
                    return "DeltaV_ShieldElectromagneticStrengthTip".Translate(
                        (1.8f * shield.electromagneticStrengthPercent).ToString("0.#"))
                        + "\n\n" + Build(shield) + "\n\n"
                        + "DeltaV_ShieldElectromagneticCostReference".Translate(
                            PulseEnergy(small * plan.addedDeg / 180f),
                            PulseEnergy(medium * plan.addedDeg / 180f),
                            PulseEnergy(small * normal.addedDeg / 180f),
                            PulseEnergy(medium * normal.addedDeg / 180f));
                default:
                    return Build(shield);
            }
        }

        private static string PowerReference(CompSpaceShield shield) =>
            "DeltaV_ShieldSettingPower".Translate(shield.WantedPowerWatts.ToString("0.##") + " W");

        private static string PulseEnergy(float wattDays) => wattDays.ToString("0.####") + " W·d";

        public static string Build(CompSpaceShield shield)
        {
            if (shield.mode == SpaceShieldMode.Guard)
                return "DeltaV_ShieldGuardReference".Translate(shield.ShieldProps.regularCapacity, shield.ShieldProps.emergencyCapacity);
            if (shield.mode == SpaceShieldMode.Kinetic)
            {
                float impulse = SpaceKinetics.ShieldImpulse(shield.kineticStrengthPercent);
                return "DeltaV_ShieldKineticImpulseReference".Translate(impulse.ToString("F0"));
            }
            Vector2 incoming = new Vector2(0.5f, -0.8660254f);
            SpaceShieldRefraction.Plan plan = SpaceShieldRefraction.Calculate(incoming, Vector2.up,
                shield.electromagneticStrengthPercent);
            return "DeltaV_ShieldElectromagneticReference".Translate(plan.addedDeg.ToString("F1"));
        }
    }
}

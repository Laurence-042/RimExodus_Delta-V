using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>The same live setting feeds the world preview and is restored on every cancel path.</summary>
    public sealed class Dialog_SpaceShieldSetting : Window
    {
        private readonly CompSpaceShield shield;
        private readonly string key;
        private readonly int min;
        private readonly int max;
        private readonly int original;
        private readonly Action<int> assign;
        private int value;
        private bool confirmed;
        private Vector2 scroll;

        public override Vector2 InitialSize => key == "DeltaV_ShieldStrength"
            ? new Vector2(530f, 440f) : new Vector2(390f, 170f);
        protected override float Margin => 12f;

        public Dialog_SpaceShieldSetting(CompSpaceShield shield, string key, int min, int max,
            int initial, Action<int> assign)
        {
            this.shield = shield;
            this.key = key;
            this.min = min;
            this.max = max;
            original = initial;
            value = initial;
            this.assign = assign;
            forcePause = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), key.Translate() + ": " + value);
            int next = Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(0f, 32f, inRect.width, 30f), value, min, max,
                middleAlignment: true, null, null, null, 1f));
            if (next != value)
            {
                value = next;
                assign(value);
                shield.SettingsChanged();
            }
            if (key == "DeltaV_ShieldStrength")
            {
                string reference = "DeltaV_ShieldStrengthDesc".Translate() + "\n\n"
                    + SpaceShieldReference.Build(value, true);
                Rect viewport = new Rect(0f, 72f, inRect.width, inRect.height - 116f);
                float height = Text.CalcHeight(reference, viewport.width - 20f);
                Widgets.BeginScrollView(viewport, ref scroll,
                    new Rect(0f, 0f, viewport.width - 18f, Mathf.Max(viewport.height, height)));
                Widgets.Label(new Rect(0f, 0f, viewport.width - 20f, height), reference);
                Widgets.EndScrollView();
            }
            float buttonWidth = (inRect.width - 10f) * 0.5f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - 30f, buttonWidth, 30f),
                "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(buttonWidth + 10f, inRect.height - 30f,
                buttonWidth, 30f), "OK".Translate()))
            {
                confirmed = true;
                Close();
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (confirmed) return;
            assign(original);
            shield.SettingsChanged();
        }
    }

    /// <summary>Reference rows are calculated from loaded projectile Defs and the combat formula.</summary>
    public static class SpaceShieldReference
    {
        private const float SampleIncidenceDeg = 30f;
        private static List<ThingDef> projectileDefs;

        public static string Build(int strength, bool detailed)
        {
            projectileDefs ??= CollectProjectileDefs();
            Vector2 incoming = new Vector2(Mathf.Sin(SampleIncidenceDeg * Mathf.Deg2Rad),
                -Mathf.Cos(SampleIncidenceDeg * Mathf.Deg2Rad));
            Vector2 outgoing = SpaceShieldRefraction.Refract(incoming, Vector2.up,
                strength, out _, out bool reflected);
            float change = Vector2.Angle(incoming, outgoing);
            var result = new StringBuilder();
            result.Append(detailed ? "DeltaV_ShieldReferenceIntro".Translate()
                : "DeltaV_ShieldPulseIntro".Translate());
            foreach (ThingDef def in projectileDefs)
            {
                CompProperties_SpaceProjectile props = def.GetCompProperties<CompProperties_SpaceProjectile>();
                SpaceShieldProjectileBehavior behavior = SpaceShieldProjectileBehavior.For(def);
                bool shatter = behavior.Shatters(props, SampleIncidenceDeg);
                float cost = behavior.PulseCost(props, strength, change, shatter);
                result.Append('\n').Append(def.LabelCap).Append(": ")
                    .Append(cost.ToString("F1")).Append(" W·d");
                if (!detailed) continue;
                result.Append(" · ");
                if (shatter) result.Append("DeltaV_ShieldReferenceShatter".Translate());
                else if (reflected) result.Append("DeltaV_ShieldReferenceReflect".Translate());
                else result.Append("DeltaV_ShieldReferenceDeviation".Translate(change.ToString("F1")));
            }
            return result.ToString();
        }

        private static List<ThingDef> CollectProjectileDefs()
        {
            var result = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                CompProperties_SpaceProjectile props = def.GetCompProperties<CompProperties_SpaceProjectile>();
                if (props != null && !SpaceShieldProjectileBehavior.For(def).IgnoresLineShield)
                    result.Add(def);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            return result;
        }
    }
}

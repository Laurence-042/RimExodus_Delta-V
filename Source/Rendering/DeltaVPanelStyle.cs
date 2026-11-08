using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum DeltaVButtonTone : byte
    {
        Neutral,
        Primary,
        Danger,
        Quiet
    }

    /// <summary>Shared sidebar hierarchy; interaction remains ordinary Verse UI input.</summary>
    internal static class DeltaVPanelStyle
    {
        internal static readonly Color Panel = new Color(.045f, .055f, .065f, .97f);
        internal static readonly Color Surface = new Color(.085f, .105f, .125f, .96f);
        internal static readonly Color Raised = new Color(.12f, .145f, .17f, .98f);
        internal static readonly Color Border = new Color(.32f, .39f, .44f, .78f);
        internal static readonly Color Muted = new Color(.68f, .72f, .75f, 1f);
        internal static readonly Color Accent = new Color(.16f, .43f, .55f, .98f);
        internal static readonly Color AccentBorder = new Color(.42f, .82f, .96f, 1f);

        internal static void DrawPanel(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, Panel);
            GUI.color = Border;
            Widgets.DrawBox(rect);
            GUI.color = Color.white;
        }

        internal static void DrawSurface(Rect rect, bool raised = false)
        {
            Widgets.DrawBoxSolid(rect, raised ? Raised : Surface);
        }

        internal static bool Button(Rect rect, string label, DeltaVButtonTone tone = DeltaVButtonTone.Neutral,
            bool selected = false, bool enabled = true, GameFont font = GameFont.Tiny)
        {
            bool hovered = enabled && Mouse.IsOver(rect);
            Color fill;
            Color border;
            if (selected)
            {
                fill = hovered ? new Color(.20f, .52f, .65f, 1f) : Accent;
                border = AccentBorder;
            }
            else if (tone == DeltaVButtonTone.Primary)
            {
                fill = hovered ? new Color(.18f, .38f, .46f, 1f) : new Color(.13f, .29f, .35f, .98f);
                border = new Color(.35f, .67f, .76f, .9f);
            }
            else if (tone == DeltaVButtonTone.Danger)
            {
                fill = hovered ? new Color(.43f, .17f, .16f, 1f) : new Color(.31f, .12f, .12f, .98f);
                border = new Color(.75f, .34f, .31f, .9f);
            }
            else if (tone == DeltaVButtonTone.Quiet)
            {
                fill = hovered ? new Color(.16f, .19f, .22f, .96f) : new Color(.075f, .09f, .105f, .9f);
                border = hovered ? Border : new Color(Border.r, Border.g, Border.b, .42f);
            }
            else
            {
                fill = hovered ? new Color(.18f, .215f, .25f, 1f) : Raised;
                border = Border;
            }
            if (!enabled)
            {
                fill = new Color(.075f, .085f, .095f, .82f);
                border = new Color(Border.r, Border.g, Border.b, .3f);
            }

            Widgets.DrawBoxSolid(rect, fill);
            GUI.color = border;
            Widgets.DrawBox(rect, selected ? 2 : 1);
            GUI.color = enabled ? Color.white : new Color(.48f, .5f, .52f, 1f);
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            Text.Font = font;
            Text.Anchor = TextAnchor.MiddleCenter;
            string shown = PlanetGlobeRenderer.ShortText(label, Mathf.Max(1f, rect.width - 8f));
            Widgets.Label(rect.ContractedBy(2f), shown);
            if (shown != label) TooltipHandler.TipRegion(rect, label);
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
            GUI.color = Color.white;
            return enabled && Widgets.ButtonInvisible(rect);
        }
    }
}

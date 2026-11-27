using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public static partial class PlanetGlobeRenderer
    {
        private static float SpriteScreenAngle(SpacePose pose, GlobeView view) =>
            SilhouetteScreenAngle(pose.position, SpaceTransform.SpriteMeshAngle(pose.angle), view.Right, view.Up);

        internal static Legend LayoutLegend(GlobeView view, GlobeDisplayEntry entry, bool important)
        {
            SpacePose pose = entry.Pose;
            bool behind = !view.Front(pose.position);
            Vector2 pos = view.Project(pose.position);
            if (entry.Minimal)
            {
                if (behind || !view.Rect.Contains(pos)) return null;
                Vector2 dimensions = entry.projectile != null ? entry.projectile.Size * view.PixelsPerCell : new Vector2(3f, 3f);
                // Projectile sprites share the +Z texture-forward convention with the world renderer.
                float rotation = entry.projectile != null ? SpriteScreenAngle(pose, view) : 0f;
                float radians = rotation * Mathf.Deg2Rad;
                float cos = Mathf.Abs(Mathf.Cos(radians)), sin = Mathf.Abs(Mathf.Sin(radians));
                Vector2 hitSizeMinimal = new Vector2(Mathf.Max(12f, dimensions.x * cos + dimensions.y * sin),
                    Mathf.Max(12f, dimensions.x * sin + dimensions.y * cos));
                return new Legend { entry = entry, position = pos, projection = entry.projectile != null,
                    rect = new Rect(pos - dimensions * .5f, dimensions), angle = rotation,
                    hitRect = new Rect(pos - hitSizeMinimal * .5f, hitSizeMinimal),
                    color = entry.Faction?.Color ?? Color.white };
            }
            bool projection = GlobeDisplayRules.UseProjection(view.ReferenceSide, view.PixelsPerCell,
                entry.owner.map != null && entry.cells?.Count > 0);
            float size = projection ? entry.projectionSide * view.PixelsPerCell : 18f;
            float angle = projection ? SilhouetteScreenAngle(pose.position, entry.owner.pose.angle, view.Right, view.Up) : 0f;
            float extent = projection ? SilhouetteHalfHeightPx(size, angle) : size * .5f;
            bool edge = behind || !view.Rect.ContractedBy(16f).Contains(pos);
            if (!important && projection && !behind && new Rect(pos.x - extent, pos.y - extent, extent * 2f, extent * 2f).Overlaps(view.Rect))
                edge = false;
            if (edge)
            {
                if (!important) return null;
                pos = view.Edge(pose.position);
                projection = false; size = 18f; extent = 9f;
            }
            bool focused = entry.ship != null && view.ReferenceShip?.IsValid == true
                && entry.ship.key == view.ReferenceShip.Identity.key
                && entry.ship.shuttle == (view.ReferenceShip is ShuttleShip);
            Color color = important ? new Color(1f, .85f, .25f) : focused ? new Color(1f, .95f, .4f)
                : entry.Faction == Faction.OfPlayerSilentFail ? Color.white
                : entry.Faction?.Color ?? new Color(.7f, .85f, 1f);
            float hitSize = Mathf.Max(24f, extent * 2f);
            Rect hit = new Rect(pos.x - hitSize * .5f, pos.y - hitSize * .5f, hitSize, hitSize);
            Rect labelArea = view.Rect.ContractedBy(Mathf.Min(view.Rect.width, view.Rect.height) * .1f);
            float labelWidth = Mathf.Min(180f, view.Rect.width * .78f);
            Rect label = new Rect(Mathf.Clamp(pos.x - labelWidth * .5f, labelArea.xMin,
                labelArea.xMax - labelWidth), pos.y + Mathf.Max(extent, 12f) + 3f, labelWidth, 32f);
            if (label.yMax > labelArea.yMax) label.y = pos.y - Mathf.Max(extent, 12f) - label.height - 3f;
            label.y = Mathf.Clamp(label.y, labelArea.yMin, labelArea.yMax - label.height);
            return new Legend { entry = entry, position = pos,
                rect = new Rect(pos.x - size * .5f, pos.y - size * .5f, size, size),
                hitRect = hit, labelRect = label, angle = angle, color = color,
                projection = projection, focused = focused, important = important, edge = edge, behind = behind };
        }

        internal static void Aggregate(List<Legend> legends)
        {
            // Deterministic bounded clusters: protected targets and actual hull projections
            // never disappear into a cluster. One cluster click exposes all of its members.
            for (int i = 0; i < legends.Count; i++)
            {
                Legend root = legends[i];
                if (root.entry.Minimal || root.important || root.focused || root.projection) continue;
                for (int j = legends.Count - 1; j > i; j--)
                {
                    Legend other = legends[j];
                    if (other.entry.Minimal || other.important || other.focused || other.projection
                        || !root.hitRect.ExpandedBy(4f).Overlaps(other.hitRect)) continue;
                    if (root.members.Count == 0) root.members.Add(root);
                    root.members.Add(other);
                    legends.RemoveAt(j);
                }
            }
        }

        internal static bool TryFit(GlobeView view, IReadOnlyList<GlobeDisplayEntry> targets, out float width)
        {
            width = view.MaximumWidth;
            if (targets.Count == 0 || targets.Any(e => !view.Front(e.Pose.position))) return false;
            Rect safe = view.Rect.ContractedBy(Mathf.Min(view.Rect.width, view.Rect.height) * .1f);
            float candidate = Mathf.Min(GlobeNavigation.MinimumWidth, view.MaximumWidth);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                GlobeView trial = view.WithWidth(candidate);
                bool fits = true;
                foreach (GlobeDisplayEntry entry in targets)
                {
                    Legend legend = LayoutLegend(trial, entry, true);
                    if (legend == null || legend.edge || !Contains(safe, legend.hitRect)
                        || !entry.Minimal && !Contains(safe, legend.labelRect))
                    { fits = false; break; }
                }
                if (fits) { width = trial.Width; return true; }
                if (candidate >= view.MaximumWidth) break;
                candidate = Mathf.Min(candidate * 1.25f, view.MaximumWidth);
            }
            return false;
        }

        internal static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin
            && inner.xMax <= outer.xMax && inner.yMin >= outer.yMin && inner.yMax <= outer.yMax;

        internal static void FitTarget(UniverseManager universe, SpaceObject context, Rect mapRect, GlobeTarget key)
        {
            var state = universe.GlobeNavigation;
            state.Refresh(universe);
            var target = state.Resolve(key);
            if (target == null) { Reject("DeltaV_NavUnavailable"); return; }
            if (!state.IsDisplayed(target)) { Reject("DeltaV_NavFilteredTarget"); return; }
            state.Select(target);
            state.Recenter();
            GlobeView view = GlobeView.Create(mapRect, universe, context);
            var fitting = new List<GlobeDisplayEntry> { target };
            var own = state.Entries.FirstOrDefault(e => view.ReferenceShip?.IsValid == true && e.ship != null
                && e.ship.key == view.ReferenceShip.Identity.key && e.ship.shuttle == (view.ReferenceShip is ShuttleShip))
                ?? state.Entries.FirstOrDefault(e => e.owner == context && e.ship == null);
            if (own != null && own != target) fitting.Add(own);
            bool fit = TryFit(view, fitting, out float width);
            state.Width = width;
            if (!fit) Reject(view.Front(target.Pose.position) ? "DeltaV_NavFitLimit" : "DeltaV_NavBehind");
        }

        private static void Reject(string key) => Messages.Message(key.Translate(), MessageTypeDefOf.RejectInput, false);

        private static string Distance(GlobeView view, GlobeDisplayEntry entry) =>
            "DeltaV_PanelGlobeDistance".Translate(OrbitSphere.ArcCells(view.Reference, entry.Pose.position).ToString("N0"));

        private static Rect Clip(Rect rect, Rect bounds) => Rect.MinMaxRect(Mathf.Max(rect.xMin, bounds.xMin),
            Mathf.Max(rect.yMin, bounds.yMin), Mathf.Min(rect.xMax, bounds.xMax), Mathf.Min(rect.yMax, bounds.yMax));

        internal static string ShortText(string text, float width)
        {
            text = text ?? "";
            if (Text.CalcSize(text).x <= width) return text;
            int low = 0, high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (Text.CalcSize(text.Substring(0, mid) + "…").x <= width) low = mid; else high = mid - 1;
            }
            return text.Substring(0, low) + "…";
        }

        // Rotate vertices in local GUI space before applying window/group offsets,
        // as Widgets.DrawLine does. Do not reconstruct a screen pivot via GUIUtility:
        // Waylith's GUIToScreenPoint and texture drawing apply group/scale differently.
        internal static Matrix4x4 LocalTextureRotation(Matrix4x4 matrix, Vector2 pivot, float angle)
        {
            float radians = angle * Mathf.Deg2Rad, c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            Matrix4x4 rotation = Matrix4x4.identity;
            rotation.m00 = c; rotation.m01 = -s; rotation.m10 = s; rotation.m11 = c;
            rotation.m03 = pivot.x - c * pivot.x + s * pivot.y;
            rotation.m13 = pivot.y - s * pivot.x - c * pivot.y;
            return matrix * rotation;
        }

        private static void DrawLegendTexture(Rect rect, Texture texture, float angle)
        {
            if (Event.current.type != EventType.Repaint) return;
            GL.PushMatrix();
            try
            {
                GL.MultMatrix(LocalTextureRotation(Matrix4x4.identity, rect.center, angle));
                GUI.DrawTexture(rect, texture);
            }
            finally { GL.PopMatrix(); }
        }

        internal static Legend PickMarker(IReadOnlyList<Legend> legends, Vector2 point)
        {
            Legend nearest = null;
            float bestDistance = float.PositiveInfinity;
            foreach (Legend legend in legends)
            {
                if (!legend.hitRect.Contains(point)) continue;
                float distance = (legend.position - point).sqrMagnitude;
                if (distance >= bestDistance) continue;
                nearest = legend;
                bestDistance = distance;
            }
            return nearest;
        }

        internal static bool TryPlaceLabel(Rect bounds, Legend legend, List<Rect> occupied, out Rect label)
        {
            label = legend.labelRect;
            if (LabelFits(bounds, label, occupied)) return true;

            label.y = Mathf.Clamp(legend.hitRect.yMin - label.height - 3f,
                bounds.yMin, bounds.yMax - label.height);
            if (LabelFits(bounds, label, occupied)) return true;
            if (!legend.important) return false;

            for (label.y = bounds.yMin; label.yMax <= bounds.yMax; label.y += label.height + 1f)
                if (LabelFits(bounds, label, occupied)) return true;
            return false;
        }

        private static bool LabelFits(Rect bounds, Rect label, List<Rect> occupied) =>
            Contains(bounds, label) && !occupied.Any(rect => rect.Overlaps(label));

        private static void DrawLegends(GlobeView view, GlobeNavigation state, UniverseManager universe,
            List<Rect> occupied, Vector2? click)
        {
            Text.Font = GameFont.Tiny;
            var ordered = Legends.OrderByDescending(l => state.IsSelected(l.entry))
                .ThenByDescending(l => state.IsCourse(l.entry)).ThenByDescending(l => l.important)
                .ThenByDescending(l => l.focused).ToList();
            Legend picked = click.HasValue ? PickMarker(ordered, click.Value) : null;
            // Reserve every visible marker before placing even the highest priority label.
            foreach (Legend legend in ordered) occupied.Add(Clip(legend.hitRect, view.Rect));
            Rect labelBounds = view.Rect.ContractedBy(4f);
            foreach (Legend legend in ordered)
            {
                var entry = legend.entry;
                if (entry.Minimal)
                {
                    if (entry.projectile?.Texture is Texture sprite)
                    {
                        GUI.BeginGroup(view.Rect);
                        Rect local = legend.rect; local.position -= view.Rect.position;
                        GUI.color = Color.white;
                        DrawLegendTexture(local, sprite, legend.angle);
                        GUI.EndGroup();
                    }
                    // No labels, target frames, direction arrows or clusters on transient detail.
                    Rect minimalHit = Clip(legend.hitRect, view.Rect);
                    TooltipHandler.TipRegion(minimalHit, entry.Label + "\n" + Distance(view, entry));
                    continue;
                }
                if (!legend.edge && legend.members.Count == 0)
                {
                    Texture2D texture = legend.projection ? GetSilhouette(entry)
                        : entry.native && entry.owner is PoiState native ? native.nativeParent?.ExpandingIcon : null;
                    if (texture != null)
                    {
                        GUI.BeginGroup(view.Rect);
                        Rect local = legend.rect; local.position -= view.Rect.position;
                        GUI.color = !legend.projection && entry.native && entry.owner is PoiState nativePoi
                            ? nativePoi.nativeParent.ExpandingIconColor : legend.color;
                        DrawLegendTexture(local, texture, legend.angle);
                        GUI.color = Color.white;
                        GUI.EndGroup();
                    }
                }
                Rect hit = Clip(legend.hitRect, view.Rect);
                if (legend.important)
                {
                    GUI.color = state.IsSelected(entry) ? new Color(1f, .85f, .25f) : new Color(.4f, .9f, 1f);
                    Widgets.DrawBox(hit, state.IsSelected(entry) ? 2 : 1);
                    GUI.color = Color.white;
                }
                if (legend.members.Count > 0)
                {
                    Widgets.DrawBoxSolid(hit, new Color(.12f, .2f, .26f));
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(hit, legend.members.Count.ToString());
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                string tip = legend.members.Count > 0 ? string.Join("\n", legend.members.Select(m => m.entry.Label))
                    : entry.Label + "\n" + Distance(view, entry)
                        + (legend.behind ? "\n" + "DeltaV_NavBehind".Translate() : "")
                        + (entry.owner.InteriorMap == null ? "\n" + "DeltaV_PanelObjectNotLoaded".Translate() : "");
                TooltipHandler.TipRegion(hit, tip);
                bool labelVisible = legend.important || entry.station
                    || GlobeDisplayRules.ShowLabel(entry.area, view.PixelsPerCell, entry.wreck);
                if (!labelVisible || legend.members.Count > 0) continue;
                if (!TryPlaceLabel(labelBounds, legend, occupied, out Rect label)) continue;
                occupied.Add(label);
                Vector2 attachment = new Vector2(Mathf.Clamp(legend.position.x, label.xMin, label.xMax),
                    Mathf.Clamp(legend.position.y, label.yMin, label.yMax));
                Vector2 lineStart = new Vector2(Mathf.Clamp(legend.position.x, view.Rect.xMin + 2f, view.Rect.xMax - 2f),
                    Mathf.Clamp(legend.position.y, view.Rect.yMin + 2f, view.Rect.yMax - 2f));
                Widgets.DrawLine(lineStart, attachment,
                    new Color(legend.color.r, legend.color.g, legend.color.b, .9f), 2f);
                Widgets.DrawBoxSolid(label, new Color(.12f, .155f, .185f, .97f));
                GUI.color = new Color(legend.color.r, legend.color.g, legend.color.b, .78f);
                Widgets.DrawBox(label);
                GUI.color = Color.white;
                string prefix = legend.behind ? "DeltaV_NavBehindShort".Translate().ToString() + " · " : "";
                Rect labelText = new Rect(label.x + 4f, label.y + 1f, label.width - 8f, label.height - 2f);
                Widgets.Label(labelText, ShortText(prefix + entry.Label, labelText.width) + "\n" + Distance(view, entry));
                TooltipHandler.TipRegion(label, tip);
                if (picked == null && click.HasValue && label.Contains(click.Value)) picked = legend;
            }
            Text.Font = GameFont.Small;
            if (picked == null) return;
            if (picked.members.Count == 0) { state.Select(picked.entry); return; }

            var options = new List<FloatMenuOption>();
            foreach (var member in picked.members)
            {
                GlobeTarget key = GlobeTarget.Of(member.entry);
                options.Add(new FloatMenuOption(member.entry.Label + " · " + Distance(view, member.entry), () =>
                { state.Refresh(universe); state.Select(state.Resolve(key)); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static Rect ScaleRect(Rect map, bool expanded) => expanded
            ? new Rect(map.x + 6f, map.yMax - 20f, map.width - 12f, 18f)
            : GlobeNavigationPanel.CompactScaleRect(map);

        private static void DrawScale(GlobeView view, bool expanded)
        {
            // There is no safe middle strip once the compact globe has collapsed this far.
            // Hiding the auxiliary scale is preferable to covering either navigation row.
            if (!expanded && view.Rect.height < 88f) return;

            Text.Font = GameFont.Tiny;
            Rect text = ScaleRect(view.Rect, expanded);
            Widgets.DrawBoxSolid(text, new Color(.04f, .06f, .08f, .85f));
            if (view.Whole) Widgets.Label(text, "DeltaV_NavWhole".Translate());
            else
            {
                float cells = CoordinateGridStepCells(view);
                float length = cells * view.PixelsPerCell;
                Widgets.DrawLineHorizontal(text.x, text.y + 14f, length);
                Widgets.Label(new Rect(text.x + length + 5f, text.y, text.width - length - 5f, 18f),
                    "DeltaV_PanelGlobeDistance".Translate(cells.ToString("N0")));
                TooltipHandler.TipRegion(text, "DeltaV_NavScaleTip".Translate());
            }
            Text.Font = GameFont.Small;
        }
    }
}

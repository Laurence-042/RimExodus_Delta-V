using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed class Dialog_GlobeFilters : Window
    {
        private readonly UniverseManager universe;
        internal GlobeFilters Draft;
        private Vector2 scroll;
        private float contentHeight = 900f;
        internal Dialog_GlobeFilters(UniverseManager universe)
        {
            this.universe = universe;
            Draft = universe.GlobeFilters.Copy();
            optionalTitle = "DeltaV_NavFilters".Translate();
            doCloseX = true; absorbInputAroundWindow = true; closeOnAccept = false;
        }
        public override Vector2 InitialSize => new Vector2(Mathf.Min(760f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 50f));
        public override void DoWindowContents(Rect rect)
        {
            if (Current.Game?.GetComponent<UniverseManager>() != universe) { Close(); return; }
            Text.Font = GameFont.Small;
            float hintHeight = Text.CalcHeight("DeltaV_NavFiltersTip".Translate(), rect.width);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, hintHeight), "DeltaV_NavFiltersTip".Translate());
            Rect outer = new Rect(rect.x, rect.y + hintHeight + 8f, rect.width, rect.height - hintHeight - 56f);
            Rect content = new Rect(0, 0, outer.width - 18f, contentHeight);
            Widgets.BeginScrollView(outer, ref scroll, content);
            float width = (content.width - 24f) * .5f;
            float left = 0f, right = 0f, x = width + 24f;
            Header(0, ref left, width, "DeltaV_NavFilterTypes");
            Group(0, ref left, width, "DeltaV_NavTypeShips".Translate(), new[] { "FoundationShip", "IntegratedShip" }, Draft.hiddenTypes);
            TypeRow(16, ref left, width - 16, "FoundationShip");
            TypeRow(16, ref left, width - 16, "IntegratedShip");
            foreach (string type in new[] { "Wreck", "Natural", "Projectile", "Scattered", "Other" }) TypeRow(0, ref left, width, type);

            Header(x, ref right, width, "DeltaV_NavFilterDisplay");
            Check(x, ref right, width, "DeltaV_NavCoordinateGrid".Translate(), ref Draft.showCoordinateGrid);
            Header(x, ref right, width, "DeltaV_NavFilterArmament");
            Draft.armament = (GlobeArmament)Flags(x, ref right, width, typeof(GlobeArmament), (int)Draft.armament);
            Header(x, ref right, width, "DeltaV_NavFilterRelations");
            Draft.relations = (GlobeRelation)Flags(x, ref right, width, typeof(GlobeRelation), (int)Draft.relations);
            Header(x, ref right, width, "DeltaV_NavFilterMissions");
            Draft.missions = (GlobeMission)Flags(x, ref right, width, typeof(GlobeMission), (int)Draft.missions);
            right += 8f;
            Check(x, ref right, width, "DeltaV_NavBookmarksOnly".Translate(), ref Draft.bookmarksOnly);
            Check(x, ref right, width, "DeltaV_NavKeepTracked".Translate(), ref Draft.keepTracked);
            contentHeight = Mathf.Max(left, right) + 8f;
            Widgets.EndScrollView();
            float button = (rect.width - 18f) / 4f;
            float y = rect.yMax - 34f;
            if (Widgets.ButtonText(new Rect(rect.x, y, button, 30f), "DeltaV_NavFilterDefault".Translate())) Draft = new GlobeFilters();
            if (Widgets.ButtonText(new Rect(rect.x + button + 6f, y, button, 30f), "DeltaV_NavFilterEverything".Translate())) Draft = GlobeFilters.Everything();
            if (Widgets.ButtonText(new Rect(rect.x + 2f * (button + 6f), y, button, 30f), "Cancel".Translate())) Close();
            if (Widgets.ButtonText(new Rect(rect.x + 3f * (button + 6f), y, button, 30f), "DeltaV_NavFilterApply".Translate()))
            { universe.GlobeNavigation.ApplyFilters(universe, Draft); Close(); }
        }

        private void TypeRow(float x, ref float y, float width, string id) =>
            Group(x, ref y, width, ("DeltaV_NavType" + id).Translate(), new[] { id }, Draft.hiddenTypes);
        private static void Header(float x, ref float y, float width, string key)
        {
            y += 8f;
            Widgets.Label(new Rect(x, y, width, 26f), key.Translate());
            y += 28f;
        }
        private static int Flags(float x, ref float y, float width, Type type, int mask)
        {
            foreach (object value in Enum.GetValues(type))
            {
                string name = value.ToString();
                if (name == "All") continue;
                int flag = Convert.ToInt32(value);
                bool enabled = (mask & flag) != 0;
                Check(x, ref y, width, ("DeltaV_Nav" + type.Name + name).Translate(), ref enabled);
                mask = enabled ? mask | flag : mask & ~flag;
            }
            return mask;
        }
        private static void Check(float x, ref float y, float width, string label, ref bool value)
        {
            float height = Mathf.Max(26f, Text.CalcHeight(label, width - 28f));
            Widgets.CheckboxLabeled(new Rect(x, y, width, height), label, ref value);
            y += height + 2f;
        }
        private static void Group(float x, ref float y, float width, string label, string[] ids, List<string> hidden)
        {
            int count = ids.Count(id => !hidden.Contains(id));
            bool enabled = count == ids.Length, previous = enabled;
            float top = y;
            Check(x, ref y, width, label, ref enabled);
            if (enabled != previous)
                foreach (string id in ids) { hidden.Remove(id); if (!enabled) hidden.Add(id); }
            else if (count > 0 && count < ids.Length)
                Widgets.DrawBoxSolid(new Rect(x + width - 19f, top + (y - top - 2f) * .5f - 2f, 14f, 4f), Color.yellow);
        }
    }
}

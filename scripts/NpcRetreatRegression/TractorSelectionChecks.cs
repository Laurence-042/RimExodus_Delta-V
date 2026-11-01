using System.Collections.Generic;
using System.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static Selector tractorSelector;
    static ScreenshotModeHandler tractorScreenshot = new();
    static Window tractorWindow;
    static bool TractorSelector(ref Selector __result) { __result = tractorSelector; return false; }
    static bool TractorScreenshot(ref ScreenshotModeHandler __result) { __result = tractorScreenshot; return false; }
    static bool TractorTexture(ref Texture2D __result) { __result = null; return false; }
    static bool TractorWindowStack(ref WindowStack __result) { __result = (WindowStack)Raw(typeof(WindowStack)); return false; }
    static bool TractorOpenWindow(Window window) { tractorWindow = window; return false; }
    static bool TractorCommandSound() => false;

    static void CheckTractorSelection(CompSpaceTractor first, CompSpaceTractor second, ThingDef item, Faction enemy)
    {
        var patch = new Harmony("DeltaV.Regression.TractorSelection");
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "Selector"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorSelector)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "ScreenshotModeHandler"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorScreenshot)));
        patch.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorWindowStack)));
        patch.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorOpenWindow)));
        patch.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), "Get"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorTexture)));
        patch.Patch(AccessTools.Method(typeof(Command), "ProcessInput"), prefix: new HarmonyMethod(typeof(Program), nameof(TractorCommandSound)));
        patch.CreateClassProcessor(typeof(Patch_TractorInspectTabs)).Patch();
        tractorSelector = (Selector)Raw(typeof(Selector));
        Set(tractorSelector, "selected", new List<object> { first.parent, second.parent });
        try
        {
            var pane = (MainTabWindow_Inspect)Raw(typeof(MainTabWindow_Inspect));
            var tab = pane.CurTabs.OfType<ITab_TractorCargo>().Single();
            Check(tab.IsVisible, "real inspect pane exposes cargo tab for two non-storage tractors");
            tractorScreenshot.Active = true;
            Check(pane.CurTabs == null, "tractor tab respects screenshot mode");
            tractorScreenshot.Active = false;
            second.GetStoreSettings().filter.SetAllow(item, false);
            var batch = new TractorCargoSelection(new[] { first, second });
            Check(!second.GetStoreSettings().filter.Allows(item), "opening batch filter preserves differing cargo settings");
            batch.GetStoreSettings().filter.AllowedHitPointsPercents = new FloatRange(0.4f, 0.9f);
            Check(first.GetStoreSettings().filter.Allows(item) && second.GetStoreSettings().filter.Allows(item)
                && second.GetStoreSettings().filter.AllowedHitPointsPercents.min == 0.4f,
                "native durability filter edit synchronizes the full cargo filter across selected ships");
            batch.GetStoreSettings().filter.SetAllow(item, false);
            Check(!first.GetStoreSettings().filter.Allows(item) && !second.GetStoreSettings().filter.Allows(item),
                "native item checkbox applies to both selected tractors");

            Gizmo Group(string key, ThingComp a, ThingComp b)
            {
                Gizmo FindCommand(ThingComp comp) => comp.CompGetGizmosExtra().First(g =>
                    AccessTools.Field(g.GetType(), "setting")?.GetValue(g) as string == key);
                var command = FindCommand(a); var other = FindCommand(b);
                Check(command.GroupsWith(other) && !command.InheritInteractionsFrom(other), key + " merges into one batch command");
                return command;
            }
            first.collectLiving = false; second.collectLiving = true;
            Group("TractorLiving", first, second).ProcessInput(null);
            Check(first.collectLiving && second.collectLiving, "mixed living toggles enable the whole selection once");
            Group("TractorLiving", first, second).ProcessInput(null);
            Check(!first.collectLiving && !second.collectLiving, "living toggle disables the whole selection once");
            first.pawnMask = CompSpaceTractor.DefaultPawnMask;
            second.pawnMask = first.pawnMask | 1;
            Group("TractorPawnFilter", first, second).ProcessInput(null);
            var living = (Dialog_TractorPawnFilter)tractorWindow;
            Check(living.State(1) == MultiCheckboxState.Partial, "pawn filter shows differing faction/type settings as mixed");
            living.SetAllowed(1, true);
            Check((first.pawnMask & 1) != 0 && (second.pawnMask & 1) != 0, "one pawn matrix edit updates both selected tractors");

            var a = first.parent.TryGetComp<CompSpaceWeapon>(); var b = second.parent.TryGetComp<CompSpaceWeapon>();
            a.aimMode = SpaceWeaponAimMode.Volley;
            Group("TractorAuto", a, b).ProcessInput(null);
            Check(a.aimMode == SpaceWeaponAimMode.PointDefense && b.aimMode == SpaceWeaponAimMode.PointDefense,
                "mixed auto-mode toggle sets both tractors to automatic");
            Group("WeaponCoverage", a, b).ProcessInput(null);
            var coverage = (Dialog_WeaponCoverage)tractorWindow;
            var draft = (CombatWeaponCoverage)AccessTools.Field(typeof(Dialog_WeaponCoverage), "draft").GetValue(coverage);
            draft.range = 23; draft.fixedDirection = true; coverage.Apply();
            Check(a.coverage.range == 23 && b.coverage.range == 23 && a.coverage.fixedDirection && b.coverage.fixedDirection
                && !ReferenceEquals(a.coverage, b.coverage), "one coverage dialog applies independent settings to both weapons");
            Set(typeof(Thing), second.parent, "factionInt", enemy);
            Check(ITab_TractorCargo.SelectedTractors(tractorSelector.SelectedObjects).SequenceEqual(new[] { first }),
                "mixed selection excludes non-player tractors");
            living.SetAllowed(1, false);
            batch.GetStoreSettings().filter.SetAllow(item, true);
            Check((second.pawnMask & 1) != 0 && !second.GetStoreSettings().filter.Allows(item),
                "captured device is excluded from already open cargo and pawn edits");
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            Set(typeof(Thing), second.parent, "factionInt", first.parent.Faction);
            foreach (var tractor in new[] { first, second })
            {
                tractor.collectLiving = true; tractor.pawnMask = CompSpaceTractor.DefaultPawnMask;
                tractor.GetStoreSettings().filter.SetAllow(item, true);
                tractor.GetStoreSettings().filter.AllowedHitPointsPercents = new FloatRange(0, 1);
                tractor.parent.TryGetComp<CompSpaceWeapon>().coverage = new CombatWeaponCoverage();
            }
            tractorSelector = null; tractorWindow = null;
        }
    }
}

using System.Collections.Generic;
using DeltaV;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static Vector2 liveAimMouse;
    static Vector2 staleAimMouse;
    static WindowStack aimWindows;
    static ColonistBar aimColonists;

    // Substitute native mouse acquisition; retain vanilla inversion and the production UI gates.
    static bool ReadLiveAimMouse(ref Vector2 __result) { __result = liveAimMouse; return false; }
    static bool ReadStaleAimMouse(ref Vector2 __result) { __result = staleAimMouse; return false; }
    static bool ReadAimWindows(ref WindowStack __result) { __result = aimWindows; return false; }
    static bool ReadAimColonists(ref ColonistBar __result) { __result = aimColonists; return false; }
    static bool EmptyAimColonists(ref bool __result) { __result = false; return false; }

    static void CheckAimUiCoordinates()
    {
        var harness = new Harmony("DeltaV.AimUiCoordinateRegression");
        int oldHeight = UI.screenHeight;
        sbyte oldMapIndex = Current.Game.currentMapIndex;
        Current.Game.currentMapIndex = -1;
        aimWindows = (WindowStack)Raw(typeof(WindowStack));
        var windows = new List<Window>();
        Set(aimWindows, "windows", windows);
        aimColonists = (ColonistBar)Raw(typeof(ColonistBar));
        AimUiRegions.Clear();
        try
        {
            harness.Patch(AccessTools.PropertyGetter(typeof(UI), nameof(UI.MousePositionOnUI)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReadLiveAimMouse)));
            harness.Patch(AccessTools.PropertyGetter(typeof(UI), nameof(UI.MousePosUIInvertedUseEventIfCan)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReadStaleAimMouse)));
            harness.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReadAimWindows)));
            harness.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.ColonistBar)),
                prefix: new HarmonyMethod(typeof(Program), nameof(ReadAimColonists)));
            harness.Patch(AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.AnyColonistOrCorpseAt)),
                prefix: new HarmonyMethod(typeof(Program), nameof(EmptyAimColonists)));

            UI.screenHeight = 1080;
            liveAimMouse = new Vector2(1279.5f, 1080f - 882f);
            staleAimMouse = new Vector2(2559f, 1764f);
            Check(!SpaceAimController.MouseOverUiWindow(),
                "closed log: doubled stale GUI coordinates cannot classify a live map cursor as bottom bar");

            var log = (EditWindow_Log)Raw(typeof(EditWindow_Log));
            log.windowRect = new Rect(1500f, 700f, 400f, 250f);
            windows.Add(log);
            liveAimMouse = new Vector2(876f, 1080f - 433.5f);
            staleAimMouse = new Vector2(1631f, 819f);
            var buttons = new AimButtonLatch();
            buttons.Press(0);
            buttons.Validate(!SpaceAimController.MouseOverUiWindow(), true, false);
            Check(buttons.Main, "open log: real map cursor retains firing despite stale coordinates over log");

            liveAimMouse = new Vector2(1631f, 1080f - 819f);
            staleAimMouse = new Vector2(876f, 433.5f);
            buttons.Validate(!SpaceAimController.MouseOverUiWindow(), true, false);
            Check(!buttons.Main, "actual mouse entry into log window still cancels firing");
            liveAimMouse = new Vector2(876f, 1080f - 433.5f);
            buttons.Validate(!SpaceAimController.MouseOverUiWindow(), true, false);
            Check(!buttons.Main, "leaving a real UI obstruction does not re-arm a held button");
            buttons.Press(0);
            buttons.Validate(!SpaceAimController.MouseOverUiWindow(), true, false);
            Check(buttons.Main, "fresh map press re-arms after leaving log");

            windows.Clear();
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                UI.screenHeight = (int)(2160f / scale);
                liveAimMouse = new Vector2(1400f / scale, 1200f / scale);
                staleAimMouse = new Vector2(2800f, 2000f);
                Check(!SpaceAimController.MouseOverUiWindow(), $"map cursor stays clear at UI scale {scale}");
                liveAimMouse = new Vector2(1400f / scale, 10f);
                Check(SpaceAimController.MouseOverUiWindow(), $"real bottom bar blocks at UI scale {scale}");
            }
            liveAimMouse = new Vector2(600f, 500f);
            log.absorbInputAroundWindow = true;
            windows.Add(log);
            Check(SpaceAimController.MouseOverUiWindow(), "modal input blocking remains effective away from window");
            windows.Clear();
            var navigation = new Window_SectorNavigation(Current.Game.GetComponent<UniverseManager>());
            navigation.windowRect = new Rect(100, 100, 900, 700);
            windows.Add(navigation);
            Check(navigation.absorbInputAroundWindow && navigation.preventCameraMotion && !navigation.forcePause,
                "expanded sector map blocks map input and camera motion without changing pause state");
            Check(SpaceAimController.MouseOverUiWindow(), "expanded sector map cancels held fire even outside its content");
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            UI.screenHeight = oldHeight;
            Current.Game.currentMapIndex = oldMapIndex;
            aimWindows = null;
            aimColonists = null;
            AimUiRegions.Clear();
        }
    }
}

using Verse;
using UnityEngine;
using RimWorld.Planet;

namespace DeltaV
{
    // Shared sampling boundary for keyboard and aim input; simulated state still advances per tick.
    internal struct PlayerInputStamp
    {
        private bool valid, focused;
        private int frame, revision;
        private Map map;
        internal bool BeginSample(int nextFrame, Map nextMap, int nextRevision, bool nextFocus)
        {
            if (valid && frame == nextFrame && map == nextMap
                && revision == nextRevision && focused == nextFocus) return false;
            valid = true; frame = nextFrame; map = nextMap; revision = nextRevision; focused = nextFocus;
            return true;
        }
        internal void Clear() { valid = false; map = null; }
    }

    // One raw snapshot for all player controllers; GUI authorization has its own invalidation.
    internal sealed class PlayerFrameInput
    {
        private readonly UniverseManager universe;
        private PlayerInputStamp rawStamp, gateStamp;
        private bool mapAllowed;
        internal Vector2 LocalMouse { get; private set; }
        internal bool MainHeld { get; private set; }
        internal bool SecondaryHeld { get; private set; }
        internal bool BrakeHeld { get; private set; }
        internal PilotService.PilotInput Drive { get; private set; }
        internal PlayerFrameInput(UniverseManager universe) { this.universe = universe; }
        internal void Sample(int frame)
        {
            bool focused = Application.isFocused;
            Map map = Find.CurrentMap;
            if (!rawStamp.BeginSample(frame, map, 0, focused)) return;
            long start = universe.Performance.Start();
            Drive = focused ? PilotService.SampleInputNow() : default;
            BrakeHeld = focused && DeltaVDefOf.DeltaV_AssistBrake != null && DeltaVDefOf.DeltaV_AssistBrake.IsDown;
            MainHeld = focused && Input.GetMouseButton(0);
            SecondaryHeld = focused && Input.GetMouseButton(1);
            Vector3 mouse = map != null ? UI.MouseMapPosition() : default;
            LocalMouse = new Vector2(mouse.x, mouse.z);
            universe.Performance.Record(PerformanceStage.AimInput, start);
        }
        internal bool MapAllowed(int frame)
        {
            Sample(frame);
            bool focused = Application.isFocused;
            if (gateStamp.BeginSample(frame, Find.CurrentMap, AimUiRegions.Revision, focused))
            {
                long start = universe.Performance.Start();
                mapAllowed = focused && Find.CurrentMap != null && WorldRendererUtility.DrawingMap
                    && !SpaceAimController.MouseOverUiWindow() && !Find.Targeter.IsTargeting
                    && Find.DesignatorManager.SelectedDesignator == null;
                universe.Performance.Record(PerformanceStage.AimInputGate, start);
            }
            return mapAllowed;
        }
        internal void InvalidateGate() { gateStamp.Clear(); }
        internal void Clear() { rawStamp.Clear(); gateStamp.Clear(); Drive = default; MainHeld = SecondaryHeld = BrakeHeld = false; }
    }

}

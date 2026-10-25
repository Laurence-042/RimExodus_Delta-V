using System.Diagnostics;
using System.Text;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum PerformanceStage
    {
        Grid, Dust, Exhaust, Shells, Foreground, Update, Tick,
        Npc, SoftDock, Projectiles, Autopilot, AutopilotSolve, Weapons, Controls,
        ControlSelection, ControlDecision, ControlCombat, PointDefense, Objects,
        PointDefensePrune, PointDefenseSearch, ObjectDrive, ObjectDrift, ObjectArchive,
        ControlVolley, VolleyAim, DriveAuthorize, DriveAllocate,
        Collision, MotionFeedback, Lifecycle, Count
    }

    /// <summary>Opt-in, per-game CPU timing and frame/tick cadence; no saved or static state.</summary>
    internal sealed class DeltaVPerformanceProbe
    {
        private const float ReportSeconds = 5f;
        private readonly long[] totals = new long[(int)PerformanceStage.Count];
        private readonly long[] peaks = new long[(int)PerformanceStage.Count];
        private int frames;
        private int ticks;
        private int dustQuads;
        private int peakDustQuads;
        private int peakObjects;
        private int peakProjectiles;
        private int peakThrustingObjects;
        private int peakDriftingObjects;
        private int peakArchivedObjects;
        private int autopilotSolves;
        private int pointDefenseInterceptionTicks;
        private int pointDefenseScatterTicks;
        private int firstStructureVersion = -1;
        private int lastStructureVersion;
        private TimeSpeed observedSpeed;
        private bool hasSpeed;
        private bool mixedSpeed;
        private float frameSeconds;
        private float worstFrameSeconds;
        private float windowStart = -1f;

        internal bool Enabled => DeltaVLog.Enabled(DeltaVLogModule.Performance);

        internal long Start() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        internal void Record(PerformanceStage stage, long start)
        {
            if (start == 0L) return;
            RecordElapsed(stage, Stopwatch.GetTimestamp() - start);
        }

        internal long Elapsed(long start) => start == 0L ? 0L : Stopwatch.GetTimestamp() - start;

        internal void RecordElapsed(PerformanceStage stage, long elapsed)
        {
            if (elapsed == 0L) return;
            int index = (int)stage;
            totals[index] += elapsed;
            if (elapsed > peaks[index]) peaks[index] = elapsed;
        }

        internal void TickFinished(long start, int objectCount, int projectileCount, int structureVersion,
            int thrustingObjects, int driftingObjects, int archivedObjects)
        {
            if (start == 0L) return;
            Record(PerformanceStage.Tick, start);
            ticks++;
            if (objectCount > peakObjects) peakObjects = objectCount;
            if (projectileCount > peakProjectiles) peakProjectiles = projectileCount;
            if (thrustingObjects > peakThrustingObjects) peakThrustingObjects = thrustingObjects;
            if (driftingObjects > peakDriftingObjects) peakDriftingObjects = driftingObjects;
            if (archivedObjects > peakArchivedObjects) peakArchivedObjects = archivedObjects;
            if (firstStructureVersion < 0) firstStructureVersion = structureVersion;
            lastStructureVersion = structureVersion;
        }

        internal void RecordDustQuads(int count)
        {
            if (!Enabled) return;
            dustQuads += count;
            if (count > peakDustQuads) peakDustQuads = count;
        }

        internal void RecordAutopilotSolve(long start)
        {
            if (start == 0L) return;
            Record(PerformanceStage.AutopilotSolve, start);
            autopilotSolves++;
        }

        internal void RecordPointDefenseTargets(bool hasInterceptionTargets, bool hasScatterTargets)
        {
            if (!Enabled) return;
            if (hasInterceptionTargets) pointDefenseInterceptionTicks++;
            if (hasScatterTargets) pointDefenseScatterTicks++;
        }

        internal void FrameFinished(long start)
        {
            if (start == 0L)
            {
                if (windowStart >= 0f) Reset();
                return;
            }
            Record(PerformanceStage.Update, start);
            float now = Time.realtimeSinceStartup;
            if (windowStart < 0f)
            {
                Reset();
                windowStart = now;
                return;
            }
            float seconds = Time.unscaledDeltaTime;
            TimeSpeed speed = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Paused;
            if (!hasSpeed) { observedSpeed = speed; hasSpeed = true; }
            else if (observedSpeed != speed) mixedSpeed = true;
            frames++;
            frameSeconds += seconds;
            if (seconds > worstFrameSeconds) worstFrameSeconds = seconds;
            float span = now - windowStart;
            if (span < ReportSeconds) return;
            var sb = new StringBuilder(320);
            sb.Append("window=").Append(span.ToString("F1")).Append("s frames=").Append(frames)
                .Append(" fps=").Append((frames / span).ToString("F1"))
                .Append(" frameAvg=").Append((frameSeconds * 1000f / frames).ToString("F1"))
                .Append("ms frameMax=").Append((worstFrameSeconds * 1000f).ToString("F1"))
                .Append("ms ticks=").Append(ticks)
                .Append(" tps=").Append((ticks / span).ToString("F1"))
                .Append(" speed=").Append(mixedSpeed ? "Mixed" : observedSpeed.ToString());
            sb.Append(" dustQuads=").Append((dustQuads / (float)frames).ToString("F1"))
                .Append('/').Append(peakDustQuads)
                .Append(" objectsMax=").Append(peakObjects)
                .Append(" thrustingMax=").Append(peakThrustingObjects)
                .Append(" driftingMax=").Append(peakDriftingObjects)
                .Append(" archivedMax=").Append(peakArchivedObjects)
                .Append(" projectilesMax=").Append(peakProjectiles)
                .Append(" mpcSolves=").Append(autopilotSolves)
                .Append(" pdInterceptTicks=").Append(pointDefenseInterceptionTicks)
                .Append(" pdScatterTicks=").Append(pointDefenseScatterTicks)
                .Append(" structureChanges=").Append(firstStructureVersion < 0 ? 0
                    : lastStructureVersion - firstStructureVersion);
            AppendStage(sb, PerformanceStage.Update, "update", frames);
            AppendStage(sb, PerformanceStage.Grid, "grid", frames);
            AppendStage(sb, PerformanceStage.Dust, "dust", frames);
            AppendStage(sb, PerformanceStage.Exhaust, "exhaust", frames);
            AppendStage(sb, PerformanceStage.Shells, "shells", frames);
            AppendStage(sb, PerformanceStage.Foreground, "foreground", frames);
            AppendStage(sb, PerformanceStage.Tick, "tick", ticks);
            AppendStage(sb, PerformanceStage.Npc, "npc", ticks);
            AppendStage(sb, PerformanceStage.SoftDock, "softDock", ticks);
            AppendStage(sb, PerformanceStage.Projectiles, "projectiles", ticks);
            AppendStage(sb, PerformanceStage.Autopilot, "autopilot", ticks);
            AppendStage(sb, PerformanceStage.AutopilotSolve, "mpcSolve", autopilotSolves);
            AppendStage(sb, PerformanceStage.Weapons, "weapons", ticks);
            AppendStage(sb, PerformanceStage.Controls, "controls", ticks);
            AppendStage(sb, PerformanceStage.ControlSelection, "controlSelection", ticks);
            AppendStage(sb, PerformanceStage.ControlDecision, "controlDecision", ticks);
            AppendStage(sb, PerformanceStage.ControlCombat, "controlCombat", ticks);
            AppendStage(sb, PerformanceStage.ControlVolley, "controlVolley", ticks);
            AppendStage(sb, PerformanceStage.VolleyAim, "volleyAim", ticks);
            AppendStage(sb, PerformanceStage.PointDefense, "pointDefense", ticks);
            AppendStage(sb, PerformanceStage.PointDefensePrune, "pdPrune", ticks);
            AppendStage(sb, PerformanceStage.PointDefenseSearch, "pdSearch", ticks);
            AppendStage(sb, PerformanceStage.Objects, "objects", ticks);
            AppendStage(sb, PerformanceStage.ObjectDrive, "objDrive", ticks);
            AppendStage(sb, PerformanceStage.DriveAuthorize, "driveAuth", ticks);
            AppendStage(sb, PerformanceStage.DriveAllocate, "driveAlloc", ticks);
            AppendStage(sb, PerformanceStage.ObjectDrift, "objDrift", ticks);
            AppendStage(sb, PerformanceStage.ObjectArchive, "objArchive", ticks);
            AppendStage(sb, PerformanceStage.Collision, "collision", ticks);
            AppendStage(sb, PerformanceStage.MotionFeedback, "motionFx", ticks);
            AppendStage(sb, PerformanceStage.Lifecycle, "lifecycle", ticks);
            DeltaVLog.Message(DeltaVLogModule.Performance, sb.ToString());
            Reset();
            windowStart = now;
        }

        private void AppendStage(StringBuilder sb, PerformanceStage stage, string name, int divisor)
        {
            int index = (int)stage;
            double scale = 1000.0 / Stopwatch.Frequency;
            sb.Append(' ').Append(name).Append('=')
                .Append((totals[index] * scale / Mathf.Max(1, divisor)).ToString("F2"))
                .Append('/').Append((peaks[index] * scale).ToString("F2")).Append("ms");
        }

        private void Reset()
        {
            System.Array.Clear(totals, 0, totals.Length);
            System.Array.Clear(peaks, 0, peaks.Length);
            frames = ticks = 0;
            dustQuads = peakDustQuads = 0;
            peakObjects = peakProjectiles = 0;
            peakThrustingObjects = peakDriftingObjects = peakArchivedObjects = 0;
            autopilotSolves = pointDefenseInterceptionTicks = pointDefenseScatterTicks = 0;
            firstStructureVersion = -1;
            lastStructureVersion = 0;
            hasSpeed = mixedSpeed = false;
            frameSeconds = worstFrameSeconds = 0f;
            windowStart = -1f;
        }
    }
}

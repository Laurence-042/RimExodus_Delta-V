using System.Diagnostics;
using System.Text;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal enum PerformanceStage
    {
        Grid, Exhaust, Shells, Foreground, Update, Tick,
        Npc, SoftDock, Projectiles, Autopilot, AutopilotSolve, Weapons, Controls,
        ControlSelection, ControlDecision, ControlCombat, PointDefense, Objects,
        PointDefensePrune, PointDefenseCandidates, PointDefenseSearch, ObjectDrive, ObjectDrift, ObjectArchive,
        ControlVolley, VolleyAim, DriveAuthorize, DriveAllocate,
        Collision, MotionFeedback, Lifecycle,
        AimInput, AimInputGate, VolleyIntent, WeaponDispatch, WeaponAim, WeaponPrepare, WeaponLaunch,
        AimPreview, WeaponLayout, WeaponAmmo, WeaponEnergy, DriveCompose, DriveState, DriveIntegrate,
        WeaponExecution, WeaponReadiness, WeaponParameters, WeaponGuidance, WeaponCommit, Count
    }

    /// <summary>Opt-in, per-game CPU timing and frame/tick cadence; no saved or static state.</summary>
    internal sealed class DeltaVPerformanceProbe
    {
        private const float ReportSeconds = 5f;
        private readonly long[] totals = new long[(int)PerformanceStage.Count];
        private readonly long[] peaks = new long[(int)PerformanceStage.Count];
        private int frames;
        private int ticks;
        private int peakObjects;
        private int peakProjectiles;
        private int peakThrustingObjects;
        private int peakDriftingObjects;
        private int peakArchivedObjects;
        private int autopilotSolves;
        private int autopilotFallbacks;
        private int autopilotApproximate;
        private int autopilotQpCalls;
        private int autopilotQpIterations;
        private int pointDefenseInterceptionTicks;
        private int pointDefenseScatterTicks;
        private int firstStructureVersion = -1;
        private int lastStructureVersion;
        private TimeSpeed observedSpeed;
        private bool hasSpeed;
        private bool mixedSpeed;
        private int aimOnlyCommands, fireCommands, shots, maxTicksPerFrame, capFrames;
        private float minMultiplier = float.MaxValue, maxMultiplier;
        private readonly int[] rejected = new int[(int)WeaponReject.Count];
        private readonly int[] calls = new int[(int)PerformanceStage.Count];
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
            calls[index]++;
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

        internal void RecordWeaponCommand(bool fire)
        { if (!Enabled) return; if (fire) fireCommands++; else aimOnlyCommands++; }
        internal void RecordWeaponResult(WeaponReject reason)
        { if (!Enabled) return; if (reason == WeaponReject.None) shots++; else rejected[(int)reason]++; }

        internal void RecordAutopilotSolve(long start, bool solved, int qpCalls, int qpIterations, bool approximate)
        {
            if (start == 0L) return;
            Record(PerformanceStage.AutopilotSolve, start);
            autopilotSolves++;
            if (!solved) autopilotFallbacks++;
            if (approximate) autopilotApproximate++;
            autopilotQpCalls += qpCalls;
            autopilotQpIterations += qpIterations;
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
            int frameTicks = Find.TickManager?.TicksThisFrame ?? 0;
            float multiplier = Find.TickManager?.TickRateMultiplier ?? 0f;
            maxTicksPerFrame = Mathf.Max(maxTicksPerFrame, frameTicks);
            minMultiplier = Mathf.Min(minMultiplier, multiplier); maxMultiplier = Mathf.Max(maxMultiplier, multiplier);
            if (multiplier > 0f && frameTicks >= multiplier * 2f) capFrames++;
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
            sb.Append(" objectsMax=").Append(peakObjects)
                .Append(" thrustingMax=").Append(peakThrustingObjects)
                .Append(" driftingMax=").Append(peakDriftingObjects)
                .Append(" archivedMax=").Append(peakArchivedObjects)
                .Append(" projectilesMax=").Append(peakProjectiles)
                .Append(" mpcSolves=").Append(autopilotSolves)
                .Append(" mpcFallbacks=").Append(autopilotFallbacks)
                .Append(" mpcApproximate=").Append(autopilotApproximate)
                .Append(" mpcQpCalls=").Append(autopilotQpCalls)
                .Append(" mpcQpIterations=").Append(autopilotQpIterations)
                .Append(" pdInterceptTicks=").Append(pointDefenseInterceptionTicks)
                .Append(" pdScatterTicks=").Append(pointDefenseScatterTicks)
                .Append(" structureChanges=").Append(firstStructureVersion < 0 ? 0
                    : lastStructureVersion - firstStructureVersion);
            AppendStage(sb, PerformanceStage.Update, "update", frames);
            AppendStage(sb, PerformanceStage.Grid, "grid", frames);
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
            AppendStage(sb, PerformanceStage.PointDefenseCandidates, "pdCandidates", ticks);
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
            sb.Append(" tickFrameAvg=").Append((ticks / (float)frames).ToString("F2")).Append(" tickFrameMax=").Append(maxTicksPerFrame).Append(" capFrames=").Append(capFrames)
                .Append(" multiplier=").Append(minMultiplier.ToString("F1")).Append('/').Append(maxMultiplier.ToString("F1"))
                .Append(" aimOnly=").Append(aimOnlyCommands).Append(" fireRequests=").Append(fireCommands)
                .Append(" shots=").Append(shots).Append(" reject=");
            for (int i = 1; i < rejected.Length; i++)
                if (rejected[i] > 0) sb.Append((WeaponReject)i).Append(':').Append(rejected[i]).Append(',');
            // Mixed frame/tick callers use per-call timing here, with count and total to preserve attribution.
            sb.Append(" detail[avgUs/maxUs/calls/totalMs]:");
            for (int i = (int)PerformanceStage.AimInput; i < (int)PerformanceStage.Count; i++)
            {
                double ms = totals[i] * (1000.0 / Stopwatch.Frequency);
                sb.Append(' ').Append((PerformanceStage)i).Append('=')
                    .Append((ms * 1000 / Mathf.Max(1, calls[i])).ToString("F1")).Append('/')
                    .Append((peaks[i] * (1000000.0 / Stopwatch.Frequency)).ToString("F1")).Append('/')
                    .Append(calls[i]).Append('/').Append(ms.ToString("F2"));
            }
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
            System.Array.Clear(calls, 0, calls.Length);
            System.Array.Clear(rejected, 0, rejected.Length);
            aimOnlyCommands = fireCommands = shots = maxTicksPerFrame = capFrames = 0;
            minMultiplier = float.MaxValue; maxMultiplier = 0f;
            frames = ticks = 0;
            peakObjects = peakProjectiles = 0;
            peakThrustingObjects = peakDriftingObjects = peakArchivedObjects = 0;
            autopilotSolves = autopilotFallbacks = autopilotApproximate = autopilotQpCalls = autopilotQpIterations = 0;
            pointDefenseInterceptionTicks = pointDefenseScatterTicks = 0;
            firstStructureVersion = -1;
            lastStructureVersion = 0;
            hasSpeed = mixedSpeed = false;
            frameSeconds = worstFrameSeconds = 0f;
            windowStart = -1f;
        }
    }
}

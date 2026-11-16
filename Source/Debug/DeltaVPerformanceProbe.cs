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
        WeaponExecution, WeaponReadiness, WeaponParameters, WeaponGuidance, WeaponCommit,
        ShellRebuild, ShellStatic, ShellDynamic, OverlaySort, OverlaySubmit,
        FrameActions, FrameAction, ArchiveCapture, ArchiveRestore, SyncMapGeneration,
        CombatPlan, CombatRule, CombatPose, CombatPoseSetup, CombatPoseSearch, CombatRisk, CombatOutput,
        CombatReposition, CombatTrajectory, CombatRoute, CombatRouteShape,
        DrivePreviewModel, DrivePreviewBraking, DrivePreviewAngular, DrivePreviewAllocation,
        MpcAssemble, MpcFeasibility, MpcQp, MpcBrakeRecovery,
        EngagementObservation, EngagementScore, EngagementDamage,
        CombatPartSelection, EngagementCoverBuild,
        EngagementWeaponTable, EngagementProtectionScan, EngagementProtectionUpdate, EngagementTableQuery, Count
    }

    [System.Flags]
    internal enum CombatReplanCause
    {
        None = 0, InitialOrTarget = 1, Configuration = 2, Groups = 4, Permission = 8,
        Drive = 16, Structure = 32, Rule = 64, Periodic = 128
    }

    /// <summary>Opt-in, per-game CPU timing and frame/tick cadence; no saved or static state.</summary>
    internal sealed class DeltaVPerformanceProbe
    {
        // Cumulative work, independent of logging windows. Scheduling changes must not
        // appear faster merely by completing fewer candidate evaluations.
        internal long engagementStarted, engagementCompleted, engagementCandidates;
        private const float ReportSeconds = 5f;
        private static readonly PerformanceStage[] TickStages =
        {
            PerformanceStage.Npc, PerformanceStage.SoftDock, PerformanceStage.Projectiles,
            PerformanceStage.Autopilot, PerformanceStage.Weapons, PerformanceStage.Controls,
            PerformanceStage.Objects, PerformanceStage.Collision, PerformanceStage.MotionFeedback,
            PerformanceStage.Lifecycle
        };
        private static readonly PerformanceStage[] TickDetail =
        {
            PerformanceStage.CombatPlan, PerformanceStage.CombatRule, PerformanceStage.CombatReposition,
            PerformanceStage.EngagementObservation, PerformanceStage.EngagementScore,
            PerformanceStage.CombatPartSelection, PerformanceStage.EngagementDamage,
            PerformanceStage.EngagementCoverBuild, PerformanceStage.EngagementWeaponTable,
            PerformanceStage.EngagementProtectionScan, PerformanceStage.EngagementProtectionUpdate,
            PerformanceStage.EngagementTableQuery, PerformanceStage.CombatRoute,
            PerformanceStage.CombatRouteShape, PerformanceStage.CombatTrajectory,
            PerformanceStage.AutopilotSolve, PerformanceStage.MpcAssemble, PerformanceStage.MpcFeasibility,
            PerformanceStage.MpcQp, PerformanceStage.MpcBrakeRecovery
        };
        private static readonly PerformanceStage[] UpdateStages =
        {
            PerformanceStage.FrameActions, PerformanceStage.Grid, PerformanceStage.Exhaust,
            PerformanceStage.Shells, PerformanceStage.Foreground
        };
        private static readonly PerformanceStage[] UpdateDetail =
        {
            PerformanceStage.FrameAction, PerformanceStage.ShellRebuild, PerformanceStage.ShellStatic,
            PerformanceStage.ShellDynamic, PerformanceStage.OverlaySort, PerformanceStage.OverlaySubmit,
            PerformanceStage.ArchiveCapture, PerformanceStage.ArchiveRestore, PerformanceStage.SyncMapGeneration
        };

        private sealed class SlowestSample
        {
            private readonly long[] startTotals = new long[(int)PerformanceStage.Count];
            private readonly int[] startCalls = new int[(int)PerformanceStage.Count];
            private readonly long[] elapsedByStage = new long[(int)PerformanceStage.Count];
            private readonly int[] callsByStage = new int[(int)PerformanceStage.Count];
            internal long elapsed;
            internal int number;

            internal void Begin(long[] totals, int[] calls)
            {
                totals.CopyTo(startTotals, 0);
                calls.CopyTo(startCalls, 0);
            }

            internal bool Capture(int number, long duration, long[] totals, int[] calls)
            {
                if (duration <= elapsed) return false;
                this.number = number;
                elapsed = duration;
                for (int i = 0; i < totals.Length; i++)
                {
                    elapsedByStage[i] = totals[i] - startTotals[i];
                    callsByStage[i] = calls[i] - startCalls[i];
                }
                return true;
            }

            internal void AppendStages(StringBuilder sb, PerformanceStage[] stages)
            {
                foreach (var stage in stages)
                {
                    int index = (int)stage;
                    sb.Append(' ').Append(stage).Append('=');
                    AppendMs(sb, elapsedByStage[index]).Append('/').Append(callsByStage[index]);
                }
            }

            internal void AppendRemainder(StringBuilder sb, PerformanceStage[] stages)
            {
                long remaining = elapsed;
                foreach (var stage in stages) remaining -= elapsedByStage[(int)stage];
                AppendMs(sb.Append(" otherMs="), System.Math.Max(0L, remaining));
            }
        }

        private readonly SlowestSample slowestTick = new SlowestSample();
        private readonly SlowestSample slowestUpdate = new SlowestSample();
        private long tickStartSearches, tickStartCandidates, slowestTickSearches, slowestTickCandidates;
        private int slowestUpdateTicks;
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
        private int feasibilityChecks, infeasiblePrechecks, recoverySolves, approachRetries;
        private int visibleSections, rebuiltSections, overlayCommands;
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
        private readonly int[] poseCauses = new int[8];
        private long slowestPose;
        private DriveSubject slowestPoseShip;
        private Map slowestPoseMap;
        private CombatReplanCause slowestPoseCause;
        private int slowestPoseWeapons, slowestPoseEnemies, slowestPoseThreats, slowestPoseAllies;

        internal bool Enabled => DeltaVLog.Enabled(DeltaVLogModule.Performance);

        // Gate before resolving the game component; pure/offline callers may have no game.
        internal static DeltaVPerformanceProbe Current => DeltaVLog.Enabled(DeltaVLogModule.Performance)
            ? Verse.Current.Game?.GetComponent<UniverseManager>()?.Performance : null;
        internal static Measurement MeasureCurrent(PerformanceStage stage) => Current?.Measure(stage) ?? default;

        internal long Start() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        internal long BeginTick()
        {
            long start = Start();
            if (start == 0L) return 0L;
            slowestTick.Begin(totals, calls);
            tickStartSearches = engagementCompleted;
            tickStartCandidates = engagementCandidates;
            return start;
        }

        internal long BeginFrame()
        {
            long start = Start();
            if (start != 0L) slowestUpdate.Begin(totals, calls);
            return start;
        }

        internal Measurement Measure(PerformanceStage stage) => new Measurement(this, stage, Start());

        internal readonly struct Measurement : System.IDisposable
        {
            private readonly DeltaVPerformanceProbe owner;
            private readonly PerformanceStage stage;
            private readonly long start;
            internal Measurement(DeltaVPerformanceProbe owner, PerformanceStage stage, long start)
            { this.owner = owner; this.stage = stage; this.start = start; }
            public void Dispose() { if (start != 0) owner.Record(stage, start); }
        }

        internal void RecordShellSection(bool rebuilt)
        {
            if (!Enabled) return;
            visibleSections++;
            if (rebuilt) rebuiltSections++;
        }
        internal void RecordOverlayCommands(int count) { if (Enabled) overlayCommands += count; }

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

        internal void RecordCombatPose(long elapsed, DriveSubject ship, Map map, CombatReplanCause cause,
            int weapons, int enemies, int threats, int allies)
        {
            if (!Enabled || elapsed <= 0) return;
            RecordElapsed(PerformanceStage.CombatPose, elapsed);
            for (int i = 0; i < poseCauses.Length; i++)
                if (((int)cause & (1 << i)) != 0) poseCauses[i]++;
            if (elapsed <= slowestPose) return;
            slowestPose = elapsed; slowestPoseShip = ship; slowestPoseMap = map; slowestPoseCause = cause;
            slowestPoseWeapons = weapons; slowestPoseEnemies = enemies;
            slowestPoseThreats = threats; slowestPoseAllies = allies;
        }

        private void AppendCombatStats(StringBuilder sb)
        {
            sb.Append(" poseReplans=").Append(calls[(int)PerformanceStage.CombatPose]).Append(" poseCauses[overlap]=");
            for (int i = 0; i < poseCauses.Length; i++)
                if (poseCauses[i] != 0) sb.Append((CombatReplanCause)(1 << i)).Append(':').Append(poseCauses[i]).Append(',');
            if (slowestPose <= 0) return;
            sb.Append(" poseWorst={ship=").Append(slowestPoseShip).Append(' ').Append(UniverseManager.MapTag(slowestPoseMap))
                .Append(" ms=").Append((slowestPose * (1000.0 / Stopwatch.Frequency)).ToString("F2"))
                .Append(" cause=").Append(slowestPoseCause).Append(" weapons=").Append(slowestPoseWeapons)
                .Append(" enemies=").Append(slowestPoseEnemies).Append(" threats=").Append(slowestPoseThreats)
                .Append(" allies=").Append(slowestPoseAllies).Append('}');
        }

        internal void TickFinished(long start, int tick, int objectCount, int projectileCount, int structureVersion,
            int thrustingObjects, int driftingObjects, int archivedObjects)
        {
            if (start == 0L) return;
            long elapsed = Elapsed(start);
            RecordElapsed(PerformanceStage.Tick, elapsed);
            CaptureTick(tick, elapsed);
            ticks++;
            if (objectCount > peakObjects) peakObjects = objectCount;
            if (projectileCount > peakProjectiles) peakProjectiles = projectileCount;
            if (thrustingObjects > peakThrustingObjects) peakThrustingObjects = thrustingObjects;
            if (driftingObjects > peakDriftingObjects) peakDriftingObjects = driftingObjects;
            if (archivedObjects > peakArchivedObjects) peakArchivedObjects = archivedObjects;
            if (firstStructureVersion < 0) firstStructureVersion = structureVersion;
            lastStructureVersion = structureVersion;
        }

        private void CaptureTick(int tick, long elapsed)
        {
            if (!slowestTick.Capture(tick, elapsed, totals, calls)) return;
            slowestTickSearches = engagementCompleted - tickStartSearches;
            slowestTickCandidates = engagementCandidates - tickStartCandidates;
        }

        private void CaptureUpdate(int frame, int frameTicks, long elapsed)
        {
            if (slowestUpdate.Capture(frame, elapsed, totals, calls)) slowestUpdateTicks = frameTicks;
        }

        private static StringBuilder AppendMs(StringBuilder sb, long elapsed) =>
            sb.Append((elapsed * (1000.0 / Stopwatch.Frequency)).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

        private void AppendSlowestSamples(StringBuilder sb)
        {
            if (slowestTick.elapsed > 0)
            {
                sb.Append(" tickWorst={tick=").Append(slowestTick.number);
                AppendMs(sb.Append(" ms="), slowestTick.elapsed)
                    .Append(" searches=").Append(slowestTickSearches).Append(" candidates=").Append(slowestTickCandidates);
                slowestTick.AppendRemainder(sb, TickStages);
                sb.Append(" stages[ms/calls]");
                slowestTick.AppendStages(sb, TickStages);
                sb.Append(" nested[ms/calls]");
                slowestTick.AppendStages(sb, TickDetail);
                sb.Append('}');
            }
            if (slowestUpdate.elapsed > 0)
            {
                sb.Append(" updateWorst={frame=").Append(slowestUpdate.number);
                AppendMs(sb.Append(" ms="), slowestUpdate.elapsed).Append(" ticksThisFrame=").Append(slowestUpdateTicks);
                slowestUpdate.AppendRemainder(sb, UpdateStages);
                sb.Append(" stages[ms/calls]");
                slowestUpdate.AppendStages(sb, UpdateStages);
                sb.Append(" nested[ms/calls]");
                slowestUpdate.AppendStages(sb, UpdateDetail);
                sb.Append('}');
            }
        }

        internal void RecordWeaponCommand(bool fire)
        { if (!Enabled) return; if (fire) fireCommands++; else aimOnlyCommands++; }
        internal void RecordWeaponResult(WeaponReject reason)
        { if (!Enabled) return; if (reason == WeaponReject.None) shots++; else rejected[(int)reason]++; }

        internal void RecordAutopilotSolve(long start, bool solved, GuidanceCommand command)
        {
            if (start == 0L) return;
            Record(PerformanceStage.AutopilotSolve, start);
            autopilotSolves++;
            if (!solved) autopilotFallbacks++;
            if (command.qpStatus == QpSolveStatus.FeasibleApproximate) autopilotApproximate++;
            autopilotQpCalls += command.qpCalls;
            autopilotQpIterations += command.qpIterations;
            infeasiblePrechecks += command.infeasiblePrechecks;
            feasibilityChecks += command.feasibilityChecks;
            recoverySolves += command.recoverySolves;
            approachRetries += command.approachRetries;
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
                if (windowStart >= 0f || slowestTick.elapsed > 0) Reset();
                return;
            }
            RecordFrame(Elapsed(start));
        }

        private void RecordFrame(long elapsed)
        {
            RecordElapsed(PerformanceStage.Update, elapsed);
            float now = Time.realtimeSinceStartup;
            if (windowStart < 0f) windowStart = now;
            float seconds = Time.unscaledDeltaTime;
            TimeSpeed speed = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Paused;
            if (!hasSpeed) { observedSpeed = speed; hasSpeed = true; }
            else if (observedSpeed != speed) mixedSpeed = true;
            frames++;
            int frameTicks = Find.TickManager?.TicksThisFrame ?? 0;
            CaptureUpdate(Time.frameCount, frameTicks, elapsed);
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
                .Append(" mpcInfeasible=").Append(infeasiblePrechecks)
                .Append(" mpcPrechecks=").Append(feasibilityChecks)
                .Append(" mpcRecoverySolves=").Append(recoverySolves)
                .Append(" mpcApproachRetries=").Append(approachRetries)
                .Append(" shellSections=").Append(visibleSections)
                .Append(" shellRebuilt=").Append(rebuiltSections)
                .Append(" overlayCommands=").Append(overlayCommands)
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
            sb.Append(" detail[avgUs/maxUs/calls/totalMs; nested,do-not-sum]:");
            for (int i = (int)PerformanceStage.AimInput; i < (int)PerformanceStage.Count; i++)
            {
                double ms = totals[i] * (1000.0 / Stopwatch.Frequency);
                sb.Append(' ').Append((PerformanceStage)i).Append('=')
                    .Append((ms * 1000 / Mathf.Max(1, calls[i])).ToString("F1")).Append('/')
                    .Append((peaks[i] * (1000000.0 / Stopwatch.Frequency)).ToString("F1")).Append('/')
                    .Append(calls[i]).Append('/').Append(ms.ToString("F2"));
            }
            AppendCombatStats(sb);
            AppendSlowestSamples(sb);
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
            System.Array.Clear(poseCauses, 0, poseCauses.Length);
            slowestPose = 0; slowestPoseShip = default; slowestPoseMap = null; slowestPoseCause = default;
            slowestPoseWeapons = slowestPoseEnemies = slowestPoseThreats = slowestPoseAllies = 0;
            aimOnlyCommands = fireCommands = shots = maxTicksPerFrame = capFrames = 0;
            minMultiplier = float.MaxValue; maxMultiplier = 0f;
            frames = ticks = 0;
            peakObjects = peakProjectiles = 0;
            peakThrustingObjects = peakDriftingObjects = peakArchivedObjects = 0;
            autopilotSolves = autopilotFallbacks = autopilotApproximate = autopilotQpCalls = autopilotQpIterations = 0;
            feasibilityChecks = infeasiblePrechecks = recoverySolves = approachRetries = 0;
            visibleSections = rebuiltSections = overlayCommands = 0;
            pointDefenseInterceptionTicks = pointDefenseScatterTicks = 0;
            firstStructureVersion = -1;
            lastStructureVersion = 0;
            slowestTick.elapsed = slowestUpdate.elapsed = 0L;
            hasSpeed = mixedSpeed = false;
            frameSeconds = worstFrameSeconds = 0f;
            windowStart = -1f;
        }
    }
}

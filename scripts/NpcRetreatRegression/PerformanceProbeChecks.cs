using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using DeltaV;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckPerformanceProbe(UniverseManager universe)
    {
        var probe = universe.Performance;
        bool previous = DeltaVMod.CurrentSettings.logPerformance;
        long originalCompleted = probe.engagementCompleted, originalCandidates = probe.engagementCandidates;
        int[] Counts() => (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(probe);
        try
        {
            Call(probe, "Reset");
            DeltaVMod.CurrentSettings.logPerformance = false;
            using (DeltaVPerformanceProbe.MeasureCurrent(PerformanceStage.CombatPlan)) { }
            probe.RecordCombatPose(100, default, null, CombatReplanCause.Structure, 1, 2, 3, 4);
            Check(DeltaVPerformanceProbe.Current == null && probe.Start() == 0 && Counts().All(n => n == 0),
                "disabled combat probe skips lookup, clock and accumulation");
            DeltaVMod.CurrentSettings.logPerformance = true;
            Check(DeltaVPerformanceProbe.Current == probe, "probe resolves this game's instance");
            probe.RecordCombatPose(100, default, null, CombatReplanCause.Structure | CombatReplanCause.Periodic, 1, 2, 3, 4);
            probe.RecordCombatPose(200, default, null, CombatReplanCause.Periodic, 5, 6, 7, 8);
            var summary = new StringBuilder(); Call(probe, "AppendCombatStats", summary);
            Check(summary.ToString().Contains("poseReplans=2") && summary.ToString().Contains("Structure:1")
                && summary.ToString().Contains("Periodic:2") && summary.ToString().Contains("weapons=5 enemies=6 threats=7 allies=8"),
                "pose counters overlap and worst sample keeps its own workload");
            Call(probe, "Reset"); summary.Clear(); Call(probe, "AppendCombatStats", summary);
            Check(Counts().All(n => n == 0) && !summary.ToString().Contains("poseWorst=")
                && !summary.ToString().Contains("Structure:1"), "window reset clears pose totals, reasons and peak");

            long ms = Stopwatch.Frequency / 1000;
            probe.BeginTick();
            probe.RecordElapsed(PerformanceStage.Autopilot, 9 * ms);
            probe.RecordElapsed(PerformanceStage.EngagementDamage, ms);
            probe.RecordElapsed(PerformanceStage.EngagementDamage, 2 * ms);
            probe.RecordElapsed(PerformanceStage.EngagementCoverBuild, ms);
            probe.engagementCompleted += 2;
            probe.engagementCandidates += 7;
            Call(probe, "CaptureTick", 41, 12 * ms);

            probe.BeginFrame();
            probe.RecordElapsed(PerformanceStage.FrameActions, 3 * ms);
            probe.RecordElapsed(PerformanceStage.Shells, 5 * ms);
            Call(probe, "CaptureUpdate", 70, 4, 10 * ms);

            probe.BeginTick();
            probe.RecordElapsed(PerformanceStage.EngagementDamage, 5 * ms);
            probe.engagementCompleted += 8;
            probe.engagementCandidates += 9;
            Call(probe, "CaptureTick", 42, 8 * ms);

            probe.BeginFrame();
            probe.RecordElapsed(PerformanceStage.Shells, 6 * ms);
            Call(probe, "CaptureUpdate", 71, 1, 7 * ms);
            summary.Clear(); Call(probe, "AppendSlowestSamples", summary);
            string peaks = summary.ToString();
            Check(peaks.Contains("tickWorst={tick=41 ms=12.000 searches=2 candidates=7 otherMs=3.000")
                && peaks.Contains("Autopilot=9.000/1") && peaks.Contains("EngagementDamage=3.000/2")
                && peaks.Contains("EngagementCoverBuild=1.000/1"),
                "slowest tick keeps its own stage durations, calls, cover builds and candidate counts");
            Check(peaks.Contains("updateWorst={frame=70 ms=10.000 ticksThisFrame=4 otherMs=2.000")
                && peaks.Contains("FrameActions=3.000/1") && peaks.Contains("Shells=5.000/1"),
                "slowest frame callback keeps its own stage durations and tick cadence");

            DeltaVMod.CurrentSettings.logPerformance = false;
            Check(probe.BeginTick() == 0 && probe.BeginFrame() == 0, "disabled probe skips both snapshots");
            probe.FrameFinished(0L);
            summary.Clear(); Call(probe, "AppendSlowestSamples", summary);
            Check(summary.Length == 0 && Counts().All(n => n == 0), "disabling resets both slowest samples");
            long completed = probe.engagementCompleted, candidates = probe.engagementCandidates;
            DeltaVMod.CurrentSettings.logPerformance = true;
            probe.BeginTick();
            Call(probe, "CaptureTick", 43, ms);
            Call(probe, "AppendSlowestSamples", summary);
            Check(probe.engagementCompleted == completed && probe.engagementCandidates == candidates
                && summary.ToString().Contains("tickWorst={tick=43 ms=1.000 searches=0 candidates=0")
                && !summary.ToString().Contains("updateWorst"), "new window accepts smaller samples with fresh deltas");
            Call(probe, "Reset");

            var mass = new ShipPhysics.MassReport { mass = 3f, inertia = 12f };
            foreach (var dir in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
                mass.thrusters.Add(new ShipPhysics.Thruster { dir = dir, thrust = 3f, operational = true });
            var drive = new DeltaV.DriveInfo(mass);
            GuidanceCommand Solve(bool enabled)
            {
                DeltaVMod.CurrentSettings.logPerformance = enabled;
                var model = drive.PreviewModel();
                var acceleration = new double[9];
                for (int axis = 0; axis < 3; axis++)
                {
                    acceleration[axis] = model.forceColumns[axis].x / mass.mass;
                    acceleration[axis + 3] = model.forceColumns[axis].y / mass.mass;
                    acceleration[axis + 6] = model.torqueColumns[axis] / mass.inertia;
                }
                var request = new GuidanceRequest { positionError = new Vector2(5f, -2f),
                    relativeVelocity = new Vector2(.2f, -.1f), dt = .1f,
                    controlModel = model, controlAcceleration = acceleration, brakingAcceleration = 1f };
                var guidance = new OnlineMpcGuidance(); guidance.Reset(AutopilotMpcProfile.Balanced);
                Check(guidance.Compute(request, out var command), "probe comparison executes production position MPC");
                return command;
            }
            var disabled = Solve(false);
            Check(Counts().All(n => n == 0), "disabled MPC and drive previews record nothing");
            var enabledCommand = Solve(true);
            Check(typeof(GuidanceCommand).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.Name != "solveMilliseconds").All(f => Equals(f.GetValue(disabled), f.GetValue(enabledCommand))),
                "enabled probe preserves every MPC result except elapsed time exactly");
            Check(Counts()[(int)PerformanceStage.DrivePreviewModel] == 1
                && Counts()[(int)PerformanceStage.MpcAssemble] > 0
                && Counts()[(int)PerformanceStage.MpcFeasibility] > 0
                && Counts()[(int)PerformanceStage.MpcQp] > 0, "production model, assembly, feasibility and QP paths reach separate probes");
        }
        finally
        {
            DeltaVMod.CurrentSettings.logPerformance = previous;
            probe.engagementCompleted = originalCompleted;
            probe.engagementCandidates = originalCandidates;
            Call(probe, "Reset");
        }

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--fleet-probe-off") || args.Contains("--fleet-probe-on"))
        {
            DeltaVMod.CurrentSettings.logPerformance = args.Contains("--fleet-probe-on");
            new Harmony("DeltaV.Regression.FleetProbe").Patch(AccessTools.Method(typeof(ShipAutopilot), "Tick"),
                prefix: new HarmonyMethod(typeof(Program), nameof(BeginFleetProbeTick)),
                postfix: new HarmonyMethod(typeof(Program), nameof(FinishFleetProbeTick)));
        }
    }

    // The fleet fixture drives the real autopilot without Unity's game-component loop.
    static void BeginFleetProbeTick(out long __state) =>
        __state = Current.Game.GetComponent<UniverseManager>().Performance.BeginTick();

    static void FinishFleetProbeTick(long __state)
    {
        if (__state == 0L) return;
        var universe = Current.Game.GetComponent<UniverseManager>();
        var probe = universe.Performance;
        probe.Record(PerformanceStage.Autopilot, __state);
        probe.TickFinished(__state, Find.TickManager.TicksGame, universe.objects.Count, 0,
            SubstructureOwnership.GlobalStructureVersion, 0, 0, 0);
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Text;
using DeltaV;
using UnityEngine;

internal static partial class Program
{
    static void CheckPerformanceProbe(UniverseManager universe)
    {
        var probe = universe.Performance;
        bool previous = DeltaVMod.CurrentSettings.logPerformance;
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
        finally { DeltaVMod.CurrentSettings.logPerformance = previous; Call(probe, "Reset"); }
    }
}

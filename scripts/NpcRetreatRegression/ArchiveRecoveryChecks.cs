using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Xml;
using RimWorld;
using DeltaV;
using UnityEngine;
using Verse;
using DriveInfo = DeltaV.DriveInfo;

internal static partial class Program
{
    static void CheckArchiveRecovery(UniverseManager universe)
    {
        CheckArchiveReferences();
        CheckArchiveLoadingContext();
        CheckPerformanceWindows();
        CheckRecoveryFailures();
        foreach (bool drag in new[] { false, true })
        foreach (float reverse in new[] { .0087f, .00002f, 0f }) CheckWeakBrake(drag, reverse);
        CheckDamagedFlight(universe, false, false);
        CheckDamagedFlight(universe, true, false);
        CheckDamagedFlight(universe, false, true);
        CheckRecoveryLifecycle(universe);
    }

    sealed class ReferenceHolder : IThingHolder
    {
        internal readonly ThingOwner<Thing> contents;
        internal readonly List<IThingHolder> children = new List<IThingHolder>();
        public IThingHolder ParentHolder => null;
        public ReferenceHolder() { contents = new ThingOwner<Thing>(this); }
        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> result) => result.AddRange(children);
    }

    static void CheckArchiveReferences()
    {
        Thing Make(Type type, int id, ThingCategory category, string name)
        {
            var thing = (Thing)Raw(type);
            thing.def = (ThingDef)Raw(typeof(ThingDef));
            thing.def.defName = name; thing.def.category = category;
            thing.def.isSaveable = category != ThingCategory.Mote;
            thing.thingIDNumber = id;
            return thing;
        }
        Thing mote1 = Make(typeof(MoteThrown), -1, ThingCategory.Mote, "Spark");
        Thing mote2 = Make(typeof(MoteThrown), -1, ThingCategory.Mote, "Smoke");
        Thing invalid = Make(typeof(Thing), -1, ThingCategory.Item, "Unassigned");
        Thing building = Make(typeof(Thing), 100, ThingCategory.Building, "Building");
        Thing item = Make(typeof(Thing), 101, ThingCategory.Item, "Item");
        Thing pawn = Make(typeof(Pawn), 102, ThingCategory.Pawn, "Pawn");
        var outer = new ReferenceHolder(); var inner = new ReferenceHolder(); outer.children.Add(inner);
        ((List<Thing>)typeof(ThingOwner<Thing>).GetField("innerList", Private).GetValue(inner.contents)).Add(pawn);
        var values = new List<Thing>(); ThingOwnerUtility.GetAllThingsRecursively(outer, values);
        values.AddRange(new[] { mote1, mote2, invalid, building, item });
        var directory = new LoadedObjectDirectory();
        foreach (Thing value in values)
            if (RimExodus.Maps.MapArchive.CanRegisterExternalReference(value)) directory.RegisterLoaded(value);
        var ids = (Dictionary<int, ILoadReferenceable>)typeof(LoadedObjectDirectory).GetField("allThingsByThingID", Private).GetValue(directory);
        Check(ids.Count == 3 && !ids.ContainsKey(-1)
            && ReferenceEquals(directory.ObjectWithLoadID<Thing>(building.GetUniqueLoadID()), building)
            && ReferenceEquals(directory.ObjectWithLoadID<Thing>(item.GetUniqueLoadID()), item)
            && ReferenceEquals(directory.ObjectWithLoadID<Pawn>(pawn.GetUniqueLoadID()), pawn),
            "archive filters duplicate -1 Motes/unassigned Things; building, item and nested Pawn resolve");
        Type comparerType = typeof(RimExodus.Maps.MapArchive).GetNestedType("ReferenceComparer", BindingFlags.NonPublic);
        var comparer = (IEqualityComparer<ILoadReferenceable>)comparerType.GetField("Instance", Private).GetValue(null);
        var seen = new HashSet<ILoadReferenceable>(comparer);
        Thing collision = Make(typeof(Thing), 100, ThingCategory.Building, "Building");
        Check(seen.Add(building) && !seen.Add(building) && seen.Add(collision)
            && RimExodus.Maps.MapArchive.CanRegisterExternalReference(collision),
            "distinct persistent ID collisions reach vanilla registration; only identical instances deduplicate");
        RimExodus.Maps.MapArchive.ValidateCaptureIds(new[] { mote1, mote2, building, item, pawn, pawn });
        Check(mote1.thingIDNumber == -1 && pawn.thingIDNumber == 102,
            "save preflight accepts nonpersistent Motes and valid nested identities without renumbering");
        bool invalidRejected = false, collisionRejected = false;
        try { RimExodus.Maps.MapArchive.ValidateCaptureIds(new[] { building, invalid }); }
        catch (InvalidDataException) { invalidRejected = true; }
        try { RimExodus.Maps.MapArchive.ValidateCaptureIds(new[] { building, collision }); }
        catch (InvalidDataException) { collisionRejected = true; }
        Check(invalidRejected && collisionRejected, "save preflight rejects invalid/duplicate persistent IDs before serializing or unloading");
    }

    static void CheckArchiveLoadingContext()
    {
        ProgramState previous = Current.ProgramState;
        StatDef originalNutrition = StatDefOf.MaxNutrition;
        try
        {
            var pawn = (Pawn)Raw(typeof(Pawn));
            pawn.def = (ThingDef)Raw(typeof(ThingDef)); pawn.def.defName = "Human"; pawn.thingIDNumber = 321;
            pawn.def.race = new RaceProperties { baseBodySize = 1f, intelligence = Intelligence.Humanlike,
                lifeStageAges = new List<LifeStageAge> { new LifeStageAge { def = new LifeStageDef { bodySizeFactor = 1f, foodMaxFactor = 1f } } } };
            pawn.ageTracker = (Pawn_AgeTracker)Raw(typeof(Pawn_AgeTracker));
            Set(pawn.ageTracker, "pawn", pawn); Set(pawn.ageTracker, "cachedLifeStageIndex", 0);
            pawn.health = new Pawn_HealthTracker(pawn);
            var addiction = new Hediff_Addiction { pawn = pawn, def = new HediffDef { defName = "TestAddiction",
                chemicalNeed = new NeedDef { defName = "Chemical" }, stages = new List<HediffStage> { new HediffStage() } } };
            pawn.health.hediffSet.hediffs.Add(addiction);
            var food = (Need_Food)Raw(typeof(Need_Food));
            typeof(Need).GetField("pawn", Private).SetValue(food, pawn);
            food.def = new NeedDef { defName = "Food" };
            StatDefOf.MaxNutrition = new StatDef { defName = "MaxNutrition", workerClass = typeof(StatWorker),
                defaultBaseValue = 1f, supressDisabledError = true };
            Current.ProgramState = ProgramState.Playing;
            bool reproduced = false;
            try { float ignored = food.MaxLevel; }
            catch (NullReferenceException ex) { reproduced = ex.StackTrace.Contains("Hediff_Addiction"); }
            Check(reproduced, "vanilla food capacity reproduces addiction/unfinished needs null reference in Playing");
            using (new RimExodus.Maps.MapArchive.MapLoadingScope(Current.ProgramState))
            {
                Check(Current.ProgramState == ProgramState.MapInitializing && food.MaxLevel == 1f && pawn.needs == null,
                    "archive loading uses vanilla initialization food capacity before needs tracker is assigned");
                using (new RimExodus.Maps.MapArchive.MapLoadingScope(Current.ProgramState)) { }
                Check(Current.ProgramState == ProgramState.MapInitializing, "nested loading scope preserves outer initialization");
            }
            Check(Current.ProgramState == ProgramState.Playing, "successful archive scope restores Playing");
            try { using (new RimExodus.Maps.MapArchive.MapLoadingScope(Current.ProgramState)) throw new InvalidOperationException(); }
            catch (InvalidOperationException) { }
            Check(Current.ProgramState == ProgramState.Playing, "failed archive scope also restores Playing");

            var document = new XmlDocument();
            document.LoadXml("<archivedMap><map><things><thing><id>Human321</id><needs><needs><li Class='Need_Food'><def>Food</def><curLevel>0.732800126</curLevel></li></needs></needs></thing></things></map></archivedMap>");
            pawn.needs = (Pawn_NeedsTracker)Raw(typeof(Pawn_NeedsTracker));
            Set(pawn.needs, "needs", new List<Need> { food });
            RimExodus.Maps.MapArchive.ValidateRestoredPawnNeeds(document, new[] { pawn });
            Check(pawn.needs.AllNeeds.Count == 1, "saved need validation accepts intact loaded Pawn");
            pawn.needs.AllNeeds.Clear(); bool rejected = false;
            try { RimExodus.Maps.MapArchive.ValidateRestoredPawnNeeds(document, new[] { pawn }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Scribe-swallowed missing Food need rejects restoration before archive is discarded");
            rejected = false;
            try { RimExodus.Maps.MapArchive.ValidateRestoredPawnNeeds(document, Array.Empty<Pawn>()); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "missing saved Pawn also rejects restoration");
        }
        finally { Current.ProgramState = previous; StatDefOf.MaxNutrition = originalNutrition; }
    }

    static void CheckPerformanceWindows()
    {
        var settings = DeltaVMod.CurrentSettings;
        settings.logPerformance = false;
        var probe = new DeltaVPerformanceProbe();
        for (int i = 0; i < 100; i++)
        {
            using (probe.Measure(PerformanceStage.ShellRebuild)) { }
            probe.RecordShellSection(true); probe.RecordOverlayCommands(10);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            using (probe.Measure(PerformanceStage.ShellRebuild)) { }
            probe.RecordShellSection(true); probe.RecordOverlayCommands(10);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        bool enabled = false;
        for (int i = 0; i < 1000; i++)
            enabled |= DeltaVLog.Enabled(DeltaVLogModule.Performance) | DeltaVLog.Enabled(DeltaVLogModule.Performance)
                | DeltaVLog.Enabled(DeltaVLogModule.Performance);
        long gateAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int[] counts = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(probe);
        Check(!enabled && probe.Start() == 0 && counts[(int)PerformanceStage.ShellRebuild] == 0 && allocated == gateAllocated,
            "disabled new performance scopes do not start timers or allocate beyond existing log gates");
        settings.logPerformance = true;
        using (probe.Measure(PerformanceStage.FrameActions))
        using (probe.Measure(PerformanceStage.FrameAction)) { System.Threading.Thread.SpinWait(1000); }
        probe.RecordShellSection(true); probe.RecordOverlayCommands(7);
        Check(counts[(int)PerformanceStage.FrameActions] == 1 && counts[(int)PerformanceStage.FrameAction] == 1,
            "nested queue timings keep separate call counts");
        var queueOwner = new UniverseManager(Current.Game);
        int executed = 0;
        queueOwner.QueueFrameAction(() => { executed++; queueOwner.QueueFrameAction(() => executed++); });
        queueOwner.QueueFrameAction(() => executed++);
        Call(queueOwner, "RunFrameActions");
        Check(executed == 2 && Actions(queueOwner).Count == 1, "timed frame queue preserves next-frame reentrancy");
        Call(queueOwner, "RunFrameActions");
        int[] queueCalls = (int[])typeof(DeltaVPerformanceProbe).GetField("calls", Private).GetValue(queueOwner.Performance);
        Check(executed == 3 && queueCalls[(int)PerformanceStage.FrameActions] == 2
            && queueCalls[(int)PerformanceStage.FrameAction] == 3, "timed frame queue reports batches and actual executed action counts");
        queueOwner.QueueFrameAction(() => throw new InvalidOperationException("expected test failure"));
        queueOwner.QueueFrameAction(() => executed++);
        try { Call(queueOwner, "RunFrameActions"); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Check(Actions(queueOwner).Count == 1, "timing scopes preserve the unexecuted queue tail after exceptions");
        Call(queueOwner, "RunFrameActions");
        Call(probe, "Reset");
        Check(counts[(int)PerformanceStage.FrameActions] == 0
            && (int)typeof(DeltaVPerformanceProbe).GetField("rebuiltSections", Private).GetValue(probe) == 0
            && (int)typeof(DeltaVPerformanceProbe).GetField("overlayCommands", Private).GetValue(probe) == 0,
            "new window/game probe statistics reset without persistent state");
        settings.logPerformance = false;
    }

    static ShipPhysics.MassReport RecoveryLayout(float reverse)
    {
        var report = new ShipPhysics.MassReport { mass = 1200, inertia = 63926 };
        foreach (float side in new[] { -1f, 1f })
        {
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0, side * 7), dir = Vector2.right, thrust = 2000, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(0, side * 7), dir = Vector2.left, thrust = 2000 * reverse, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(side * 4, 0), dir = Vector2.up, thrust = 4000, operational = true });
            report.thrusters.Add(new ShipPhysics.Thruster { localPos = new Vector2(side * 4, 0), dir = Vector2.down, thrust = 4000, operational = true });
        }
        return report;
    }
    static GuidanceRequest RecoveryRequest(DriveInfo info, float distance, float speed)
    {
        var model = info.PreviewModel(); var matrix = new double[9];
        ShipAutopilot.BuildControlAcceleration(default, model, 0f, matrix);
        return new GuidanceRequest { positionError = new Vector2(distance, 0), relativeVelocity = new Vector2(speed, 0),
            dt = .1f, controlModel = model, controlAcceleration = matrix, elapsedSteps = 4,
            brakingAcceleration = info.PreviewBraking(distance >= 0 ? Vector2.right : Vector2.left), accuracy = GuidanceAccuracy.Braking };
    }
    static void CheckWeakBrake(bool drag, float reverse)
    {
        DeltaVMod.CurrentSettings.passiveDrag = drag;
        var info = new DriveInfo(RecoveryLayout(reverse));
        var request = RecoveryRequest(info, reverse == .0087f ? 62.4f : 1500f, reverse == .0087f ? 4.95f : 8.95f);
        var guidance = new OnlineMpcGuidance(); guidance.Reset(AutopilotMpcProfile.Balanced);
        if (reverse > 0)
            Check(!guidance.Compute(request, out var impossible) && impossible.qpStatus == QpSolveStatus.ProvenInfeasible
                && impossible.qpCalls == 0 && !guidance.TryGetPlannedStep(1, request.controlModel, out _),
                $"weak brake {reverse} drag={drag}: proved impossible with zero solver calls and no playback");
        var state = new BrakingRecoveryState();
        bool solved = state.Compute(guidance, AutopilotMpcProfile.Balanced, request, info, default, 0, false, true, out var command, out _);
        Check(solved && state.Active && command.qpStatus == QpSolveStatus.Optimal && command.recoverySolves == 1
            && command.qpCalls == 1 && state.NextControlTick == 24 && state.NextApproachTick == 60,
            $"weak brake {reverse} drag={drag}: strict velocity recovery succeeds");
        info.Preview(command.desiredLocalForce, command.desiredTorque, out var force, out _);
        Check(force.x <= .001f, "recovery requests braking, without fictitious reverse thrust");
        if (reverse == 0)
        {
            request = RecoveryRequest(info, -20, 8.95f);
            state.Compute(guidance, AutopilotMpcProfile.Balanced, request, info, default, 72, false, true, out _, out _);
            Check(state.Active, "zero reverse cannot exit recovery merely because overshoot reverses the position direction");
        }
    }

    sealed class FailedGuidance : IAutopilotGuidance
    {
        internal int calls; internal GuidanceRequest last;
        public void Reset(AutopilotMpcProfile profile) { }
        public bool Compute(GuidanceRequest request, out GuidanceCommand command)
        { calls++; last = request; BrakingGuidanceFallback.Compute(request, out command); command.qpCalls = 1; command.qpStatus = QpSolveStatus.NumericalFailure; return false; }
        public bool TryGetPlannedStep(int step, ShipPhysics.ControlModel model, out GuidanceCommand command) { command = default; return false; }
    }
    static void CheckRecoveryFailures()
    {
        var info = new DriveInfo(RecoveryLayout(1)); var request = RecoveryRequest(info, 40, 4);
        var state = new BrakingRecoveryState(); var failed = new FailedGuidance();
        state.Compute(failed, AutopilotMpcProfile.Balanced, request, info, default, 0, false, true, out _, out _);
        Check(state.Active && state.Fallback && failed.calls == 2 && state.NextControlTick == 60
            && failed.last.motionOnly && failed.last.accuracy == GuidanceAccuracy.Strict && failed.last.voidBlend == 0,
            "ordinary numerical failure enters strict recovery; recovery failure sets 60-tick QP cooldown");
        float firstForce = 0, lastForce = 0;
        for (int tick = 6; tick < 60; tick += 6)
        {
            request.relativeVelocity = new Vector2(tick == 6 ? 2 : .01f, 0);
            state.Compute(failed, AutopilotMpcProfile.Balanced, request, info, default, tick, false, true, out var cmd, out _);
            if (tick == 6) firstForce = cmd.desiredLocalForce.x; lastForce = cmd.desiredLocalForce.x;
            if (cmd.recoverySolves != 0 || cmd.qpCalls != 0) throw new Exception("fallback retried early");
        }
        Check(failed.calls == 2 && firstForce != lastForce, "failed plans never replay; six-tick fallback updates feedback without retrying QP");
        state.Compute(failed, AutopilotMpcProfile.Balanced, request, info, default, 60, false, true, out var retry, out _);
        Check(failed.calls == 4 && retry.approachRetries == 1 && retry.recoverySolves == 1, "both failed QPs retry after 60 ticks");
        state.Compute(failed, AutopilotMpcProfile.Balanced, request, info, default, 61, true, true, out retry, out _);
        Check(failed.calls == 6 && retry.approachRetries == 1, "capability version change immediately rechecks both modes");
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var excluded = new BrakingRecoveryState(); request.motionOnly = mode == 0; request.intercept = mode == 1;
            excluded.Compute(failed, AutopilotMpcProfile.Balanced, request, info, default, 0, false, false, out _, out _);
            Check(!excluded.Active, "sync/intercept/combat do not enter docking recovery");
        }
        // Every row intersects the box, but the two equalities cannot both hold.
        Check(!OnlineMpcGuidance.BoxProvesInfeasible(1, 3, new[] { 1.0, 1, 1 }, new[] { 0.0, .2, .8 },
            new[] { 1.0, .2, .8 }, new[] { 0.0, 0, 0 }), "interval precheck makes no claim of joint feasibility");
    }

    sealed class RecoveryFlightObject : SpaceObject
    {
        internal readonly ShipPhysics.MassReport layout;
        internal readonly DriveInfo info;
        internal float lastVoidBlend;
        internal RecoveryFlightObject(int id, float reverse) : base(id, new SpacePose(Vector3.forward, 0))
        { layout = RecoveryLayout(reverse); info = new DriveInfo(layout); ambientLabel = "Recovery probe"; }
        public override bool TryGetDrive(DriveSubject subject, out DriveInfo result) { result = info; return true; }
        public override DriveResult ApplyDrive(DriveSubject subject, DriveControl control, float dt, out float torque)
        {
            lastVoidBlend = control.voidBlend;
            info.Preview(control.force, control.torque, out Vector2 force, out torque);
            ShipPhysics.Integrate(this, layout, force, torque, dt, false, false, false);
            return DriveResult.Executed;
        }
        internal void Reverse(float fraction)
        { foreach (var t in layout.thrusters) if (t.dir == Vector2.left) t.thrust = 2000 * fraction; layout.version++; }
    }
    static object RecoveryFlight(ShipAutopilot pilot, DriveSubject subject)
        => ((IDictionary)typeof(ShipAutopilot).GetField("active", Private).GetValue(pilot))[subject];
    static T FlightValue<T>(object flight, string field)
        => (T)flight.GetType().GetField(field, Private | BindingFlags.Public).GetValue(flight);
    static void RecoveryTick(ShipAutopilot pilot, int tick)
    { Set(Find.TickManager, "ticksGameInt", tick); pilot.Tick(); }

    static void CheckDamagedFlight(UniverseManager universe, bool drag, bool moving)
    {
        DeltaVMod.CurrentSettings.passiveDrag = drag;
        var obj = new RecoveryFlightObject(79111, 1); universe.objects.Add(obj); Set(universe, "objectById", null);
        var subject = new DriveSubject(obj.objectId, obj.objectId); var pilot = new ShipAutopilot();
        Vector3 target = OrbitSphere.Move(obj.pose.position, new Vector2(62.4f, 0));
        obj.pose.velocity = new Vector2(4.95f, 0);
        bool arrived = false, failed = false;
        if (moving)
            pilot.FollowReference(subject, _ => new SpacePose(target, 0) { velocity = Vector2.right }, false, () => true, () => failed = true);
        else pilot.FlyTo(subject, target, 0, () => arrived = true, () => failed = true, allowVoidThrust: false);
        int lastRecoverySolve = -100, lastApproach = -100, recoveryCount = 0; bool entered = false, exited = false, crossed = false;
        for (int tick = 0; tick < 7200 && pilot.IsActive(subject); tick++)
        {
            bool changed = tick == 61 || tick == 961;
            if (tick == 61) obj.Reverse(.0087f);
            if (tick == 961) obj.Reverse(1);
            if (moving) target = OrbitSphere.Move(target, Vector2.right / 60f);
            object flight = RecoveryFlight(pilot, subject);
            var state = FlightValue<BrakingRecoveryState>(flight, "recovery");
            int oldControl = state.NextControlTick, oldApproach = state.NextApproachTick;
            RecoveryTick(pilot, tick);
            if (!pilot.IsActive(subject)) break;
            if (FlightValue<float>(flight, "elapsed") != tick + 1
                || !moving && FlightValue<Vector3>(flight, "direction") != target)
                throw new Exception("recovery reset elapsed time or replaced the target");
            if (state.Active)
            {
                entered = true;
                if (state.NextControlTick != oldControl)
                {
                    if (!changed && tick - lastRecoverySolve < 24) throw new Exception("recovery solved before 24 ticks");
                    lastRecoverySolve = tick; recoveryCount++;
                }
                if (oldApproach != 0 && state.NextApproachTick != oldApproach)
                {
                    if (!changed && tick - lastApproach < 60) throw new Exception("approach retried before 60 ticks");
                    lastApproach = tick;
                }
                if (obj.lastVoidBlend != 0) throw new Exception("void thrust during recovery");
            }
            else if (entered) exited = true;
            crossed |= OrbitSphere.Offset(obj.pose.position, target).x < -.05f;
            if (moving && tick > 1200 && OrbitSphere.ArcCells(obj.pose.position, target) < .05f
                && (obj.pose.velocity - Vector2.right).magnitude < .15f) { arrived = true; break; }
        }
        Check(entered && exited && arrived && !failed && recoveryCount > 0 && (drag || crossed),
            $"production flight damage/repair drag={drag} moving={moving}: entered={entered} exited={exited} arrived={arrived} failed={failed} crossed={crossed} recoverySolves={recoveryCount} tick={Find.TickManager.TicksGame} d={OrbitSphere.ArcCells(obj.pose.position, target):F3} v={obj.pose.velocity.magnitude:F3}");
        pilot.Cancel(subject); universe.objects.Remove(obj); Set(universe, "objectById", null);
    }

    static void CheckRecoveryLifecycle(UniverseManager universe)
    {
        DeltaVMod.CurrentSettings.passiveDrag = false;
        var obj = new RecoveryFlightObject(79112, 0); universe.objects.Add(obj); Set(universe, "objectById", null);
        var subject = new DriveSubject(obj.objectId, obj.objectId); var pilot = new ShipAutopilot();
        Vector3 target = OrbitSphere.Move(obj.pose.position, new Vector2(1500, 0));
        obj.pose.velocity = new Vector2(8.95f, 0); bool failed = false;
        pilot.FlyTo(subject, target, 0, onFailed: () => failed = true, allowVoidThrust: false);
        RecoveryTick(pilot, 0);
        object old = RecoveryFlight(pilot, subject);
        Check(FlightValue<BrakingRecoveryState>(old, "recovery").Active, "zero brake enters recovery in production Tick");
        for (int tick = 1; tick <= 601; tick++) RecoveryTick(pilot, tick);
        Check(failed && !pilot.IsActive(subject), "zero brake with no drag fails after ten seconds without progress");
        failed = false; obj.Reverse(.0087f); obj.pose.velocity = new Vector2(4.95f, 0);
        target = OrbitSphere.Move(obj.pose.position, new Vector2(62.4f, 0));
        pilot.FlyTo(subject, target, 0, onFailed: () => failed = true, allowVoidThrust: false);
        RecoveryTick(pilot, 700); object flight = RecoveryFlight(pilot, subject);
        Set(flight, "elapsed", 7200f); RecoveryTick(pilot, 701);
        Check(failed && !pilot.IsActive(subject), "recovery obeys the original total 120-second timeout");
        pilot.FlyTo(subject, target, 0, allowVoidThrust: false); RecoveryTick(pilot, 710);
        old = RecoveryFlight(pilot, subject);
        Vector3 replacement = OrbitSphere.Move(obj.pose.position, new Vector2(400, 0));
        pilot.FlyTo(subject, replacement, 0, allowVoidThrust: false);
        Check(!ReferenceEquals(old, RecoveryFlight(pilot, subject))
            && !FlightValue<BrakingRecoveryState>(RecoveryFlight(pilot, subject), "recovery").Active,
            "explicit target replacement discards old recovery/cooldown");
        pilot.Cancel(subject); Check(!pilot.IsActive(subject), "cancel removes recovery flight and plans");
        universe.objects.Remove(obj); Set(universe, "objectById", null);
    }
}

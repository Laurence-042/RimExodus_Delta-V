using System;
using System.Reflection;
using DeltaV;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static void CheckPlayerFlightOrders(UniverseManager universe)
    {
        CheckPlayerControlAuthority(universe);
        Type goalType = typeof(ShipAutopilot).Assembly.GetType("DeltaV.PlayerNavigationTarget", true);
        ConstructorInfo constructor = goalType.GetConstructor(Private, null,
            new[] { typeof(Vector3), typeof(float), typeof(int), typeof(Vector2) }, null);
        MethodInfo reference = goalType.GetMethod("Reference", Private);
        MethodInfo valid = goalType.GetMethod("Valid", Private);
        const float absoluteAngle = .73f;
        Vector3 fixedPosition = OrbitSphere.Move(Vector3.forward, new Vector2(24f, -8f));
        object fixedGoal = constructor.Invoke(new object[] { fixedPosition, absoluteAngle, 0, Vector2.zero });
        SpacePose fixedPose = (SpacePose)reference.Invoke(fixedGoal, new object[] { universe });
        Check(OrbitSphere.ArcCells(fixedPose.position, fixedPosition) < .001f
            && fixedPose.velocity == Vector2.zero && fixedPose.angularVelocity == 0f
            && Mathf.Abs(SpaceTransform.WrapAngle(fixedPose.angle - absoluteAngle)) < 1e-6f,
            "fixed player target freezes position and absolute heading with zero target velocity");

        var host = new SpaceObject(49991, new SpacePose(Vector3.forward, .29f)
            { velocity = new Vector2(2f, -1f), angularVelocity = .12f });
        universe.objects.Add(host);
        Set(universe, "objectById", null);
        Vector2 offset = new Vector2(35f, -21f);
        Vector3 selectedPosition = SpaceTransform.ShipLocalToUniverse(host.pose, offset);
        Vector2 confirmedOffset = SpaceTransform.UniverseToShipLocal(host, selectedPosition);
        object movingGoal = constructor.Invoke(new object[]
            { selectedPosition, absoluteAngle, host.objectId, confirmedOffset });
        Check((bool)valid.Invoke(movingGoal,
            new object[] { universe, new DriveSubject(0, 49990) }),
            "visible host target is valid while the selected object exists");
        foreach (float rotation in new[] { .29f, 1.13f })
        {
            host.pose.angle = rotation;
            SpacePose actual = (SpacePose)reference.Invoke(movingGoal, new object[] { universe });
            SpacePose expected = SpaceTransform.RigidPointPose(host.pose, confirmedOffset, 0f);
            Check(OrbitSphere.ArcCells(actual.position, expected.position) < .001f
                && (actual.velocity - expected.velocity).sqrMagnitude < 1e-7f
                && Mathf.Abs(SpaceTransform.WrapAngle(actual.angle - absoluteAngle)) < 1e-6f
                && actual.angularVelocity == 0f,
                "rotating host moves target point and v+omega cross r without rotating the absolute heading");
        }
        universe.objects.Remove(host);
        Set(universe, "objectById", null);
        Check(!(bool)valid.Invoke(movingGoal,
            new object[] { universe, new DriveSubject(0, 49990) }),
            "removing the reference invalidates the moving player target");

        Type taskType = typeof(ShipAutopilot).Assembly.GetType("DeltaV.PlayerAutoTaskKind", true);
        Type controllerType = typeof(PlayerShipController);
        object controller = controllerType.GetConstructor(Private, null,
            new[] { typeof(UniverseManager), typeof(DriveSubject) }, null)
            .Invoke(new object[] { universe, new DriveSubject(0, 49990) });
        MethodInfo replace = controllerType.GetMethod("ReplaceAutoTask", Private);
        MethodInfo clear = controllerType.GetMethod("ClearAutoTask", Private);
        MethodInfo resume = controllerType.GetMethod("ResumeAutoTask", Private);
        object task(string name) => Enum.Parse(taskType, name);
        string current() => controllerType.GetField("autoTask", Private).GetValue(controller).ToString();
        replace.Invoke(controller, new[] { task("Navigate"), fixedGoal, false });
        Check(current() == "Navigate", "confirmed navigation becomes the sole player auto task");
        replace.Invoke(controller, new[] { task("Combat"), Activator.CreateInstance(goalType), false });
        clear.Invoke(controller, new[] { task("Navigate") });
        Check(current() == "Combat", "replaced navigation cannot clear a newer combat task");
        resume.Invoke(controller, null);
        Check(current() == "Drift", "invalid player ship cannot resume a paused auto task");
    }

    static void CheckPlayerControlAuthority(UniverseManager universe)
    {
        var player = (Faction)Raw(typeof(Faction));
        var enemy = (Faction)Raw(typeof(Faction));
        var oldFactions = Find.World.factionManager;
        bool oldGodMode = DebugSettings.godMode;
        var oldConsoleDef = ThingDefOf.PilotConsole;
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        ThingDefOf.PilotConsole = TestBuildingDef("AuthorityConsole");
        var map = TestMap(49980);
        var frame = new MapSpaceObject(49980, map, new SpacePose(Vector3.forward, 0));
        universe.objects.Add(frame);
        try
        {
            var own = AddTestConsole(map, player, 49981);
            var foreign = AddTestConsole(map, enemy, 49982);
            var shuttle = AddTestHull(map, enemy, 49983);
            ShipStructureChanges.Notify(map);
            foreach (bool godMode in new[] { false, true })
            {
                DebugSettings.godMode = godMode;
                Check(universe.Pilot.CanPlayerControl(own.Vessel)
                    && universe.Pilot.CanPlayerControl(foreign.Vessel) == godMode
                    && universe.Pilot.CanPlayerControl(shuttle.Vessel) == godMode,
                    "mixed-map authority permits foreign anchors only in God Mode=" + godMode);
                Check(universe.Controls.For(own.Vessel) is PlayerShipController
                    && universe.Controls.For(foreign.Vessel) is NpcShipController,
                    "God Mode alone leaves foreign ships under NPC control=" + godMode);
                var takeover = Call(universe.Pilot, "ControllerFor", PilotTarget.Of(foreign.Vessel));
                Check((takeover is PlayerShipController) == godMode,
                    "explicit foreign ship takeover requires God Mode=" + godMode);
                if (takeover is PlayerShipController debugController)
                {
                    debugController.Enter(PilotTarget.Of(foreign.Vessel));
                    Set(universe.Pilot, "activePlayerController", debugController);
                    Check(ReferenceEquals(universe.Controls.For(foreign.Vessel), debugController),
                        "active God Mode takeover survives controller lookup");
                    DebugSettings.godMode = false;
                    Check(universe.Controls.For(foreign.Vessel) is NpcShipController
                        && !universe.Pilot.PilotedTarget(universe).IsValid,
                        "turning off God Mode releases the foreign ship and clears manual control");
                }
            }
            Set(typeof(Thing), foreign.parent, "factionInt", player);
            universe.Controls.ReconcileOwner(foreign.Vessel.Identity);
            Check(universe.Controls.For(foreign.Vessel) is PlayerShipController,
                "claiming the actual main console grants player control");
            Set(typeof(Thing), foreign.parent, "factionInt", enemy);
            universe.Controls.ReconcileOwner(foreign.Vessel.Identity);
            Check(universe.Controls.For(foreign.Vessel) is NpcShipController,
                "losing the main console revokes ordinary player control");
        }
        finally
        {
            universe.Controls.Reset();
            universe.objects.Remove(frame);
            Find.Maps.Remove(map);
            Find.World.factionManager = oldFactions;
            DebugSettings.godMode = oldGodMode;
            ThingDefOf.PilotConsole = oldConsoleDef;
        }
    }
}

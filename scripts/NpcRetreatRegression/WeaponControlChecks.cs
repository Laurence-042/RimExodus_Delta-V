using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    static bool WeaponMenuLanguage(ref LanguageWorker __result)
    { __result = new LanguageWorker_English(); return false; }
    static bool SkipWeaponMenuLayout() => false;

    static void CheckWeaponControlFixes(UniverseManager universe, Faction player, Faction enemy)
    {
        var bullet = TestBuildingDef("InterceptRegressionBullet");
        bullet.comps.Add(new CompProperties_SpaceProjectile { speed = 100f, hasMap = false });
        var source = new SpaceObject(45000, new SpacePose(Vector3.forward, 0f));
        var cases = new[]
        {
            (Vector2.zero, new Vector2(0, 20)),
            (new Vector2(0, 30), new Vector2(0, 30)),
            (new Vector2(0, 30), Vector2.zero),
            (new Vector2(0, 30), new Vector2(0, -20)),
            (new Vector2(40, 0), new Vector2(-20, 10))
        };
        foreach (var velocities in cases)
        foreach (Vector2 muzzleOffset in new[] { Vector2.zero, new Vector2(15, -8) })
        {
            source.pose.velocity = velocities.Item1;
            Vector3 muzzle = OrbitSphere.Move(source.pose.position, muzzleOffset);
            var victim = new SpacePose(OrbitSphere.Move(muzzle, new Vector2(200, 15)), 0)
                { velocity = velocities.Item2 };
            Check(ShipFireControl.TrySolveIntercept(muzzle, source.pose, victim, 100f, out var aim),
                $"intercept exists: source {velocities.Item1}, target {velocities.Item2}, muzzle {muzzleOffset}");
            Vector2 aiming = OrbitSphere.Offset(muzzle, aim);
            OrbitSphere.Basis(muzzle, out var east, out var north);
            Vector2 direction = aiming.normalized;
            Check(universe.Projectiles.Launch(bullet, source, muzzle, east * direction.x + north * direction.y,
                player, null), "production launch accepts corrected intercept direction");
            SpaceObject projectile = universe.objects.Last();
            float flightTime = aiming.magnitude / 100f;
            Vector3 arrival = OrbitSphere.Move(muzzle, projectile.pose.velocity * flightTime);
            Vector3 targetArrival = OrbitSphere.Move(victim.position, victim.velocity * flightTime);
            Check(OrbitSphere.Offset(arrival, targetArrival).magnitude < 0.1f,
                "actual inherited launch velocity meets the moving target within 0.1 cell");
        }
        Check(!ShipFireControl.TrySolveIntercept(source.pose.position, new SpacePose(source.pose.position, 0),
                new SpacePose(OrbitSphere.Move(source.pose.position, new Vector2(100, 0)), 0)
                    { velocity = new Vector2(200, 0) }, 100, out _),
            "a receding target faster than the projectile has no intercept");
        universe.Projectiles.ResetFlow();
        Call(universe, "RunFrameActions");
        universe.objects.Clear();

        Map map = TestMap(45100);
        var first = new CompSpaceWeapon();
        var second = new CompSpaceWeapon { salvoSet = SpaceWeaponSalvoSet.Secondary, groupNumber = 6 };
        var captured = new CompSpaceWeapon();
        AddTestBuilding(map, player, 45101, TestBuildingDef("GroupOne"), first);
        AddTestBuilding(map, player, 45102, TestBuildingDef("GroupTwo"), second);
        AddTestBuilding(map, enemy, 45103, TestBuildingDef("GroupCaptured"), captured);
        Command_WeaponGroup Command(CompSpaceWeapon weapon)
        {
            // Skip icon loading: only Unity texture construction is outside this offline fixture.
            var command = (Command_WeaponGroup)Raw(typeof(Command_WeaponGroup));
            Set(command, "weapons", new List<CompSpaceWeapon> { weapon });
            return command;
        }
        var grouped = Command(first);
        var other = Command(second);
        Check(grouped.GroupsWith(other), "different existing weapon assignments share one group command");
        Check(!grouped.InheritInteractionsFrom(other), "merged weapons do not open individual float menus");
        grouped.InheritInteractionsFrom(Command(captured));
        FactionManager previousFactions = Find.World.factionManager;
        Find.World.factionManager = (FactionManager)Raw(typeof(FactionManager));
        Set(Find.World.factionManager, "ofPlayer", player);
        bool previousGodMode = DebugSettings.godMode;
        DebugSettings.godMode = false;
        var menuHarness = new Harmony("DeltaV.WeaponMenuRegression");
        menuHarness.Patch(AccessTools.PropertyGetter(typeof(Find), "ActiveLanguageWorker"),
            prefix: new HarmonyMethod(typeof(Program), nameof(WeaponMenuLanguage)));
        menuHarness.Patch(AccessTools.Method(typeof(FloatMenuOption), "SetSizeMode"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipWeaponMenuLayout)));
        var options = grouped.BuildOptions();
        Set(typeof(Thing), captured.parent, "mapIndexOrState", (sbyte)-1);
        options[6].action();
        Check(first.salvoSet == SpaceWeaponSalvoSet.Secondary && first.groupNumber == 1
            && second.salvoSet == SpaceWeaponSalvoSet.Secondary && second.groupNumber == 1
            && captured.salvoSet == SpaceWeaponSalvoSet.Main,
            "one Secondary 1 menu choice updates both selected weapons and skips a removed weapon");
        options[5].action();
        Check(first.salvoSet == SpaceWeaponSalvoSet.Main && first.groupNumber == 6
            && second.salvoSet == SpaceWeaponSalvoSet.Main && second.groupNumber == 6,
            "the captured menu selection can assign Main 6 to all surviving members");
        DebugSettings.godMode = false;
        Set(typeof(Thing), second.parent, "factionInt", enemy);
        options[6].action();
        Check(second.salvoSet == SpaceWeaponSalvoSet.Main && second.groupNumber == 6,
            "a weapon captured while the menu is open cannot be reassigned");
        DebugSettings.godMode = previousGodMode;
        Find.World.factionManager = previousFactions;
        menuHarness.UnpatchAll(menuHarness.Id);
        Find.Maps.Remove(map);

        var calls = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI"))
            .Select(i => i.operand).OfType<MethodInfo>().ToList();
        int input = calls.FindIndex(m => m == AccessTools.Method(typeof(MapInterface), "HandleMapClicks"));
        int shortcut = calls.FindIndex(m => m.Name == "HandleLowPriorityShortcuts");
        int windows = calls.FindIndex(m => m.Name == "WindowStackOnGUI");
        Check(windows >= 0 && windows < input && input < shortcut,
            "installed game's chosen aim input hook follows UI windows and precedes the right-click shortcut");
        Check(Patch_AimModeClicks.TargetMethods().All(m => m != null),
            "both aim click Harmony targets resolve against the installed game");
    }
}

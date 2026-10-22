using System;
using System.Collections.Generic;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

internal static partial class Program
{
    sealed class UndockTransferReached : Exception { }
    static Rot4 undockFacing;
    static bool UndockDomain(Ship ship, ref HashSet<IntVec3> __result)
    { __result = new HashSet<IntVec3> { ship.console.parent.Position }; return false; }
    static void CaptureUndockFacing(Rot4 targetFacing)
    { undockFacing = targetFacing; throw new UndockTransferReached(); }
    static void CheckUndockHeading(UniverseManager universe)
    {
        var harness = new Harmony("DeltaV.Regression.UndockHeading");
        ThingDef oldPort = DeltaVDefOf.DeltaV_DockingPort;
        DeltaVDefOf.DeltaV_DockingPort = TestBuildingDef("undock_port");
        Map map = TestMap(49770);
        var console = AddTestConsole(map, Faction.OfPlayer, 49771);
        var frame = new MapSpaceObject(49770, map, new SpacePose(Vector3.forward, .43f));
        universe.objects.Add(frame);
        try
        {
            harness.Patch(AccessTools.Method(typeof(ShipRegistry), nameof(ShipRegistry.ControlDomain), new[] { typeof(Ship) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(UndockDomain)));
            harness.Patch(AccessTools.Method(typeof(ShipFlightTransitionService), "TransferShipToPocket"),
                prefix: new HarmonyMethod(typeof(Program), nameof(CaptureUndockFacing)));
            foreach (Rot4 facing in Rot4.AllRotations)
            {
                Set(typeof(Thing), console.parent, "rotationInt", facing);
                undockFacing = Rot4.Invalid;
                try { Call(universe.ShipTransitions, "ExecuteUndockSeparation", universe, console.Ship); }
                catch (TargetInvocationException e) when (e.InnerException is UndockTransferReached) { }
                Check(undockFacing == facing && ShipOrientation.PlacementRotation(facing, undockFacing) == Rot4.North,
                    "actual undock command preserves current grid heading instead of forcing east");
            }
        }
        finally
        {
            harness.UnpatchAll(harness.Id);
            DeltaVDefOf.DeltaV_DockingPort = oldPort;
            universe.objects.Remove(frame); Find.Maps.Remove(map);
        }
    }
}

using System;
using System.Linq;
using System.Xml.Linq;
using DeltaV;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

internal static partial class Program
{
    static void CheckCargoVisualGeometry()
    {
        var node = XDocument.Load("1.6/Defs/ThingDefs_Buildings/DeltaV_StationTrade.xml").Root
            .Elements("ThingDef").Single(n => (string)n.Element("defName") == "DeltaV_CargoContainer");
        var def = MakeDef("cargoGeometry");
        def.size = ParseHelper.FromString<IntVec2>((string)node.Element("size"));
        def.hasInteractionCell = (bool)node.Element("hasInteractionCell");
        def.interactionCellOffset = ParseHelper.FromString<IntVec3>((string)node.Element("interactionCellOffset"));
        var bay = new Building_SealedSpaceCargoContainer { def = def };
        Set(bay, "positionInt", new IntVec3(20, 0, 20));
        var pawn = new Pawn { def = MakeDef("cargoHacker") };
        Set(pawn, "mapIndexOrState", (sbyte)-1);
        pawn.jobs = new Pawn_JobTracker(pawn);
        pawn.rotationTracker = new Pawn_RotationTracker(pawn);
        JobDef oldHack = JobDefOf.Hack;
        JobDefOf.Hack = new JobDef { defName = "HackGeometry" };
        var patch = new Harmony("DeltaV.Regression.CargoVisuals");
        patch.CreateClassProcessor(typeof(CompSpaceCargo).Assembly.GetType("DeltaV.Patch_SpaceCargoHackFacing")).Patch();
        try
        {
            pawn.jobs.curJob = new Job { def = JobDefOf.Hack, targetA = bay };
            for (int i = 0; i < 4; i++)
            {
                var rot = new Rot4(i); Set(bay, "rotationInt", rot);
                IntVec3 stand = bay.InteractionCell;
                Set(pawn, "positionInt", stand);
                pawn.rotationTracker.FaceTarget(bay);
                Vector2 expected = SpaceTransform.RotatePlacementOffset(new Vector2(-.5f, 0f), rot);
                Vector3 center = bay.TrueCenter();
                Check(bay.OccupiedRect().Contains(stand)
                    && Math.Abs(stand.x + .5f - center.x - expected.x) < .0001f
                    && Math.Abs(stand.z + .5f - center.z - expected.y) < .0001f
                    && pawn.Rotation == rot,
                    "native interaction cell and Hack facing use the middle-left operator / top-left console: " + rot);
            }
            // A cell target and a different job must retain the original facing rules.
            pawn.rotationTracker.FaceTarget(pawn.Position + IntVec3.West);
            Check(pawn.Rotation == Rot4.West, "hack facing adapter leaves ordinary cell targets unchanged");
        }
        finally { patch.UnpatchAll(patch.Id); JobDefOf.Hack = oldHack; }

        var project = AccessTools.Method(typeof(CompSpaceCargo).Assembly.GetType("DeltaV.ShellExteriorShadow"), "TryMatrix");
        foreach (Vector2 sun in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1),
            new Vector2(0, -1), new Vector2(.6f, .8f), new Vector2(-.6f, -.8f) })
        {
            CellRect rect = new CellRect(20, 20, 2, 3);
            int sides = 0;
            for (int side = 0; side < 2; side++)
            {
                object[] args = { rect, 1f, sun, side, Matrix4x4.identity };
                if (!(bool)project.Invoke(null, args)) continue;
                sides++;
                var transform = (Matrix4x4)args[4];
                foreach (float x in new[] { -.5f, .5f })
                foreach (float z in new[] { -.5f, .5f })
                {
                    Vector3 p = transform.MultiplyPoint3x4(new Vector3(x, 0, z));
                    Check(p.x >= rect.minX + Math.Min(0, -sun.x) - .0001f
                        && p.x <= rect.maxX + 1 + Math.Max(0, -sun.x) + .0001f
                        && p.z >= rect.minZ + Math.Min(0, -sun.y) - .0001f
                        && p.z <= rect.maxZ + 1 + Math.Max(0, -sun.y) + .0001f,
                        "exterior shadow remains inside the height-projected footprint bounds");
                }
            }
            Check(sides == (sun.x == 0 || sun.y == 0 ? 1 : 2),
                "exterior shadow has no degenerate edge in cardinal light");
        }
    }
}

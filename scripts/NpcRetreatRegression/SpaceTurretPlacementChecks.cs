using System;
using System.Reflection;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    static void CheckSpaceTurretPlacement(Faction faction)
    {
        Map turretMap = TestMap(99101, 12);
        Map ordinaryMap = TestMap(99102, 12);
        ThingDef turretDef = TestBuildingDef("placementLaserTurret");
        turretDef.label = "laser turret";
        turretDef.thingClass = typeof(Building_SpaceTurretGun);
        turretDef.hasInteractionCell = true;
        turretDef.interactionCellOffset = IntVec3.South;
        turretDef.passability = Traversability.PassThroughOnly;
        var turret = (Building_SpaceTurretGun)Raw(typeof(Building_SpaceTurretGun));
        AddTestBuilding(turretMap, faction, 99111, turretDef, new EmptyComp(), turret);
        MoveCombatFixture(turret, new IntVec3(5, 0, 5));

        ThingDef ordinaryDef = TestBuildingDef("placementOrdinaryConsole");
        ordinaryDef.label = "ordinary console";
        ordinaryDef.hasInteractionCell = true;
        ordinaryDef.interactionCellOffset = IntVec3.South;
        ordinaryDef.passability = Traversability.PassThroughOnly;
        Building ordinary = AddTestBuilding(ordinaryMap, faction, 99112, ordinaryDef, new EmptyComp());
        MoveCombatFixture(ordinary, new IntVec3(5, 0, 5));

        ThingDef blockerDef = TestBuildingDef("placementShelf");
        blockerDef.label = "shelf";
        blockerDef.passability = Traversability.PassThroughOnly;
        IntVec3 blockedCell = new IntVec3(5, 0, 4);
        MethodInfo original = AccessTools.Method(typeof(GenConstruct),
            nameof(GenConstruct.NotBlockingAnyInteractionCells));
        Check(!GenConstruct.NotBlockingAnyInteractionCells(blockerDef, blockedCell, Rot4.North, turretMap).Accepted,
            "vanilla placement rejects a shelf over the Delta-V turret gunner position");

        Type patchType = typeof(DeltaVMod).Assembly.GetType("DeltaV.Patch_SpaceWeaponManningConstruction", true);
        MethodInfo transpiler = patchType.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
        var harmony = new Harmony("DeltaV.Regression.SpaceTurretPlacement");
        harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));
        try
        {
            Check(GenConstruct.NotBlockingAnyInteractionCells(blockerDef, blockedCell, Rot4.North, turretMap).Accepted,
                "Delta-V placement patch permits covering a completed top-turret gunner position");
            Check(!GenConstruct.NotBlockingAnyInteractionCells(blockerDef, blockedCell, Rot4.North, ordinaryMap).Accepted,
                "Delta-V placement patch preserves ordinary interaction-cell protection");

            Type manningType = typeof(DeltaVMod).Assembly.GetType("DeltaV.SpaceWeaponManning", true);
            MethodInfo count = manningType.GetMethod("CountTurretsBlockedBy", BindingFlags.Static | BindingFlags.NonPublic);
            int affected = (int)count.Invoke(null,
                new object[] { blockerDef, blockedCell, Rot4.North, turretMap, null });
            Check(affected == 1, "placement warning identifies the affected Delta-V turret once");

            blockerDef.passability = Traversability.Standable;
            affected = (int)count.Invoke(null,
                new object[] { blockerDef, blockedCell, Rot4.North, turretMap, null });
            Check(affected == 0, "standable construction does not warn about blocking a gunner position");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            Find.Maps.Remove(turretMap);
            Find.Maps.Remove(ordinaryMap);
        }
    }
}

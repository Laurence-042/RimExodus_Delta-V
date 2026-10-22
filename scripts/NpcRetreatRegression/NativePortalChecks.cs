using System.Collections.Generic;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

internal static partial class Program
{
    static void CheckNativePortal(SoftDockPortal portal, Map map)
    {
        Set(typeof(Thing), portal, "mapIndexOrState", (sbyte)Find.Maps.IndexOf(map));
        map.lordManager = (LordManager)Raw(typeof(LordManager));
        map.lordManager.lords = new List<Lord>();
        var cargo = (Thing)Raw(typeof(Thing));
        cargo.def = TestBuildingDef("portal_cargo");
        cargo.def.category = ThingCategory.Item;
        cargo.def.stackLimit = 75;
        cargo.stackCount = 5;
        var row = new TransferableOneWay(); row.things.Add(cargo); row.AdjustTo(3);
        var dialog = (Dialog_EnterPortal)Raw(typeof(Dialog_EnterPortal));
        Set(dialog, "portal", portal);
        Set(dialog, "transferables", new List<TransferableOneWay> { row });
        var harness = new Harmony("DeltaV.Regression.NativePortal");
        try
        {
            var patch = typeof(UniverseManager).Assembly.GetType("DeltaV.Patch_SoftDockPortalDialog", true);
            Check(harness.CreateClassProcessor(patch).Patch().Count == 1,
                "stale soft-portal window guard binds to native dialog acceptance");
            Check((bool)Call(dialog, "TryAccept") && portal.leftToLoad.Count == 1
                && portal.leftToLoad[0].CountToTransfer == 3 && map.lordManager.lords.Count == 0,
                "actual vanilla dialog accepts cargo-only selection through native loading-list and lord utility");
            var delivery = (Thing)Raw(typeof(Thing)); delivery.def = cargo.def; delivery.stackCount = 1;
            portal.Notify_ThingAdded(delivery);
            Check(portal.leftToLoad[0].CountToTransfer == 2,
                "native portal owns partial delivery accounting");
            portal.CancelLoad();
            Check(!portal.LoadInProgress, "native CancelLoad clears the native loading state");
            Check(AccessTools.Method(typeof(SoftDockPortal), "Tick").DeclaringType == typeof(MapPortal)
                && AccessTools.Method(typeof(SoftDockPortal), "GetDirectlyHeldThings").DeclaringType == typeof(MapPortal),
                "adapter inherits original portal tick and container mechanics without replacement");
        }
        finally { harness.UnpatchAll(harness.Id); }
    }
}

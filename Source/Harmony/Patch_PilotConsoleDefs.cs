using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve))]
    internal static class Patch_PilotConsoleDefs
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                AddMissingConsoleComponent(def);
        }

        internal static void AddMissingConsoleComponent(ThingDef def)
        {
            if (def.category != ThingCategory.Building || !typeof(Building).IsAssignableFrom(def.thingClass)
                || def.comps == null || !def.HasComp<CompPilotConsole>() || def.HasComp<CompDeltaVPilotConsole>())
                return;

            // A generated Def can share its comp list with another Def.
            def.comps = new List<CompProperties>(def.comps) { new CompProperties_DeltaVPilotConsole() };
        }
    }
}

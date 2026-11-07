using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    [HarmonyPatch]
    public static class Patch_SafeBatteryShortCircuit
    {
        private const string DrainMethod = "DrainBatteriesAndCauseExplosion";

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ShortCircuitUtility), nameof(ShortCircuitUtility.DoShortCircuit));
            yield return AccessTools.Method(typeof(ShortCircuitUtility), DrainMethod);
        }

        // Both vanilla versions read PowerNet.batteryComps here. Filter only the incident's
        // view, leaving the real network and its registration intact during explosion callbacks.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase original)
        {
            var field = AccessTools.Field(typeof(PowerNet), nameof(PowerNet.batteryComps));
            var filter = AccessTools.Method(typeof(Patch_SafeBatteryShortCircuit), nameof(EligibleBatteries));
            LocalBuilder batteries = null;
            if (original.Name == DrainMethod)
            {
                batteries = generator.DeclareLocal(typeof(List<CompPowerBattery>));
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
                yield return new CodeInstruction(OpCodes.Stloc, batteries);
            }
            int matches = 0;
            foreach (var instruction in instructions)
            {
                if (!instruction.LoadsField(field)) { yield return instruction; continue; }
                matches++;
                if (batteries == null)
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = filter;
                    yield return instruction;
                }
                else
                {
                    instruction.opcode = OpCodes.Pop; instruction.operand = null;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Ldloc, batteries);
                }
            }
            if (matches == 0) throw new InvalidOperationException("Missing vanilla short-circuit battery list in " + original.Name);
        }

        internal static List<CompPowerBattery> EligibleBatteries(PowerNet net) =>
            net.batteryComps.FindAll(battery => battery is not CompSwitchableBattery);
    }

    [HarmonyPatch(typeof(ShortCircuitUtility), nameof(ShortCircuitUtility.TryShortCircuitInRain))]
    public static class Patch_SafeBatteryRain
    {
        public static bool Prefix(Thing thing, ref bool __result)
        {
            if (thing.TryGetComp<CompSwitchableBattery>() == null) return true;
            __result = false;
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    [HarmonyPatch(typeof(GenLeaving), "GetBuildingResourcesLeaveCalculator")]
    internal static class Patch_SalvageBuildingYield
    {
        private static bool Prefix(Thing destroyedThing, DestroyMode mode, ref Func<int, int> __result)
        {
            if (mode != DestroyMode.Deconstruct || destroyedThing is Frame) return true;
            float factor = SalvageOutputScope.BuildingYieldFactor(destroyedThing);
            if (factor == 1f || !GenLeaving.CanBuildingLeaveResources(destroyedThing, mode)) return true;
            float fraction = destroyedThing.def.resourcesFractionWhenDeconstructed * factor;
            __result = count => Mathf.Min(GenMath.RoundRandom(count * fraction), count);
            return false;
        }
    }

    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
        new[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect),
            typeof(Predicate<IntVec3>), typeof(List<Thing>) })]
    internal static class Patch_SalvageForcedBuildingYield
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var forced = AccessTools.Field(typeof(BuildingProperties), nameof(BuildingProperties.forcedCostLeavings));
            var stackCount = AccessTools.Field(typeof(Thing), nameof(Thing.stackCount));
            var add = AccessTools.Method(typeof(ThingOwner), nameof(ThingOwner.TryAdd),
                new[] { typeof(Thing), typeof(bool) });
            // 原版的强制返料直接写原始造价数量，再加入返料容器。
            int fieldIndex = code.FindIndex(instruction => instruction.LoadsField(forced));
            int index = fieldIndex < 0 ? -1 : code.FindIndex(fieldIndex + 1, instruction => instruction.Calls(add));
            if (index < 4 || code.Count(instruction => instruction.LoadsField(forced)) != 1
                || !code[index - 4].StoresField(stackCount) || !code[index - 1].LoadsConstant(1))
            {
                Log.Error("[DeltaV] Could not identify the forced building dismantling yield.");
                return code;
            }

            CodeInstruction call = code[index];
            code.InsertRange(index, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call).MoveBlocksFrom(call),
                new CodeInstruction(OpCodes.Ldarg_2)
            });
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(Patch_SalvageForcedBuildingYield), nameof(AddForcedReturn));
            return code;
        }

        private static bool AddForcedReturn(ThingOwner owner, Thing item, bool canMergeWithExistingStacks,
            Thing building, DestroyMode mode)
        {
            if (mode == DestroyMode.Deconstruct)
            {
                float factor = SalvageOutputScope.BuildingYieldFactor(building);
                if (factor != 1f)
                {
                    int count = GenMath.RoundRandom(item.stackCount * factor);
                    if (count == 0) return false;
                    item.stackCount = count;
                }
            }
            return owner.TryAdd(item, canMergeWithExistingStacks);
        }
    }

    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
        new[] { typeof(TerrainDef), typeof(IntVec3), typeof(Map) })]
    internal static class Patch_SalvageTerrainYield
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            FieldInfo fraction = AccessTools.Field(typeof(BuildableDef), nameof(BuildableDef.resourcesFractionWhenDeconstructed));
            MethodInfo drop = Patch_CombatLeavings.NativeTryDrop();
            if (drop == null || code.Count(instruction => instruction.Calls(drop)) != 1)
            {
                Log.Error("[DeltaV] Could not identify the terrain dismantling placement.");
                return code;
            }
            if (code.Count(instruction => instruction.LoadsField(fraction)) != 1)
            {
                Log.Error("[DeltaV] Could not identify the terrain dismantling yield calculation.");
                return code;
            }
            int index = code.FindIndex(instruction => instruction.LoadsField(fraction));
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SalvageOutputScope),
                    nameof(SalvageOutputScope.AdjustTerrainReturn)))
            });
            index = code.FindIndex(instruction => instruction.Calls(drop));
            CodeInstruction call = code[index];
            code.Insert(index, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call).MoveBlocksFrom(call));
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(Patch_SalvageTerrainYield), nameof(DropReturn));
            return code;
        }

        private static bool DropReturn(ThingOwner<Thing> owner, Thing item, IntVec3 cell, Map map,
            ThingPlaceMode mode, out Thing result, Action<Thing, int> placedAction, Predicate<IntVec3> validator,
            TerrainDef terrain)
        {
            if (!SalvageOutputScope.TryCollect(owner, item, map, terrain, cell))
                return owner.TryDrop(item, cell, map, mode, out result, placedAction, validator);
            result = item;
            return true;
        }
    }

    [HarmonyPatch(typeof(CompExplosive), nameof(CompExplosive.PostDestroy))]
    internal static class Patch_SalvageCutExplosion
    {
        private static bool Prefix(CompExplosive __instance, DestroyMode mode, Map previousMap) =>
            !SalvageOutputScope.IsRemoving(__instance.parent, previousMap, mode);
    }

    [HarmonyPatch]
    internal static class Patch_SalvageOutput_Placement
    {
        // 两个重载最终汇入带 out 参数的入口，避免同一次放置重复回报。
        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(GenPlace),
            nameof(GenPlace.TryPlaceThing), new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode),
                typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>),
                typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int)
            });

        private static void Postfix(Map map, bool __result) =>
            SalvageOutputScope.PlacementFinished(map, __result);
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), new[] { typeof(string) })]
    internal static class Patch_SalvageOutput_Warning
    {
        private static bool Prefix(string text) => !SalvageOutputScope.ReplacesWarning(text);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    internal sealed class CombatLeavingsScope : IDisposable
    {
        [ThreadStatic] private static CombatLeavingsScope current;
        private readonly CombatLeavingsScope previous;
        private readonly UniverseManager universe;
        private readonly Map map;

        private CombatLeavingsScope(UniverseManager universe, Map map)
        {
            this.universe = universe;
            this.map = map;
            previous = current;
            current = this;
        }

        internal static CombatLeavingsScope Enter(Map map)
        {
            var universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || map == null || map.Disposed || universe.IsResettingScene
                || SpaceLifecycleService.IsUniverseHost(map) || !FreeFlightBootstrap.IsZeroGravity(universe, map)
                || universe.SpaceObjectOnMap(map) is not SpaceObject frame || frame.IsActiveCollider || frame.IsScattered)
                return null;
            return new CombatLeavingsScope(universe, map);
        }

        internal static bool TryEject(ThingOwner owner, Thing item, Map map, IntVec3 cell) =>
            current != null && current.map == map && item.def.category == ThingCategory.Item
            && current.universe.Scatter.TryEjectCombatLeavings(owner, item, map, cell);

        public void Dispose() => current = previous;
    }

    [HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell))]
    internal static class Patch_CombatExplosionLeavings
    {
        private static void Prefix(Explosion explosion, out CombatLeavingsScope __state) =>
            __state = explosion.projectile?.GetCompProperties<CompProperties_SpaceProjectile>() != null
                ? CombatLeavingsScope.Enter(explosion.Map) : null;

        private static void Finalizer(CombatLeavingsScope __state) => __state?.Dispose();
    }

    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
        new[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect),
            typeof(Predicate<IntVec3>), typeof(List<Thing>) })]
    internal static class Patch_CombatLeavings
    {
        internal static MethodInfo NativeTryDrop() =>
            AccessTools.DeclaredMethod(typeof(ThingOwner<Thing>), nameof(ThingOwner<Thing>.TryDrop),
                new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode),
                    typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>), typeof(Predicate<IntVec3>) });

        private static bool Prefix(Thing diedThing, Map map, DestroyMode mode) =>
            !SalvageOutputScope.IsRemoving(diedThing, map, mode);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var original = NativeTryDrop();
            if (original == null || code.Count(instruction => instruction.Calls(original)) != 1)
            {
                Log.Error("[DeltaV] Could not identify the building destruction leavings placement.");
                return code;
            }

            int index = code.FindIndex(instruction => instruction.Calls(original));
            CodeInstruction call = code[index];
            code.InsertRange(index, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call).MoveBlocksFrom(call),
                new CodeInstruction(OpCodes.Ldarg_2)
            });
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(Patch_CombatLeavings), nameof(DropLeavings));
            return code;
        }

        private static bool DropLeavings(ThingOwner<Thing> owner, Thing item, IntVec3 cell, Map map,
            ThingPlaceMode mode, out Thing result, Action<Thing, int> placedAction, Predicate<IntVec3> validator,
            Thing destroyedThing, DestroyMode destroyMode)
        {
            if ((destroyMode == DestroyMode.Deconstruct && SalvageOutputScope.TryCollect(owner, item, map, destroyedThing))
                || (destroyedThing is Building && destroyMode is (DestroyMode.KillFinalize or DestroyMode.KillFinalizeLeavingsOnly)
                    && CombatLeavingsScope.TryEject(owner, item, map, cell)))
            {
                result = item;
                return true;
            }
            return owner.TryDrop(item, cell, map, mode, out result, placedAction, validator);
        }
    }
}

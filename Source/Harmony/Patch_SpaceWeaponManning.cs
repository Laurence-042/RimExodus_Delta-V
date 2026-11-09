using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeltaV
{
    internal static class SpaceWeaponManningPatchUtility
    {
        // 基于 1.6 实际 IL：调用点各出现一次；签名漂移时保留原方法并报告。
        internal static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions,
            MethodInfo original, string replacement, bool passThis = false)
        {
            var list = new List<CodeInstruction>(instructions);
            if (list.Count(i => i.Calls(original)) != 1)
            {
                Log.Warning($"[DeltaV] Manning patch signature drift at {original}: expected one call; leaving method unchanged.");
                return list;
            }
            int index = list.FindIndex(i => i.Calls(original));
            CodeInstruction call = list[index];
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(SpaceWeaponManning), replacement);
            if (passThis)
            {
                var instance = new CodeInstruction(OpCodes.Ldarg_0);
                instance.labels.AddRange(call.labels);
                instance.blocks.AddRange(call.blocks);
                call.labels.Clear();
                call.blocks.Clear();
                list.Insert(index, instance);
            }
            return list;
        }
    }

    [HarmonyPatch(typeof(Building_TurretGun), "Tick")]
    internal static class Patch_SpaceWeaponManningFireControl
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => SpaceWeaponManningPatchUtility.ReplaceCall(instructions,
                AccessTools.PropertyGetter(typeof(CompMannable), nameof(CompMannable.MannedNow)),
                nameof(SpaceWeaponManning.CanFire));
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DrawExtraSelectionOverlays))]
    internal static class Patch_SpaceWeaponManningInteractionOverlay
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => SpaceWeaponManningPatchUtility.ReplaceCall(instructions,
                AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawInteractionCells)),
                nameof(SpaceWeaponManning.DrawInteractionCells), passThis: true);
    }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.NotBlockingAnyInteractionCells))]
    internal static class Patch_SpaceWeaponManningConstruction
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo hasCells = AccessTools.PropertyGetter(typeof(ThingDef),
                nameof(ThingDef.HasSingleOrMultipleInteractionCells));
            MethodInfo position = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));
            // 原版在同一循环中先检查 Def，再枚举 thing 的交互格。复用该 thing 局部变量，
            // 不按坐标重新查实例，也不修改 ThingUtility 返回的共享临时列表。
            int check = list.FindIndex(i => i.Calls(hasCells));
            int loadThing = list.FindIndex(i => i.Calls(position)) - 1;
            if (list.Count(i => i.Calls(hasCells)) != 1
                || list.Count(i => i.Calls(position)) != 1 || loadThing < 0
                || !list[loadThing].IsLdloc())
            {
                Log.Warning("[DeltaV] Manning construction signature drift: expected one interaction-cell check and Thing.Position read; leaving method unchanged.");
                return list;
            }
            CodeInstruction call = list[check];
            var instance = new CodeInstruction(list[loadThing].opcode, list[loadThing].operand);
            instance.MoveLabelsFrom(call);
            instance.MoveBlocksFrom(call);
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(SpaceWeaponManning),
                nameof(SpaceWeaponManning.HasProtectedInteractionCells));
            list.Insert(check, instance);
            return list;
        }
    }

    internal readonly struct SpaceWeaponInteractionWarningState
    {
        internal readonly Map map;
        internal readonly IntVec3 cell;
        internal readonly Rot4 rotation;
        internal readonly BuildableDef def;
        internal readonly int turretCount;

        internal SpaceWeaponInteractionWarningState(Map map, IntVec3 cell, Rot4 rotation,
            BuildableDef def, int turretCount)
        {
            this.map = map;
            this.cell = cell;
            this.rotation = rotation;
            this.def = def;
            this.turretCount = turretCount;
        }
    }

    internal static class SpaceWeaponInteractionWarning
    {
        private static readonly AccessTools.FieldRef<Designator_Place, Rot4> PlacingRotation =
            AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot");

        internal static SpaceWeaponInteractionWarningState BeforePlacement(Designator_Place designator,
            IntVec3 cell, Thing thingToIgnore = null)
        {
            Map map = Find.CurrentMap;
            BuildableDef def = designator?.PlacingDef;
            Rot4 rotation = designator == null ? Rot4.North : PlacingRotation(designator);
            int count = SpaceWeaponManning.CountTurretsBlockedBy(def, cell, rotation, map, thingToIgnore);
            return new SpaceWeaponInteractionWarningState(map, cell, rotation, def, count);
        }

        internal static void AfterPlacement(SpaceWeaponInteractionWarningState state)
        {
            if (state.turretCount <= 0 || state.map == null || state.def == null
                || !state.cell.InBounds(state.map)) return;
            List<Thing> things = state.cell.GetThingList(state.map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing placed = things[i];
                if (placed.Position != state.cell || placed.Rotation != state.rotation
                    || placed.def != state.def && placed.def.entityDefToBuild != state.def) continue;
                Messages.Message("DeltaV_WeaponInteractionCellBlocked".Translate(state.turretCount),
                    new TargetInfo(state.cell, state.map), MessageTypeDefOf.CautionInput, historical: false);
                return;
            }
        }
    }

    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
    internal static class Patch_SpaceWeaponManningBuildWarning
    {
        private static void Prefix(Designator_Build __instance, IntVec3 c,
            out SpaceWeaponInteractionWarningState __state)
            => __state = SpaceWeaponInteractionWarning.BeforePlacement(__instance, c);

        private static void Postfix(SpaceWeaponInteractionWarningState __state)
            => SpaceWeaponInteractionWarning.AfterPlacement(__state);
    }

    [HarmonyPatch(typeof(Designator_Install), nameof(Designator_Install.DesignateSingleCell))]
    internal static class Patch_SpaceWeaponManningInstallWarning
    {
        private static readonly MethodInfo ThingToInstallGetter = AccessTools.PropertyGetter(
            typeof(Designator_Install), "ThingToInstall");

        private static void Prefix(Designator_Install __instance, IntVec3 c,
            out SpaceWeaponInteractionWarningState __state)
        {
            Thing ignored = ThingToInstallGetter?.Invoke(__instance, null) as Thing;
            __state = SpaceWeaponInteractionWarning.BeforePlacement(__instance, c, ignored);
        }

        private static void Postfix(SpaceWeaponInteractionWarningState __state)
            => SpaceWeaponInteractionWarning.AfterPlacement(__state);
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Cleanup))]
    internal static class Patch_SpaceWeaponManningJobEnded
    {
        private static void Postfix(JobDriver __instance)
        {
            CompSpaceWeapon weapon = SpaceWeaponManning.WeaponFor(__instance.job);
            if (weapon == null) return;
            ShipControlService.NotifyGunnerChange(weapon.parent.Map ?? __instance.pawn?.Map);
        }
    }

    // 原版自动征用器只适用于迫击炮弹药流程，并会把普通 Building 强转成炮塔。
    // 舰炮自动征用由共同 controller 负责；手动菜单和 ManTurret 本体完全保留。
    [HarmonyPatch]
    internal static class Patch_SpaceWeaponManningVanillaDispatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = typeof(JobGiver_ManTurrets).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.Contains("<TryGiveJob>g__Validator") && m.ReturnType == typeof(bool)
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Thing))
                .ToList();
            if (methods.Count != 1)
            {
                Log.Warning("[DeltaV] Vanilla turret dispatcher signature drift: expected one validator; leaving it unchanged.");
                yield break;
            }
            yield return methods[0];
        }

        private static bool Prefix(Thing __0, ref bool __result)
        {
            if (__0.TryGetComp<CompSpaceWeapon>() == null) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.DrawLinesBetweenTargets))]
    internal static class Patch_SpaceWeaponManningSelectionLines
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            FieldInfo targetA = AccessTools.Field(typeof(Job), nameof(Job.targetA));
            MethodInfo isValid = AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.IsValid));
            var checks = new List<int>();
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i].opcode == OpCodes.Ldflda && Equals(list[i].operand, targetA)
                    && list[i + 1].Calls(isValid) && list[i + 1].labels.Count == 0
                    && list[i + 1].blocks.Count == 0) checks.Add(i);
            // 只扩展当前任务与排队任务的两个绘制门禁，其余原版目标读取和连线顺序不动。
            if (checks.Count != 2)
            {
                Log.Warning("[DeltaV] Manning selection-line signature drift: expected two targetA validity checks; leaving method unchanged.");
                return list;
            }
            foreach (int index in checks.OrderByDescending(i => i))
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = AccessTools.Method(typeof(SpaceWeaponManning),
                    nameof(SpaceWeaponManning.HasSelectionLine));
                list.RemoveAt(index + 1);
            }
            return list;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeltaV
{
    /// <summary>
    /// 原版环境 API 把 Map.Tile 同时当作世界占位和地理采样点；Delta-V 只替换
    /// 后一种语义。绝不 patch Map.Tile getter 本身，避免污染 FindMap/降落/世界对象身份。
    ///
    /// 星球层语义同属采样语义：原版以 X.Map.Tile.LayerDef 判定是否在太空，并按层过滤
    /// 游戏条件与受限内容。Map.Tile 无效时这些分支一律按地表处理，船/POI 口袋图因此
    /// 会被世界级耀斑断电，并拿到「行星居所」与地面基因惩罚。下列 transpiler 让口袋图
    /// 在这些消费点改读 TileFor 的轨道格，走与真实轨道图相同的原版分支。
    /// 事件目标语义不在本文件：口袋图无 IncidentTargetTags、IsPlayerHome 恒 false，
    /// 仍不会被 storyteller 选中。
    /// </summary>
    public static class Patch_SpaceEnvironment
    {
        private static readonly MethodInfo MapTileGetter = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile));
        private static readonly MethodInfo EnvironmentTileFor = AccessTools.Method(typeof(SpaceEnvironment), nameof(SpaceEnvironment.TileFor));

        /// <summary>
        /// 把方法体内对 Map.Tile 的全部读取换成 SpaceEnvironment.TileFor；读取数与预期
        /// 不符时告警并原样返回，不留下半替换的方法体。
        /// </summary>
        private static IEnumerable<CodeInstruction> ReplaceMapTileGetter(
            IEnumerable<CodeInstruction> instructions, MethodBase original, int expected)
            => ReplaceCalls(instructions, original, MapTileGetter, EnvironmentTileFor, expected, "Environment");

        /// <summary>把方法体内对 from 的全部调用换成签名相同的 to；数量不符时告警并原样返回。</summary>
        internal static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> instructions, MethodBase original,
            MethodInfo from, MethodInfo to, int expected, string patchLabel)
        {
            var list = new List<CodeInstruction>(instructions);
            var matches = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction instruction = list[i];
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && Equals(instruction.operand, from))
                {
                    matches.Add(i);
                }
            }
            if (matches.Count != expected)
            {
                Log.Warning($"[DeltaV] {patchLabel} patch signature drift in {original.DeclaringType?.FullName}.{original.Name}: found {matches.Count} Map.Tile call(s), expected {expected}; leaving the method unchanged.");
                return list;
            }
            foreach (int index in matches)
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = to;
            }
            return list;
        }

        [HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.CelestialSunGlow),
            new Type[] { typeof(Map), typeof(int) })]
        private static class Patch_GenCelestial_CelestialSunGlow_Map
        {
            private static bool Prefix(Map map, int ticksAbs, ref float __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                PlanetTile tile = SpaceEnvironment.TileFor(map);
                __result = tile.Valid ? GenCelestial.CelestialSunGlow(tile, ticksAbs) : 1f;
                return false;
            }
        }

        private static readonly string[] IntDateMethods =
        {
            nameof(GenLocalDate.DayOfYear), nameof(GenLocalDate.HourOfDay),
            nameof(GenLocalDate.DayOfTwelfth), nameof(GenLocalDate.Year),
            nameof(GenLocalDate.DayOfSeason), nameof(GenLocalDate.DayOfQuadrum),
            nameof(GenLocalDate.DayTick), nameof(GenLocalDate.HourInteger)
        };

        private static readonly string[] FloatDateMethods =
        {
            nameof(GenLocalDate.DayPercent), nameof(GenLocalDate.YearPercent),
            nameof(GenLocalDate.HourFloat)
        };

        private static IEnumerable<MethodBase> ResolveMapOverloads(IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                MethodInfo method = AccessTools.Method(typeof(GenLocalDate), name, new Type[] { typeof(Map) });
                if (method != null) yield return method;
                else Log.Warning($"[DeltaV] GenLocalDate.{name}(Map) not found; date compatibility for that method is disabled.");
            }
        }

        [HarmonyPatch]
        private static class Patch_GenLocalDate_Map_Int
        {
            private static IEnumerable<MethodBase> TargetMethods() => ResolveMapOverloads(IntDateMethods);

            private static bool Prefix(Map map, MethodBase __originalMethod, ref int __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                float longitude = SpaceEnvironment.DateLocationFor(map).x;
                int ticks = GenTicks.TicksAbs;
                switch (__originalMethod.Name)
                {
                    case nameof(GenLocalDate.DayOfYear):
                        __result = Current.ProgramState == ProgramState.Playing ? GenDate.DayOfYear(ticks, longitude) : 0;
                        break;
                    case nameof(GenLocalDate.HourOfDay): __result = GenDate.HourOfDay(ticks, longitude); break;
                    case nameof(GenLocalDate.DayOfTwelfth): __result = GenDate.DayOfTwelfth(ticks, longitude); break;
                    case nameof(GenLocalDate.Year):
                        __result = Current.ProgramState == ProgramState.Playing ? GenDate.Year(ticks, longitude) : 5500;
                        break;
                    case nameof(GenLocalDate.DayOfSeason): __result = GenDate.DayOfSeason(ticks, longitude); break;
                    case nameof(GenLocalDate.DayOfQuadrum): __result = GenDate.DayOfQuadrum(ticks, longitude); break;
                    case nameof(GenLocalDate.DayTick): __result = GenDate.DayTick(ticks, longitude); break;
                    case nameof(GenLocalDate.HourInteger): __result = GenDate.HourInteger(ticks, longitude); break;
                    default: return true;
                }
                return false;
            }
        }

        [HarmonyPatch]
        private static class Patch_GenLocalDate_Map_Float
        {
            private static IEnumerable<MethodBase> TargetMethods() => ResolveMapOverloads(FloatDateMethods);

            private static bool Prefix(Map map, MethodBase __originalMethod, ref float __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                float longitude = SpaceEnvironment.DateLocationFor(map).x;
                int ticks = GenTicks.TicksAbs;
                switch (__originalMethod.Name)
                {
                    case nameof(GenLocalDate.DayPercent): __result = GenDate.DayPercent(ticks, longitude); break;
                    case nameof(GenLocalDate.YearPercent): __result = GenDate.YearPercent(ticks, longitude); break;
                    case nameof(GenLocalDate.HourFloat): __result = GenDate.HourFloat(ticks, longitude); break;
                    default: return true;
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(GenLocalDate), nameof(GenLocalDate.Twelfth), new Type[] { typeof(Map) })]
        private static class Patch_GenLocalDate_Map_Twelfth
        {
            private static bool Prefix(Map map, ref Twelfth __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                __result = GenDate.Twelfth(GenTicks.TicksAbs, SpaceEnvironment.DateLocationFor(map).x);
                return false;
            }
        }

        [HarmonyPatch(typeof(GenLocalDate), nameof(GenLocalDate.Season), new Type[] { typeof(Map) })]
        private static class Patch_GenLocalDate_Map_Season
        {
            private static bool Prefix(Map map, ref Season __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                __result = GenDate.Season(GenTicks.TicksAbs, SpaceEnvironment.DateLocationFor(map));
                return false;
            }
        }

        [HarmonyPatch]
        private static class Patch_GenLocalDate_LocationForDate_Thing
        {
            private static MethodBase target;

            private static bool Prepare()
            {
                target = AccessTools.Method(typeof(GenLocalDate), "LocationForDate", new Type[] { typeof(Thing) });
                if (target != null) return true;
                Log.Warning("[DeltaV] GenLocalDate.LocationForDate(Thing) not found; Thing/Pawn date compatibility is disabled.");
                return false;
            }

            private static MethodBase TargetMethod() => target;

            private static bool Prefix(Thing thing, ref Vector2 __result)
            {
                Map map = thing?.MapHeld;
                if (!SpaceEnvironment.IsDeltaVPocket(map)) return true;
                __result = SpaceEnvironment.DateLocationFor(map);
                return false;
            }
        }

        [HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
        private static class Patch_WeatherWorker_CurSkyTarget
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            {
                return ReplaceMapTileGetter(instructions, __originalMethod, 1);
            }
        }

        [HarmonyPatch(typeof(WorldGrid), nameof(WorldGrid.LongLatOf))]
        private static class Patch_WorldGrid_LongLatOf
        {
            private static bool Prefix(PlanetTile tile, ref Vector2 __result)
            {
                Map current = Find.CurrentMap;
                if (tile.Valid || !SpaceEnvironment.IsDeltaVPocket(current)) return true;
                __result = SpaceEnvironment.DateLocationFor(current);
                return false;
            }
        }

        // ---- 星球层语义 ----
        // 原版按 X.Map.Tile.LayerDef 决定太空分支。以下 transpiler 让口袋图在这些消费点
        // 取 TileFor 的轨道格，与真实轨道图走同一分支；数字是各方法内 Map.Tile 的读取数。

        /// <summary>游戏条件的星球层过滤：轨道层只放行白名单条件，世界级耀斑等不再作用于口袋图。</summary>
        [HarmonyPatch(typeof(GameCondition), nameof(GameCondition.MapExcludedByFilter))]
        private static class Patch_GameCondition_MapExcludedByFilter
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 5);
        }

        /// <summary>条件提示的层名与当地时间：被所在层排除的条件在提示里写明不影响轨道层。</summary>
        [HarmonyPatch(typeof(GameCondition), nameof(GameCondition.TooltipString), MethodType.Getter)]
        private static class Patch_GameCondition_TooltipString
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 3);
        }

        /// <summary>基因的太空判定；NotInSpace 子类对结果取反，经同一处覆盖。</summary>
        [HarmonyPatch(typeof(ConditionalStatAffecter_InSpace), nameof(ConditionalStatAffecter_InSpace.Applies))]
        private static class Patch_ConditionalStatAffecter_InSpace_Applies
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 1);
        }

        /// <summary>心理崩溃的星球层白名单。</summary>
        [HarmonyPatch(typeof(MentalBreakWorker), nameof(MentalBreakWorker.BreakCanOccur))]
        private static class Patch_MentalBreakWorker_BreakCanOccur
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 2);
        }

        /// <summary>头衔许可的星球层黑名单；原版方法非公开，按真实名称绑定。</summary>
        [HarmonyPatch(typeof(RoyalTitlePermitWorker), "AidDisabled_NewTemp")]
        private static class Patch_RoyalTitlePermitWorker_AidDisabled
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 1);
        }

        /// <summary>可操作建筑的星球层白名单（原版迫击炮仅限地表）：检查面板。</summary>
        [HarmonyPatch(typeof(CompMannable), nameof(CompMannable.CompInspectStringExtra))]
        private static class Patch_CompMannable_CompInspectStringExtra
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 2);
        }

        /// <summary>
        /// 可操作建筑的右键菜单。CompFloatMenuOptions 是迭代器，读取位于编译器生成的
        /// MoveNext 而非存根方法，目标因此是状态机的 MoveNext；解析不到时由 Prepare
        /// 跳过，不让 Harmony 报目标缺失。
        /// </summary>
        [HarmonyPatch]
        private static class Patch_CompMannable_CompFloatMenuOptions
        {
            private static MethodBase TargetMethod()
            {
                MethodInfo stub = AccessTools.Method(typeof(CompMannable), nameof(CompMannable.CompFloatMenuOptions));
                Type machine = stub?.GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>()?.StateMachineType;
                return machine == null ? null : AccessTools.Method(machine, "MoveNext");
            }

            private static bool Prepare() => TargetMethod() != null;

            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 2);
        }

        /// <summary>仪式的星球层黑白名单（原版天灯节在轨道禁用）。</summary>
        [HarmonyPatch(typeof(RitualBehaviorWorker), nameof(RitualBehaviorWorker.CanStartRitualNow))]
        private static class Patch_RitualBehaviorWorker_CanStartRitualNow
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 2);
        }

        /// <summary>灵能仪式的星球层白名单；口袋图本就禁止施放，这里只让层判定与轨道图一致。</summary>
        [HarmonyPatch(typeof(GameComponent_PsychicRitualManager), nameof(GameComponent_PsychicRitualManager.CanInvoke))]
        private static class Patch_GameComponent_PsychicRitualManager_CanInvoke
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 4);
        }

        /// <summary>风力发电机的层名提示；是否发电由生态群系的真空判定，口袋图本就不发电。</summary>
        [HarmonyPatch(typeof(CompPowerPlantWind), nameof(CompPowerPlantWind.CompInspectStringExtra))]
        private static class Patch_CompPowerPlantWind_CompInspectStringExtra
        {
            private static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
                => ReplaceMapTileGetter(instructions, __originalMethod, 1);
        }

        /// <summary>「太空居所」信条读 p.Tile，口袋图无效时被判为行星居所；口袋图恒在轨道。</summary>
        [HarmonyPatch(typeof(ThoughtWorker_Precept_SpaceHabitat), "ShouldHaveThought")]
        private static class Patch_ThoughtWorker_Precept_SpaceHabitat
        {
            private static bool Prefix(Pawn p, ref ThoughtState __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(p.MapHeld)) return true;
                if (!ModsConfig.OdysseyActive || !p.SpawnedOrAnyParentSpawned) return true;
                __result = ThoughtState.ActiveAtStage(1);
                return false;
            }
        }

        /// <summary>观星报告：1.6 读 Map.Tile、1.7 读 TileInfo.Layer，两版对口袋图都判为非太空。</summary>
        [HarmonyPatch(typeof(JobDriver_Skygaze), nameof(JobDriver_Skygaze.GetReport))]
        private static class Patch_JobDriver_Skygaze_GetReport
        {
            private static bool Prefix(JobDriver __instance, ref string __result)
            {
                if (!SpaceEnvironment.IsDeltaVPocket(__instance.pawn?.Map)) return true;
                __result = "Stargazing".Translate();
                return false;
            }
        }
    }
}

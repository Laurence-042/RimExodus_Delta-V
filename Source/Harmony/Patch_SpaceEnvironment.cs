using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 原版环境 API 把 Map.Tile 同时当作世界占位和地理采样点；Delta-V 只替换
    /// 后一种语义。绝不 patch Map.Tile getter 本身，避免污染 FindMap/降落/世界对象身份。
    /// </summary>
    public static class Patch_SpaceEnvironment
    {
        private static readonly MethodInfo MapTileGetter = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile));
        private static readonly MethodInfo EnvironmentTileFor = AccessTools.Method(typeof(SpaceEnvironment), nameof(SpaceEnvironment.TileFor));

        private static IEnumerable<CodeInstruction> ReplaceMapTileGetter(
            IEnumerable<CodeInstruction> instructions, MethodBase original, int expected)
        {
            var list = new List<CodeInstruction>(instructions);
            var matches = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction instruction = list[i];
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && Equals(instruction.operand, MapTileGetter))
                {
                    matches.Add(i);
                }
            }
            if (matches.Count != expected)
            {
                Log.Warning($"[DeltaV] Environment patch signature drift in {original.DeclaringType?.FullName}.{original.Name}: found {matches.Count} Map.Tile call(s), expected {expected}; leaving the method unchanged.");
                return list;
            }
            foreach (int index in matches)
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = EnvironmentTileFor;
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
    }
}

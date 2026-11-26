using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeltaV;
using HarmonyLib;
using RimWorld;
using Verse;

internal static partial class Program
{
    /// <summary>
    /// 口袋图星球层补丁的真实 IL 检查。对当前版本的游戏程序集逐个解析 Patch_SpaceEnvironment 与
    /// 驾驶台两个补丁类的目标并运行生产 transpiler，断言 Map.Tile 读取全部被替换、无残留；没有
    /// transpiler 的补丁只断言目标存在。行为本身需要真实宇宙状态，留待游戏内验收。
    /// </summary>
    static void EnvironmentLayerChecks()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var mapTile = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Tile));
        var replacements = new[]
        {
            AccessTools.Method(typeof(SpaceEnvironment), nameof(SpaceEnvironment.TileFor)),
            AccessTools.Method(typeof(ShipFlightTransitionService), nameof(ShipFlightTransitionService.StrategicSelectionTile))
        };
        // 编译器为迭代器生成的嵌套类型不是补丁类。
        var patchTypes = typeof(Patch_SpaceEnvironment).GetNestedTypes(BindingFlags.NonPublic)
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .Concat(new[]
            {
                typeof(Patch_CompPilotConsole_StartChoosingDestination_WorldJump),
                typeof(Patch_CompPilotConsole_InspectRange_StrategicLayer)
            });
        int transpiled = 0, targeted = 0;
        foreach (Type patchType in patchTypes)
        {
            MethodInfo transpiler = patchType.GetMethod("Transpiler", flags);
            MethodInfo targetMethod = patchType.GetMethod("TargetMethod", flags);
            MethodInfo targetMethods = patchType.GetMethod("TargetMethods", flags);
            var targets = new List<MethodBase>();
            if (targetMethod != null)
            {
                MethodInfo prepare = patchType.GetMethod("Prepare", flags);
                Check(prepare == null || (bool)prepare.Invoke(null, null), $"{patchType.Name}: Prepare");
                targets.Add(targetMethod.Invoke(null, null) as MethodBase);
            }
            else if (targetMethods != null)
            {
                foreach (object target in (IEnumerable)targetMethods.Invoke(null, null)) targets.Add(target as MethodBase);
            }
            else
            {
                HarmonyMethod attributes = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patchType));
                Check(attributes?.declaringType != null && attributes.methodName != null,
                    $"{patchType.Name}: Harmony target attributes");
                targets.Add(attributes.methodType == MethodType.Getter
                    ? AccessTools.PropertyGetter(attributes.declaringType, attributes.methodName)
                    : AccessTools.Method(attributes.declaringType, attributes.methodName, attributes.argumentTypes));
            }

            foreach (MethodBase original in targets)
            {
                string where = $"{patchType.Name} -> {original?.DeclaringType?.FullName}.{original?.Name}";
                Check(original != null, $"{patchType.Name}: target resolves on this game version");
                targeted++;
                var before = PatchProcessor.GetOriginalInstructions(original).ToList();
                int reads = before.Count(i => IsEnvironmentCall(i) && Equals(i.operand, mapTile));
                if (transpiler == null) continue;
                var after = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { before, original })).ToList();
                int left = after.Count(i => IsEnvironmentCall(i) && Equals(i.operand, mapTile));
                int replaced = after.Count(i => IsEnvironmentCall(i) && replacements.Contains(i.operand));
                Check(reads > 0, $"{where}: expected at least one Map.Tile read");
                Check(after.Count == before.Count, $"{where}: {after.Count} instruction(s), expected {before.Count}");
                Check(left == 0 && replaced == reads, $"{where}: {reads} Map.Tile read(s), {replaced} replaced, {left} left");
                transpiled++;
            }
        }
        Check(transpiled >= 13, $"environment layer patches: {transpiled} transpiler target(s), expected at least 13");
        Console.WriteLine($"Environment layer: {transpiled} transpiler target(s) fully replaced, {targeted} patch target(s) resolve on this game version. In-game behaviour not covered.");
    }

    static bool IsEnvironmentCall(CodeInstruction instruction)
        => instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 分帧路径不调用 MapGenerator.GenerateMap 方法体，因此在等价 finally 之后尽力复放第三方 Postfix。
    /// Prefix/Transpiler 无法脱离原方法安全复放，只报告一次明确兼容警告。
    /// </summary>
    internal static class GenerateMapPostfixReplay
    {
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static readonly MethodInfo GenerateMapMethod = AccessTools.Method(typeof(MapGenerator),
            nameof(MapGenerator.GenerateMap));

        internal static void Replay(Map map, object[] originalArgs)
        {
            Patches info = GenerateMapMethod == null ? null : Harmony.GetPatchInfo(GenerateMapMethod);
            if (info == null) return;

            WarnUnsupported(info.Prefixes, "Prefix");
            WarnUnsupported(info.Transpilers, "Transpiler");
            foreach (Patch patch in info.Postfixes)
            {
                if (patch.owner == "RimExodus.DeltaV") continue;
                MethodInfo method = patch.PatchMethod;
                if (method == null || !method.IsStatic) continue;
                try
                {
                    object[] args = BuildArguments(method, map, originalArgs);
                    if (args == null)
                    {
                        WarnOnce(patch.owner + ":postfix:" + method.Name,
                            $"cannot safely bind GenerateMap postfix {patch.owner}:{method.DeclaringType?.FullName}.{method.Name}; skipped");
                        continue;
                    }
                    method.Invoke(null, args);
                }
                catch (Exception ex)
                {
                    WarnOnce(patch.owner + ":postfix-error:" + method.Name,
                        $"GenerateMap postfix replay failed for {patch.owner}:{method.Name}: {ex.GetBaseException()}");
                }
            }
        }

        private static object[] BuildArguments(MethodInfo patch, Map result, object[] originals)
        {
            ParameterInfo[] source = GenerateMapMethod.GetParameters();
            ParameterInfo[] target = patch.GetParameters();
            var args = new object[target.Length];
            for (int i = 0; i < target.Length; i++)
            {
                ParameterInfo p = target[i];
                Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                string name = p.Name;
                if (name == "__result") args[i] = result;
                else if (name == "__instance") args[i] = null;
                else if (name == "__originalMethod") args[i] = GenerateMapMethod;
                else if (name == "__runOriginal") args[i] = true;
                else if (name == "__exception") args[i] = null;
                else if (name == "__args") args[i] = originals;
                // __state 只能由该 owner 的 Prefix 产生；分帧路径未执行 Prefix，不能伪造。
                else if (name == "__state") return null;
                else if (name != null && name.StartsWith("___", StringComparison.Ordinal))
                {
                    FieldInfo field = AccessTools.Field(typeof(MapGenerator), name.Substring(3));
                    if (field == null) return null;
                    args[i] = field.GetValue(null);
                }
                else
                {
                    int index = Array.FindIndex(source, x => x.Name == name);
                    if (index < 0 && name != null && name.StartsWith("__", StringComparison.Ordinal)
                        && int.TryParse(name.Substring(2), out int positional)) index = positional;
                    if (index < 0 || index >= originals.Length) return null;
                    object value = originals[index];
                    if (value != null && type != null && !type.IsInstanceOfType(value)) return null;
                    args[i] = value;
                }
            }
            return args;
        }

        private static void WarnUnsupported(IEnumerable<Patch> patches, string kind)
        {
            foreach (Patch patch in patches.Where(p => p.owner != "RimExodus.DeltaV"))
            {
                WarnOnce(patch.owner + ":" + kind,
                    $"{kind} patches on MapGenerator.GenerateMap owned by '{patch.owner}' cannot be replayed " +
                    "for incremental generation; its completed-map behavior may differ.");
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key)) Log.Warning("[DeltaV] " + message);
        }
    }
}

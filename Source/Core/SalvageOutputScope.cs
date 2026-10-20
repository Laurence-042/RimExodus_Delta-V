using System;
using Verse;

namespace DeltaV
{
    // 仅包围本次采矿出料调用；原版负责材料和落点，作用域只接收实际放置结果。
    internal sealed class SalvageOutputScope : IDisposable
    {
        [ThreadStatic] private static SalvageOutputScope current;
        private readonly SalvageOutputScope previous;
        private readonly Thing launcher;
        private readonly Map map;
        private bool failed;

        internal SalvageOutputScope(Thing launcher)
        {
            this.launcher = launcher;
            map = launcher.Map;
            previous = current;
            current = this;
        }

        internal static void PlacementFinished(Map map, bool success)
        {
            if (!success && current != null && current.map == map)
                current.failed = true;
        }

        // GenLeaving 在失败后打印同一类警告；本作用域改用玩家提示，其他调用照常。
        internal static bool ReplacesWarning(string text) => current?.failed == true && text != null
            && (text.StartsWith("Failed to place all leavings for destroyed thing ", StringComparison.Ordinal)
                || text.StartsWith("Failed to place all leavings for removed terrain ", StringComparison.Ordinal));

        public void Dispose()
        {
            current = previous;
            if (failed)
                Current.Game?.GetComponent<UniverseManager>()?.Weapons.NotifySalvageOutputLost(launcher);
        }
    }
}

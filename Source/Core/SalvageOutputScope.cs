using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DeltaV
{
    // 仅在本次同步切割中接收原版新生成的材料。
    internal sealed class SalvageOutputScope : IDisposable
    {
        [ThreadStatic] private static SalvageOutputScope current;
        private readonly SalvageOutputScope previous;
        private readonly Thing launcher;
        private readonly Map map;
        private readonly object cutTarget;
        private readonly IntVec3 cell;
        private readonly float yieldFactor;
        private List<Thing> returns;
        private bool collecting;
        private Building removing;
        private bool failed;

        internal SalvageOutputScope(Thing launcher) : this(launcher, launcher.Map, null, default, 1f) { }

        internal SalvageOutputScope(Thing launcher, Map map, Thing building, float yieldFactor)
            : this(launcher, map, building, default, yieldFactor) { }

        internal SalvageOutputScope(Thing launcher, Map map, TerrainDef terrain, IntVec3 cell, float yieldFactor)
            : this(launcher, map, (object)terrain, cell, yieldFactor) { }

        private SalvageOutputScope(Thing launcher, Map map, object cutTarget, IntVec3 cell, float yieldFactor)
        {
            this.launcher = launcher;
            this.map = map;
            this.cutTarget = cutTarget;
            this.cell = cell;
            this.yieldFactor = yieldFactor;
            previous = current;
            current = this;
        }

        internal IReadOnlyList<Thing> CollectReturns()
        {
            returns = new List<Thing>();
            collecting = true;
            try
            {
                if (cutTarget is Building building)
                    GenLeaving.DoLeavingsFor(building, map, DestroyMode.Deconstruct);
                else
                    GenLeaving.DoLeavingsFor((TerrainDef)cutTarget, cell, map);
                return returns;
            }
            finally { collecting = false; }
        }

        internal void RemoveBuilding(Building building)
        {
            removing = building;
            try { building.Destroy(DestroyMode.Deconstruct); }
            finally { removing = null; }
        }

        internal static bool IsRemoving(Thing target, Map map, DestroyMode mode) =>
            mode == DestroyMode.Deconstruct && current?.removing != null && current.removing == target && current.map == map;

        internal static bool TryCollect(ThingOwner owner, Thing item, Map map, object target, IntVec3 cell = default)
        {
            var scope = current;
            if (scope?.collecting != true || scope.map != map || scope.cutTarget != target
                || target is TerrainDef && scope.cell != cell || !owner.Remove(item)) return false;
            scope.returns.Add(item);
            return true;
        }

        internal static float BuildingYieldFactor(Thing building) =>
            current?.cutTarget == building ? current.yieldFactor : 1f;

        internal static float AdjustTerrainReturn(float fraction, TerrainDef terrain, IntVec3 cell, Map map) =>
            current?.cutTarget == terrain && current.map == map && current.cell == cell
                ? fraction * current.yieldFactor : fraction;

        internal static void PlacementFinished(Map map, bool success)
        {
            if (!success && current?.launcher != null && current.launcher.Map == map)
                current.failed = true;
        }

        // GenLeaving 在失败后打印同一类警告；本作用域改用玩家提示，其他调用照常。
        internal static bool ReplacesWarning(string text) => current?.failed == true && text != null
            && (text.StartsWith("Failed to place all leavings for destroyed thing ", StringComparison.Ordinal)
                || text.StartsWith("Failed to place all leavings for removed terrain ", StringComparison.Ordinal));

        public void Dispose()
        {
            current = previous;
            if (returns != null)
                foreach (Thing item in returns)
                    if (!item.Destroyed && !item.Spawned && item.holdingOwner == null)
                        item.Destroy(DestroyMode.Vanish);
            if (failed)
                Current.Game?.GetComponent<UniverseManager>()?.Weapons.NotifySalvageOutputLost(launcher);
        }
    }
}

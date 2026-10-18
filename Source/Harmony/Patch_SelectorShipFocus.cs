using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 点击选中物 → 侧边栏聚焦其归属船：Selector.Select 是原版单击/框选/双击/殖民者栏
    /// 跳转的单一汇入口（SelectInternal 内部已完成跨图切换与去重），postfix 在此只读
    /// 点击物的船体或基架归属改面板选择，包括无主台残骸与未占有基架；不改原版选择
    /// 行为，不在未纳管的 RimExodus 地表生效。
    /// </summary>
    [HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
    public static class Patch_SelectorShipFocus
    {
        [HarmonyPostfix]
        public static void Postfix(object obj)
        {
            if (obj is Thing thing)
            {
                if (Current.Game?.GetComponent<UniverseManager>()?.FrameOf(thing.Map) == null) return;
                ShipBase ship = ShipRegistry.ShipOf(thing);
                if (ship.IsValid)
                {
                    ShipStatusPanel.SelectShip(ship);
                }
                else
                {
                    SubstructureOwnership ownership = SubstructureOwnership.TryGet(thing.Map);
                    foreach (IntVec3 cell in thing.OccupiedRect())
                    {
                        if (!SubstructureOwnership.HasFoundation(thing.Map, cell)) continue;
                        ShipStatusPanel.SelectStructure(thing.Map,
                            new DriveSubject(0, ownership?.OwnerAt(cell) ?? SubstructureOwnership.Unassigned));
                        break;
                    }
                }
            }
        }
    }
}

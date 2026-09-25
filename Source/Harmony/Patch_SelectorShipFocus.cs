using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 点击选中物 → 侧边栏聚焦其归属船：Selector.Select 是原版单击/框选/双击/殖民者栏
    /// 跳转的单一汇入口（SelectInternal 内部已完成跨图切换与去重），postfix 在此只读
    /// 点击物的归属域（ShipRegistry.ShipByThing）改面板「当前查看/操作的船」，不改任何
    /// 原版选择行为。非 Thing（Zone/Plan）、无归属组件的图、未占有格与无主台残骸域
    /// 自然落空——不在 RimExodus 地表生效。
    /// </summary>
    [HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
    public static class Patch_SelectorShipFocus
    {
        [HarmonyPostfix]
        public static void Postfix(object obj)
        {
            if (obj is Thing thing)
            {
                Ship ship = ShipRegistry.ShipByThing(thing);
                if (ship.IsValid)
                {
                    ShipStatusPanel.SelectShip(ship);
                }
            }
        }
    }
}

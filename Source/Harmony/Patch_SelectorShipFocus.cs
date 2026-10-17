using HarmonyLib;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 点击选中物 → 侧边栏聚焦其归属船：Selector.Select 是原版单击/框选/双击/殖民者栏
    /// 跳转的单一汇入口（SelectInternal 内部已完成跨图切换与去重），postfix 在此只读
    /// 点击物的船体归属（ShipRegistry.ShipOf）改面板「当前查看/操作的船」，不改任何
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
                if (Current.Game?.GetComponent<UniverseManager>()?.FrameOf(thing.Map) == null) return;
                ShipBase ship = ShipRegistry.ShipOf(thing);
                if (ship.IsValid)
                {
                    ShipStatusPanel.SelectShip(ship);
                }
            }
        }
    }
}

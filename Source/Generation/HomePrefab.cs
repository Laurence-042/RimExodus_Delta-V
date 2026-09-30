using System;
using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// prefab 导出/导入接口挂点（当前为空实现，调用即抛 NotImplementedException）：
    /// - ExportHomeArea：调试动作入口，将来把当前聚焦图居住区导出为 prefab。
    /// - SpawnInto：测试场景布局的首选数据源；未实现、prefab 缺失等一切失败都以异常
    ///   表达，由调用方（ShipTestScene）捕获后回退程序生成布局（ShipProceduralLayout）。
    /// </summary>
    public static class HomePrefab
    {
        public static void ExportHomeArea(string name)
        {
            throw new NotImplementedException("prefab export is not implemented yet.");
        }

        public static void SpawnInto(Faction faction, Map map, IntVec3 position)
        {
            throw new NotImplementedException("prefab import is not implemented yet.");
        }
    }
}

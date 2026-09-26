using Verse;

namespace DeltaV
{
    /// <summary>
    /// 跳帮 shuttle 的数值档位（modExtension，2026-09-27）：canBoard 与出力覆盖放
    /// modExtensions 而非 comps——comps li 追加会与原版 PassengerShuttle patch 的
    /// CompSpaceShuttle 实例重复（TryGetComp 取首个），modExtensions 是允许追加的
    /// 字段族且原版无此项。读取唯一入口 = CompSpaceShuttle.CanBoard/EffectiveThrust。
    /// </summary>
    public class DefModExtension_BoardingProfile : DefModExtension
    {
        /// <summary>具备侵入式对接能力（跳帮特化）。</summary>
        public bool canBoard;

        /// <summary>虚拟推进器出力覆盖（≤0 = 用 comp 默认值）。</summary>
        public float thrusterThrust = -1f;
    }
}

using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 口袋图的持久元数据。弹体图即使处于空闲池、或跨过存读档而失去运行时
    /// 容器，也必须仍可从普通飞船口袋图区分，以便 UI 隐藏和读档回收。
    /// </summary>
    public sealed class DeltaVPocketMapMetadata : CustomMapComponent
    {
        public bool projectileMap;

        public DeltaVPocketMapMetadata(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref projectileMap, "projectileMap", false);
        }
    }
}

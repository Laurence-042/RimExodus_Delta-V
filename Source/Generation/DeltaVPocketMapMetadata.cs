using Verse;

namespace DeltaV
{
    /// <summary>
    /// 池生成图的持久用途标记。读档只恢复 tag 和真实内容对应的租约；
    /// 空闲队列由内容重建，不按地图尺寸猜测用途。
    /// </summary>
    public sealed class DeltaVPocketMapMetadata : CustomMapComponent
    {
        public bool pooledMap;
        public SpaceObjectTag poolTag;
        public bool worksite;

        public DeltaVPocketMapMetadata(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pooledMap, "pooledMap", false);
            Scribe_Values.Look(ref poolTag, "poolTag", (SpaceObjectTag)0);
            Scribe_Values.Look(ref worksite, "worksite", false);
        }
    }
}

using Verse;

namespace DeltaV
{
    /// <summary>
    /// 持久记录真实口袋图的舱外作业用途。
    /// </summary>
    public sealed class DeltaVPocketMapMetadata : CustomMapComponent
    {
        public bool worksite;

        public DeltaVPocketMapMetadata(Map map) : base(map)
        {
        }

        /// <summary>
        /// 作业用途属于 Map，而不是承载它的 SpaceObject。Delta-V 口袋图由生成器
        /// 预装本组件；原版 Space worldtile 图没有该配置，战略降落完成时必须按需
        /// 补入同一组件，避免另建一套降落状态。
        /// </summary>
        internal static DeltaVPocketMapMetadata Ensure(Map map)
        {
            if (map == null || map.Disposed) return null;
            DeltaVPocketMapMetadata metadata = map.GetComponent<DeltaVPocketMapMetadata>();
            if (metadata != null) return metadata;
            metadata = new DeltaVPocketMapMetadata(map);
            map.components.Add(metadata);
            return metadata;
        }

        internal static bool MarkWorksite(Map map)
        {
            DeltaVPocketMapMetadata metadata = Ensure(map);
            if (metadata == null) return false;
            metadata.worksite = true;
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worksite, "worksite", false);
        }
    }
}

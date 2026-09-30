using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// prefab spawn 完成后的展开上下文：整片内容（foundation/terrain/things/roofs）已就位，
    /// marker 之间可以依赖彼此的产物。Universe 可为 null（非宇宙图上的 prefab 重建），
    /// 需要物化其他 SpaceObject 的展开器以此为准入判断。
    /// </summary>
    public sealed class PrefabSpawnContext
    {
        public Map Map { get; }
        public Faction Faction { get; }

        /// <summary>结构归属 ID；0 = 本次 spawn 未指定归属（地面基地等无基架域场景）。</summary>
        public int OwnerId { get; }

        /// <summary>prefab 放置的 root 角格（与 SpawnPrefab 内部使用的同一 root）。</summary>
        public IntVec3 Root { get; }
        public Rot4 Rotation { get; }
        public UniverseManager Universe { get; }

        public PrefabSpawnContext(Map map, Faction faction, int ownerId, IntVec3 root, Rot4 rotation,
            UniverseManager universe)
        {
            Map = map;
            Faction = faction;
            OwnerId = ownerId;
            Root = root;
            Rotation = rotation;
            Universe = universe;
        }
    }

    /// <summary>
    /// 标记建筑基类：MapPrefab.Spawn 在整片 prefab 放置完成后统一触发 Expand。
    /// 新的展开器（生成 pawn、子结构、物化 SpaceObject 等）= 新子类 + 对应
    /// CompProperties，发现机制（TryGetComp&lt;CompPrefabMarker&gt; 的基类匹配）
    /// 零修改纳入，不写家族特判。
    /// 不在 PostSpawnSetup 里展开：单个 building spawn 时整片 prefab 尚未就位，
    /// marker 依赖的相邻内容可能还不存在。
    /// </summary>
    public class CompPrefabMarker : ThingComp
    {
        public virtual void Expand(PrefabSpawnContext context)
        {
        }
    }
}

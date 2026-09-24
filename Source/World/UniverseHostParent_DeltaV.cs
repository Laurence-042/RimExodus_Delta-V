using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 专用宿主口袋图的 WorldObject。
    ///
    /// 职责只有一个：作为所有 Delta-V 口袋图（飞船/POI）的 PocketMapParent.sourceMap，
    /// 承接原版 storyteller/财富/移除级联从口袋图重定向到 sourceMap 的引擎契约。
    ///
    /// 纪律（AGENTS §10/§11）：
    /// - Tile 恒 PlanetTile.Invalid——不占轨道 tile，轨道 tile 只属于真实降落图；
    /// - 只存在于 Find.World.pocketMaps，绝不进 Find.WorldObjects（无 tile WorldObject
    ///   的 SpawnSetup 会在 FastTileFinder 越界崩溃）；
    /// - 永不被聚焦、永不出现在世界视图、tick 常冻结（Patch_PoiDormancy）；
    /// - sourceMap 自身为 null；宿主被排除在事件目标之外，null 不会被解引用。
    /// </summary>
    public class UniverseHostParent_DeltaV : PocketMapParent
    {
        /// <summary>
        /// 防御性覆写：宿主永不聚焦（renderWorld=false），本属性正常不被消费；
        /// 若被查询绝不回落 base（Invalid tile 会 GetTileCenter(-1) 每帧红字），
        /// 返回主参考船球面点作为最接近的合理值。
        /// </summary>
        public override Vector3 WorldCameraPosition
        {
            get
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                Ship primary = universe == null ? Ship.Invalid : universe.PrimaryPlayerShip();
                return primary.IsValid
                    ? OrbitSphere.SpherePoint(ShipRegistry.ShipPose(primary).position)
                    : Vector3.zero;
            }
        }
    }
}

using UnityEngine;

namespace DeltaV
{
    /// <summary>
    /// 开火后效接口（太空战架构 §5.8，预留）：发射时刻回调——将来「震晕范围内乘员」
    /// 等效果经此接入。阶段5 只定义接口、无实现 def；实现方挂在武器 def 的
    /// modExtension/comps 上，由发射路径（SpaceWeaponService.TryFire）在发射成功后
    /// 调用 NotifyFired。
    /// </summary>
    public interface IFireEffect
    {
        void NotifyFired(CompSpaceWeapon weapon, SpaceObject container, Vector3 muzzle);
    }
}

using RimWorld;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// 玩家对当前对象的稳定访问分类。分类由实时派系关系与公共标记派生，
    /// 不保存第二份权限状态；具体功能再选择它允许的等级。
    /// </summary>
    internal enum PlayerAccessLevel
    {
        Hostile,
        Neutral,
        Public,
        Player
    }

    internal static class PlayerAccess
    {
        internal static PlayerAccessLevel For(Faction owner, bool isPublic)
        {
            Faction player = Faction.OfPlayerSilentFail;
            if (player != null && owner == player) return PlayerAccessLevel.Player;
            if (player != null && owner?.HostileTo(player) == true) return PlayerAccessLevel.Hostile;
            if (isPublic) return PlayerAccessLevel.Public;
            return PlayerAccessLevel.Neutral;
        }

        internal static PlayerAccessLevel For(ShipBase ship) => ship?.IsValid == true
            ? For(ship.Faction, ship.IsPublicFacility)
            : PlayerAccessLevel.Neutral;

        internal static PlayerAccessLevel For(Thing thing)
        {
            CompDeltaVPilotConsole publicConsole = thing?.Spawned == true
                ? PublicFacility.ConsoleAt(thing.Map, thing.Position)
                : null;
            return For(thing?.Faction ?? publicConsole?.parent.Faction, publicConsole != null);
        }

        /// <summary>驾驶、配置与公共设施管理：只允许玩家所有或明确公开的对象。</summary>
        internal static bool CanOperate(PlayerAccessLevel access) =>
            access == PlayerAccessLevel.Player || access == PlayerAccessLevel.Public;

        /// <summary>通行、搬运与转移：所有非敌对对象均可交互。</summary>
        internal static bool CanInteract(PlayerAccessLevel access) => access != PlayerAccessLevel.Hostile;

        internal static bool CanInteract(PlayerAccessLevel source, PlayerAccessLevel target) =>
            CanInteract(source) && CanInteract(target);

        /// <summary>骇入是从可交互一端占领敌对目标，不是中立设施的使用前置。</summary>
        internal static bool CanHack(PlayerAccessLevel source, PlayerAccessLevel target) =>
            CanInteract(source) && target == PlayerAccessLevel.Hostile;

        internal static bool CanOperate(Thing thing) => CanOperate(For(thing));
        internal static bool CanInteract(Thing thing) => CanInteract(For(thing));
        internal static bool CanInteract(Thing source, Thing target) => CanInteract(For(source), For(target));
        internal static bool CanHack(Thing source, Thing target) => CanHack(For(source), For(target));
    }
}

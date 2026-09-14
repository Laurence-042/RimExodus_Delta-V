using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Delta-V 驾驶台（阶段3）：正式进入/退出驾驶的入口 + 主驾驶台规则。
    ///
    /// 主驾驶台规则（story.md「固定轨道/建造模式」）：第一台自动成为主驾驶台，
    /// 第二台不会取代主台，除非玩家显式用 gizmo 设置；返回飞行时飞船朝向跟随
    /// 主驾驶台的朝向（DockController 消费）。主台被拆除时转交给船上最老的一台。
    ///
    /// 本 Comp 由 XML patch 追加到原版 PilotConsole，与原版 CompPilotConsole 共存：原版
    /// Gravship 继续按 ThingDefOf.PilotConsole 识别设施与发射方向，本 Comp 只在 Delta-V
    /// 飞船上追加自己的 gizmo；Toggle 快捷键保留（无驾驶台的船仍可用，dev 便利）。
    /// </summary>
    public class CompProperties_DeltaVPilotConsole : CompProperties
    {
        public CompProperties_DeltaVPilotConsole()
        {
            compClass = typeof(CompDeltaVPilotConsole);
        }
    }

    public class CompDeltaVPilotConsole : ThingComp
    {
        private UniverseManager Universe => Current.Game?.GetComponent<UniverseManager>();

        private ShipState Ship
        {
            get
            {
                UniverseManager universe = Universe;
                Map map = parent.Map;
                ShipState ship = universe?.ShipOnMap(map);
                if (ship != null)
                {
                    return ship;
                }
                // 停靠船的 SpaceObject 不再持有口袋图；通过原版 GravEngine.ValidSubstructure
                // 识别宿主图上的实际船体，使停靠期新建的第二台驾驶台也能获得 gizmo，
                // 同时避免把能力扩散到宿主图上的无关建筑或普通地表驾驶台。
                return universe?.Ships.FirstOrDefault(s => DockController.ContainsDockedThing(s, parent));
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (respawningAfterLoad)
            {
                return;
            }
            ShipState ship = Ship;
            if (ship == null)
            {
                return;
            }
            EnsurePrimaryConsole(ship);
        }

        /// <summary>
        /// 场景盖章发生在 ShipState 注册之前，Comp.PostSpawnSetup 当时无法认领主台；注册后
        /// 由场景入口补一次。普通游戏中后建造的驾驶台仍由 PostSpawnSetup 自动认领。
        /// </summary>
        public static Building EnsurePrimaryConsole(ShipState ship)
        {
            if (ship == null)
            {
                return null;
            }
            Building current = ship.primaryConsole;
            if (current != null && !current.Destroyed && current.Spawned)
            {
                return current;
            }
            ship.primaryConsole = OldestConsoleOn(ship);
            if (ship.primaryConsole != null)
            {
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"{ship.label}: primary vanilla pilot console auto-assigned ({ship.primaryConsole.Position})");
            }
            return ship.primaryConsole;
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            // 驾驶台可能在口袋图（飞行中）也可能在宿主图（停靠中），按所在图双路径找船。
            UniverseManager universe = Universe;
            ShipState ship = universe?.ShipOnMap(previousMap);
            if (ship == null && universe != null)
            {
                ship = universe.Ships.FirstOrDefault(s => s.docked && s.dockedMap == previousMap
                    && s.primaryConsole == parent);
            }
            if (ship != null && ship.primaryConsole == parent)
            {
                ship.primaryConsole = OldestConsoleOn(ship);
                DeltaVLog.Message(DeltaVLogModule.Core,
                    $"{ship.label}: primary console destroyed; {(ship.primaryConsole == null ? "none left" : "transferred to oldest remaining")}");
            }
            base.PostDestroy(mode, previousMap);
        }

        private static Building OldestConsoleOn(ShipState ship)
        {
            // 停靠中的船上其余驾驶台与被拆台同图（宿主图），从宿主图找；飞行中从口袋图找。
            Map consoleMap = ship.docked ? ship.dockedMap : ship.map;
            if (consoleMap == null)
            {
                return null;
            }
            Building oldest = null;
            foreach (Thing thing in consoleMap.listerThings.ThingsOfDef(ThingDefOf.PilotConsole))
            {
                if (thing.TryGetComp<CompDeltaVPilotConsole>() != null
                    && (!ship.docked || DockController.ContainsDockedThing(ship, thing))
                    && (oldest == null || thing.thingIDNumber < oldest.thingIDNumber))
                {
                    oldest = thing as Building;
                }
            }
            return oldest;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            ShipState ship = Ship;
            if (ship == null)
            {
                yield break;
            }
            UniverseManager universe = Universe;
            if (ship.docked)
            {
                yield return NewAction("DeltaV_LaunchButton".Translate(), TexCommand.Draft,
                    () => DockController.TryLaunch(universe, ship));
            }
            else
            {
                yield return NewAction(
                    universe.pilotShipId == ship.objectId
                        ? "DeltaV_PilotButtonExit".Translate()
                        : "DeltaV_PilotButtonEnter".Translate(),
                    TexCommand.Draft, () => PilotController.Toggle(universe));
            }

            if (ship.primaryConsole != parent)
            {
                yield return NewAction("DeltaV_ConsoleGizmoMakePrimary".Translate(), TexCommand.Install,
                    delegate
                    {
                        ship.primaryConsole = parent as Building;
                        Messages.Message("DeltaV_ConsolePrimarySet".Translate(parent.LabelCap), MessageTypeDefOf.NeutralEvent, false);
                    },
                    "DeltaV_ConsoleGizmoPrimaryTip".Translate());
            }
        }

        private static Command_Action NewAction(string label, Texture2D icon, System.Action action, string tip = null)
        {
            return new Command_Action
            {
                defaultLabel = label,
                defaultDesc = tip ?? label,
                icon = icon,
                action = action,
                hotKey = null
            };
        }
    }
}

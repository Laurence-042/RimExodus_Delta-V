using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class ShipFlightTransitionService
    {
        /// <summary>本次拆分的选择交接；只保存事务输入，不建立第二套观察身份。</summary>
        internal sealed class SplitSelection
        {
            internal readonly Map source;
            internal readonly DriveSubject identity;
            private readonly HashSet<IntVec3> remaining;
            private readonly IntVec3 mainCell;
            private readonly List<(Map map, DriveSubject identity, int count, bool main)> moved =
                new List<(Map, DriveSubject, int, bool)>();
            private bool mainRemaining;

            internal SplitSelection(Map source, DriveSubject identity)
            {
                this.source = source;
                this.identity = identity;
                remaining = new HashSet<IntVec3>(ShipRegistry.StructureCells(source, identity));
                ShipBase ship = ShipRegistry.ShipByIdentity(identity);
                mainRemaining = ship.IsValid && ship.Map == source && remaining.Contains(ship.Anchor.Position);
                mainCell = mainRemaining ? ship.Anchor.Position : IntVec3.Invalid;
            }

            internal void Transferred(HashSet<IntVec3> cells, Map destination, int ownerId)
            {
                int count = cells.Count(remaining.Contains);
                if (count == 0) return;
                bool main = mainRemaining && cells.Contains(mainCell);
                mainRemaining &= !main;
                remaining.ExceptWith(cells);
                // 单建筑船的身份不随宿主基架重新盖章；普通残片使用本次搬运产生的归属。
                DriveSubject next = identity.shuttle ? identity : new DriveSubject(0, ownerId);
                moved.Add((destination, next, count, main));
            }

            internal (Map map, DriveSubject identity) Winner()
            {
                var best = (map: source, identity, count: remaining.Count, main: mainRemaining);
                foreach (var candidate in moved)
                    if (candidate.main && !best.main
                        || candidate.main == best.main && candidate.count > best.count)
                        best = candidate;
                return (best.map, best.identity);
            }
        }

        private sealed class SplitFocus
        {
            internal readonly SplitSelection selection;
            private readonly Vector3 viewUniverse;
            private readonly float zoom;

            private SplitFocus(SpaceObject source, DriveSubject identity)
            {
                selection = new SplitSelection(source.map, identity);
                Vector3 center = Find.CameraDriver.CurrentViewRect.CenterVector3;
                viewUniverse = SpaceTransform.MapPointToUniverse(source, source.map, new Vector2(center.x, center.z));
                zoom = Find.CameraDriver.RootSize;
            }

            internal static SplitFocus Capture(UniverseManager universe, Map map)
            {
                if (Find.CurrentMap != map || universe.FrameOf(map) == null
                    || !ShipStatusPanel.TryGetSelectedStructure(map, out DriveSubject identity)) return null;
                return new SplitFocus(universe.FrameOf(map), identity);
            }

            internal void Complete(UniverseManager universe)
            {
                if (Find.CurrentMap != selection.source) return;
                var winner = selection.Winner();
                SpaceObject frame = universe.FrameOf(winner.map);
                if (frame == null) return;
                ShipStatusPanel.SelectStructure(winner.map, winner.identity);
                if (winner.map != selection.source)
                    universe.SetFocusedWithCamera(frame,
                        SpaceTransform.UniverseToMapPoint(frame, winner.map, viewUniverse), zoom);
            }
        }
    }
}

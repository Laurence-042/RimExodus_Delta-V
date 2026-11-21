using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal sealed partial class SpaceMissionService
    {
        internal delegate void FlightObservation(DriveSubject subject, ShipPhysics.Command command,
            Vector2 force, float torque, float frameAngle, bool forward, bool lateral, bool turn);
        internal event FlightObservation FlightObserved;
        internal event Func<DriveSubject, Map, Action> DockPreparing;
        internal void ObserveFlight(DriveSubject subject, ShipPhysics.Command command, Vector2 force, float torque,
            float angle, bool x, bool z, bool w) => FlightObserved?.Invoke(subject, command, force, torque, angle, x, z, w);
        internal Action BeginDock(DriveSubject subject, Map map)
        {
            Action completed = null;
            if (DockPreparing != null)
                foreach (Func<DriveSubject, Map, Action> listener in DockPreparing.GetInvocationList()) completed += listener(subject, map);
            return completed;
        }
        internal GlobeTarget NavigationTarget(SpaceMission m) =>
            (m.kind == SpaceMissionKind.Combat ? (m.combatComplete ? m.source : m.enemyTarget) : m.destination)?.Navigation;
        internal IEnumerable<(string label, GlobeTarget target)> NavigationTargets()
        {
            if (universe.Tutorial.NavigationTarget is GlobeTarget tutorial)
                yield return ("DeltaV_Tutorial_Title".Translate(), tutorial);
            foreach (var m in Active)
            {
                GlobeTarget target = NavigationTarget(m);
                if (target != null) yield return (m.Title, target);
                if (m.kind != SpaceMissionKind.Combat && m.source != null && !m.source.Navigation.Equals(target))
                    yield return (m.Title + " — " + m.source.label, m.source.Navigation);
            }
        }
        internal void Locate(SpaceMission m)
        {
            if (m == null) return;
            universe.GlobeNavigation.MissionSelection = NavigationTarget(m);
            GlobeNavigationPanel.Open(universe, NavigationTarget(m));
        }
        internal void ChooseNavigation()
        {
            var options = NavigationTargets().Select(n => new FloatMenuOption(n.label, () =>
            {
                universe.GlobeNavigation.MissionSelection = n.target;
                GlobeNavigationPanel.Open(universe, n.target);
            })).ToList();
            if (options.Count > 0) Verse.Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}

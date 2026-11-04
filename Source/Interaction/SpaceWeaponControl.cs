using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public enum WeaponControlSource : byte { Manual, Automatic, PointDefense }
    internal enum WeaponReject : byte
    { None, Invalid, Cooldown, Operating, Power, Ammo, Range, Arc, Alignment, Energy, Safety, Gate, Intercept, Launch, Count }

    /// <summary>Controller intent only. A resolved target is valid for this call, never a held position snapshot.</summary>
    public readonly struct WeaponControlCommand
    {
        public readonly WeaponControlSource Source;
        public readonly bool Trigger, Predict;
        public readonly Vector2? LocalPoint;
        public readonly CombatTarget Target;
        public readonly ResolvedCombatTarget Resolved;
        public WeaponControlCommand(Vector2 localPoint, bool trigger)
        { Source = WeaponControlSource.Manual; Trigger = trigger; Predict = false;
            LocalPoint = localPoint; Target = default; Resolved = default; }
        public WeaponControlCommand(CombatTarget target, ResolvedCombatTarget resolved,
            bool predict, WeaponControlSource source, bool trigger = true)
        { Source = source; Trigger = trigger; Predict = predict; Target = target; Resolved = resolved; LocalPoint = null; }
    }

    public sealed partial class SpaceWeaponService
    {
        public bool ApplyControl(CompSpaceWeapon weapon, SpaceObject container, WeaponControlCommand command)
        {
            if (weapon == null || weapon.executingControl) return Reject(WeaponReject.Invalid, command.Trigger);
            long start = universe.Performance.Start();
            weapon.executingControl = true;
            try { return ExecuteControl(weapon, container, command); }
            finally { weapon.executingControl = false; universe.Performance.Record(PerformanceStage.WeaponExecution, start); }
        }

        private bool ExecuteControl(CompSpaceWeapon weapon, SpaceObject container, WeaponControlCommand command)
        {
            var perf = universe.Performance;
            perf.RecordWeaponCommand(command.Trigger);
            if (container == null || weapon?.parent?.Spawned != true || weapon.parent.Destroyed
                || weapon.Container != container || !weapon.SpaceModeEnabled)
                return Reject(WeaponReject.Invalid, command.Trigger);

            if (weapon.OwnerShip.IsValid && weapon.OwnerShip.EffectiveFireControlMode == ShipFireControlMode.Disabled)
                return Reject(WeaponReject.Gate, command.Trigger);
            if (command.Source == WeaponControlSource.Automatic && weapon.WeaponProps.manualOnly
                || command.Source == WeaponControlSource.PointDefense && !weapon.AllowsPointDefense)
                return Reject(WeaponReject.Gate, command.Trigger);
            long aimStart = perf.Start();
            if (command.LocalPoint.HasValue && !command.Trigger)
            {
                if (weapon.Component<CompSpaceWeaponTraverse>() != null)
                    AimLocal(weapon, command.LocalPoint.Value, SpaceWeaponGeometry.MuzzleMapPoint(weapon));
                RecordAim(perf, command.Source, aimStart);
                return false;
            }
            int skill = 0;
            long readinessStart = perf.Start();
            WeaponReject readiness = command.Trigger ? CheckReadiness(weapon, out skill) : WeaponReject.None;
            if (command.Trigger && (readiness == WeaponReject.None || readiness == WeaponReject.Cooldown))
                weapon.lastBeamTriggerTick = Find.TickManager.TicksGame;
            perf.Record(PerformanceStage.WeaponReadiness, readinessStart);
            bool canFire = command.Trigger && readiness == WeaponReject.None;
            if (command.LocalPoint.HasValue && !canFire)
            {
                if (weapon.Component<CompSpaceWeaponTraverse>() != null)
                    AimLocal(weapon, command.LocalPoint.Value, SpaceWeaponGeometry.MuzzleMapPoint(weapon));
                RecordAim(perf, command.Source, aimStart);
                return Reject(readiness, command.Trigger);
            }

            // Query workspace may be reused; a potential shot gets a ledger isolated from visual callbacks.
            SpaceWeaponEnergy energy = canFire ? weapon.ExecutionEnergy : weapon.QueryEnergy;
            SpaceWeaponParameters parameters = SpaceWeaponBallistics.Read(weapon, container, energy, canFire);
            WeaponAimInfo info = parameters.aim;
            Vector3 aim;
            Vector2 localAim;
            bool interceptPossible = true;
            if (command.LocalPoint.HasValue)
            {
                localAim = command.LocalPoint.Value;
                aim = SpaceTransform.MapPointToUniverse(container, weapon.parent.Map, localAim);
            }
            else
            {
                aim = command.Resolved.Pose.position;
                if (command.Predict && !SpaceWeaponBallistics.TryAim(info, container.pose, command.Resolved.Pose, out aim))
                {
                    if (command.Source == WeaponControlSource.PointDefense)
                    {
                        RecordAim(perf, command.Source, aimStart);
                        return Reject(WeaponReject.Intercept, command.Trigger);
                    }
                    interceptPossible = false;
                    aim = command.Resolved.Pose.position;
                }
                localAim = SpaceTransform.UniverseToMapPoint(container, weapon.parent.Map, aim);
            }
            AimLocal(weapon, localAim, info.MuzzleMap);
            RecordAim(perf, command.Source, aimStart);
            if (!canFire) return Reject(readiness, command.Trigger);
            if (!interceptPossible) return Reject(WeaponReject.Intercept, true);

            if (!weapon.MagazineAvailable(parameters.magazine) || info.Projectile == null)
                return Reject(WeaponReject.Ammo, true);
            // Traverse moved this tick; freeze the physical barrel exit without querying ammo/energy again.
            parameters = SpaceWeaponBallistics.AtCurrentMuzzle(weapon, container, parameters);
            info = parameters.aim;
            long guidanceStart = perf.Start();
            CombatTarget guidance = command.Target;
            if (!guidance.HasLock && info.Guided && command.Source == WeaponControlSource.Manual)
                guidance = ShipFireControl.ManualTargetAlongRay(universe, container, info.Muzzle,
                    OrbitSphere.Offset(info.Muzzle, aim).normalized, weapon.parent.Faction);
            perf.Record(PerformanceStage.WeaponGuidance, guidanceStart);
            var request = new WeaponFireRequest(aim, guidance, command.Source == WeaponControlSource.Manual);
            return new SpaceWeaponShot(universe, weapon, container, request, parameters, skill, localAim,
                command.Resolved.Container, command.Resolved.Container != null && guidance == command.Target).TryExecute();
        }

        private static void RecordAim(DeltaVPerformanceProbe perf, WeaponControlSource source, long start)
        {
            long elapsed = perf.Elapsed(start);
            perf.RecordElapsed(PerformanceStage.WeaponAim, elapsed);
            if (source != WeaponControlSource.PointDefense) perf.RecordElapsed(PerformanceStage.VolleyAim, elapsed);
        }

        private bool Reject(WeaponReject reason, bool requested)
        { if (requested && reason != WeaponReject.None) universe.Performance.RecordWeaponResult(reason); return false; }

        internal static WeaponReject CheckReadiness(CompSpaceWeapon weapon, out int skill)
        {
            skill = 0;
            if (weapon.CooldownTicksLeft > 0) return WeaponReject.Cooldown;
            if (!weapon.TryGetSkillLevel(out skill)) return WeaponReject.Operating;
            if (weapon.Component<CompPowerTrader>() is { PowerOn: false }) return WeaponReject.Power;
            return WeaponReject.None;
        }

        internal static void AimLocal(CompSpaceWeapon weapon, Vector2 point, Vector2 muzzle)
        {
            var traverse = weapon.Component<CompSpaceWeaponTraverse>();
            if (traverse == null) return;
            Vector2 delta = point - muzzle;
            if (delta.sqrMagnitude > 1e-6f) traverse.AimAt(weapon.ClampAim(Mathf.Atan2(delta.y, delta.x)));
        }

        internal static Vector3 TargetingMuzzle(CompSpaceWeapon weapon, SpaceObject container)
            => SpaceTransform.MapPointToUniverse(container, weapon.parent.Map,
                weapon.Layout.acceleration.muzzleMap ?? SpaceWeaponGeometry.MuzzleMapPoint(weapon));

        internal static bool CanTrack(CompSpaceWeapon weapon)
            => !weapon.WeaponProps.tractor || SpaceTractor.CanOperate(weapon);
        internal static bool CanAcquire(CompSpaceWeapon weapon)
            => !weapon.WeaponProps.tractor || weapon.QueryEnergy.Evaluate(weapon, includeSegments: false);
    }
}

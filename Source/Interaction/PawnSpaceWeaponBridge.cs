using RimWorld;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>
    /// Direct personal-weapon entry for a one-cell spaceflight map. It asks the real
    /// equipment Verb to fire, while a same-call Harmony scope removes only the local
    /// map warmup/LOS constraints that cannot represent a universe target.
    /// </summary>
    internal static class PawnSpaceWeaponBridge
    {
        [System.ThreadStatic] private static Verb directCastVerb;
        [System.ThreadStatic] private static Verb pendingShotVerb;
        [System.ThreadStatic] private static List<PendingProjectile> pendingProjectiles;

        private readonly struct PendingProjectile
        {
            internal readonly Projectile projectile;
            internal readonly Pawn launcher;
            internal readonly Thing equipment;
            internal readonly Vector3 origin;
            internal readonly Vector2 direction;

            internal PendingProjectile(Projectile projectile, Pawn launcher, Thing equipment,
                Vector3 origin, Vector2 direction)
            {
                this.projectile = projectile;
                this.launcher = launcher;
                this.equipment = equipment;
                this.origin = origin;
                this.direction = direction;
            }
        }

        internal static bool DirectCastActive(Verb verb) => verb != null && directCastVerb == verb;

        internal static bool SupportsCapturedProjectile(Verb verb) =>
            verb?.verbProps?.defaultProjectile?.projectile != null;

        internal static bool IsSpacePrimaryVerb(Verb verb)
        {
            if (verb?.CasterPawn is not Pawn pawn || pawn.Spawned != true
                || pawn.equipment?.PrimaryEq?.PrimaryVerb != verb) return false;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            return universe?.FrameOf(pawn.Map) is MapSpaceObject frame
                && frame.PersonnelPawn == pawn && frame.PersonnelShip?.IsValid == true;
        }

        internal static bool TryAimAngle(Verb verb, out float angle)
        {
            angle = 0f;
            if (!IsSpacePrimaryVerb(verb)) return false;
            return TryAimAngle(verb.EquipmentSource, out angle);
        }

        internal static bool TryAimAngle(Thing equipment, out float angle)
        {
            angle = 0f;
            if (!TryAimSource(equipment, out _, out PawnSpaceflightShip vessel)) return false;
            Vector2 direction = vessel.WeaponAimDirection;
            angle = new Vector3(direction.x, 0f, direction.y).AngleFlat();
            return true;
        }

        internal static bool TryAimPose(Thing equipment, ref Vector3 drawLocation, ref float aimAngle)
        {
            if (!TryAimSource(equipment, out Pawn pawn, out PawnSpaceflightShip vessel)) return false;
            Vector2 direction = vessel.WeaponAimDirection;
            float desiredAngle = new Vector3(direction.x, 0f, direction.y).AngleFlat();
            if (DegenerateLocalAim(pawn))
                drawLocation = ReorientDrawReach(drawLocation, aimAngle, desiredAngle, 0.4f);
            aimAngle = desiredAngle;
            return true;
        }

        private static bool TryAimSource(Thing equipment, out Pawn pawn,
            out PawnSpaceflightShip vessel)
        {
            pawn = null;
            vessel = null;
            if (equipment?.ParentHolder is not Pawn_EquipmentTracker tracker
                || tracker.pawn?.equipment?.Primary != equipment) return false;
            pawn = tracker.pawn;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.FrameOf(pawn.Map) is not MapSpaceObject frame
                || frame.PersonnelPawn != pawn || frame.PersonnelShip?.IsValid != true) return false;
            vessel = frame.PersonnelShip;
            return true;
        }

        private static bool DegenerateLocalAim(Pawn pawn)
        {
            if (pawn?.stances?.curStance is not Stance_Busy busy || !busy.focusTarg.IsValid)
                return false;
            Vector3 target = busy.focusTarg.HasThing
                ? busy.focusTarg.Thing.DrawPos
                : busy.focusTarg.Cell.ToVector3Shifted();
            return (target - pawn.DrawPos).MagnitudeHorizontalSquared() <= 0.001f;
        }

        internal static Vector3 ReorientDrawReach(Vector3 drawLocation, float previousAngle,
            float desiredAngle, float reach)
        {
            Vector3 previous = ForwardReach(previousAngle, reach);
            Vector3 desired = ForwardReach(desiredAngle, reach);
            return drawLocation - previous + desired;
        }

        private static Vector3 ForwardReach(float angle, float reach)
        {
            double radians = angle * System.Math.PI / 180d;
            return new Vector3((float)System.Math.Sin(radians) * reach, 0f,
                (float)System.Math.Cos(radians) * reach);
        }

        internal static bool BypassShootLine(Verb verb) => DirectCastActive(verb)
            || verb?.state == VerbState.Bursting && IsSpacePrimaryVerb(verb);

        // Launch can be virtual: capturing in the base Launch postfix would remove the
        // projectile before a derived Launch finishes. The non-virtual burst-shot wrapper
        // supplies the safe boundary after the complete real Verb shot has run.
        internal static void BeginShot(Verb verb)
        {
            if (!IsSpacePrimaryVerb(verb)) return;
            if (pendingShotVerb != null && pendingShotVerb != verb)
                AbortShot(pendingShotVerb);
            pendingShotVerb = verb;
            (pendingProjectiles ??= new List<PendingProjectile>(2)).Clear();
        }

        internal static void NoteProjectile(Projectile projectile, Pawn launcher, Thing equipment,
            Vector3 origin, LocalTargetInfo usedTarget)
        {
            if (pendingShotVerb == null || projectile == null || launcher != pendingShotVerb.CasterPawn
                || equipment != null && equipment != pendingShotVerb.EquipmentSource) return;
            equipment ??= pendingShotVerb.EquipmentSource;
            if (equipment == null) return;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            Vector2 direction = universe?.FrameOf(launcher.Map) is MapSpaceObject frame
                && frame.PersonnelPawn == launcher && frame.PersonnelShip?.IsValid == true
                ? frame.PersonnelShip.WeaponAimDirection
                : new Vector2(launcher.Rotation.FacingCell.x, launcher.Rotation.FacingCell.z);
            pendingProjectiles.Add(new PendingProjectile(projectile, launcher, equipment,
                origin, direction));
        }

        internal static void CompleteShot(Verb verb)
        {
            if (pendingShotVerb != verb) return;
            try
            {
                UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
                foreach (PendingProjectile pending in pendingProjectiles)
                {
                    bool captured = false;
                    try
                    {
                        captured = universe != null
                            && universe.Projectiles.CapturePawnProjectile(pending.projectile,
                                pending.launcher, pending.equipment, pending.origin, pending.direction);
                    }
                    finally
                    {
                        if (!captured) RetireLocalProjectile(pending.projectile);
                    }
                }
            }
            finally
            {
                pendingProjectiles.Clear();
                pendingShotVerb = null;
            }
        }

        internal static void AbortShot(Verb verb)
        {
            if (pendingShotVerb != verb) return;
            try
            {
                if (pendingProjectiles != null)
                    foreach (PendingProjectile pending in pendingProjectiles)
                        RetireLocalProjectile(pending.projectile);
                try { verb?.Reset(); }
                catch (System.Exception ex)
                {
                    Log.Error($"[DeltaV] Could not reset an exceptional personnel weapon Verb: {ex}");
                }
            }
            finally
            {
                pendingProjectiles?.Clear();
                pendingShotVerb = null;
            }
        }

        private static void RetireLocalProjectile(Projectile projectile)
        {
            if (projectile == null || projectile.Destroyed) return;
            try { projectile.Destroy(DestroyMode.Vanish); }
            catch (System.Exception ex)
            {
                Log.Error($"[DeltaV] Could not retire an uncaptured personnel projectile: {ex}");
            }
        }

        internal static bool TryFire(UniverseManager universe, PawnSpaceflightShip vessel,
            SpaceObject container, Vector2 mapPoint)
        {
            Pawn pawn = vessel?.pawn;
            if (universe == null || pawn?.Spawned != true || pawn.Map != container?.map
                || vessel.IsValid != true) return false;
            vessel.AimPersonalWeapon(mapPoint);
            if (Find.TickManager.Paused || pawn.stances?.curStance is Stance_Busy
                || pawn.WorkTagIsDisabled(WorkTags.Violent)
                || pawn.WorkTagIsDisabled(WorkTags.Shooting))
                return false;
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (!SupportsCapturedProjectile(verb))
            {
                if (verb != null)
                    Log.WarningOnce($"[DeltaV] EVA personnel weapon '{pawn.equipment.Primary.LabelCap}' "
                        + $"uses unsupported projectile behavior ({verb.GetType().FullName}).",
                        Gen.HashCombineInt(pawn.thingIDNumber, pawn.equipment.Primary.thingIDNumber));
                return false;
            }
            if (!verb.Available())
                return false;

            // The real Verb only needs a legal local placeholder. Universe aim is carried
            // separately by the personnel ship; an out-of-map CurrentTarget lets third-party
            // muzzle/animation patches index the 1x1 map with invalid coordinates.
            IntVec3 targetCell = pawn.Position;

            Verb previous = directCastVerb;
            directCastVerb = verb;
            bool started;
            try
            {
                started = verb.TryStartCastOn(targetCell, surpriseAttack: false,
                    canHitNonTargetPawns: true, preventFriendlyFire: false,
                    nonInterruptingSelfCast: false);
            }
            finally { directCastVerb = previous; }
            return started;
        }
    }
}

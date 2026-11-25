using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    internal static class PawnSpaceflightPatches
    {
        internal static PawnSpaceflightService Service => Current.Game?.GetComponent<UniverseManager>()?.Personnel;
        // The lock belongs to the active space representation, not to the pawn's
        // current capacity. Downing or injury queues demotion, but must not open a
        // short equipment-change window before that frame transaction completes.
        internal static bool Locked(Pawn pawn) => Service?.IsActive(pawn) == true;
        internal static bool ManualAimControls(Pawn pawn)
        {
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (pawn?.Spawned != true || universe?.FrameOf(pawn.Map) is not MapSpaceObject frame
                || frame.PersonnelPawn != pawn || frame.PersonnelShip?.IsValid != true) return false;
            PawnSpaceflightShip ship = frame.PersonnelShip;
            return ship.EffectiveFireControlMode == ShipFireControlMode.Manual
                && universe.Controls.ManualAimOwner(ship.Identity);
        }
        internal static bool Allow(Pawn pawn)
        {
            if (!Locked(pawn)) return true;
            if (pawn?.Faction == Faction.OfPlayerSilentFail)
                Messages.Message("DeltaV_PawnSpaceflightEquipmentLocked".Translate(), pawn,
                    MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
    internal static class Patch_PawnSpaceflight_Rotation
    {
        // A drafted idle Pawn is forced south by vanilla after Delta-V has already selected
        // its camera-projected facing. While the personnel aim session is authoritative,
        // leave rotation entirely to ArcadeShipAimHandler; all other pawns stay vanilla.
        private static bool Prefix(Pawn ___pawn) => !PawnSpaceflightPatches.ManualAimControls(___pawn);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_PawnSpaceflight_Gizmos
    {
        private static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = Append(__instance, __result);
        }

        private static IEnumerable<Gizmo> Append(Pawn pawn, IEnumerable<Gizmo> source)
        {
            foreach (Gizmo gizmo in source) yield return gizmo;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe == null || pawn?.Faction != Faction.OfPlayerSilentFail || !pawn.Drafted) yield break;
            if (universe.Personnel.IsActive(pawn))
                yield break;
            if (universe.FrameOf(pawn.Map) == null) yield break;
            var command = new Command_Action
            {
                defaultLabel = "DeltaV_PawnSpaceflightExit".Translate(),
                defaultDesc = "DeltaV_PawnSpaceflightExitDesc".Translate(),
                icon = TexCommand.DropCarriedPawn,
                action = () => universe.Scatter.TryQueuePersonnelRelease(pawn)
            };
            yield return command;
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Wear))]
    internal static class Patch_PawnSpaceflight_Wear
    {
        private static bool Prefix(Pawn ___pawn) => PawnSpaceflightPatches.Allow(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.TryMoveToInventory))]
    internal static class Patch_PawnSpaceflight_MoveApparel
    {
        private static bool Prefix(Pawn ___pawn, ref bool __result)
        {
            if (PawnSpaceflightPatches.Allow(___pawn)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnSpaceflight_DropApparel
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Pawn_ApparelTracker),
            nameof(Pawn_ApparelTracker.TryDrop), new[]
            {
                typeof(Apparel), typeof(Apparel).MakeByRefType(), typeof(IntVec3), typeof(bool)
            });
        private static bool Prefix(Pawn ___pawn, ref Apparel resultingAp, ref bool __result)
        {
            if (PawnSpaceflightPatches.Allow(___pawn)) return true;
            resultingAp = null;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment))]
    internal static class Patch_PawnSpaceflight_AddEquipment
    {
        private static bool Prefix(Pawn ___pawn) => PawnSpaceflightPatches.Allow(___pawn);
    }

    [HarmonyPatch]
    internal static class Patch_PawnSpaceflight_DropEquipment
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Pawn_EquipmentTracker),
            nameof(Pawn_EquipmentTracker.TryDropEquipment));
        private static bool Prefix(Pawn ___pawn, ref ThingWithComps resultingEq, ref bool __result)
        {
            if (PawnSpaceflightPatches.Allow(___pawn)) return true;
            resultingEq = null;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.TryTransferEquipmentToContainer))]
    internal static class Patch_PawnSpaceflight_TransferEquipment
    {
        private static bool Prefix(Pawn ___pawn, ref bool __result)
        {
            if (PawnSpaceflightPatches.Allow(___pawn)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnSpaceflight_GearTabDrop
    {
        private static readonly MethodInfo SelPawnGetter = AccessTools.PropertyGetter(
            typeof(ITab_Pawn_Gear), "SelPawnForGear");

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(ITab_Pawn_Gear),
            "InterfaceDrop", new[] { typeof(Thing) });

        private static bool Prefix(ITab_Pawn_Gear __instance, Thing t)
        {
            Pawn pawn = SelPawnGetter?.Invoke(__instance, null) as Pawn;
            UniverseManager universe = Current.Game?.GetComponent<UniverseManager>();
            if (universe?.Personnel.IsActive(pawn) != true) return true;
            if (t?.holdingOwner == pawn.inventory?.innerContainer)
            {
                if (!universe.Scatter.TryDropPawnInventory(pawn, t))
                    Messages.Message("DeltaV_PawnSpaceflightCargoDropFailed".Translate(), pawn,
                        MessageTypeDefOf.RejectInput, false);
                return false;
            }
            PawnSpaceflightPatches.Allow(pawn);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
    internal static class Patch_PawnSpaceflight_ApparelAdded
    {
        private static void Postfix(Pawn ___pawn) => PawnSpaceflightPatches.Service?.NotifyPawnChanged(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
    internal static class Patch_PawnSpaceflight_ApparelRemoved
    {
        private static void Postfix(Pawn ___pawn) => PawnSpaceflightPatches.Service?.NotifyPawnChanged(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentAdded))]
    internal static class Patch_PawnSpaceflight_EquipmentAdded
    {
        private static void Postfix(Pawn ___pawn) => PawnSpaceflightPatches.Service?.NotifyPawnChanged(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentRemoved))]
    internal static class Patch_PawnSpaceflight_EquipmentRemoved
    {
        private static void Postfix(Pawn ___pawn) => PawnSpaceflightPatches.Service?.NotifyPawnChanged(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_HediffChanged))]
    internal static class Patch_PawnSpaceflight_HealthChanged
    {
        private static void Postfix(Pawn ___pawn) => PawnSpaceflightPatches.Service?.NotifyPawnChanged(___pawn);
    }
}

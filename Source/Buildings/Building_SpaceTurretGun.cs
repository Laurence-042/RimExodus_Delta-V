using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class DefModExtension_SpaceTurretArt : DefModExtension
    {
        public string topTexPath;
    }

    /// <summary>Only substitutes the rotating artwork; vanilla turret draw and rotation stay intact.</summary>
    public class Building_SpaceTurretGun : Building_TurretGun, IShellTurretTopSource
    {
        private Material topMaterial;

        private bool GroundFireEnabled => this.TryGetComp<CompSpaceWeapon>()?.GroundModeEnabled == true;

        protected override bool CanSetForcedTarget => GroundFireEnabled && base.CanSetForcedTarget;

        protected override bool HideForceTargetGizmo => !GroundFireEnabled || base.HideForceTargetGizmo;

        public Material ShellTopMaterial => TurretTopMaterial;

        public float ShellTopAngle => CurrentEffectiveVerb?.AimAngleOverride ?? top?.CurRotation ?? Rotation.AsAngle;

        public override Material TurretTopMaterial
        {
            get
            {
                string path = def.GetModExtension<DefModExtension_SpaceTurretArt>()?.topTexPath;
                if (path.NullOrEmpty()) return base.TurretTopMaterial;
                if (topMaterial == null) topMaterial = MaterialPool.MatFrom(path);
                return topMaterial;
            }
        }

        public override LocalTargetInfo TryFindNewTarget()
        {
            if (!GroundFireEnabled)
                return LocalTargetInfo.Invalid;
            return base.TryFindNewTarget();
        }

        public override void OrderAttack(LocalTargetInfo targ)
        {
            if (!GroundFireEnabled && targ.IsValid) return;
            base.OrderAttack(targ);
        }

        protected override void BeginBurst()
        {
            if (GroundFireEnabled) base.BeginBurst();
        }

        public void NotifyEngagementModeChanged()
        {
            CompSpaceWeapon weapon = this.TryGetComp<CompSpaceWeapon>();
            int now = Find.TickManager.TicksGame;
            if (GroundFireEnabled)
            {
                burstCooldownTicksLeft = Mathf.Max(burstCooldownTicksLeft,
                    (weapon?.cooldownUntilTick ?? now) - now);
                return;
            }
            if (AttackVerb.state == VerbState.Bursting)
                burstCooldownTicksLeft = Mathf.Max(burstCooldownTicksLeft,
                    def.building.turretBurstCooldownTime.SecondsToTicks());
            if (weapon != null)
                weapon.cooldownUntilTick = Mathf.Max(weapon.cooldownUntilTick,
                    now + burstCooldownTicksLeft);
            OrderAttack(LocalTargetInfo.Invalid);
            currentTargetInt = LocalTargetInfo.Invalid;
            burstWarmupTicksLeft = 0;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                // 原版停火钮只控制同图 Verb；专用点防由武器组统一启停。
                if (!GroundFireEnabled && gizmo is Command_Toggle toggle
                    && toggle.defaultLabel == "CommandHoldFire".Translate()) continue;
                yield return gizmo;
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            if (!GroundFireEnabled) return;
            base.DrawExtraSelectionOverlays();
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    public sealed class DefModExtension_SpaceTurretArt : DefModExtension
    {
        public string topTexPath;
    }

    /// <summary>舰炮顶部美术与地面/太空交互适配；原版继续绘制和旋转炮身。</summary>
    public class Building_SpaceTurretGun : Building_TurretGun, IShellTurretTopSource
    {
        // 太空模式跳过地面射程/强制目标提示，但仍调用原版 ThingWithComps 的完整覆盖层入口。
        private static readonly Action<ThingWithComps> DrawCompSelectionOverlays =
            AccessTools.MethodDelegate<Action<ThingWithComps>>(
                AccessTools.DeclaredMethod(typeof(ThingWithComps), nameof(DrawExtraSelectionOverlays)),
                virtualCall: false);
        private Material topMaterial;
        private Material[,] heatMaterials;
        private Texture2D topTexture, topMask;

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
                CompWeaponTuning heat = this.TryGetComp<CompWeaponTuning>();
                if (heat?.Settings.chemical != true)
                {
                    if (topMaterial == null) topMaterial = MaterialPool.MatFrom(path);
                    return topMaterial;
                }
                if (topTexture == null) topTexture = ContentFinder<Texture2D>.Get(path);
                if (topMask == null) topMask = ContentFinder<Texture2D>.Get(path + "_m");
                int barrelLevel = WeaponHeatVisuals.BarrelHeatLevel(heat);
                int bodyLevel = WeaponHeatVisuals.BodyHeatLevel(heat);
                int bodyIndex = bodyLevel == 0 ? 0 : 1;
                if (heatMaterials == null) heatMaterials = new Material[33, 2];
                if (heatMaterials[barrelLevel, bodyIndex] == null)
                {
                    var request = new MaterialRequest(topTexture, ShaderDatabase.CutoutComplex,
                        WeaponHeatVisuals.HeatColor(barrelLevel))
                    {
                        maskTex = topMask,
                        colorTwo = WeaponHeatVisuals.HeatColor(bodyLevel)
                    };
                    heatMaterials[barrelLevel, bodyIndex] = MaterialPool.MatFrom(request);
                }
                return heatMaterials[barrelLevel, bodyIndex];
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
            if (GroundFireEnabled && AttackVerb.Available()) base.BeginBurst();
        }

        protected override float BurstCooldownTime() =>
            (this.TryGetComp<CompSpaceWeapon>()?.ShotInterval ?? 60) / 60f;

        public void NotifyEngagementModeChanged()
        {
            CompSpaceWeapon weapon = this.TryGetComp<CompSpaceWeapon>();
            int now = Find.TickManager.TicksGame;
            if (GroundFireEnabled)
            {
                burstCooldownTicksLeft = weapon?.Tuning != null ? weapon.CooldownTicksLeft
                    : Mathf.Max(burstCooldownTicksLeft, weapon?.CooldownTicksLeft ?? 0);
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
            if (GroundFireEnabled) base.DrawExtraSelectionOverlays();
            else DrawCompSelectionOverlays(this);
        }
    }
}

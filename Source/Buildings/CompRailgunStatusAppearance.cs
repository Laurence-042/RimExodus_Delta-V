using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    /// <summary>CutoutComplex's red mask uses the primary color supplied by ThingComp.ForceColor.</summary>
    internal static class RailgunStatusAppearance
    {
        public static readonly Color ReadyTint = new Color(0.66f, 0.92f, 1f);
        public static readonly Color CoolingTint = new Color(1f, 0.62f, 0.20f);
        public static readonly Color UnavailableTint = new Color(0.72f, 0.19f, 0.22f);

        public static void LogPrinted(ThingWithComps parent)
        {
            if (!DeltaVLog.Enabled(DeltaVLogModule.Render)) return;
            Graphic graphic = parent.Graphic;
            Material source = graphic.MatAt(parent.Rotation, parent);
            bool sourceRequestFound = MaterialPool.TryGetRequestForMat(source, out MaterialRequest sourceRequest);
            bool atlasUsed = Graphic.TryGetTextureAtlasReplacementInfo(source,
                parent.def.category.ToAtlasGroup(), false, true, out Material printed, out _, out Color32 vertexColor);
            bool printedRequestFound = MaterialPool.TryGetRequestForMat(printed, out MaterialRequest printedRequest);
            DeltaVLog.Message(DeltaVLogModule.Render,
                $"rail-status print thing={parent.ThingID} runtimeType={parent.GetType().Name} graphic={graphic.GetType().Name} " +
                $"drawColor={parent.DrawColor} defShader={parent.def.graphicData?.shaderType?.defName} sourceShader={source.shader?.name} " +
                $"sourceTex={source.mainTexture?.name} sourceMask={(sourceRequestFound ? sourceRequest.maskTex?.name : "request-missing")} " +
                $"sourceColor={(sourceRequestFound ? sourceRequest.color.ToString() : "request-missing")} " +
                $"sourceColorTwo={(sourceRequestFound ? sourceRequest.colorTwo.ToString() : "request-missing")} " +
                $"atlas={atlasUsed} printedShader={printed.shader?.name} " +
                $"printedMask={(printedRequestFound ? printedRequest.maskTex?.name : "request-missing")} " +
                $"vertexColor={vertexColor}");
        }
    }

    public class CompProperties_RailgunReadinessAppearance : CompProperties
    {
        public CompProperties_RailgunReadinessAppearance()
        {
            compClass = typeof(CompRailgunReadinessAppearance);
        }
    }

    /// <summary>The loading unit changes the masked control-screen color at state edges.</summary>
    public class CompRailgunReadinessAppearance : ThingComp, ISpaceWeaponFireCommit
    {
        private enum State : byte { Unknown, Unavailable, Cooling, Ready }
        private State lastState;
        private bool logNextPrint;

        public Color Tint => lastState == State.Ready ? RailgunStatusAppearance.ReadyTint
            : lastState == State.Cooling ? RailgunStatusAppearance.CoolingTint
            : RailgunStatusAppearance.UnavailableTint;

        public override Color? ForceColor() => Tint;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            lastState = State.Unknown;
            Refresh();
        }

        public override void PostPrintOnto(SectionLayer layer)
        {
            if (!logNextPrint || !DeltaVLog.Enabled(DeltaVLogModule.Render)) return;
            logNextPrint = false;
            RailgunStatusAppearance.LogPrinted(parent);
        }

        public override void CompTick()
        {
            if (parent.IsHashIntervalTick(60)) Refresh();
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff"
                || signal == "Refueled" || signal == "RanOutOfFuel") Refresh();
        }

        public void NotifyShotCommitted(SpaceWeaponShot shot) => Refresh();

        public override string CompInspectStringExtra()
        {
            string key = lastState == State.Ready ? "DeltaV_RailReady"
                : lastState == State.Cooling ? "DeltaV_RailCooling" : "DeltaV_RailUnavailable";
            return "DeltaV_RailReadiness".Translate(key.Translate());
        }

        private void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceWeapon weapon = parent.TryGetComp<CompSpaceWeapon>();
            if (weapon == null) return;
            CompPowerTrader power = parent.TryGetComp<CompPowerTrader>();
            bool resources = (power == null || power.PowerOn)
                && weapon.TryGetSkillLevel(out _)
                && (parent.TryGetComp<CompSpaceWeaponMagazine>() == null
                    || weapon.SelectMagazine() != null)
                && SpaceWeaponShot.HasShotEnergy(weapon)
                && SpaceWeaponShot.HasClearFixedPath(weapon);
            State state = !resources ? State.Unavailable
                : Find.TickManager.TicksGame < weapon.cooldownUntilTick ? State.Cooling : State.Ready;
            if (state == lastState) return;
            if (DeltaVLog.Enabled(DeltaVLogModule.Render))
                DeltaVLog.Message(DeltaVLogModule.Render,
                    $"rail-status loader thing={parent.ThingID} {lastState}->{state} " +
                    $"power={(power == null || power.PowerOn)} crew={weapon.TryGetSkillLevel(out _)} " +
                    $"ammo={(parent.TryGetComp<CompSpaceWeaponMagazine>() == null || weapon.SelectMagazine() != null)} " +
                    $"shotEnergy={SpaceWeaponShot.HasShotEnergy(weapon)} path={SpaceWeaponShot.HasClearFixedPath(weapon)} " +
                    $"cooldownUntil={weapon.cooldownUntilTick} tick={Find.TickManager.TicksGame}");
            lastState = state;
            parent.Notify_ColorChanged();
            if (DeltaVLog.Enabled(DeltaVLogModule.Render))
            {
                logNextPrint = true;
                DeltaVLog.Message(DeltaVLogModule.Render,
                    $"rail-status color-refresh thing={parent.ThingID} runtimeType={parent.GetType().Name} " +
                    $"drawColor={parent.DrawColor} tint={Tint}");
            }
        }
    }

    public class CompProperties_RailgunAcceleratorAppearance : CompProperties
    {
        public CompProperties_RailgunAcceleratorAppearance()
        {
            compClass = typeof(CompRailgunAcceleratorAppearance);
        }
    }

    /// <summary>Coil color uses the same sequential energy plan as the actual shot.</summary>
    public class CompRailgunAcceleratorAppearance : ThingComp
    {
        private bool? lastActive;
        private bool logNextPrint;

        public Color Tint => lastActive == true ? RailgunStatusAppearance.ReadyTint
            : RailgunStatusAppearance.UnavailableTint;

        public override Color? ForceColor() => Tint;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            Refresh();
        }

        public override void PostPrintOnto(SectionLayer layer)
        {
            if (!logNextPrint || !DeltaVLog.Enabled(DeltaVLogModule.Render)) return;
            logNextPrint = false;
            RailgunStatusAppearance.LogPrinted(parent);
        }

        public override void CompTick()
        {
            if (parent.IsHashIntervalTick(60)) Refresh();
        }

        public override void ReceiveCompSignal(string signal)
        {
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff") Refresh();
        }

        public override string CompInspectStringExtra() =>
            "DeltaV_RailAcceleratorStatus".Translate((lastActive == true
                ? "DeltaV_RailAcceleratorActive" : "DeltaV_RailAcceleratorSkipped").Translate());

        public void Refresh()
        {
            if (!parent.Spawned || parent.Map == null) return;
            CompSpaceRailgun segment = parent.TryGetComp<CompSpaceRailgun>();
            if (segment == null) return;
            CompSpaceWeapon weapon = segment.ConnectedWeapon();
            bool active = SpaceWeaponShot.WillAccelerate(weapon, segment);
            if (lastActive == active) return;
            if (DeltaVLog.Enabled(DeltaVLogModule.Render))
                DeltaVLog.Message(DeltaVLogModule.Render,
                    $"rail-status accelerator thing={parent.ThingID} {lastActive?.ToString() ?? "unknown"}->{active} " +
                    $"loader={weapon?.parent?.ThingID ?? "none"} power={parent.TryGetComp<CompPowerTrader>()?.PowerOn} " +
                    $"tick={Find.TickManager.TicksGame}");
            lastActive = active;
            parent.Notify_ColorChanged();
            if (DeltaVLog.Enabled(DeltaVLogModule.Render))
            {
                logNextPrint = true;
                DeltaVLog.Message(DeltaVLogModule.Render,
                    $"rail-status color-refresh thing={parent.ThingID} runtimeType={parent.GetType().Name} " +
                    $"drawColor={parent.DrawColor} tint={Tint}");
            }
        }
    }
}

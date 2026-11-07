using RimWorld;
using UnityEngine;
using Verse;

namespace DeltaV
{
    // RealtimeOnly lets the map and shell read actual connection and charge without
    // caching game state or rebuilding static geometry when the network changes.
    public class Graphic_SafetyBattery : Graphic_Single
    {
        private static Material filledMaterial;
        private static Material unfilledMaterial;

        public override Graphic GetColoredVersion(Shader shader, Color first, Color second)
            => GraphicDatabase.Get<Graphic_SafetyBattery>(path, shader, drawSize, first, second, data);

        internal static Color ConnectionColor(CompSwitchableBattery battery)
            => battery.WantsGridConnection && battery.PowerNet != null ? StorageStateColors.Active : StorageStateColors.Reserved;

        public override Material MatSingleFor(Thing thing) => MatAt(thing?.Rotation ?? Rot4.North, thing);

        public override Material MatAt(Rot4 rot, Thing thing = null)
        {
            CompSwitchableBattery battery = thing?.TryGetComp<CompSwitchableBattery>();
            if (battery == null || !Shader.SupportsMaskTex()) return base.MatAt(rot, thing);
            return MaterialPool.MatFrom(new MaterialRequest((Texture2D)mat.mainTexture, Shader, ConnectionColor(battery))
            {
                maskTex = (Texture2D)mat.GetTexture(ShaderPropertyIDs.MaskTex),
                colorTwo = colorTwo,
                renderQueue = mat.renderQueue
            });
        }

        internal static GenDraw.FillableBarRequest ChargeBar(Vector3 loc, Rot4 rot, CompSwitchableBattery battery)
            => new GenDraw.FillableBarRequest
            {
                center = loc + Vector3.up * 0.1f,
                size = new Vector2(2.25f, 0.16f),
                rotation = rot,
                fillPercent = battery.Props.storedEnergyMax > 0f
                    ? Mathf.Clamp01(battery.StoredEnergy / battery.Props.storedEnergyMax) : 0f,
                margin = 0.06f
            };

        public override void DrawWorker(Vector3 loc, Rot4 rot, ThingDef thingDef, Thing thing, float extraRotation)
        {
            base.DrawWorker(loc, rot, thingDef, thing, extraRotation);
            CompSwitchableBattery battery = thing?.TryGetComp<CompSwitchableBattery>();
            if (battery == null) return;
            filledMaterial ??= SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.9f, 0.85f, 0.2f));
            unfilledMaterial ??= SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.3f, 0.3f));
            GenDraw.FillableBarRequest bar = ChargeBar(loc, rot, battery);
            bar.filledMat = filledMaterial;
            bar.unfilledMat = unfilledMaterial;
            GenDraw.DrawFillableBar(bar);
        }
    }
}

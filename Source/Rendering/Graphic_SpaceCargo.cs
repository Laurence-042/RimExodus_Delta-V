using UnityEngine;
using Verse;

namespace DeltaV
{
    // Red mask = native cargo state; green mask = the building's ordinary paint.
    // Keep the Graphic type when vanilla/ASF requests a painted variant.
    public class Graphic_SpaceCargo : Graphic_Single
    {
        public override Graphic GetColoredVersion(Shader shader, Color first, Color second)
            => GraphicDatabase.Get<Graphic_SpaceCargo>(path, shader, drawSize, first, second, data);

        public override Material MatSingleFor(Thing thing) => MatAt(thing?.Rotation ?? Rot4.North, thing);

        public override Material MatAt(Rot4 rot, Thing thing = null)
        {
            CompSpaceCargo cargo = thing?.TryGetComp<CompSpaceCargo>();
            if (cargo == null || !Shader.SupportsMaskTex()) return base.MatAt(rot, thing);
            return MaterialPool.MatFrom(new MaterialRequest((Texture2D)mat.mainTexture, Shader, cargo.TerminalColor)
            {
                maskTex = (Texture2D)mat.GetTexture(ShaderPropertyIDs.MaskTex),
                colorTwo = thing.DrawColor,
                renderQueue = mat.renderQueue
            });
        }
    }
}

using UnityEngine;

namespace DeltaV
{
    // Shared CutoutComplex multipliers; see the state-color table in 美术资源施工表.
    internal static class StorageStateColors
    {
        internal static readonly Color Active = new Color(193f / 255f, 245f / 255f, 1f);
        internal static readonly Color Reserved = new Color(224f / 255f, 224f / 255f, 224f / 255f);
        internal static readonly Color Locked = new Color(82f / 255f, 206f / 255f, 102f / 255f);
        internal static readonly Color HostileLocked = new Color(214f / 255f, 78f / 255f, 64f / 255f);
    }
}

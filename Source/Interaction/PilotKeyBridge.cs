using System.Collections.Generic;
using UnityEngine;

namespace DeltaV
{
    /// <summary>Harmony 按键读取的唯一静态快速通道；不持有任何游戏对象。</summary>
    internal static class PilotKeyBridge
    {
        internal static volatile bool SuppressVanillaKeys;
        internal static HashSet<KeyCode> ActiveKeys = new HashSet<KeyCode>();

        internal static void Reset()
        {
            SuppressVanillaKeys = false;
            ActiveKeys = new HashSet<KeyCode>();
        }
    }
}

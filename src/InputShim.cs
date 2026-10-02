using System.Collections.Generic;
using UnityEngine;

namespace NOInfantry
{
    // All gameplay input goes through here so the automated test can drive the soldier without
    // touching the real mouse/keyboard. Scripted mode replaces Unity's Input entirely.
    internal static class InputShim
    {
        internal static bool Scripted;

        private static readonly HashSet<KeyCode> held = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> pending = new HashSet<KeyCode>();
        private static readonly Dictionary<KeyCode, int> downFrame = new Dictionary<KeyCode, int>();
        private static readonly bool[] mouseHeld = new bool[3];
        private static readonly bool[] mousePending = new bool[3];
        private static readonly int[] mouseDownFrame = { -1, -1, -1 };

        internal static bool Key(KeyCode k) => Scripted ? held.Contains(k) : Input.GetKey(k);

        internal static bool KeyDown(KeyCode k)
        {
            if (!Scripted) return Input.GetKeyDown(k);
            // A scripted press is consumed by the first frame that asks for it.
            if (pending.Remove(k)) downFrame[k] = Time.frameCount;
            return downFrame.TryGetValue(k, out int f) && f == Time.frameCount;
        }

        internal static bool Mouse(int b) => Scripted ? mouseHeld[b] : Input.GetMouseButton(b);

        internal static bool MouseDown(int b)
        {
            if (!Scripted) return Input.GetMouseButtonDown(b);
            if (mousePending[b]) { mousePending[b] = false; mouseDownFrame[b] = Time.frameCount; }
            return mouseDownFrame[b] == Time.frameCount;
        }

        internal static float Scroll => Scripted ? 0f : Input.mouseScrollDelta.y;

        // Test-side controls
        internal static void Hold(KeyCode k, bool on) { if (on) held.Add(k); else held.Remove(k); }
        internal static void Press(KeyCode k) => pending.Add(k);
        internal static void HoldMouse(int b, bool on) => mouseHeld[b] = on;
        internal static void ClickMouse(int b) => mousePending[b] = true;

        internal static void ReleaseAll()
        {
            held.Clear();
            pending.Clear();
            for (int i = 0; i < 3; i++) { mouseHeld[i] = false; mousePending[i] = false; }
        }
    }
}

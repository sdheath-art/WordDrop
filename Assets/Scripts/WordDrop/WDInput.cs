using UnityEngine;

namespace WordDrop
{
    /// <summary>The ONE place gameplay code reads the pointer (2026-11-16, for MonkeyTester).
    /// Normal play: a straight pass-through to UnityEngine.Input - nothing changes.
    /// Monkey test: FakeActive = true and the monkey supplies one fake finger (as a mouse); real mouse/touch is ignored.
    /// Keys (GetKeyDown) are NOT routed through here - debug hotkeys keep reading the real keyboard.</summary>
    public static class WDInput
    {
        /// <summary>True while the monkey owns the pointer. Only MonkeyTester sets this.</summary>
        public static bool FakeActive { get; private set; }
        private static bool s_down, s_held, s_up;
        private static Vector3 s_pos;

        public static void BeginFake() { FakeActive = true; s_down = s_held = s_up = false; }
        public static void EndFake()   { FakeActive = false; s_down = s_held = s_up = false; }
        /// <summary>Set this frame's fake pointer. Same meaning as Unity's mouse: down = pressed THIS frame, held = button is
        /// down (true on the down frame too), up = released THIS frame.</summary>
        public static void SetFake(bool down, bool held, bool up, Vector3 screenPos) { s_down = down; s_held = held || down; s_up = up; s_pos = screenPos; }

        /// <summary>11-27: true while a full-screen event (the treasure chest) owns the screen - the board sees no finger at all. UI buttons are unaffected (they go through the EventSystem).</summary>
        public static bool Blocked;
        public static bool GetMouseButtonDown(int b) => Blocked ? false : FakeActive ? (b == 0 && s_down) : Input.GetMouseButtonDown(b);
        public static bool GetMouseButton(int b)     => Blocked ? false : FakeActive ? (b == 0 && s_held) : Input.GetMouseButton(b);
        public static bool GetMouseButtonUp(int b)   => Blocked ? false : FakeActive ? (b == 0 && s_up)   : Input.GetMouseButtonUp(b);
        // the UI's own view of the (fake) finger - never blocked, so buttons on a full-screen event still work for the test bot
        public static bool UiDown(int b) => FakeActive ? (b == 0 && s_down) : Input.GetMouseButtonDown(b);
        public static bool UiHeld(int b) => FakeActive ? (b == 0 && s_held) : Input.GetMouseButton(b);
        public static bool UiUp(int b)   => FakeActive ? (b == 0 && s_up)   : Input.GetMouseButtonUp(b);
        public static Vector3 mousePosition          => FakeActive ? s_pos : Input.mousePosition;
        public static int touchCount                 => Blocked || FakeActive ? 0 : Input.touchCount;
        public static Touch GetTouch(int i)          => Input.GetTouch(i);   // never reached while faking (touchCount is 0)
    }
}

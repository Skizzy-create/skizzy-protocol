using System;

namespace Skizzy.C2.Toolbar
{
    /// <summary>
    /// The C2 window's entry in the host's Skizzy menu (the host's toolbar button, src/Skizzy.Host/Ui). The C2 window
    /// registers <see cref="Toggle"/> and <see cref="IsOpen"/> in flight and clears them when the scene ends; the host
    /// menu draws an item labelled <see cref="Label"/> while <see cref="Toggle"/> is set. Without the host the C2
    /// window adds its own toolbar button instead. Main thread only.
    /// </summary>
    public static class C2MenuEntry
    {
        public const string Label = "C2 command";

        /// <summary>Opens or closes the C2 window; null outside flight.</summary>
        public static Action Toggle;

        /// <summary>Whether the C2 window is open; null outside flight.</summary>
        public static Func<bool> IsOpen;

        public static bool Open
        {
            get
            {
                var f = IsOpen;
                if (f == null) return false;
                try { return f(); }
                catch (Exception) { return false; }
            }
        }
    }
}

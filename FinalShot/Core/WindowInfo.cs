using System;
using System.Drawing;

namespace PluginScreenshot
{
    // Lightweight record of a visible window or control rectangle in screen coordinates.
    // Equivalent to ShareX's SimpleWindowInfo.
    internal class WindowInfo
    {
        // Native window handle
        public IntPtr Handle { get; set; }

        // Bounding rectangle in screen coordinates
        public Rectangle Rectangle { get; set; }

        // True for top-level windows from EnumWindows; false for child controls
        public bool IsTopLevel { get; set; }
    }
}

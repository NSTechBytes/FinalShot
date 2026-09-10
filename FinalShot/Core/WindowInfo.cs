using System;
using System.Drawing;

namespace PluginScreenshot
{
    /// <summary>
    /// Lightweight record of a visible window or control rectangle, in screen coordinates.
    /// Equivalent to ShareX's SimpleWindowInfo.
    /// </summary>
    internal class WindowInfo
    {
        /// <summary>Native window handle.</summary>
        public IntPtr Handle { get; set; }

        /// <summary>Bounding rectangle in screen coordinates.</summary>
        public Rectangle Rectangle { get; set; }

        /// <summary>True for top-level windows enumerated by EnumWindows; false for child controls.</summary>
        public bool IsTopLevel { get; set; }
    }
}

using System;
using System.Drawing;

namespace PluginScreenshot
{
    internal class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public Rectangle Rectangle { get; set; }
        public bool IsTopLevel { get; set; }
    }
}

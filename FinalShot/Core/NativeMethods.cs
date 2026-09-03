using System;
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    /// <summary>
    /// Centralized Win32 P/Invoke declarations and structs used across FinalShot.
    /// </summary>
    internal static class NativeMethods
    {
        // DPI Awareness

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        public static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // Window

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        // Cursor

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        public static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);

        // Constants

        public const int CURSOR_SHOWING = 0x00000001;

        // PrintWindow: PW_RENDERFULLCONTENT = 0x00000002
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        // Structs

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }
    }
}

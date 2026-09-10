using System;
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    /// <summary>
    /// Centralized Win32 P/Invoke declarations and structs used across FinalShot.
    /// </summary>
    internal static class NativeMethods
    {
        // ──────────────────────────────────────────────────────────────
        // DPI Awareness
        // ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        public static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // ──────────────────────────────────────────────────────────────
        // Window — find / rect / print
        // ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        // ──────────────────────────────────────────────────────────────
        // Window — enumeration (Task 1)
        // ──────────────────────────────────────────────────────────────

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // ──────────────────────────────────────────────────────────────
        // Window — style / class (Task 1)
        // Runtime branch handles x86 vs x64 without separate builds.
        // ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        /// <summary>Returns the window long at <paramref name="nIndex"/> for both x86 and x64.</summary>
        public static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? (int)GetWindowLongPtr64(hWnd, nIndex)
                : GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        // ──────────────────────────────────────────────────────────────
        // Window — client rect (Task 1)
        // ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        // ──────────────────────────────────────────────────────────────
        // DWM — cloaked window check (Task 1)
        // ──────────────────────────────────────────────────────────────

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        /// <summary>Overload for retrieving a RECT attribute (e.g. DWMWA_EXTENDED_FRAME_BOUNDS).</summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        /// <summary>DWMWA_CLOAKED = 14 — non-zero means the window is cloaked (virtual desktop, etc.).</summary>
        public const int DWMWA_CLOAKED = 14;

        /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS = 9 — the visible DWM frame rect, excluding shadow pixels.</summary>
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // ──────────────────────────────────────────────────────────────
        // GWL / extended-style constants (Task 1)
        // ──────────────────────────────────────────────────────────────

        public const int GWL_EXSTYLE          = -20;
        public const int WS_EX_TOOLWINDOW     = 0x00000080;
        public const int WS_EX_NOACTIVATE     = 0x08000000;

        // PrintWindow flag
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        // ──────────────────────────────────────────────────────────────
        // Cursor
        // ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        public static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);

        public const int CURSOR_SHOWING = 0x00000001;

        // ──────────────────────────────────────────────────────────────
        // Structs
        // ──────────────────────────────────────────────────────────────

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

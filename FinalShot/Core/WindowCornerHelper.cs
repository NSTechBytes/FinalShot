/*
 * Copyright (c) 2026 nstechbytes
 *
 * Licensed under the MIT License.
 * You may obtain a copy of the License at:
 * https://opensource.org/licenses/MIT
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PluginScreenshot
{
    /// <summary>
    /// Windows 11 rounded-corner mask (CaptureGraphicsWin2D-compatible).
    /// Clears desktop bleed outside the DWM rounded rect to transparent pixels.
    /// </summary>
    internal static class WindowCornerHelper
    {
        // DWM_WINDOW_CORNER_PREFERENCE (same as CaptureGraphicsWin2D)
        private const int DWMWCP_DEFAULT    = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        private const string ClassDesktop = "Progman";
        private const string ClassTaskbar = "Shell_TrayWnd";
        private const float ScaleFactor1 = 96.0f;

        private static readonly Version Windows11Version = new Version(10, 0, 22000);

        public static bool IsWindows11OrGreater()
        {
            try
            {
                var info = new OSVERSIONINFOEX
                {
                    dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX))
                };
                if (RtlGetVersion(ref info) == 0)
                {
                    return info.dwMajorVersion > 10
                        || (info.dwMajorVersion == 10 && info.dwBuildNumber >= 22000);
                }
            }
            catch { /* fall through */ }

            try
            {
                return Environment.OSVersion.Version >= Windows11Version;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// CaptureGraphicsWin2D-compatible corner radius in pixels (float).
        /// Maximized / desktop / taskbar / DONOTROUND → 0.
        /// ROUND / DEFAULT → 8 × (DPI/96); ROUNDSMALL → 4 × (DPI/96).
        /// </summary>
        public static float GetWindowCornerRadius(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindows11OrGreater())
                return 0f;

            try
            {
                // Maximized windows never have rounded corners
                if (IsZoomed(hwnd))
                    return 0f;

                string cls = GetWindowClassName(hwnd);
                if (string.Equals(cls, ClassDesktop, StringComparison.Ordinal)
                    || string.Equals(cls, ClassTaskbar, StringComparison.Ordinal))
                {
                    return 0f;
                }

                int preference;
                int hr = NativeMethods.DwmGetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    out preference,
                    sizeof(int));

                // Match CaptureGraphicsWin2D: only trust S_OK; otherwise no rounding
                if (hr != 0)
                    return 0f;

                float scale = GetWindowScaleFactor(hwnd);
                switch (preference)
                {
                    case DWMWCP_DONOTROUND:
                        return 0f;
                    case DWMWCP_ROUNDSMALL:
                        return scale * 4f; // context menus / small popups
                    case DWMWCP_ROUND:
                        return scale * 8f;
                    case DWMWCP_DEFAULT:
                        return scale * 8f; // Win11 default for top-level windows
                    default:
                        return 0f;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("WindowCornerHelper.GetWindowCornerRadius: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>DPI / 96 — same as CaptureGraphicsWin2D.GetWindowScaleFactor.</summary>
        public static float GetWindowScaleFactor(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0) dpi = 96;
                return dpi / ScaleFactor1;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// If the window has rounded corners, returns a new 32bpp ARGB bitmap
        /// clipped to the rounded rect (transparent outside). Otherwise returns
        /// <paramref name="source"/> unchanged. When a new bitmap is returned,
        /// <paramref name="source"/> is disposed.
        /// </summary>
        public static Bitmap ApplyRoundedCornersIfNeeded(Bitmap source, IntPtr hWnd)
        {
            if (source == null || hWnd == IntPtr.Zero)
                return source;

            float radius = GetWindowCornerRadius(hWnd);
            if (radius <= 0f)
                return source;

            Logger.Log("WindowCornerHelper: applying rounded corners (Win2D-style), radius="
                       + radius.ToString("0.##") + "px");
            Bitmap rounded = ApplyRoundedCorners(source, radius);
            source.Dispose();
            return rounded;
        }

        /// <summary>
        /// Win2D CreateLayer(roundedRect) equivalent:
        /// clear transparent → clip to rounded rect → draw source.
        /// </summary>
        public static Bitmap ApplyRoundedCorners(Bitmap source, float cornerRadius)
        {
            if (source == null)
                return null;
            if (cornerRadius <= 0f)
                return new Bitmap(source);

            int w = source.Width;
            int h = source.Height;
            float radius = Math.Min(cornerRadius, Math.Min(w, h) / 2f);

            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                // Match Win2D: Clear(0,0,0,0) then DrawImage inside rounded geometry
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;

                using (var path = CreateRoundedRectanglePath(new RectangleF(0, 0, w, h), radius))
                {
                    g.SetClip(path);
                    g.DrawImage(source, 0, 0, w, h);
                    g.ResetClip();
                }
            }
            return result;
        }

        // Kept for call sites that pass an integer radius
        public static Bitmap ApplyRoundedCorners(Bitmap source, int cornerRadius)
            => ApplyRoundedCorners(source, (float)cornerRadius);

        private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
        {
            // Same construction as CanvasGeometry.CreateRoundedRectangle
            var path = new GraphicsPath();
            if (radius <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }

            float diameter = radius * 2f;
            if (diameter >= Math.Min(rect.Width, rect.Height))
            {
                path.AddEllipse(rect);
                return path;
            }

            var arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));
            path.AddArc(arc, 180, 90); // top-left
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90); // top-right
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);   // bottom-right
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);  // bottom-left
            path.CloseFigure();
            return path;
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }
    }
}

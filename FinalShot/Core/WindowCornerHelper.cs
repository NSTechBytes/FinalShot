/*
 * Copyright (c) 2025 nstechbytes
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

namespace PluginScreenshot
{
    // Clears Windows 11 rounded-corner "desktop bleed" from rectangular window captures
    // by masking outside the DWM rounded path to transparent pixels.
    internal static class WindowCornerHelper
    {
        // DWM_WINDOW_CORNER_PREFERENCE
        private const int DWMWCP_DEFAULT    = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        // Design-time radii at 96 DPI (Win11 system chrome)
        private const float RoundRadiusDip      = 8f;
        private const float RoundSmallRadiusDip = 4f;

        private static readonly Version Windows11Version = new Version(10, 0, 22000);

        public static bool IsWindows11OrGreater()
        {
            try
            {
                // Prefer build number via RtlGetVersion -- Environment.OSVersion is often capped.
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

        // Returns the pixel corner radius for hWnd, or 0 if no rounding.
        public static int GetCornerRadiusPixels(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindows11OrGreater())
                return 0;

            try
            {
                if (IsZoomed(hWnd))
                    return 0; // Maximized windows are square on Win11

                int preference = DWMWCP_DEFAULT;
                int hr = NativeMethods.DwmGetWindowAttribute(
                    hWnd,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    out preference,
                    sizeof(int));

                if (hr != 0)
                    preference = DWMWCP_DEFAULT;

                float dipRadius;
                switch (preference)
                {
                    case DWMWCP_DONOTROUND:
                        return 0;
                    case DWMWCP_ROUNDSMALL:
                        dipRadius = RoundSmallRadiusDip;
                        break;
                    case DWMWCP_ROUND:
                    case DWMWCP_DEFAULT:
                    default:
                        // Most top-level app windows use standard rounding under DEFAULT.
                        dipRadius = RoundRadiusDip;
                        break;
                }

                uint dpi = 96;
                try { dpi = GetDpiForWindow(hWnd); }
                catch { dpi = 96; }
                if (dpi == 0) dpi = 96;

                int px = (int)Math.Round(dipRadius * dpi / 96.0);
                return Math.Max(0, px);
            }
            catch (Exception ex)
            {
                Logger.Log("WindowCornerHelper.GetCornerRadiusPixels: " + ex.Message);
                return 0;
            }
        }

        // If the window has rounded corners, returns a new 32bpp ARGB bitmap with
        // outside-corner pixels cleared. Otherwise returns source unchanged.
        // When a new bitmap is returned, source is disposed.
        public static Bitmap ApplyRoundedCornersIfNeeded(Bitmap source, IntPtr hWnd)
        {
            if (source == null || hWnd == IntPtr.Zero)
                return source;

            int radius = GetCornerRadiusPixels(hWnd);
            if (radius <= 0)
                return source;

            Logger.Log("WindowCornerHelper: applying rounded corners, radius=" + radius + "px");
            Bitmap rounded = ApplyRoundedCorners(source, radius);
            source.Dispose();
            return rounded;
        }

        // Draws source clipped to a rounded rectangle onto a transparent canvas.
        public static Bitmap ApplyRoundedCorners(Bitmap source, int cornerRadius)
        {
            if (source == null)
                return null;
            if (cornerRadius <= 0)
                return new Bitmap(source);

            int w = source.Width;
            int h = source.Height;
            float radius = Math.Min(cornerRadius, Math.Min(w, h) / 2f);

            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;

                using (var path = CreateRoundedRectanglePath(new RectangleF(0, 0, w, h), radius))
                using (var brush = new TextureBrush(source))
                {
                    g.FillPath(brush, path);
                }
            }
            return result;
        }

        private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
        {
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

            // Top-left
            path.AddArc(arc, 180, 90);
            // Top-right
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            // Bottom-right
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            // Bottom-left
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
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

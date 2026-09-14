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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace PluginScreenshot
{
    public static class ScreenshotManager
    {
        private static string _lastSavedPath = "";

        // Full path of the last successfully saved screenshot this session.
        public static string LastSavedPath => _lastSavedPath ?? "";

        public static void OpenLastSaved()
        {
            OpenFile(_lastSavedPath);
        }

        internal static void OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Logger.Log("OpenFile: missing or empty path: " + path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log("OpenFile error: " + ex.Message);
            }
        }

        public static void DrawCursor(Graphics g, Rectangle bounds)
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.CURSORINFO)) };
            if (!NativeMethods.GetCursorInfo(out ci) || ci.flags != NativeMethods.CURSOR_SHOWING)
                return;

            if (!NativeMethods.GetIconInfo(ci.hCursor, out NativeMethods.ICONINFO iconInfo))
                return;

            // GetIconInfo allocates mask/color bitmaps — must DeleteObject both.
            try
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    int x = ci.ptScreenPos.x - bounds.Left - iconInfo.xHotspot;
                    int y = ci.ptScreenPos.y - bounds.Top - iconInfo.yHotspot;
                    NativeMethods.DrawIcon(hdc, x, y, ci.hCursor);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            finally
            {
                if (iconInfo.hbmMask != IntPtr.Zero)
                    NativeMethods.DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    NativeMethods.DeleteObject(iconInfo.hbmColor);
            }
        }
        private static void WithHighDpiContext(Action action)
        {
            IntPtr old = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            try { action(); }
            finally { NativeMethods.SetThreadDpiAwarenessContext(old); }
        }
        public static void TakeFullScreen(Settings settings)
        {
            if (string.IsNullOrEmpty(settings.SavePath)) return;
            WithHighDpiContext(() =>
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                using (var bmp = new Bitmap(bounds.Width, bounds.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    if (settings.ShowCursor)
                        DrawCursor(g, bounds);
                    SaveImageSafely(bmp, settings);
                }
            });
            if (settings.ShowNotification)
            {
                Logger.Log("TakeFullScreen: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath, "Full Screen", settings);
            }
            ExecuteFinishAction(settings);
        }
        public static void TakePredefined(Settings settings)
        {
            var r = settings.PredefinedRegion;
            if (string.IsNullOrEmpty(settings.SavePath) || r.Width <= 0 || r.Height <= 0)
                return;
            WithHighDpiContext(() =>
            {
                using (var bmp = new Bitmap(r.Width, r.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    if (settings.ShowCursor)
                        DrawCursor(g, r);
                    SaveImageSafely(bmp, settings);
                }
            });
            if (settings.ShowNotification)
            {
                Logger.Log("TakePredefined: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath, "Predefined Region", settings);
            }
            ExecuteFinishAction(settings);
        }
        public static void TakeCustom(Settings settings, Action finishCallback)
        {
            Logger.Log($"TakeCustom() called. SavePath='{settings.SavePath}'  ShowCursor={settings.ShowCursor}");
            if (string.IsNullOrWhiteSpace(settings.SavePath))
            {
                Logger.Log("TakeCustom: SavePath is empty, aborting custom capture.");
                return;
            }
            CustomScreenshotForm.RunModal(settings, finishCallback);
        }

        // Open Smart Window Snap overlay to store a HWND only (no capture).
        public static void PickWindowHandle(Settings settings)
        {
            Logger.Log("PickWindowHandle() called.");
            CustomScreenshotForm.RunModal(settings, null, CustomScreenshotMode.WindowHandlePick);
        }

        // Capture previously stored HWND from -wh. Clears store if invalid.
        public static void TakeStoredWindowScreenshot(Settings settings)
        {
            if (!StoredWindowTarget.TryGetValid(out IntPtr hWnd, out string title))
            {
                Logger.Log("TakeStoredWindowScreenshot: no valid stored window; aborting.");
                return;
            }
            TakeWindowScreenshot(settings, hWnd, string.IsNullOrEmpty(title) ? "Stored Window" : title);
        }

        public static void TakeWindowScreenshot(Settings settings, string windowTitle)
        {
            Logger.Log($"TakeWindowScreenshot() called. WindowTitle='{windowTitle}', UsePrintWindow={settings.UsePrintWindow}");
            if (string.IsNullOrEmpty(settings.SavePath))
            {
                Logger.Log("TakeWindowScreenshot: SavePath is empty, aborting.");
                return;
            }
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                Logger.Log("TakeWindowScreenshot: WindowTitle is empty, aborting.");
                return;
            }

            IntPtr hWnd = IntPtr.Zero;
            WithHighDpiContext(() =>
            {
                hWnd = NativeMethods.FindWindow(null, windowTitle);
            });

            if (hWnd == IntPtr.Zero)
            {
                Logger.Log($"TakeWindowScreenshot: Window '{windowTitle}' not found.");
                return;
            }

            TakeWindowScreenshot(settings, hWnd, windowTitle);
        }

        public static void TakeWindowScreenshot(Settings settings, IntPtr hWnd, string displayName)
        {
            Logger.Log($"TakeWindowScreenshot(HWND) called. HWND={hWnd.ToInt64()}, DisplayName='{displayName}', UsePrintWindow={settings.UsePrintWindow}");
            if (string.IsNullOrEmpty(settings.SavePath))
            {
                Logger.Log("TakeWindowScreenshot: SavePath is empty, aborting.");
                return;
            }
            if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            {
                Logger.Log("TakeWindowScreenshot: HWND is invalid, aborting.");
                return;
            }

            WithHighDpiContext(() =>
            {
                if (!NativeMethods.IsWindow(hWnd))
                {
                    Logger.Log("TakeWindowScreenshot: HWND became invalid before capture.");
                    return;
                }

                Rectangle bounds = GetWindowBounds(hWnd);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Logger.Log($"TakeWindowScreenshot: Invalid window dimensions {bounds.Width}x{bounds.Height}");
                    return;
                }

                Logger.Log($"TakeWindowScreenshot: Capturing window at {bounds}");
                Bitmap bmp = new Bitmap(bounds.Width, bounds.Height);
                try
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        if (settings.UsePrintWindow)
                        {
                            Logger.Log("TakeWindowScreenshot: Using PrintWindow API");
                            IntPtr hdc = g.GetHdc();
                            try
                            {
                                bool result = NativeMethods.PrintWindow(hWnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                                if (!result)
                                {
                                    Logger.Log("TakeWindowScreenshot: PrintWindow failed, falling back to CopyFromScreen");
                                    g.ReleaseHdc(hdc);
                                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                                }
                                else
                                {
                                    g.ReleaseHdc(hdc);
                                    Logger.Log("TakeWindowScreenshot: PrintWindow succeeded");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"TakeWindowScreenshot: PrintWindow exception: {ex.Message}");
                                try { g.ReleaseHdc(hdc); } catch { }
                                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                            }
                        }
                        else
                        {
                            Logger.Log("TakeWindowScreenshot: Using CopyFromScreen");
                            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        }
                        if (settings.ShowCursor)
                            DrawCursor(g, bounds);
                    }

                    bmp = settings.RoundWindowCorners
                        ? WindowCornerHelper.ApplyRoundedCornersIfNeeded(bmp, hWnd)
                        : bmp;
                    SaveImageSafely(bmp, settings);
                }
                finally
                {
                    if (bmp != null)
                        bmp.Dispose();
                }
            });
            if (settings.ShowNotification)
            {
                Logger.Log("TakeWindowScreenshot: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath,
                    string.IsNullOrEmpty(displayName) ? "Window" : $"Window: {displayName}",
                    settings);
            }
            ExecuteFinishAction(settings);
        }

        private static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            int hr = NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT dwmRect,
                Marshal.SizeOf(typeof(NativeMethods.RECT)));
            if (hr == 0)
            {
                int w = dwmRect.Right - dwmRect.Left;
                int h = dwmRect.Bottom - dwmRect.Top;
                if (w > 0 && h > 0)
                    return new Rectangle(dwmRect.Left, dwmRect.Top, w, h);
            }

            if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
            {
                return new Rectangle(rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
            return Rectangle.Empty;
        }
        // Captures a screen rectangle into a new bitmap (multi-monitor stitch).
        // When roundCornersForWindow is a top-level Win11 window, rounded corners
        // are masked so desktop pixels do not appear in the corners.
        // Caller owns and must dispose the returned bitmap. Returns null on failure.
        public static Bitmap CaptureRegionToBitmap(Rectangle rect, Settings settings,
            IntPtr roundCornersForWindow = default(IntPtr))
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                Logger.Log("CaptureRegionToBitmap: invalid rectangle.");
                return null;
            }

            Bitmap finalBmp = null;
            try
            {
                finalBmp = new Bitmap(rect.Width, rect.Height);
                using (var finalG = Graphics.FromImage(finalBmp))
                {
                    foreach (var scr in Screen.AllScreens)
                    {
                        var inter = Rectangle.Intersect(rect, scr.Bounds);
                        if (inter.Width <= 0 || inter.Height <= 0)
                            continue;
                        using (var part = new Bitmap(inter.Width, inter.Height))
                        using (var g = Graphics.FromImage(part))
                        {
                            g.CopyFromScreen(inter.Location, Point.Empty, inter.Size);
                            if (settings != null && settings.ShowCursor)
                                DrawCursor(g, inter); // screen-space bounds for hotspot math
                            finalG.DrawImage(part,
                                             inter.Left - rect.Left,
                                             inter.Top - rect.Top);
                        }
                    }
                }

                if (roundCornersForWindow != IntPtr.Zero &&
                    settings != null && settings.RoundWindowCorners)
                    finalBmp = WindowCornerHelper.ApplyRoundedCornersIfNeeded(finalBmp, roundCornersForWindow);

                return finalBmp;
            }
            catch (Exception ex)
            {
                Logger.Log("CaptureRegionToBitmap: " + ex.Message);
                if (finalBmp != null)
                    finalBmp.Dispose();
                return null;
            }
        }

        public static void CompositeCapture(Rectangle rect, Settings settings,
            IntPtr roundCornersForWindow = default(IntPtr))
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.SavePath))
            {
                Logger.Log("CompositeCapture: no SavePath, skipping.");
                return;
            }
            using (var finalBmp = CaptureRegionToBitmap(rect, settings, roundCornersForWindow))
            {
                if (finalBmp == null)
                {
                    Logger.Log("CompositeCapture: capture failed.");
                    return;
                }
                SaveImageSafely(finalBmp, settings);
            }
            if (settings.ShowNotification)
            {
                Logger.Log("CompositeCapture: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath, "Custom Region", settings);
            }
        }
        private static void SaveImageSafely(Bitmap source, Settings settings)
        {
            try
            {
                if (source == null) { Logger.Log("SaveImageSafely: source bitmap is null"); return; }
                if (settings == null) { Logger.Log("SaveImageSafely: settings is null"); return; }
                string path = settings.SavePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    Logger.Log("SaveImageSafely: SavePath is null or empty");
                    return;
                }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var fmt = GetImageFormat(path);

                // JPEG has no alpha -- flatten transparent rounded corners onto black.
                if (fmt.Guid == ImageFormat.Jpeg.Guid &&
                    (source.PixelFormat == PixelFormat.Format32bppArgb ||
                     source.PixelFormat == PixelFormat.Format32bppPArgb))
                {
                    using (var opaque = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
                    using (var g = Graphics.FromImage(opaque))
                    {
                        g.Clear(Color.Black);
                        g.DrawImage(source, 0, 0, source.Width, source.Height);
                        SaveBitmapToStream(opaque, path, fmt, settings);
                    }
                    _lastSavedPath = path;
                    return;
                }

                using (var clone = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(clone))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(source, 0, 0);
                    SaveBitmapToStream(clone, path, fmt, settings);
                }
                _lastSavedPath = path;
            }
            catch (Exception ex)
            {
                Logger.Log("Error saving screenshot: " + ex.ToString());
            }
        }

        private static void SaveBitmapToStream(Bitmap bitmap, string path, ImageFormat fmt, Settings settings)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (fmt.Guid == ImageFormat.Jpeg.Guid)
                {
                    var enc = ImageCodecInfo
                                .GetImageEncoders()
                                .FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
                    if (enc == null)
                    {
                        Logger.Log("SaveImageSafely: JPEG encoder not found, falling back to PNG");
                        bitmap.Save(fs, ImageFormat.Png);
                    }
                    else
                    {
                        using (var pars = new EncoderParameters(1))
                        {
                            pars.Param[0] = new EncoderParameter(Encoder.Quality, settings.JpegQuality);
                            bitmap.Save(fs, enc, pars);
                        }
                    }
                }
                else
                {
                    bitmap.Save(fs, fmt);
                }
            }
        }
        private static ImageFormat GetImageFormat(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg") return ImageFormat.Jpeg;
            if (ext == ".bmp") return ImageFormat.Bmp;
            if (ext == ".tiff" || ext == ".tif") return ImageFormat.Tiff;
            return ImageFormat.Png;
        }
        public static void ExecuteFinishAction(Settings settings)
        {
            if (string.IsNullOrEmpty(settings.FinishAction)) return;
            try
            {
                BangQueue.Enqueue(settings.Api, settings.FinishAction);
            }
            catch (Exception ex)
            {
                Logger.Log("Error running finish action: " + ex.Message);
            }
        }
        private static void ShowNotificationWithImage(string imagePath, string captureType,
                                                      Settings settings)
        {
            try
            {
                if (!File.Exists(imagePath))
                {
                    Logger.Log($"ShowNotificationWithImage: Image file not found at '{imagePath}'");
                    return;
                }
                Logger.Log($"ShowNotificationWithImage: Creating notification for '{captureType}'");
                if (settings.PlayNotificationSound)
                    NotificationSound.Play();

                // Apply theme to shared UI windows before showing
                GifEncodingWindow.SetTheme(settings.UITheme);
                GifStateDialog.SetTheme(settings.UITheme);

                var notificationThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        Application.Run(new NotificationForm(imagePath, captureType, settings));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Notification thread error: {ex.Message}");
                    }
                });
                notificationThread.SetApartmentState(System.Threading.ApartmentState.STA);
                notificationThread.IsBackground = true;
                notificationThread.Start();
                Logger.Log($"Notification thread started for '{captureType}'.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing notification: {ex.Message}");
            }
        }
    }
}
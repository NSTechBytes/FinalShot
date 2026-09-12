using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace PluginScreenshot
{
    public static class ScreenshotManager
    {
        public static void DrawCursor(Graphics g, Rectangle bounds)
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.CURSORINFO)) };
            if (NativeMethods.GetCursorInfo(out ci) && ci.flags == NativeMethods.CURSOR_SHOWING)
            {
                if (NativeMethods.GetIconInfo(ci.hCursor, out NativeMethods.ICONINFO iconInfo))
                {
                    IntPtr hdc = g.GetHdc();
                    int x = ci.ptScreenPos.x - bounds.Left - iconInfo.xHotspot;
                    int y = ci.ptScreenPos.y - bounds.Top - iconInfo.yHotspot;
                    NativeMethods.DrawIcon(hdc, x, y, ci.hCursor);
                    g.ReleaseHdc();
                }
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
            WithHighDpiContext(() =>
            {
                IntPtr hWnd = NativeMethods.FindWindow(null, windowTitle);
                if (hWnd == IntPtr.Zero)
                {
                    Logger.Log($"TakeWindowScreenshot: Window '{windowTitle}' not found.");
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
                ShowNotificationWithImage(settings.SavePath, $"Window: {windowTitle}", settings);
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
        /// <summary>
        /// Captures a screen rectangle into a new bitmap (multi-monitor stitch).
        /// When <paramref name="roundCornersForWindow"/> is a top-level Win11 window,
        /// rounded corners are masked so desktop pixels do not appear in the corners.
        /// Caller owns and must dispose the returned bitmap. Returns null on failure.
        /// </summary>
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
                                DrawCursor(g, new Rectangle(Point.Empty, inter.Size));
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

                // JPEG has no alpha — flatten transparent rounded corners onto black.
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
                        var pars = new EncoderParameters(1);
                        pars.Param[0] = new EncoderParameter(Encoder.Quality, settings.JpegQuality);
                        bitmap.Save(fs, enc, pars);
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
                settings.Api.Execute(settings.FinishAction);
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
                try { System.Media.SystemSounds.Asterisk.Play(); }
                catch (Exception ex) { Logger.Log($"Notification sound error: {ex.Message}"); }

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
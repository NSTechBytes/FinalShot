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
        // Cursor Drawing

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

        // DPI Context Helper

        private static void WithHighDpiContext(Action action)
        {
            IntPtr old = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            try { action(); }
            finally { NativeMethods.SetThreadDpiAwarenessContext(old); }
        }

        // Full-Screen Capture

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
                ShowNotificationWithImage(settings.SavePath, "Full Screen");
            }

            ExecuteFinishAction(settings);
        }

        // Predefined Region Capture

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
                ShowNotificationWithImage(settings.SavePath, "Predefined Region");
            }

            ExecuteFinishAction(settings);
        }

        // Custom Selection Capture

        public static void TakeCustom(Settings settings, Action finishCallback)
        {
            Logger.Log($"TakeCustom() called. SavePath='{settings.SavePath}'  ShowCursor={settings.ShowCursor}");
            if (string.IsNullOrWhiteSpace(settings.SavePath))
            {
                Logger.Log("TakeCustom: SavePath is empty, aborting custom capture.");
                return;
            }
            Application.Run(new CustomScreenshotForm(settings, finishCallback));
        }

        // Window Capture

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

                if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
                {
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;

                    if (width <= 0 || height <= 0)
                    {
                        Logger.Log($"TakeWindowScreenshot: Invalid window dimensions {width}x{height}");
                        return;
                    }

                    Rectangle bounds = new Rectangle(rect.Left, rect.Top, width, height);
                    Logger.Log($"TakeWindowScreenshot: Capturing window at {bounds}");

                    using (var bmp = new Bitmap(width, height))
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
                        SaveImageSafely(bmp, settings);
                    }
                }
                else
                {
                    Logger.Log($"TakeWindowScreenshot: Failed to get window rect for '{windowTitle}'");
                }
            });

            if (settings.ShowNotification)
            {
                Logger.Log("TakeWindowScreenshot: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath, $"Window: {windowTitle}");
            }

            ExecuteFinishAction(settings);
        }

        // Composite (Multi-Monitor) Capture

        public static void CompositeCapture(Rectangle rect, Settings settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.SavePath))
            {
                Logger.Log("CompositeCapture: no SavePath, skipping.");
                return;
            }

            using (var finalBmp = new Bitmap(rect.Width, rect.Height))
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
                        if (settings.ShowCursor)
                            DrawCursor(g, new Rectangle(Point.Empty, inter.Size));
                        finalG.DrawImage(part,
                                         inter.Left - rect.Left,
                                         inter.Top - rect.Top);
                    }
                }

                SaveImageSafely(finalBmp, settings);
            }

            if (settings.ShowNotification)
            {
                Logger.Log("CompositeCapture: ShowNotification is enabled");
                ShowNotificationWithImage(settings.SavePath, "Custom Region");
            }
        }

        // Image Saving

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

                using (var clone = new Bitmap(source.Width, source.Height, source.PixelFormat))
                using (var g = Graphics.FromImage(clone))
                {
                    g.DrawImageUnscaled(source, 0, 0);

                    var fmt = GetImageFormat(path);
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                    if (fmt.Guid == ImageFormat.Jpeg.Guid)
                    {
                        var enc = ImageCodecInfo
                                    .GetImageEncoders()
                                    .FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
                        if (enc == null)
                        {
                            Logger.Log("SaveImageSafely: JPEG encoder not found, falling back to PNG");
                            clone.Save(fs, ImageFormat.Png);
                        }
                        else
                        {
                            var pars = new EncoderParameters(1);
                            pars.Param[0] = new EncoderParameter(Encoder.Quality, settings.JpegQuality);
                            clone.Save(fs, enc, pars);
                        }
                    }
                    else
                    {
                        clone.Save(fs, fmt);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error saving screenshot: " + ex.ToString());
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

        // Finish Action

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

        // Notification

        private static void ShowNotificationWithImage(string imagePath, string captureType)
        {
            try
            {
                if (!File.Exists(imagePath))
                {
                    Logger.Log($"ShowNotificationWithImage: Image file not found at '{imagePath}'");
                    return;
                }

                Logger.Log($"ShowNotificationWithImage: Creating notification for '{captureType}'");

                try
                {
                    SystemSounds.Asterisk.Play();
                    Logger.Log("Notification sound played");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to play notification sound: {ex.Message}");
                }

                var notificationThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        Logger.Log("Notification thread started");
                        Application.Run(new NotificationForm(imagePath, captureType));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Notification thread error: {ex.Message}");
                    }
                });
                notificationThread.SetApartmentState(System.Threading.ApartmentState.STA);
                notificationThread.IsBackground = true;
                notificationThread.Start();

                Logger.Log($"Notification thread started for '{captureType}' capture.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error showing notification: {ex.Message}");
            }
        }
    }
}

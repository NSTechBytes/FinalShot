using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Media;
using Microsoft.Win32;
using Rainmeter;

namespace PluginScreenshot
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        public static bool DebugEnabled = false;
        public static string LogFilePath = "FinalShotDebug.log";
        public static long MaxLogFileSize = 5 * 1024 * 1024;

        public static void Log(string message)
        {
            if (!DebugEnabled) return;
            try
            {
                lock (_lock)
                {
                    RotateIfNeeded();
                    string logMessage = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                        " " + message + Environment.NewLine;
                    File.AppendAllText(LogFilePath, logMessage);
                }
            }
            catch {}
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Length >= MaxLogFileSize)
                    {
                        string name = Path.GetFileNameWithoutExtension(LogFilePath);
                        string ext = Path.GetExtension(LogFilePath);
                        string archive = string.Format("{0}_{1:yyyyMMddHHmmss}{2}",
                                                       name, DateTime.Now, ext);
                        File.Move(LogFilePath, archive);
                    }
                }
            }
            catch {}
        }
    }

    public class Settings
    {
        public API Api { get; }
        public string SavePath { get; private set; }
        public string FinishAction { get; private set; }
        public bool ShowCursor { get; private set; }
        public int JpegQuality { get; private set; }
        public Rectangle PredefinedRegion { get; private set; }
        public bool ShowNotification { get; private set; }

        public Settings(API api)
        {
            Api = api;
            SavePath = api.ReadString("SavePath", "");
            FinishAction = api.ReadString("ScreenshotFinishAction", "");
            ShowCursor = api.ReadInt("ShowCursor", 0) > 0;
            JpegQuality = api.ReadInt("JpgQuality", 70);
            ShowNotification = api.ReadInt("ShowNotification", 0) > 0;
            int x = api.ReadInt("PredefX", 0);
            int y = api.ReadInt("PredefY", 0);
            int w = api.ReadInt("PredefWidth", 0);
            int h = api.ReadInt("PredefHeight", 0);
            PredefinedRegion = new Rectangle(x, y, w, h);

            Logger.DebugEnabled = api.ReadInt("DebugLog", 0) == 1;
            string dbg = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(dbg))
                Logger.LogFilePath = dbg;

            Logger.Log("Settings reloaded. SavePath=" + SavePath);
        }
    }

    public static class ScreenshotManager
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
        private static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;         
            public int xHotspot;     
            public int yHotspot;       
            public IntPtr hbmMask;    
            public IntPtr hbmColor;   
        }

        private const int CURSOR_SHOWING = 0x00000001;

        public static void DrawCursor(Graphics g, Rectangle bounds)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (GetCursorInfo(out ci) && ci.flags == CURSOR_SHOWING)
            {
                if (GetIconInfo(ci.hCursor, out ICONINFO iconInfo))
                {
                    IntPtr hdc = g.GetHdc();
                    int x = ci.ptScreenPos.x - bounds.Left - iconInfo.xHotspot;
                    int y = ci.ptScreenPos.y - bounds.Top - iconInfo.yHotspot;
                    DrawIcon(hdc, x, y, ci.hCursor);
                    g.ReleaseHdc();
                }
            }
        }

        private static void WithHighDpiContext(Action action)
        {
            IntPtr old = SetThreadDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2);
            try { action(); }
            finally { SetThreadDpiAwarenessContext(old); }
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
                ShowNotificationWithImage(settings.SavePath, "Full Screen");
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
                ShowNotificationWithImage(settings.SavePath, "Predefined Region");
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
            Application.Run(new CustomScreenshotForm(settings, finishCallback));
        }

        public static void TakeWindowScreenshot(Settings settings, string windowTitle)
        {
            Logger.Log($"TakeWindowScreenshot() called. WindowTitle='{windowTitle}'");
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
                IntPtr hWnd = FindWindow(null, windowTitle);
                if (hWnd == IntPtr.Zero)
                {
                    Logger.Log($"TakeWindowScreenshot: Window '{windowTitle}' not found.");
                    return;
                }

                if (GetWindowRect(hWnd, out RECT rect))
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
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
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


        public static void CompositeCapture(Rectangle rect, Settings settings)
        {
            // ←—— Guard against missing SavePath
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

                // Play Windows notification sound
                try
                {
                    SystemSounds.Asterisk.Play();
                    Logger.Log("Notification sound played");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to play notification sound: {ex.Message}");
                }

                // Create notification form on a separate thread
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

    public class CustomScreenshotForm : Form
    {
        private readonly Settings _settings;
        private readonly Action _finishCallback;
        private Point _start;
        private Rectangle _selection;
        private bool _dragging;

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        private static readonly IntPtr DPI_CTX = new IntPtr(-4);

        public CustomScreenshotForm(Settings settings, Action finishCallback)
        {
            SetThreadDpiAwarenessContext(DPI_CTX);
            _settings = settings;
            _finishCallback = finishCallback;

            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Black;
            Opacity = 0.25;
            TopMost = true;
            Cursor = Cursors.Cross;
            StartPosition = FormStartPosition.Manual;
            Location = SystemInformation.VirtualScreen.Location;

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            Paint += OnPaint;
        }

        private void OnMouseDown(object s, MouseEventArgs e)
        {
            _start = e.Location;
            _dragging = true;
        }

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            if (!_dragging) return;
            int x = Math.Min(_start.X, e.X);
            int y = Math.Min(_start.Y, e.Y);
            int w = Math.Abs(_start.X - e.X);
            int h = Math.Abs(_start.Y - e.Y);
            _selection = new Rectangle(x, y, w, h);
            Invalidate();
        }

        private void OnMouseUp(object s, MouseEventArgs e)
        {
            _dragging = false;
            Logger.Log($"CustomScreenshotForm: user dropped selection {_selection}");
            if (_selection.Width < 1 || _selection.Height < 1)
            {
                Logger.Log("CustomScreenshotForm: selection too small, closing.");
                Close();
                return;
            }

            Hide();
            var absRect = new Rectangle(
                Bounds.Left + _selection.X,
                Bounds.Top + _selection.Y,
                _selection.Width,
                _selection.Height);

            ScreenshotManager.CompositeCapture(absRect, _settings);
            _finishCallback();
            Close();
        }

        private void OnPaint(object s, PaintEventArgs e)
        {
            if (_dragging)
            {
                using (var pen = new Pen(Color.Blue, 3))
                {
                    pen.DashStyle = DashStyle.Dash;
                    e.Graphics.DrawRectangle(pen, _selection);
                }
            }
        }
    }

    public class NotificationForm : Form
    {
        private readonly Timer _autoCloseTimer;
        private readonly Timer _fadeOutTimer;
        private readonly string _imagePath;
        private const int NotificationWidth = 380;
        private const int NotificationHeight = 120;
        private const int DisplayDuration = 4000; // 4 seconds
        private const int FadeOutDuration = 500; // 0.5 seconds
        private double _opacity = 1.0;
        private readonly bool _isDarkMode;

        public NotificationForm(string imagePath, string captureType)
        {
            _imagePath = imagePath;
            _isDarkMode = IsWindowsDarkMode();

            // Form settings
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            Width = NotificationWidth;
            Height = NotificationHeight;
            BackColor = _isDarkMode ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            Opacity = 0;

            // Position at bottom-right of screen
            var workingArea = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                workingArea.Right - Width - 20,
                workingArea.Bottom - Height - 20
            );

            // Create UI
            CreateNotificationUI(captureType);

            // Auto-close timer
            _autoCloseTimer = new Timer { Interval = DisplayDuration };
            _autoCloseTimer.Tick += (s, e) =>
            {
                _autoCloseTimer.Stop();
                StartFadeOut();
            };

            // Fade-out timer
            _fadeOutTimer = new Timer { Interval = 20 };
            _fadeOutTimer.Tick += FadeOutTick;

            // Click to close
            Click += (s, e) => StartFadeOut();

            // Show with fade-in effect
            Load += (s, e) =>
            {
                FadeIn();
                _autoCloseTimer.Start();
            };
        }

        private bool IsWindowsDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value != null)
                        {
                            return (int)value == 0; // 0 = Dark Mode, 1 = Light Mode
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to detect Windows theme: {ex.Message}");
            }
            return true; // Default to dark mode if detection fails
        }

        private void CreateNotificationUI(string captureType)
        {
            // Color scheme based on theme
            Color panelBackColor = _isDarkMode ? Color.FromArgb(40, 40, 40) : Color.FromArgb(250, 250, 250);
            Color textColor = _isDarkMode ? Color.White : Color.FromArgb(30, 30, 30);
            Color subtitleColor = _isDarkMode ? Color.FromArgb(180, 180, 180) : Color.FromArgb(100, 100, 100);
            Color closeButtonColor = _isDarkMode ? Color.FromArgb(150, 150, 150) : Color.FromArgb(100, 100, 100);
            Color closeButtonHoverColor = _isDarkMode ? Color.White : Color.Black;
            Color thumbnailBorderColor = _isDarkMode ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 200, 200);

            // Main panel
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = panelBackColor
            };

            // Thumbnail image
            var thumbnail = new PictureBox
            {
                Location = new Point(10, 10),
                Size = new Size(100, 100),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = thumbnailBorderColor
            };

            try
            {
                using (var img = Image.FromFile(_imagePath))
                {
                    thumbnail.Image = new Bitmap(img, thumbnail.Size);
                }
            }
            catch
            {
                thumbnail.BackColor = thumbnailBorderColor;
            }

            // Success icon/text
            var successLabel = new Label
            {
                Text = "FinalShot",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 200, 100),
                Location = new Point(120, 10),
                Size = new Size(200, 30),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Screenshot Captured!",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = textColor,
                Location = new Point(120, 45),
                Size = new Size(250, 25),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Subtitle
            var subtitleLabel = new Label
            {
                Text = captureType,
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                ForeColor = subtitleColor,
                Location = new Point(120, 70),
                Size = new Size(250, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Close button
            var closeButton = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = closeButtonColor,
                Location = new Point(NotificationWidth - 35, 5),
                Size = new Size(25, 25),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            closeButton.Click += (s, e) => StartFadeOut();
            closeButton.MouseEnter += (s, e) => closeButton.ForeColor = closeButtonHoverColor;
            closeButton.MouseLeave += (s, e) => closeButton.ForeColor = closeButtonColor;

            // Add all controls
            panel.Controls.Add(thumbnail);
            panel.Controls.Add(successLabel);
            panel.Controls.Add(titleLabel);
            panel.Controls.Add(subtitleLabel);
            panel.Controls.Add(closeButton);
            Controls.Add(panel);

            // Make labels click-through to panel
            foreach (Control ctrl in panel.Controls)
            {
                if (ctrl is Label && ctrl != closeButton)
                {
                    ctrl.Click += (s, e) => StartFadeOut();
                }
            }
        }

        private void FadeIn()
        {
            var fadeInTimer = new Timer { Interval = 10 };
            double targetOpacity = 0.95;
            double step = 0.05;

            fadeInTimer.Tick += (s, e) =>
            {
                _opacity += step;
                if (_opacity >= targetOpacity)
                {
                    _opacity = targetOpacity;
                    Opacity = _opacity;
                    fadeInTimer.Stop();
                    fadeInTimer.Dispose();
                }
                else
                {
                    Opacity = _opacity;
                }
            };
            fadeInTimer.Start();
        }

        private void StartFadeOut()
        {
            if (_fadeOutTimer.Enabled) return;
            _autoCloseTimer.Stop();
            _fadeOutTimer.Start();
        }

        private void FadeOutTick(object sender, EventArgs e)
        {
            _opacity -= 0.05;
            if (_opacity <= 0)
            {
                _fadeOutTimer.Stop();
                Close();
            }
            else
            {
                Opacity = _opacity;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _autoCloseTimer?.Dispose();
                _fadeOutTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            var api = new API(rm);
            var settings = new Settings(api);
            data = GCHandle.ToIntPtr(GCHandle.Alloc(settings));
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            var handle = GCHandle.FromIntPtr(data);
            var settings = new Settings(new API(rm));
            handle.Target = settings;
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            return 0.0;
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            string cmd = Marshal.PtrToStringUni(args);
            var settings = (Settings)GCHandle.FromIntPtr(data).Target;

            if (string.Equals(cmd, "-fs", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakeFullScreen(settings);
            }
            else if (string.Equals(cmd, "-ps", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakePredefined(settings);
            }
            else if (string.Equals(cmd, "-cs", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakeCustom(settings, () =>
                {
                    Logger.Log("Custom capture done, calling FinishAction.");
                    ScreenshotManager.ExecuteFinishAction(settings);
                });
            }
            else if (cmd.StartsWith("-ws|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring(4); // Extract everything after "-ws|"
                Logger.Log($"ExecuteBang: Window screenshot requested for '{windowTitle}'");
                ScreenshotManager.TakeWindowScreenshot(settings, windowTitle);
            }
            else if (cmd.StartsWith("ExecuteBatch ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(cmd.Split(' ')[1], out int code))
                {
                    if (code == 1) ScreenshotManager.TakeFullScreen(settings);
                    if (code == 2) ScreenshotManager.TakeCustom(settings, () => { });
                    if (code == 3) ScreenshotManager.TakePredefined(settings);
                }
            }
        }
    }
}

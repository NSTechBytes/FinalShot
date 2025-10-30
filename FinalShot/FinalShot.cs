using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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

        public Settings(API api)
        {
            Api = api;
            SavePath = api.ReadString("SavePath", "");
            FinishAction = api.ReadString("ScreenshotFinishAction", "");
            ShowCursor = api.ReadInt("ShowCursor", 0) > 0;
            JpegQuality = api.ReadInt("JpgQuality", 70);
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

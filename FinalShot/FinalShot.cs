using System;
using System.Drawing;
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
        public static bool DebugEnabled = false;
        public static string LogFilePath = "FinalShotDebug.log";

        public static void Log(string message)
        {
            if (DebugEnabled)
            {
                try
                {
                    string logMessage = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine;
                    File.AppendAllText(LogFilePath, logMessage);
                }
                catch {}
            }
        }
    }

    internal class Measure
    {
        private string savePath;        
        private string finishAction = ""; 
        private Rainmeter.API api;      
        public static bool showCursor; 
        public static int jpegQuality; 
        private string finishAction = "";  
        private Rainmeter.API api;       
        public static bool showCursor;
        private int predefX;
        private int predefY;
        private int predefWidth;
        private int predefHeight;


        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")]
        private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        private const int CURSOR_SHOWING = 0x00000001;

        public Measure(Rainmeter.API api)
        {
            this.api = api;
        }

        public void Reload(Rainmeter.API api, ref double maxValue)
        {
            savePath = api.ReadString("SavePath", "");
            finishAction = api.ReadString("ScreenshotFinishAction", "");
            showCursor = api.ReadInt("ShowCursor", 0) > 0;
            jpegQuality = api.ReadInt("JpgQuality", 70);
            predefX = api.ReadInt("PredefX", 0);
            predefY = api.ReadInt("PredefY", 0);
            predefWidth = api.ReadInt("PredefWidth", 0);
            predefHeight = api.ReadInt("PredefHeight", 0);
            bool debugEnabled = api.ReadInt("DebugLog", 0) == 1;
            Logger.DebugEnabled = debugEnabled;
            string debugPath = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(debugPath))
                Logger.LogFilePath = debugPath;

            Logger.Log("Reload complete. SavePath: " + savePath);
        }

        public static void DrawCursor(Graphics g, Rectangle bounds)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (GetCursorInfo(out ci) && ci.flags == CURSOR_SHOWING)
            {
                IntPtr hdc = g.GetHdc();
                DrawIcon(hdc,
                         ci.ptScreenPos.x - bounds.Left,
                         ci.ptScreenPos.y - bounds.Top,
                         ci.hCursor);
                g.ReleaseHdc();
            }
        }

        private void RunWithHighDpiContext(Action action)
        {
            IntPtr oldContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try { action(); }
            finally { SetThreadDpiAwarenessContext(oldContext); }
        }

        public void ExecuteBatch(int actionCode)
        {
            Logger.Log("ExecuteBatch called with actionCode: " + actionCode);
            if (actionCode == 1)
                TakeScreenshot();
            else if (actionCode == 2)
                TakeCustomScreenshot();
            else if (actionCode == 3)
                TakePredefinedScreenshot();
        }

        private void TakeScreenshot()
        {
            if (string.IsNullOrEmpty(savePath))
            {
                api.Log(API.LogType.Error, "Error: No valid SavePath specified.");
                Logger.Log("Error: No valid SavePath specified.");
                return;
            }

            RunWithHighDpiContext(() =>
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                Logger.Log("Full screenshot bounds: " + bounds);
                using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        if (showCursor)
                            DrawCursor(g, bounds);
                    }
                    SaveImage(bitmap);
                }
            });
            ExecuteFinishAction();
        }

        private void TakeCustomScreenshot()
        {
            if (string.IsNullOrEmpty(savePath))
            {
                api.Log(API.LogType.Error, "Error: No valid SavePath specified.");
                Logger.Log("Error: No valid SavePath for custom screenshot.");
                return;
            }
            Logger.Log("Launching custom screenshot form.");
            Application.Run(new CustomScreenshotForm(savePath, ExecuteFinishAction));
        }

        private void TakePredefinedScreenshot()
        {
            if (string.IsNullOrEmpty(savePath))
            {
                api.Log(API.LogType.Error, "Error: No valid SavePath specified.");
                Logger.Log("Error: No valid SavePath for predefined screenshot.");
                return;
            }
            if (predefWidth <= 0 || predefHeight <= 0)
            {
                api.Log(API.LogType.Error, "Error: Invalid predefined coordinates specified.");
                Logger.Log("Error: Invalid predefined coordinates: " + predefX + "," + predefY + " " + predefWidth + "x" + predefHeight);
                return;
            }
            RunWithHighDpiContext(() =>
            {
                Rectangle region = new Rectangle(predefX, predefY, predefWidth, predefHeight);
                Logger.Log("Predefined screenshot region: " + region);
                using (Bitmap bitmap = new Bitmap(region.Width, region.Height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(new Point(region.Left, region.Top), Point.Empty, region.Size);
                        if (showCursor)
                            DrawCursor(g, region);
                    }
                    SaveImage(bitmap);
                }
            });
            ExecuteFinishAction();
        }

        private void SaveImage(Bitmap bitmap)
        {
            try
            {
                ImageFormat format = GetImageFormat(savePath);

                if (format.Equals(ImageFormat.Jpeg))
                {
                    var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, Measure.jpegQuality);
                    bitmap.Save(savePath, encoder, encoderParams);
                }
                else
                {
                    bitmap.Save(savePath, format);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error saving custom screenshot: " + ex.Message);
            }
        }

        private ImageFormat GetImageFormat(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    return ImageFormat.Jpeg;
                case ".png":
                    return ImageFormat.Png;
                case ".bmp":
                    return ImageFormat.Bmp;
                case ".tiff":
                    return ImageFormat.Tiff;
                default:
                    return ImageFormat.Png; // fallback
            }
        }

        private void ExecuteFinishAction()
        {
            if (!string.IsNullOrEmpty(finishAction))
            {
                Logger.Log("Executing finish action: " + finishAction);
                api.Execute(finishAction);
            }
        }
    }

    public class CustomScreenshotForm : Form
    {
        private Point startPoint;      
        private Rectangle selection;   
        private string savePath;
        private bool isSelecting;
        private Action finishAction;

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        public CustomScreenshotForm(string savePath, Action finishAction)
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            this.savePath = savePath;
            this.finishAction = finishAction;
            this.DoubleBuffered = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Bounds = SystemInformation.VirtualScreen;
            this.BackColor = Color.Black;
            this.Opacity = 0.25;
            this.TopMost = true;
            this.Cursor = Cursors.Cross;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = SystemInformation.VirtualScreen.Location;

            this.MouseDown += OnMouseDown;
            this.MouseMove += OnMouseMove;
            this.MouseUp += OnMouseUp;
            this.Paint += OnPaint;

            Logger.Log("CustomScreenshotForm initialized. Bounds: " + this.Bounds);
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            startPoint = e.Location;
            isSelecting = true;
            Logger.Log("Custom screenshot started at: " + startPoint);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (isSelecting)
            {
                int x = Math.Min(startPoint.X, e.X);
                int y = Math.Min(startPoint.Y, e.Y);
                int width = Math.Abs(startPoint.X - e.X);
                int height = Math.Abs(startPoint.Y - e.Y);
                selection = new Rectangle(x, y, width, height);
                Invalidate();
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            isSelecting = false;
            Logger.Log("Custom screenshot ended with selection: " + selection);
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                Logger.Log("Selection too small, closing form.");
                this.Close();
                return;
            }
            this.Hide();
            Rectangle captureRect = new Rectangle(
                this.Bounds.Left + selection.X,
                this.Bounds.Top + selection.Y,
                selection.Width,
                selection.Height);
            Logger.Log("Capture rectangle (absolute physical coordinates): " + captureRect);
            TakeScreenshot(captureRect);
            this.Close();
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            if (isSelecting)
            {
                using (Pen borderPen = new Pen(Color.Blue, 3))
                {
                    borderPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    e.Graphics.DrawRectangle(borderPen, selection);
                }
            }
        }

        private void TakeScreenshot(Rectangle captureRect)
        {
            Logger.Log("Taking screenshot from rectangle: " + captureRect);
            bool isContained = false;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.Contains(captureRect))
                {
                    isContained = true;
                    break;
                }
            }

            if (isContained)
            {
                Logger.Log("Capture rectangle contained in one monitor; using single capture.");
                TakeSingleCapture(captureRect);
            }
            else
            {
                Logger.Log("Capture rectangle spans multiple monitors; composing capture from parts.");
                TakeCompositeCapture(captureRect);
            }
            finishAction();
        }

        private void TakeSingleCapture(Rectangle rect)
        {
            IntPtr oldContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, rect.Size);
                        if (Measure.showCursor)
                            Measure.DrawCursor(g, rect);
                    }
                    SaveImage(bitmap);
                }
            }
            finally
            {
                SetThreadDpiAwarenessContext(oldContext);
            }
        }

        private void TakeCompositeCapture(Rectangle rect)
        {
            Bitmap finalBitmap = new Bitmap(rect.Width, rect.Height);
            using (Graphics finalGraphics = Graphics.FromImage(finalBitmap))
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle intersect = Rectangle.Intersect(rect, screen.Bounds);
                    if (intersect.Width > 0 && intersect.Height > 0)
                    {
                        Logger.Log("Capturing intersection with screen (" + screen.DeviceName + "): " + intersect);
                        using (Bitmap part = new Bitmap(intersect.Width, intersect.Height))
                        {
                            using (Graphics g = Graphics.FromImage(part))
                            {
                                g.CopyFromScreen(new Point(intersect.Left, intersect.Top), Point.Empty, intersect.Size);
                                if (Measure.showCursor)
                                    Measure.DrawCursor(finalGraphics, rect);
                            }
                            finalGraphics.DrawImage(part, new Rectangle(intersect.Left - rect.Left, intersect.Top - rect.Top, intersect.Width, intersect.Height));
                        }
                    }
                }
            }
            SaveImage(finalBitmap);
        }

        private void SaveImage(Bitmap bitmap)
        {
            try
            {
                ImageFormat format = GetImageFormat(savePath);

                if (format.Equals(ImageFormat.Jpeg))
                {
                    var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, Measure.jpegQuality);
                    bitmap.Save(savePath, encoder, encoderParams);
                }
                else
                {
                    bitmap.Save(savePath, format);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error saving custom screenshot: " + ex.Message);
            }
        }
        private ImageFormat GetImageFormat(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    return ImageFormat.Jpeg;
                case ".png":
                    return ImageFormat.Png;
                case ".bmp":
                    return ImageFormat.Bmp;
                case ".tiff":
                    return ImageFormat.Tiff;
                default:
                    return ImageFormat.Png; 
            }
        }
    }

    public static class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            Rainmeter.API api = new Rainmeter.API(rm);
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new Measure(api)));
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            measure.Reload(new Rainmeter.API(rm), ref maxValue);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            return 0.0;
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            string arguments = Marshal.PtrToStringUni(args);
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            if (arguments.Equals("-fs", StringComparison.OrdinalIgnoreCase))
                measure.ExecuteBatch(1);
            else if (arguments.Equals("-cs", StringComparison.OrdinalIgnoreCase))
                measure.ExecuteBatch(2);
            else if (arguments.Equals("-ps", StringComparison.OrdinalIgnoreCase))
                measure.ExecuteBatch(3);
            else if (arguments.StartsWith("ExecuteBatch"))
            {
                string[] parts = arguments.Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[1], out int actionCode))
                    measure.ExecuteBatch(actionCode);
            }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Rainmeter;

namespace PluginScreenshot
{
    internal class Measure
    {
        private string savePath; // Store the save path from the .ini file
        private string finishAction = ""; // Action to execute after screenshot is finished
        private Rainmeter.API api; // Store reference to API

        public Measure(Rainmeter.API api)
        {
            this.api = api; // Store API reference
        }

        public void Reload(Rainmeter.API api, ref double maxValue)
        {
            // Read the configuration from the .ini file
            savePath = api.ReadString("SavePath", ""); // Do not set default path here
            finishAction = api.ReadString("ScreenshotFinishAction", "");
        }

        public void ExecuteBatch(int actionCode)
        {
            if (actionCode == 1) // Full screenshot
            {
                TakeScreenshot();
            }
            else if (actionCode == 2) // Custom screenshot
            {
                TakeCustomScreenshot();
            }
        }

        private void TakeScreenshot()
        {
            if (string.IsNullOrEmpty(savePath))
            {
                api.Log(API.LogType.Error, "Error: No valid SavePath specified in the .ini file.");
                return; // If save path is not specified, do nothing
            }

            // Take a screenshot of the entire screen
            Rectangle bounds = Screen.GetBounds(Point.Empty);
            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                }

                SaveImage(bitmap);
            }

            // Execute the finish action after the screenshot is taken
            ExecuteFinishAction();
        }

        private void TakeCustomScreenshot()
        {
            if (string.IsNullOrEmpty(savePath))
            {
                api.Log(API.LogType.Error, "Error: No valid SavePath specified in the .ini file.");
                return; // If save path is not specified, do nothing
            }

            Application.Run(new CustomScreenshotForm(savePath, ExecuteFinishAction));
        }

        private void SaveImage(Bitmap bitmap)
        {
            // Always save as PNG format
            bitmap.Save(savePath, ImageFormat.Png);
        }

        private void ExecuteFinishAction()
        {
            if (!string.IsNullOrEmpty(finishAction))
            {
                // Trigger the finish action (such as !ActivateConfig)
                api.Execute(finishAction); // Use the API instance here
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

        public CustomScreenshotForm(string savePath, Action finishAction)
        {
            this.savePath = savePath;
            this.finishAction = finishAction;
            this.DoubleBuffered = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.BackColor = Color.Black;
            this.Opacity = 0.25;
            this.TopMost = true;
            this.Cursor = Cursors.Cross; // Set cursor to crosshair for selection mode
            this.MouseDown += new MouseEventHandler(OnMouseDown);
            this.MouseMove += new MouseEventHandler(OnMouseMove);
            this.MouseUp += new MouseEventHandler(OnMouseUp);
            this.Paint += new PaintEventHandler(OnPaint);
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            startPoint = e.Location;
            isSelecting = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (isSelecting)
            {
                selection = new Rectangle(
                    Math.Min(startPoint.X, e.X),
                    Math.Min(startPoint.Y, e.Y),
                    Math.Abs(startPoint.X - e.X),
                    Math.Abs(startPoint.Y - e.Y));
                Invalidate();
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            isSelecting = false;

            // Hide the form before capturing the screenshot to avoid capturing the overlay
            this.Hide();
            TakeScreenshot(selection);
            this.Close(); // Close the form after capture
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            if (isSelecting)
            {
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    e.Graphics.DrawRectangle(pen, selection);
                }
            }
        }

        private void TakeScreenshot(Rectangle area)
        {
            using (Bitmap bitmap = new Bitmap(area.Width, area.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                }

                SaveImage(bitmap);
            }

            // Execute the finish action after the screenshot is taken
            finishAction();
        }

        private void SaveImage(Bitmap bitmap)
        {
            // Always save as PNG format
            bitmap.Save(savePath, ImageFormat.Png);
        }
    }

    public static class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            Rainmeter.API api = new Rainmeter.API(rm); // Create API instance
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new Measure(api))); // Pass API to Measure
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
            return 0.0; // No numeric output
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            string arguments = Marshal.PtrToStringUni(args);

            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            if (arguments.StartsWith("ExecuteBatch"))
            {
                // Parse the action code
                string[] parts = arguments.Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[1], out int actionCode))
                {
                    measure.ExecuteBatch(actionCode);
                }
            }
        }
    }
}

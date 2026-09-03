using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PluginScreenshot
{
    public class CustomScreenshotForm : Form
    {
        private readonly Settings _settings;
        private readonly Action _finishCallback;
        private Point _start;
        private Rectangle _selection;
        private bool _dragging;

        public CustomScreenshotForm(Settings settings, Action finishCallback)
        {
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
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
}

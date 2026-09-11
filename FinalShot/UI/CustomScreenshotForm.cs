using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    public class CustomScreenshotForm : Form
    {
        private readonly Settings _settings;
        private readonly Action   _finishCallback;

        // Desktop snapshot — shown dimmed; the hovered/selected region is shown undimmed.
        private Bitmap _desktopSnapshot;

        // Drag-selection state
        private Point     _start;
        private Rectangle _selection;
        private bool      _dragging;

        // Window-detection state
        private List<WindowInfo> _windows             = new List<WindowInfo>();
        private WindowInfo       _hoveredWindow        = null;
        private bool             _windowsLoaded        = false;
        private bool             _pendingWindowCapture = false;

        // ------------------------------------------------------------------ //
        //  Static factory — always use this, never Application.Run directly
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Spawns a fresh STA thread and blocks until the form closes.
        /// Using a new thread every time prevents the Rainmeter crash on the second call.
        /// </summary>
        public static void RunModal(Settings settings, Action finishCallback)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Application.Run(new CustomScreenshotForm(settings, finishCallback));
                }
                catch (Exception ex)
                {
                    Logger.Log("CustomScreenshotForm thread error: " + ex.Message);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
        }

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //

        public CustomScreenshotForm(Settings settings, Action finishCallback)
        {
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            _settings       = settings;
            _finishCallback = finishCallback;

            // Snapshot the desktop before the overlay appears so we can render
            // the dimmed background and the undimmed highlighted region ourselves.
            Rectangle screen = SystemInformation.VirtualScreen;
            _desktopSnapshot = new Bitmap(screen.Width, screen.Height);
            using (Graphics g = Graphics.FromImage(_desktopSnapshot))
                g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);

            DoubleBuffered  = true;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar   = false;
            Bounds          = screen;
            BackColor       = Color.FromArgb(1, 1, 1); // avoids TransparencyKey flicker
            Opacity         = 1.0;                      // we paint everything manually
            TopMost         = true;
            Cursor          = Cursors.Cross;
            StartPosition   = FormStartPosition.Manual;
            Location        = screen.Location;
            KeyPreview      = true;

            Load      += OnFormLoad;
            KeyDown   += OnKeyDown;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp   += OnMouseUp;
            Paint     += OnPaint;
        }

        // ------------------------------------------------------------------ //
        //  Form Load — enumerate windows on a background thread
        // ------------------------------------------------------------------ //

        private void OnFormLoad(object sender, EventArgs e)
        {
            if (!_settings.DetectWindows) return;

            var form = this;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var detector = new WindowsDetector
                    {
                        IncludeChildWindows = _settings.DetectControls
                    };
                    detector.IgnoreHandles.Add(form.Handle);

                    List<WindowInfo> result = detector.GetWindowList();

                    if (!form.IsDisposed)
                    {
                        form.BeginInvoke(new Action(() =>
                        {
                            if (!form.IsDisposed && !_dragging)
                            {
                                _windows       = result;
                                _windowsLoaded = true;
                                Logger.Log("WindowsDetector: loaded " + result.Count + " entries.");
                                form.Invalidate();
                            }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("WindowsDetector error: " + ex.Message);
                }
            });
        }

        // ------------------------------------------------------------------ //
        //  Keyboard
        // ------------------------------------------------------------------ //

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        }

        // ------------------------------------------------------------------ //
        //  Mouse down
        // ------------------------------------------------------------------ //

        private void OnMouseDown(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left)  return;

            _start = e.Location;

            if (_windowsLoaded && _hoveredWindow != null && _settings.DetectWindows)
            {
                _pendingWindowCapture = true;
                _dragging             = false;
            }
            else
            {
                _pendingWindowCapture = false;
                _dragging             = true;
            }
        }

        // ------------------------------------------------------------------ //
        //  Mouse move
        // ------------------------------------------------------------------ //

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            // Switch from pending-capture to free-drag if the user moves > 4 px.
            if (_pendingWindowCapture)
            {
                double dist = Math.Sqrt(
                    Math.Pow(e.X - _start.X, 2) +
                    Math.Pow(e.Y - _start.Y, 2));

                if (dist >= 4)
                {
                    _pendingWindowCapture = false;
                    _hoveredWindow        = null;
                    _dragging             = true;
                }
                else
                {
                    return;
                }
            }

            if (_dragging)
            {
                _selection = new Rectangle(
                    Math.Min(_start.X, e.X),
                    Math.Min(_start.Y, e.Y),
                    Math.Abs(_start.X - e.X),
                    Math.Abs(_start.Y - e.Y));
                Invalidate();
                return;
            }

            // Idle hover: find the topmost window under the cursor.
            if (_windowsLoaded && _settings.DetectWindows)
            {
                Point      screenPt = PointToScreen(e.Location);
                WindowInfo found    = null;

                for (int i = 0; i < _windows.Count; i++)
                {
                    if (_windows[i].Rectangle.Contains(screenPt))
                    {
                        found = _windows[i];
                        break;
                    }
                }

                if (found != _hoveredWindow)
                {
                    _hoveredWindow = found;
                    Invalidate();
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Mouse up
        // ------------------------------------------------------------------ //

        private void OnMouseUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // Window-snap capture
            if (_pendingWindowCapture && _hoveredWindow != null)
            {
                _pendingWindowCapture = false;
                Rectangle captureRect = _hoveredWindow.Rectangle;
                Logger.Log("Window capture: " + captureRect);
                Hide();
                ScreenshotManager.CompositeCapture(captureRect, _settings);
                _finishCallback();
                Close();
                return;
            }

            // Free-selection capture
            _dragging             = false;
            _pendingWindowCapture = false;

            if (_selection.Width < 1 || _selection.Height < 1)
            {
                Close();
                return;
            }

            Hide();
            var absRect = new Rectangle(
                Bounds.Left + _selection.X,
                Bounds.Top  + _selection.Y,
                _selection.Width,
                _selection.Height);

            ScreenshotManager.CompositeCapture(absRect, _settings);
            _finishCallback();
            Close();
        }

        // ------------------------------------------------------------------ //
        //  Paint
        // ------------------------------------------------------------------ //

        private void OnPaint(object s, PaintEventArgs e)
        {
            Graphics  g      = e.Graphics;
            Rectangle client = ClientRectangle;

            // 1. Dimmed desktop snapshot as the base layer.
            g.DrawImage(_desktopSnapshot, 0, 0);
            using (var dim = new SolidBrush(GifSnapSelector.DimColor))
                g.FillRectangle(dim, client);

            // ---- Drag mode ----
            if (_dragging && _selection.Width > 1 && _selection.Height > 1)
            {
                Rectangle sel = _selection;

                // Show undimmed desktop inside the selection.
                g.DrawImage(_desktopSnapshot,
                            new Rectangle(sel.X, sel.Y, sel.Width, sel.Height),
                            sel, GraphicsUnit.Pixel);

                // Blue fill + border + handles + label.
                using (var fill = new SolidBrush(GifSnapSelector.SelectionFillColor))
                    g.FillRectangle(fill, sel);
                using (var pen = new Pen(GifSnapSelector.SelectionBorderColor, 2))
                    g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
                GifSnapSelector.DrawCornerHandles(g, sel);
                GifSnapSelector.DrawSizeLabel(g, sel, client);
                return;
            }

            // ---- Window-hover mode ----
            if (!_windowsLoaded || _hoveredWindow == null) return;

            // Convert hovered rect from screen coords to form-local coords.
            Rectangle formRect = new Rectangle(
                _hoveredWindow.Rectangle.X - Bounds.Left,
                _hoveredWindow.Rectangle.Y - Bounds.Top,
                _hoveredWindow.Rectangle.Width,
                _hoveredWindow.Rectangle.Height);

            Rectangle active = Rectangle.Intersect(formRect, client);
            if (active.Width <= 0 || active.Height <= 0) return;

            // Show undimmed desktop inside the hovered region.
            g.DrawImage(_desktopSnapshot,
                        new Rectangle(active.X, active.Y, active.Width, active.Height),
                        active, GraphicsUnit.Pixel);

            // Extra dim on the four surrounding bands.
            using (var band = new SolidBrush(GifSnapSelector.DimColor))
            {
                if (active.Top    > 0)            g.FillRectangle(band, 0,            0,             client.Width,                active.Top);
                if (active.Left   > 0)            g.FillRectangle(band, 0,            active.Top,    active.Left,                 active.Height);
                if (active.Right  < client.Width)  g.FillRectangle(band, active.Right, active.Top,    client.Width  - active.Right, active.Height);
                if (active.Bottom < client.Height) g.FillRectangle(band, 0,            active.Bottom, client.Width,                client.Height - active.Bottom);
            }

            // Blue fill + border + handles.
            using (var fill = new SolidBrush(GifSnapSelector.SelectionFillColor))
                g.FillRectangle(fill, active);
            using (var pen = new Pen(GifSnapSelector.SelectionBorderColor, 2))
                g.DrawRectangle(pen, active.X, active.Y, active.Width - 1, active.Height - 1);
            GifSnapSelector.DrawCornerHandles(g, active);

            // Size label uses the actual window pixel dimensions.
            Rectangle sizeRect = new Rectangle(active.X, active.Y,
                                               _hoveredWindow.Rectangle.Width,
                                               _hoveredWindow.Rectangle.Height);
            GifSnapSelector.DrawSizeLabel(g, sizeRect, client);
        }

        // ------------------------------------------------------------------ //
        //  Cleanup
        // ------------------------------------------------------------------ //

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _desktopSnapshot?.Dispose();
            base.Dispose(disposing);
        }
    }
}

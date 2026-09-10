using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// Full-screen overlay for ShareX-style window highlight + free-select drag capture.
    ///
    /// Three bugs fixed in this version
    /// ──────────────────────────────────
    /// 1. TASKBAR ICON
    ///    ShowInTaskbar = false added.
    ///
    /// 2. INVISIBLE OVERLAY / WHITE BORDER
    ///    TransparencyKey approach removed.  TransparencyKey only does colour-keying;
    ///    semi-transparent GDI brushes (Color.FromArgb with alpha) do NOT work — only
    ///    the exact key colour gets punched out, everything else is fully opaque on a
    ///    black surface, making the whole form look wrong or invisible.
    ///    Back to Opacity = 0.5 (Black background).  Colours are pre-brightened so they
    ///    survive the 0.5 multiply and are clearly visible on screen:
    ///      Cyan  (0,255,255) × 0.5  →  (0,127,127)  — crisp teal border
    ///      White (255,255,255) × 0.5 → (127,127,127) — visible dash overlay
    ///    At 50% the dim overlay also renders correctly as a dark semi-transparent layer.
    ///
    /// 3. RAINMETER CRASH ON SECOND CLICK
    ///    TakeCustom() was calling Application.Run() directly on Rainmeter's unmanaged
    ///    plugin thread.  The first call works, but after the form closes the thread is
    ///    left in a bad state; the second call crashes Rainmeter.
    ///    Fix: RunModal() spawns a fresh dedicated STA thread for every capture session
    ///    and blocks via Thread.Join() until it exits — exactly the same pattern used
    ///    by ShowNotificationWithImage() in this project.
    ///    Also removed async/await from OnFormLoad.  async void captures the
    ///    SynchronizationContext at the point of first await; on a Rainmeter-originated
    ///    thread that context is null, so the continuation after Task.Run() ran on a
    ///    ThreadPool thread and called Invalidate() from the wrong thread.
    ///    Replaced with ThreadPool.QueueUserWorkItem + BeginInvoke to marshal the
    ///    result back to the UI (STA) thread safely.
    /// </summary>
    public class CustomScreenshotForm : Form
    {
        private readonly Settings _settings;
        private readonly Action   _finishCallback;

        // drag-selection state
        private Point     _start;
        private Rectangle _selection;
        private bool      _dragging;

        // window-detection state
        private List<WindowInfo> _windows             = new List<WindowInfo>();
        private WindowInfo       _hoveredWindow        = null;
        private bool             _windowsLoaded        = false;
        private bool             _pendingWindowCapture = false;

        // ── Static factory ───────────────────────────────────────────

        /// <summary>
        /// Spawns a fresh STA thread, runs the overlay form on it, and blocks until
        /// the form closes.  Call this instead of new + Application.Run directly.
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
            thread.Join();  // block Rainmeter's plugin thread until the form exits
        }

        // ── Constructor ──────────────────────────────────────────────

        public CustomScreenshotForm(Settings settings, Action finishCallback)
        {
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            _settings       = settings;
            _finishCallback = finishCallback;

            DoubleBuffered  = true;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar   = false;          // Fix 1: no taskbar button
            Bounds          = SystemInformation.VirtualScreen;
            BackColor       = Color.Black;
            Opacity         = 0.5;            // Fix 2: visible overlay; colours pre-brightened
            TopMost         = true;
            Cursor          = Cursors.Cross;
            StartPosition   = FormStartPosition.Manual;
            Location        = SystemInformation.VirtualScreen.Location;
            KeyPreview      = true;

            Load      += OnFormLoad;
            KeyDown   += OnKeyDown;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp   += OnMouseUp;
            Paint     += OnPaint;
        }

        // ── Form Load: enumerate windows on background thread ────────

        private void OnFormLoad(object sender, EventArgs e)
        {
            if (!_settings.DetectWindows) return;

            // Fix 3: no async/await — use ThreadPool + BeginInvoke to stay thread-safe
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

        // ── Keyboard ─────────────────────────────────────────────────

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Logger.Log("CustomScreenshotForm: Escape, closing.");
                Close();
            }
        }

        // ── MouseDown ────────────────────────────────────────────────

        private void OnMouseDown(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left)  return;

            _start = e.Location;

            if (_windowsLoaded && _hoveredWindow != null && _settings.DetectWindows)
            {
                // Hover candidate present — pending capture (4 px drag threshold below)
                _pendingWindowCapture = true;
                _dragging             = false;
            }
            else
            {
                _pendingWindowCapture = false;
                _dragging             = true;
            }
        }

        // ── MouseMove ────────────────────────────────────────────────

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            // PendingHover → Creating at 4 px (matches ShareX behaviour)
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
                    // fall through into drag block
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

            // Idle: hover detection
            if (_windowsLoaded && _settings.DetectWindows)
            {
                Point      screenPt = PointToScreen(e.Location);
                WindowInfo found    = null;

                // EnumWindows gives topmost-first; first hit wins
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

        // ── MouseUp ──────────────────────────────────────────────────

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

        // ── OnPaint ──────────────────────────────────────────────────

        private void OnPaint(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Drag mode: dashed selection rectangle
            if (_dragging)
            {
                // Cyan × 0.5 opacity → visible teal-blue on screen
                using (var pen = new Pen(Color.Cyan, 2) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, _selection);
                return;
            }

            // Hover mode
            if (!_windowsLoaded || _hoveredWindow == null) return;

            // Screen → form-local coords
            Rectangle formRect = new Rectangle(
                _hoveredWindow.Rectangle.X - Bounds.Left,
                _hoveredWindow.Rectangle.Y - Bounds.Top,
                _hoveredWindow.Rectangle.Width,
                _hoveredWindow.Rectangle.Height);

            Rectangle active = Rectangle.Intersect(
                formRect, new Rectangle(0, 0, Width, Height));

            if (active.Width <= 0 || active.Height <= 0) return;

            // 1. Additional dim on surrounding bands.
            //    The form base is Black@50%; painting extra dark on the outside makes
            //    the hovered region visibly brighter/clearer by contrast.
            using (var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                if (active.Top    > 0)
                    g.FillRectangle(dimBrush, 0, 0, Width, active.Top);
                if (active.Left   > 0)
                    g.FillRectangle(dimBrush, 0, active.Top, active.Left, active.Height);
                if (active.Right  < Width)
                    g.FillRectangle(dimBrush, active.Right, active.Top, Width - active.Right, active.Height);
                if (active.Bottom < Height)
                    g.FillRectangle(dimBrush, 0, active.Bottom, Width, Height - active.Bottom);
            }

            // 2. Solid bright border — Cyan × 0.5 opacity = (0,127,127) on screen
            using (var accentPen = new Pen(Color.Cyan, 3))
                g.DrawRectangle(accentPen, active);

            // 3. White dashed overlay for the ant-march effect
            //    White × 0.5 = (127,127,127) — clearly visible on the dark overlay
            using (var dashPen = new Pen(Color.White, 1))
            {
                dashPen.DashStyle   = DashStyle.Custom;
                dashPen.DashPattern = new float[] { 5f, 5f };
                g.DrawRectangle(dashPen, active);
            }
        }
    }
}

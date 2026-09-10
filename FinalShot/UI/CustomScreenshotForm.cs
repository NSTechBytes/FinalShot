using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// Full-screen overlay form that lets the user either:
    ///   (a) click on a detected window/control to capture it instantly, OR
    ///   (b) drag a free-selection rectangle anywhere on screen.
    ///
    /// When DetectWindows = true the form loads the visible window list asynchronously
    /// on startup (Task 4) and highlights the window under the cursor ShareX-style
    /// (Tasks 5 + 6).  A left-click on a highlighted window captures it immediately
    /// (Task 7).  Esc / right-click dismiss the overlay (Task 8).
    ///
    /// When DetectWindows = false the form behaves exactly like the original drag-only
    /// version (Task 8 guard).
    /// </summary>
    public class CustomScreenshotForm : Form
    {
        // ── injected dependencies ────────────────────────────────────
        private readonly Settings _settings;
        private readonly Action   _finishCallback;

        // ── drag-selection state ─────────────────────────────────────
        private Point     _start;
        private Rectangle _selection;
        private bool      _dragging;

        // ── window-detection state (Tasks 4-7) ──────────────────────
        private List<WindowInfo> _windows      = new List<WindowInfo>();
        private WindowInfo       _hoveredWindow = null;
        private bool             _windowsLoaded = false;
        private bool             _pendingWindowCapture = false;

        // ────────────────────────────────────────────────────────────
        // Constructor
        // ────────────────────────────────────────────────────────────

        public CustomScreenshotForm(Settings settings, Action finishCallback)
        {
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            _settings       = settings;
            _finishCallback = finishCallback;

            DoubleBuffered    = true;
            FormBorderStyle   = FormBorderStyle.None;
            Bounds            = SystemInformation.VirtualScreen;
            BackColor         = Color.Black;
            Opacity           = 0.25;
            TopMost           = true;
            Cursor            = Cursors.Cross;
            StartPosition     = FormStartPosition.Manual;
            Location          = SystemInformation.VirtualScreen.Location;

            // Enable keyboard input so Esc works (Task 8)
            KeyPreview        = true;

            Load      += OnFormLoad;
            KeyDown   += OnKeyDown;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp   += OnMouseUp;
            Paint     += OnPaint;
        }

        // ────────────────────────────────────────────────────────────
        // Task 4 — Load window list asynchronously on form Load
        // ────────────────────────────────────────────────────────────

        private async void OnFormLoad(object sender, EventArgs e)
        {
            if (!_settings.DetectWindows) return;

            try
            {
                var detector = new WindowsDetector
                {
                    IncludeChildWindows = _settings.DetectControls
                };
                // Ignore our own overlay handle so it never appears as a candidate
                detector.IgnoreHandles.Add(this.Handle);

                // Background thread — UI stays responsive immediately
                List<WindowInfo> result = await Task.Run(() => detector.GetWindowList());

                if (!IsDisposed && !_dragging)
                {
                    _windows      = result;
                    _windowsLoaded = true;
                    Logger.Log($"WindowsDetector: loaded {result.Count} window entries.");
                    Invalidate();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("WindowsDetector load error: " + ex.Message);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Task 8 — Keyboard / right-click cancellation
        // ────────────────────────────────────────────────────────────

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Logger.Log("CustomScreenshotForm: Escape pressed, closing.");
                Close();
            }
        }

        // ────────────────────────────────────────────────────────────
        // Task 7 + 8 — MouseDown
        // ────────────────────────────────────────────────────────────

        private void OnMouseDown(object s, MouseEventArgs e)
        {
            // Right-click → cancel (Task 8)
            if (e.Button == MouseButtons.Right)
            {
                Logger.Log("CustomScreenshotForm: right-click cancel.");
                Close();
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            _start = e.Location;

            if (_windowsLoaded && _hoveredWindow != null && _settings.DetectWindows)
            {
                // Task 7: clicking on a highlighted window — wait for mouse-up to confirm
                // (may transition to free-drag if the user moves ≥4px first)
                _pendingWindowCapture = true;
                _dragging             = false;
            }
            else
            {
                _pendingWindowCapture = false;
                _dragging             = true;
            }
        }

        // ────────────────────────────────────────────────────────────
        // Tasks 5 + 7 — MouseMove
        // ────────────────────────────────────────────────────────────

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            // ── pending window-capture: check if the user starts dragging ──
            // Mirrors ShareX's PendingHover → Creating transition at 4 px.
            if (_pendingWindowCapture)
            {
                double dist = Math.Sqrt(
                    Math.Pow(e.X - _start.X, 2) +
                    Math.Pow(e.Y - _start.Y, 2));

                if (dist >= 4)
                {
                    // User is dragging — cancel window-snap and start free-select
                    _pendingWindowCapture = false;
                    _hoveredWindow        = null;
                    _dragging             = true;
                    // Fall through to the dragging block below
                }
                else
                {
                    return; // Still inside the 4px dead-zone — wait
                }
            }

            // ── existing drag-selection logic ──
            if (_dragging)
            {
                int x = Math.Min(_start.X, e.X);
                int y = Math.Min(_start.Y, e.Y);
                int w = Math.Abs(_start.X - e.X);
                int h = Math.Abs(_start.Y - e.Y);
                _selection = new Rectangle(x, y, w, h);
                Invalidate();
                return;
            }

            // ── Task 5: hover detection (only when idle and windows are loaded) ──
            if (_windowsLoaded && _settings.DetectWindows)
            {
                // Convert form-local mouse position to screen coordinates
                Point screenPt = PointToScreen(e.Location);

                WindowInfo found = null;

                // EnumWindows returns windows in z-order (topmost first).
                // _results mirrors that order, so the first hit is the topmost window
                // at this point — exactly what we want.
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

        // ────────────────────────────────────────────────────────────
        // Tasks 7 + 8 — MouseUp
        // ────────────────────────────────────────────────────────────

        private void OnMouseUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // ── Task 7: window-snap capture ──
            if (_pendingWindowCapture && _hoveredWindow != null)
            {
                _pendingWindowCapture = false;
                Rectangle captureRect = _hoveredWindow.Rectangle;
                Logger.Log($"CustomScreenshotForm: window capture {captureRect}");

                Hide();
                ScreenshotManager.CompositeCapture(captureRect, _settings);
                _finishCallback();
                Close();
                return;
            }

            // ── Original free-selection capture ──
            _dragging             = false;
            _pendingWindowCapture = false;

            Logger.Log($"CustomScreenshotForm: user dropped selection {_selection}");

            if (_selection.Width < 1 || _selection.Height < 1)
            {
                Logger.Log("CustomScreenshotForm: selection too small, closing.");
                Close();
                return;
            }

            Hide();

            // Convert form-local selection to absolute screen coordinates
            var absRect = new Rectangle(
                Bounds.Left + _selection.X,
                Bounds.Top  + _selection.Y,
                _selection.Width,
                _selection.Height);

            ScreenshotManager.CompositeCapture(absRect, _settings);
            _finishCallback();
            Close();
        }

        // ────────────────────────────────────────────────────────────
        // Task 6 — OnPaint: ShareX-style highlight + drag rectangle
        // ────────────────────────────────────────────────────────────

        private void OnPaint(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // ── Drag mode: draw the classic dashed blue selection box ──
            if (_dragging)
            {
                using (var pen = new Pen(Color.DodgerBlue, 2) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, _selection);
                return;
            }

            // ── Hover mode: ShareX-style window highlight ──
            if (_windowsLoaded && _hoveredWindow != null)
            {
                // Convert hovered window rect from screen coords to form-local coords
                Rectangle formRect = new Rectangle(
                    _hoveredWindow.Rectangle.X - Bounds.Left,
                    _hoveredWindow.Rectangle.Y - Bounds.Top,
                    _hoveredWindow.Rectangle.Width,
                    _hoveredWindow.Rectangle.Height);

                // Clamp to form surface so we never draw outside our own bounds
                Rectangle clipped = Rectangle.Intersect(
                    formRect,
                    new Rectangle(0, 0, Width, Height));

                if (clipped.Width <= 0 || clipped.Height <= 0) return;

                // 1. Dim the four surrounding bands (ShareX DrawDimmedOutside)
                //    The form itself is already at 25% opacity; the extra dim on the
                //    surrounding area makes the highlighted window stand out clearly.
                using (var dimBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                {
                    // Top band
                    if (clipped.Top > 0)
                        g.FillRectangle(dimBrush, 0, 0, Width, clipped.Top);

                    // Left band
                    if (clipped.Left > 0)
                        g.FillRectangle(dimBrush, 0, clipped.Top, clipped.Left, clipped.Height);

                    // Right band
                    int rightStart = clipped.Right;
                    if (rightStart < Width)
                        g.FillRectangle(dimBrush, rightStart, clipped.Top, Width - rightStart, clipped.Height);

                    // Bottom band
                    int bottomStart = clipped.Bottom;
                    if (bottomStart < Height)
                        g.FillRectangle(dimBrush, 0, bottomStart, Width, Height - bottomStart);
                }

                // 2. Solid accent border — blue (ShareX DrawAntRectangle accent layer)
                using (var accentPen = new Pen(Color.DodgerBlue, 2))
                    g.DrawRectangle(accentPen, clipped);

                // 3. Dashed black overlay border (ShareX AntDashPen)
                using (var dashPen = new Pen(Color.FromArgb(230, 0, 0, 0), 1))
                {
                    dashPen.DashStyle   = DashStyle.Custom;
                    dashPen.DashPattern = new float[] { 5f, 5f };
                    g.DrawRectangle(dashPen, clipped);
                }
            }
        }
    }
}

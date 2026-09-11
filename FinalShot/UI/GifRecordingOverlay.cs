using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// A borderless, click-through overlay shown during GIF recording.
    ///
    /// Layout:
    ///   • A dashed blue border drawn around the capture region (the interior is
    ///     fully transparent so the screen underneath is visible and recordable).
    ///   • A dark toolbar anchored to the bottom centre of the region containing
    ///     Stop, Pause/Resume, and Abort buttons plus a live elapsed timer.
    ///
    /// The overlay is owned and shown by <see cref="GifCaptureManager"/> via
    /// <see cref="Show(Rectangle, Action, Action, Action)"/>.
    /// Call <see cref="CloseOverlay"/> from any thread to dismiss it.
    /// </summary>
    internal static class GifRecordingOverlay
    {
        private static OverlayForm _form;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Creates and shows the overlay on a dedicated STA thread.
        /// </summary>
        /// <param name="region">Capture region in screen coordinates.</param>
        /// <param name="onStop">Called when the user clicks Stop.</param>
        /// <param name="onPause">Called when the user clicks Pause/Resume.</param>
        /// <param name="onAbort">Called when the user clicks Abort.</param>
        public static void Show(Rectangle region,
                                Action onStop,
                                Action onPause,
                                Action onAbort)
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    _form = new OverlayForm(region, onStop, onPause, onAbort);
                    Application.Run(_form);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifRecordingOverlay: thread error — {ex.Message}");
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "FinalShot-GifOverlay";
            thread.Start();
        }

        /// <summary>
        /// Closes the overlay from any thread. Safe to call even if no overlay is open.
        /// </summary>
        public static void CloseOverlay()
        {
            try
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke(new Action(() => f.Close()));
                    else
                        f.Close();
                }
            }
            catch { }
            finally { _form = null; }
        }

        /// <summary>
        /// Updates the pause button label from any thread.
        /// </summary>
        public static void SetPaused(bool paused)
        {
            try
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke(new Action(() => f.UpdatePauseState(paused)));
                    else
                        f.UpdatePauseState(paused);
                }
            }
            catch { }
        }

        // ================================================================== //
        //  OverlayForm
        // ================================================================== //

        internal sealed class OverlayForm : Form
        {
            // ---- colours / sizes ----
            private static readonly Color BorderColor   = Color.FromArgb(255,  0, 120, 212); // blue
            private static readonly Color ToolbarBg     = Color.FromArgb(220, 20,  20,  20);
            private static readonly Color BtnNormal     = Color.FromArgb(255, 50,  50,  50);
            private static readonly Color BtnHover      = Color.FromArgb(255, 80,  80,  80);
            private static readonly Color BtnStop       = Color.FromArgb(255,180,  40,  40);
            private static readonly Color BtnStopHover  = Color.FromArgb(255,210,  60,  60);
            private static readonly Color BtnAbort      = Color.FromArgb(255,120,  40,  40);
            private static readonly Color BtnAbortHover = Color.FromArgb(255,150,  55,  55);
            private static readonly Color TextColor     = Color.White;

            private const int ToolbarH   = 36;
            private const int BtnW       = 72;
            private const int BtnH       = 26;
            private const int BtnSpacing = 8;
            private const int BorderW    = 2;
            private const int BorderPad  = 2; // extra space outside the capture region

            // ---- state ----
            private readonly Rectangle _region;   // screen coords of capture region
            private readonly Action    _onStop;
            private readonly Action    _onPause;
            private readonly Action    _onAbort;

            private readonly Timer     _timer;
            private readonly DateTime  _startTime = DateTime.Now;
            private bool               _paused    = false;

            // ---- toolbar button hit-areas (form-local coords) ----
            private Rectangle _btnStop;
            private Rectangle _btnPause;
            private Rectangle _btnAbort;
            private Rectangle _timerRect;
            private int       _hoveredBtn = -1; // 0=stop 1=pause 2=abort

            public OverlayForm(Rectangle region, Action onStop, Action onPause, Action onAbort)
            {
                _region  = region;
                _onStop  = onStop;
                _onPause = onPause;
                _onAbort = onAbort;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = true;
                DoubleBuffered  = true;

                // The form covers just the border ring + toolbar area around the region.
                // We make it slightly larger than the region to draw the outer border,
                // then punch a transparent hole in the middle using a Region.
                int pad = BorderPad + BorderW + 2;

                // Toolbar sits below the region; form height = region + pad top + toolbar
                int formX = region.X  - pad;
                int formY = region.Y  - pad;
                int formW = region.Width  + pad * 2;
                int formH = region.Height + pad * 2 + ToolbarH + 4;

                Bounds        = new Rectangle(formX, formY, formW, formH);
                StartPosition = FormStartPosition.Manual;
                Location      = new Point(formX, formY);

                // Transparent background — we'll punch a hole for the interior.
                BackColor        = Color.Lime;
                TransparencyKey  = Color.Lime;

                // Build the window Region: full rect minus the capture region interior.
                using (var full  = new System.Drawing.Drawing2D.GraphicsPath())
                using (var inner = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    full.AddRectangle(new Rectangle(0, 0, formW, formH));

                    // Inner hole = the capture region mapped to form-local coords, inset by border width.
                    var hole = new Rectangle(pad, pad, region.Width - 1, region.Height - 1);
                    inner.AddRectangle(hole);

                    // Combine: form region = full minus hole (so the interior is click-through).
                    var rgn = new System.Drawing.Region(full);
                    rgn.Exclude(inner);
                    Region = rgn;
                }

                // Pre-compute toolbar button rects (form-local).
                LayoutToolbar(formW);

                // Live timer — redraws every second.
                _timer          = new Timer { Interval = 100 };
                _timer.Tick    += (s, e) => Invalidate(_timerRect);
                _timer.Start();

                MouseMove  += OnMouseMove;
                MouseDown  += OnMouseDown;
                MouseLeave += (s, e) => { _hoveredBtn = -1; Invalidate(); };
            }

            private void LayoutToolbar(int formW)
            {
                // Four items: [Stop] [Pause] [Abort] [00:00:00] centred in formW
                int totalW = BtnW * 3 + BtnSpacing * 2 + 90; // 90 for timer
                int startX = (formW - totalW) / 2;
                int btnY   = _region.Height + (BorderPad + BorderW + 2) * 2 + 4;
                int btnCy  = btnY + (ToolbarH - BtnH) / 2;

                _btnStop  = new Rectangle(startX,                          btnCy, BtnW, BtnH);
                _btnPause = new Rectangle(startX + BtnW + BtnSpacing,      btnCy, BtnW, BtnH);
                _btnAbort = new Rectangle(startX + (BtnW + BtnSpacing) * 2, btnCy, BtnW, BtnH);
                _timerRect = new Rectangle(_btnAbort.Right + BtnSpacing,   btnCy, 90,   BtnH);
            }

            public void UpdatePauseState(bool paused)
            {
                _paused = paused;
                Invalidate(_btnPause);
            }

            // ---------------------------------------------------------------- //
            //  Paint
            // ---------------------------------------------------------------- //

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g   = e.Graphics;
                int      pad = BorderPad + BorderW + 2;

                // ---- Dashed border around the capture region ----
                var borderRect = new Rectangle(pad - BorderW, pad - BorderW,
                                               _region.Width  + BorderW,
                                               _region.Height + BorderW);

                using (var pen = new Pen(BorderColor, BorderW))
                {
                    pen.DashStyle   = DashStyle.Dash;
                    pen.DashPattern = new float[] { 6f, 3f };
                    g.DrawRectangle(pen, borderRect);
                }

                // ---- Toolbar background ----
                var tbRect = new Rectangle(0, borderRect.Bottom + 4,
                                           Width, ToolbarH);
                using (var bg = new SolidBrush(ToolbarBg))
                    g.FillRectangle(bg, tbRect);

                // ---- Buttons ----
                DrawButton(g, _btnStop,  "Stop",
                    _hoveredBtn == 0 ? BtnStopHover  : BtnStop,  TextColor);
                DrawButton(g, _btnPause, _paused ? "Resume" : "Pause",
                    _hoveredBtn == 1 ? BtnHover : BtnNormal, TextColor);
                DrawButton(g, _btnAbort, "Abort",
                    _hoveredBtn == 2 ? BtnAbortHover : BtnAbort, TextColor);

                // ---- Elapsed timer ----
                TimeSpan elapsed = DateTime.Now - _startTime;
                string   time    = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}:{elapsed.Milliseconds / 10:D2}";

                using (var font = new Font("Consolas", 10f, FontStyle.Bold))
                using (var fg   = new SolidBrush(Color.FromArgb(255, 180, 220, 255)))
                {
                    SizeF ts = g.MeasureString(time, font);
                    float tx = _timerRect.Left + (_timerRect.Width  - ts.Width)  / 2f;
                    float ty = _timerRect.Top  + (_timerRect.Height - ts.Height) / 2f;
                    g.DrawString(time, font, fg, tx, ty);
                }
            }

            private void DrawButton(Graphics g, Rectangle r, string text,
                                    Color bg, Color fg)
            {
                using (var bgBrush = new SolidBrush(bg))
                using (var path    = RoundedRect(r.X, r.Y, r.Width, r.Height, 4))
                    g.FillPath(bgBrush, path);

                using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (var fgBrush = new SolidBrush(fg))
                {
                    var sf = new StringFormat
                    {
                        Alignment     = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(text, font, fgBrush, r, sf);
                }
            }

            private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
            {
                var path = new GraphicsPath();
                path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
                path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
                path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
                path.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
                path.CloseFigure();
                return path;
            }

            // ---------------------------------------------------------------- //
            //  Mouse
            // ---------------------------------------------------------------- //

            private void OnMouseMove(object s, MouseEventArgs e)
            {
                int hit = HitTest(e.Location);
                if (hit != _hoveredBtn)
                {
                    _hoveredBtn = hit;
                    Invalidate();
                }
            }

            private void OnMouseDown(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                switch (HitTest(e.Location))
                {
                    case 0: _onStop?.Invoke();  break;
                    case 1: _onPause?.Invoke(); break;
                    case 2: _onAbort?.Invoke(); break;
                }
            }

            private int HitTest(Point p)
            {
                if (_btnStop.Contains(p))  return 0;
                if (_btnPause.Contains(p)) return 1;
                if (_btnAbort.Contains(p)) return 2;
                return -1;
            }

            // ---------------------------------------------------------------- //
            //  Cleanup
            // ---------------------------------------------------------------- //

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _timer?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

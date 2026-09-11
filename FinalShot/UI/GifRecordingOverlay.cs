using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// A borderless overlay shown during GIF recording.
    ///
    /// Layout:
    ///   • A dashed blue border drawn *outside* the capture region so no overlay
    ///     pixel ever falls inside the area being recorded.
    ///   • The interior of the capture region is a transparent hole — fully
    ///     click-through and invisible so recording is unaffected.
    ///   • A dark toolbar anchored below the region with Stop, Pause/Resume,
    ///     Abort buttons and a live elapsed timer that pauses when recording pauses.
    /// </summary>
    internal static class GifRecordingOverlay
    {
        private static OverlayForm _form;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

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

        /// <summary>Closes the overlay from any thread.</summary>
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

        /// <summary>Syncs the pause state (button label + timer) from any thread.</summary>
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
            // ---- style ----
            private static readonly Color BorderColor   = Color.FromArgb(255,  0, 120, 212);
            private static readonly Color ToolbarBg     = Color.FromArgb(230, 18,  18,  18);
            private static readonly Color BtnNormal     = Color.FromArgb(255, 55,  55,  55);
            private static readonly Color BtnHover      = Color.FromArgb(255, 85,  85,  85);
            private static readonly Color BtnStop       = Color.FromArgb(255,180,  40,  40);
            private static readonly Color BtnStopHover  = Color.FromArgb(255,210,  60,  60);
            private static readonly Color BtnAbort      = Color.FromArgb(255,120,  38,  38);
            private static readonly Color BtnAbortHover = Color.FromArgb(255,155,  55,  55);
            private static readonly Color TextColor     = Color.White;
            private static readonly Color TimerColor    = Color.FromArgb(255,180, 220, 255);

            // Border drawn OUTSIDE the capture rect — these pixels are never captured.
            private const int BorderW    = 2;   // pen width
            private const int BorderGap  = 1;   // gap between capture edge and border pen centre
                                                 // total outside offset = BorderGap + BorderW/2

            // Toolbar
            private const int ToolbarH   = 36;
            private const int ToolbarGap  = 4;   // gap between border bottom and toolbar top
            private const int BtnW        = 72;
            private const int BtnH        = 26;
            private const int BtnSpacing  = 8;
            private const int TimerW      = 94;

            // ---- capture region (screen coords) ----
            private readonly Rectangle _region;

            // ---- callbacks ----
            private readonly Action _onStop;
            private readonly Action _onPause;
            private readonly Action _onAbort;

            // ---- timer / pause state ----
            private readonly Timer    _ticker;
            private readonly DateTime _startUtc = DateTime.UtcNow;
            private TimeSpan          _pausedTotal = TimeSpan.Zero;   // accumulated paused duration
            private DateTime?         _pauseStartUtc = null;          // when current pause began
            private bool              _paused = false;

            // ---- hit-areas (form-local) ----
            private Rectangle _btnStopR;
            private Rectangle _btnPauseR;
            private Rectangle _btnAbortR;
            private Rectangle _timerR;
            private int       _hovered = -1;

            // ---- geometry ----
            // How far the form origin is offset from the capture region origin.
            // The capture region maps to form-local rect (offsetX, offsetY, W, H).
            private int _offsetX;
            private int _offsetY;

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

                // ---- geometry ----
                // We need the form to extend outside the capture region by enough to
                // draw the border without any pixel landing inside the region.
                //
                // Border pen is centred on its path. For a pen of width BorderW drawn
                // at distance BorderGap outside the region edge:
                //   outermost pixel = BorderGap + BorderW/2  pixels outside region edge
                //   innermost pixel = BorderGap - BorderW/2  pixels outside region edge
                //                   = BorderGap - 1          (for BorderW=2)
                //
                // With BorderGap=1, BorderW=2: innermost = 0 — border just touches the
                // edge of the capture region but NEVER enters it.
                //
                // Form padding (how many pixels outside the region the form extends on each side):
                int formPad = BorderGap + BorderW + 2; // 5px — enough for border + antialiasing

                _offsetX = formPad;
                _offsetY = formPad;

                int formW = region.Width  + formPad * 2;
                int formH = region.Height + formPad * 2 + ToolbarGap + ToolbarH;

                Bounds        = new Rectangle(region.X - formPad, region.Y - formPad, formW, formH);
                StartPosition = FormStartPosition.Manual;

                // Transparent background via TransparencyKey.
                BackColor       = Color.Lime;
                TransparencyKey = Color.Lime;

                // Build the Region that punches a hole exactly over the capture area.
                // The hole is EXACTLY region.Width × region.Height so not one pixel of
                // the overlay form overlaps the capture rect.
                using (var outerPath = new GraphicsPath())
                using (var holePath  = new GraphicsPath())
                {
                    outerPath.AddRectangle(new Rectangle(0, 0, formW, formH));

                    // Hole in form-local coords = exactly the capture region footprint.
                    holePath.AddRectangle(new Rectangle(_offsetX, _offsetY,
                                                        region.Width, region.Height));

                    var rgn = new System.Drawing.Region(outerPath);
                    rgn.Exclude(holePath);
                    Region = rgn;
                }

                LayoutToolbar(formW);

                _ticker         = new Timer { Interval = 100 };
                _ticker.Tick   += (s, e) => Invalidate(_timerR);
                _ticker.Start();

                MouseMove  += OnMouseMove;
                MouseDown  += OnMouseDown;
                MouseLeave += (s, e) => { _hovered = -1; Invalidate(); };
            }

            private void LayoutToolbar(int formW)
            {
                int totalBtnW = BtnW * 3 + BtnSpacing * 2 + BtnSpacing + TimerW;
                int startX    = (formW - totalBtnW) / 2;

                // Toolbar Y in form-local coords = below the capture region hole + gap.
                int tbY  = _offsetY + _region.Height + ToolbarGap;
                int btnY = tbY + (ToolbarH - BtnH) / 2;

                _btnStopR  = new Rectangle(startX,                              btnY, BtnW,   BtnH);
                _btnPauseR = new Rectangle(startX + BtnW + BtnSpacing,          btnY, BtnW,   BtnH);
                _btnAbortR = new Rectangle(startX + (BtnW + BtnSpacing) * 2,    btnY, BtnW,   BtnH);
                _timerR    = new Rectangle(_btnAbortR.Right + BtnSpacing,        btnY, TimerW, BtnH);
            }

            // ---------------------------------------------------------------- //
            //  Pause state
            // ---------------------------------------------------------------- //

            public void UpdatePauseState(bool paused)
            {
                _paused = paused;

                if (paused)
                {
                    // Record when this pause started.
                    _pauseStartUtc = DateTime.UtcNow;
                    _ticker.Stop();   // freeze timer display
                }
                else
                {
                    // Accumulate the pause duration we just completed.
                    if (_pauseStartUtc.HasValue)
                    {
                        _pausedTotal  += DateTime.UtcNow - _pauseStartUtc.Value;
                        _pauseStartUtc = null;
                    }
                    _ticker.Start();  // resume timer display
                }

                Invalidate(_btnPauseR);
                Invalidate(_timerR);
            }

            // ---------------------------------------------------------------- //
            //  Paint
            // ---------------------------------------------------------------- //

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;

                // ---- Dashed border ----
                // Draw the pen centred at (BorderGap) pixels outside the capture edge.
                // With _offsetX = formPad and pen centred at BorderGap outside the hole:
                float bx = _offsetX - BorderGap - BorderW / 2f;
                float by = _offsetY - BorderGap - BorderW / 2f;
                float bw = _region.Width  + (BorderGap + BorderW / 2f) * 2;
                float bh = _region.Height + (BorderGap + BorderW / 2f) * 2;

                using (var pen = new Pen(BorderColor, BorderW))
                {
                    pen.DashStyle   = DashStyle.Dash;
                    pen.DashPattern = new float[] { 6f, 3f };
                    g.DrawRectangle(pen, bx, by, bw, bh);
                }

                // ---- Toolbar background ----
                int tbTop = _offsetY + _region.Height + ToolbarGap;
                using (var bg = new SolidBrush(ToolbarBg))
                    g.FillRectangle(bg, 0, tbTop, Width, ToolbarH);

                // ---- Buttons ----
                DrawButton(g, _btnStopR,  "Stop",
                    _hovered == 0 ? BtnStopHover  : BtnStop,  TextColor);
                DrawButton(g, _btnPauseR, _paused ? "Resume" : "Pause",
                    _hovered == 1 ? BtnHover       : BtnNormal, TextColor);
                DrawButton(g, _btnAbortR, "Abort",
                    _hovered == 2 ? BtnAbortHover : BtnAbort, TextColor);

                // ---- Timer — only counts time actually recording (excludes paused duration) ----
                TimeSpan recorded = (DateTime.UtcNow - _startUtc) - _pausedTotal;
                if (recorded < TimeSpan.Zero) recorded = TimeSpan.Zero;

                string time = $"{(int)recorded.TotalMinutes:D2}:{recorded.Seconds:D2}:{recorded.Milliseconds / 10:D2}";

                using (var font = new Font("Consolas", 10f, FontStyle.Bold))
                using (var fg   = new SolidBrush(TimerColor))
                {
                    var sf = new StringFormat
                    {
                        Alignment     = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(time, font, fg, _timerR, sf);
                }
            }

            private void DrawButton(Graphics g, Rectangle r, string text, Color bg, Color fg)
            {
                using (var bgBrush = new SolidBrush(bg))
                using (var path    = RoundedRect(r.X, r.Y, r.Width, r.Height, 4))
                    g.FillPath(bgBrush, path);

                using (var font    = new Font("Segoe UI", 9f))
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
                var p = new GraphicsPath();
                p.AddArc(x,             y,             r * 2, r * 2, 180, 90);
                p.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
                p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
                p.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
                p.CloseFigure();
                return p;
            }

            // ---------------------------------------------------------------- //
            //  Mouse
            // ---------------------------------------------------------------- //

            private void OnMouseMove(object s, MouseEventArgs e)
            {
                int hit = HitTest(e.Location);
                if (hit != _hovered) { _hovered = hit; Invalidate(); }
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
                if (_btnStopR.Contains(p))  return 0;
                if (_btnPauseR.Contains(p)) return 1;
                if (_btnAbortR.Contains(p)) return 2;
                return -1;
            }

            // ---------------------------------------------------------------- //
            //  Cleanup
            // ---------------------------------------------------------------- //

            protected override void Dispose(bool disposing)
            {
                if (disposing) _ticker?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

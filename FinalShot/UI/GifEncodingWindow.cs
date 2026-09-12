using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

using WinTimer = System.Windows.Forms.Timer;

namespace PluginScreenshot
{
    /// <summary>
    /// Borderless toast-style window shown while GIF encoding is in progress.
    ///
    /// Appearance (matches the existing dark FinalShot theme):
    ///   ┌─────────────────────────────────────────┐
    ///   │  ●  Encoding GIF…                    ✕  │
    ///   │     Frame 12 / 43                        │
    ///   │  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │
    ///   └─────────────────────────────────────────┘
    ///
    ///  • Animated blue spinner on the left.
    ///  • Indeterminate progress bar that marches right until Close() is called,
    ///    then snaps to 100 % briefly before the window fades out.
    ///  • Updates frame count via UpdateProgress(current, total).
    ///  • Thread-safe: Show/Close/UpdateProgress may be called from any thread.
    /// </summary>
    internal static class GifEncodingWindow
    {
        private static EncodingForm _form;
        private static Thread       _thread;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>Shows the encoding window (non-blocking). Call from any thread.</summary>
        public static void Show(int totalFrames)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _form = new EncodingForm(totalFrames);
                    Application.Run(_form);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifEncodingWindow: thread error — {ex.Message}");
                }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "FinalShot-EncodingWindow";
            _thread.Start();
        }

        /// <summary>
        /// Updates the "Frame X / Y" label. Call from the encoding thread.
        /// </summary>
        public static void UpdateProgress(int current, int total)
        {
            try
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke(new Action(() => f.SetProgress(current, total)));
                    else
                        f.SetProgress(current, total);
                }
            }
            catch { }
        }

        /// <summary>
        /// Marks encoding complete: fills the progress bar then fades the window out.
        /// </summary>
        public static void Close()
        {
            try
            {
                var f = _form;
                if (f != null && !f.IsDisposed)
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke(new Action(() => f.FinishAndClose()));
                    else
                        f.FinishAndClose();
                }
            }
            catch { }
            finally { _form = null; }
        }

        // ================================================================== //
        //  EncodingForm
        // ================================================================== //

        internal sealed class EncodingForm : Form
        {
            // ---- colours (dark theme) ----
            private static readonly Color BgColor      = Color.FromArgb(255,  16,  18,  22);
            private static readonly Color BorderColor  = Color.FromArgb(255,   0, 120, 212);
            private static readonly Color AccentColor  = Color.FromArgb(255,   0, 120, 212);
            private static readonly Color TextPrimary  = Color.FromArgb(255, 220, 220, 220);
            private static readonly Color TextSecondary= Color.FromArgb(255, 140, 150, 165);
            private static readonly Color BarBg        = Color.FromArgb(255,  35,  38,  45);
            private static readonly Color BarFill      = Color.FromArgb(255,   0, 120, 212);
            private static readonly Color BarFillDone  = Color.FromArgb(255,   0, 200, 100);
            private static readonly Color CloseHover   = Color.White;
            private static readonly Color CloseNormal  = Color.FromArgb(255, 120, 120, 120);

            // ---- layout ----
            private const int W            = 340;
            private const int H            = 88;
            private const int Pad          = 14;
            private const int SpinnerSize  = 20;
            private const int BarH         = 4;
            private const int BarY         = H - Pad - BarH;
            private const int AccentH      = 2;

            // ---- state ----
            private int      _current;
            private int      _total;
            private bool     _done;
            private float    _barPos;        // indeterminate bar left edge 0..1
            private float    _spinAngle;
            private bool     _closeHover;
            private double   _opacity = 0.0;

            private readonly WinTimer _animTimer;
            private readonly WinTimer _fadeInTimer;
            private readonly WinTimer _fadeOutTimer;

            // ---------------------------------------------------------------- //
            //  Constructor
            // ---------------------------------------------------------------- //

            public EncodingForm(int totalFrames)
            {
                _total = totalFrames;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = false; // set via SetWindowPos in OnHandleCreated
                DoubleBuffered  = true;
                Width           = W;
                Height          = H;
                BackColor       = BgColor;
                Opacity         = 0.0;

                // Position: bottom-right of primary screen, above taskbar
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - W - 20, wa.Bottom - H - 20);
                StartPosition = FormStartPosition.Manual;

                // Animation WinTimer: spinner + indeterminate bar
                _animTimer          = new WinTimer { Interval = 30 };
                _animTimer.Tick    += (s, e) => { TickAnim(); Invalidate(); };
                _animTimer.Start();

                // Fade in
                _fadeInTimer        = new WinTimer { Interval = 16 };
                _fadeInTimer.Tick  += FadeInTick;

                // Fade out
                _fadeOutTimer       = new WinTimer { Interval = 16 };
                _fadeOutTimer.Tick += FadeOutTick;

                MouseMove  += (s, e) =>
                {
                    bool over = CloseRect.Contains(e.Location);
                    if (over != _closeHover) { _closeHover = over; Invalidate(CloseRect); }
                };
                MouseLeave += (s, e) => { _closeHover = false; Invalidate(CloseRect); };
                MouseDown  += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location))
                        FinishAndClose();
                };

                Load += (s, e) => _fadeInTimer.Start();
            }

            private Rectangle CloseRect =>
                new Rectangle(W - Pad - 16, Pad - 4, 20, 20);

            private void TickAnim()
            {
                _spinAngle = (_spinAngle + 8f) % 360f;
                if (!_done)
                {
                    // Indeterminate: move a 30%-wide bar across 0..1, bounce
                    _barPos += 0.018f;
                    if (_barPos > 1.3f) _barPos = -0.3f;
                }
            }

            // ---------------------------------------------------------------- //
            //  Public update methods (called on UI thread via BeginInvoke)
            // ---------------------------------------------------------------- //

            public void SetProgress(int current, int total)
            {
                _current = current;
                _total   = total;
                Invalidate();
            }

            public void FinishAndClose()
            {
                _done = true;
                _animTimer.Stop();
                Invalidate();
                // Brief pause at 100 % then fade out
                var holdTimer = new WinTimer { Interval = 600 };
                holdTimer.Tick += (s, e) => { holdTimer.Stop(); holdTimer.Dispose(); _fadeOutTimer.Start(); };
                holdTimer.Start();
            }

            // ---------------------------------------------------------------- //
            //  Fade
            // ---------------------------------------------------------------- //

            private void FadeInTick(object s, EventArgs e)
            {
                _opacity += 0.07;
                if (_opacity >= 0.95) { _opacity = 0.95; _fadeInTimer.Stop(); }
                Opacity = _opacity;
            }

            private void FadeOutTick(object s, EventArgs e)
            {
                _opacity -= 0.07;
                if (_opacity <= 0) { _opacity = 0; Opacity = 0; _fadeOutTimer.Stop(); Close(); }
                else Opacity = _opacity;
            }

            // ---------------------------------------------------------------- //
            //  Paint
            // ---------------------------------------------------------------- //

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Background
                g.Clear(BgColor);

                // Top accent line
                using (var b = new SolidBrush(AccentColor))
                    g.FillRectangle(b, 0, 0, W, AccentH);

                // Border
                using (var p = new Pen(BorderColor, 1))
                    g.DrawRectangle(p, 0, 0, W - 1, H - 1);

                // ---- Spinner (arc) ----
                int sx = Pad;
                int sy = Pad + 2;
                var spinRect = new RectangleF(sx, sy, SpinnerSize, SpinnerSize);
                using (var p = new Pen(BarBg, 2.5f))
                    g.DrawArc(p, spinRect, 0, 360);
                using (var p = new Pen(AccentColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, spinRect, _spinAngle, 260);

                // ---- Primary label ----
                int textX = Pad + SpinnerSize + 10;
                using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
                using (var brush = new SolidBrush(TextPrimary))
                    g.DrawString("Encoding GIF\u2026", font, brush, textX, Pad + 1);

                // ---- Secondary label: frame count ----
                string sub = _total > 0
                    ? $"Frame {_current} / {_total}"
                    : "Preparing\u2026";
                using (var font = new Font("Segoe UI", 8.5f))
                using (var brush = new SolidBrush(TextSecondary))
                    g.DrawString(sub, font, brush, textX, Pad + 22);

                // ---- Progress bar ----
                // Background track
                var trackRect = new Rectangle(Pad, BarY, W - Pad * 2, BarH);
                using (var b = new SolidBrush(BarBg))
                    g.FillRectangle(b, trackRect);

                // Fill
                if (_done)
                {
                    // Full bar in green
                    using (var b = new SolidBrush(BarFillDone))
                        g.FillRectangle(b, trackRect);
                }
                else
                {
                    // Indeterminate sliding segment (30 % wide)
                    int barInnerW = trackRect.Width;
                    int segW      = (int)(barInnerW * 0.30f);
                    int segLeft   = trackRect.Left + (int)(_barPos * barInnerW) - segW / 2;
                    segLeft = Math.Max(trackRect.Left, Math.Min(trackRect.Right - segW, segLeft));
                    using (var b = new SolidBrush(BarFill))
                        g.FillRectangle(b, segLeft, BarY, segW, BarH);
                }

                // ---- Close button ----
                using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var brush = new SolidBrush(_closeHover ? CloseHover : CloseNormal))
                    g.DrawString("\u2715", font, brush, CloseRect.Left, CloseRect.Top);
            }

            // ---------------------------------------------------------------- //
            //  No-activate topmost
            // ---------------------------------------------------------------- //

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                NativeMethods.MakeTopMostNoActivate(Handle);
            }

            // ---------------------------------------------------------------- //
            //  Cleanup
            // ---------------------------------------------------------------- //

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _animTimer?.Dispose();
                    _fadeInTimer?.Dispose();
                    _fadeOutTimer?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}

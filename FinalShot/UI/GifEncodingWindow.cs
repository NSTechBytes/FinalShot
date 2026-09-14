/*
 * Copyright (c) 2026 nstechbytes
 *
 * Licensed under the MIT License.
 * You may obtain a copy of the License at:
 * https://opensource.org/licenses/MIT
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

using WinTimer = System.Windows.Forms.Timer;

namespace PluginScreenshot
{
    // Borderless toast-style window shown while GIF encoding is in progress.
    //
    // Features:
    //   Animated blue spinner on the left.
    //   Indeterminate progress bar that marches right until Close() is called,
    //   then snaps to 100% briefly before the window fades out.
    //   Updates frame count via UpdateProgress(current, total).
    //   Thread-safe: Show/Close/UpdateProgress may be called from any thread.
    internal static class GifEncodingWindow
    {
        private static readonly object Sync = new object();
        private static EncodingForm _form;
        private static Thread       _thread;
        private static UITheme      _theme = UITheme.Dark;

        public static void SetTheme(UITheme theme) => _theme = theme;

        public static void Show(int totalFrames)
        {
            var theme = _theme;
            var ready = new ManualResetEventSlim(false);

            lock (Sync)
            {
                // Avoid overlapping encode windows
                if (_form != null && !_form.IsDisposed)
                    return;
            }

            _thread = new Thread(() =>
            {
                EncodingForm form = null;
                try
                {
                    form = new EncodingForm(totalFrames, theme);
                    form.FormClosed += (s, e) =>
                    {
                        lock (Sync)
                        {
                            if (_form == form)
                                _form = null;
                        }
                    };
                    lock (Sync) { _form = form; }
                    ready.Set();
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifEncodingWindow: thread error -- {ex.Message}");
                    ready.Set();
                    lock (Sync)
                    {
                        if (_form == form)
                            _form = null;
                    }
                }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "FinalShot-EncodingWindow";
            _thread.Start();
            ready.Wait(2000);
        }

        public static void UpdateProgress(int current, int total)
        {
            EncodingForm f;
            lock (Sync) { f = _form; }
            try
            {
                if (f != null && !f.IsDisposed)
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke(new Action(() =>
                        {
                            if (!f.IsDisposed) f.SetProgress(current, total);
                        }));
                    else
                        f.SetProgress(current, total);
                }
            }
            catch { }
        }

        public static void Close()
        {
            EncodingForm f;
            lock (Sync) { f = _form; }
            try
            {
                if (f == null || f.IsDisposed)
                    return;
                if (f.InvokeRequired)
                    f.BeginInvoke(new Action(() =>
                    {
                        if (!f.IsDisposed) f.FinishAndClose();
                    }));
                else
                    f.FinishAndClose();
            }
            catch { }
            // _form cleared in FormClosed — do not null here (races UpdateProgress / Show)
        }

        //  EncodingForm

        internal sealed class EncodingForm : Form
        {
            private readonly ThemeColors _t;

            private const int W            = 340;
            private const int H            = 88;
            private const int Pad          = 14;
            private const int SpinnerSize  = 20;
            private const int BarH         = 4;
            private const int BarY         = H - Pad - BarH;
            private const int AccentH      = 2;

            private int      _current;
            private int      _total;
            private bool     _done;
            private float    _barPos;
            private float    _spinAngle;
            private bool     _closeHover;
            private double   _opacity = 0.0;

            private readonly WinTimer _animTimer;
            private readonly WinTimer _fadeInTimer;
            private readonly WinTimer _fadeOutTimer;

            public EncodingForm(int totalFrames, UITheme theme)
            {
                _total = totalFrames;
                _t     = ThemeColors.Resolve(theme);

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = false; // set via SetWindowPos in OnHandleCreated
                DoubleBuffered  = true;
                Width           = W;
                Height          = H;
                BackColor       = _t.Background;
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

            //  Public update methods (called on UI thread via BeginInvoke)

            public void SetProgress(int current, int total)
            {
                _current = current;
                _total   = total;
                Invalidate();
            }

            private WinTimer _holdTimer;

            public void FinishAndClose()
            {
                if (_done) return;
                _done = true;
                _animTimer.Stop();
                Invalidate();
                if (_holdTimer != null)
                {
                    _holdTimer.Stop();
                    _holdTimer.Dispose();
                }
                _holdTimer = new WinTimer { Interval = 600 };
                _holdTimer.Tick += (s, e) =>
                {
                    _holdTimer.Stop();
                    _holdTimer.Dispose();
                    _holdTimer = null;
                    if (!IsDisposed) _fadeOutTimer.Start();
                };
                _holdTimer.Start();
            }

            //  Fade

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

            //  Paint

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                g.Clear(_t.Background);

                using (var b = new SolidBrush(_t.AccentBlue))
                    g.FillRectangle(b, 0, 0, W, AccentH);

                using (var p = new Pen(_t.Border, 1))
                    g.DrawRectangle(p, 0, 0, W - 1, H - 1);

                int sx = Pad;
                int sy = Pad + 2;
                var spinRect = new RectangleF(sx, sy, SpinnerSize, SpinnerSize);
                using (var p = new Pen(_t.BarTrack, 2.5f))
                    g.DrawArc(p, spinRect, 0, 360);
                using (var p = new Pen(_t.AccentBlue, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, spinRect, _spinAngle, 260);

                int textX = Pad + SpinnerSize + 10;
                using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
                using (var brush = new SolidBrush(_t.TextPrimary))
                    g.DrawString("Encoding GIF\u2026", font, brush, textX, Pad + 1);

                string sub = _total > 0 ? $"Frame {_current} / {_total}" : "Preparing\u2026";
                using (var font = new Font("Segoe UI", 8.5f))
                using (var brush = new SolidBrush(_t.TextSecondary))
                    g.DrawString(sub, font, brush, textX, Pad + 22);

                var trackRect = new Rectangle(Pad, BarY, W - Pad * 2, BarH);
                using (var b = new SolidBrush(_t.BarTrack))
                    g.FillRectangle(b, trackRect);

                if (_done)
                {
                    using (var b = new SolidBrush(_t.AccentGreen))
                        g.FillRectangle(b, trackRect);
                }
                else
                {
                    int barInnerW = trackRect.Width;
                    int segW      = (int)(barInnerW * 0.30f);
                    int segLeft   = trackRect.Left + (int)(_barPos * barInnerW) - segW / 2;
                    segLeft = Math.Max(trackRect.Left, Math.Min(trackRect.Right - segW, segLeft));
                    using (var b = new SolidBrush(_t.AccentBlue))
                        g.FillRectangle(b, segLeft, BarY, segW, BarH);
                }

                using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var brush = new SolidBrush(_closeHover ? _t.CloseHover : _t.CloseNormal))
                    g.DrawString("\u2715", font, brush, CloseRect.Left, CloseRect.Top);
            }

            //  No-activate topmost

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

            //  Cleanup

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _animTimer?.Dispose();
                    _fadeInTimer?.Dispose();
                    _fadeOutTimer?.Dispose();
                    if (_holdTimer != null)
                    {
                        _holdTimer.Stop();
                        _holdTimer.Dispose();
                        _holdTimer = null;
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}

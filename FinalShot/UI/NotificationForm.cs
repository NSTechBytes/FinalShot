/*
 * Copyright (c) 2025 nstechbytes
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
using System.Drawing.Text;
using System.Windows.Forms;

using WinTimer = System.Windows.Forms.Timer;

namespace PluginScreenshot
{
    // Borderless toast notification shown after a screenshot or GIF is saved.
    //
    // Layout (400 x 100):
    //   3px accent bar at top.
    //   Left: 80x80 cover image (object-fit:cover crop).
    //   Right: App name, title, subtitle, close button.
    //
    // Themed: Dark / Light / System.
    // Clicking the body (not close button) executes NotificationClickAction.
    // Fades in, auto-closes after 4s, fades out.
    public sealed class NotificationForm : Form
    {
        //  Layout constants
        private const int W           = 400;
        private const int H           = 100;
        private const int AccentH     = 3;
        private const int CoverX      = 12;
        private const int CoverY      = 12 + AccentH;
        private const int CoverSize   = 74;   // square cover image
        private const int TextX       = CoverX + CoverSize + 12;
        private const int TextW       = W - TextX - 36; // leave room for close btn
        private const int CloseSize   = 18;
        private const int DisplayMs   = 4000;

        //  State
        private readonly ThemeColors  _t;
        private readonly string       _imagePath;
        private readonly string       _captureType;
        private readonly string       _clickAction;
        private readonly Settings     _settings;
        private Bitmap                _cover;       // cropped cover bitmap
        private bool                  _closeHover;
        private double                _opacity;

        private readonly WinTimer _fadeInTimer;
        private readonly WinTimer _holdTimer;
        private readonly WinTimer _fadeOutTimer;

        //  Construction

        public NotificationForm(string imagePath, string captureType,
                                Settings settings)
        {
            _imagePath   = imagePath;
            _captureType = captureType;
            _settings    = settings;
            _clickAction = settings?.NotificationClickAction ?? "";
            _t           = ThemeColors.Resolve(settings?.UITheme ?? UITheme.Dark);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = false;
            TopMost         = false;   // set in OnHandleCreated
            DoubleBuffered  = true;
            Width           = W;
            Height          = H;
            BackColor       = _t.Background;
            Opacity         = 0;
            KeyPreview      = true;

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - W - 20, wa.Bottom - H - 20);

            // Load cover image
            LoadCover();

            // Timers
            _fadeInTimer        = new WinTimer { Interval = 12 };
            _fadeInTimer.Tick  += FadeInTick;

            _holdTimer          = new WinTimer { Interval = DisplayMs };
            _holdTimer.Tick    += (s, e) => { _holdTimer.Stop(); _fadeOutTimer.Start(); };

            _fadeOutTimer       = new WinTimer { Interval = 12 };
            _fadeOutTimer.Tick += FadeOutTick;

            // Mouse
            MouseMove  += OnMouseMove;
            MouseLeave += (s, e) => { _closeHover = false; Invalidate(CloseRect); };
            MouseDown  += OnMouseDown;
            KeyDown    += (s, e) => { if (e.KeyCode == Keys.Escape) StartClose(); };

            Load += (s, e) => { _fadeInTimer.Start(); };
        }

        //  Cover image -- crop to fill CoverSize x CoverSize

        private void LoadCover()
        {
            try
            {
                using (var src = Image.FromFile(_imagePath))
                {
                    _cover = new Bitmap(CoverSize, CoverSize);
                    using (var g = Graphics.FromImage(_cover))
                    {
                        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode      = SmoothingMode.HighQuality;
                        g.PixelOffsetMode    = PixelOffsetMode.HighQuality;

                        // Object-fit: cover -- scale to fill, crop excess
                        float scale = Math.Max(
                            (float)CoverSize / src.Width,
                            (float)CoverSize / src.Height);
                        int sw = (int)(src.Width  * scale);
                        int sh = (int)(src.Height * scale);
                        int ox = (CoverSize - sw) / 2;
                        int oy = (CoverSize - sh) / 2;

                        g.Clear(_t.CardBg);
                        g.DrawImage(src, ox, oy, sw, sh);
                    }
                }
            }
            catch
            {
                // No image -- cover stays null, placeholder is drawn in OnPaint
            }
        }

        //  Geometry helpers

        private Rectangle CloseRect =>
            new Rectangle(W - 28, AccentH + 6, CloseSize, CloseSize);

        private Rectangle BodyRect =>
            new Rectangle(0, AccentH, W - CloseSize - 8, H - AccentH);

        //  Fade in / out

        private void FadeInTick(object s, EventArgs e)
        {
            _opacity += 0.08;
            if (_opacity >= 0.96) { _opacity = 0.96; _fadeInTimer.Stop(); _holdTimer.Start(); }
            Opacity = _opacity;
        }

        private void FadeOutTick(object s, EventArgs e)
        {
            _opacity -= 0.06;
            if (_opacity <= 0) { _opacity = 0; Opacity = 0; _fadeOutTimer.Stop(); Close(); }
            else Opacity = _opacity;
        }

        private void StartClose()
        {
            _fadeInTimer.Stop();
            _holdTimer.Stop();
            if (!_fadeOutTimer.Enabled)
                _fadeOutTimer.Start();
        }

        //  Mouse

        private void OnMouseMove(object s, MouseEventArgs e)
        {
            bool over = CloseRect.Contains(e.Location);
            if (over != _closeHover) { _closeHover = over; Invalidate(CloseRect); }
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        private void OnMouseDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (CloseRect.Contains(e.Location))
            {
                StartClose();
                return;
            }

            // Click anywhere else -> execute action then close
            if (!string.IsNullOrEmpty(_clickAction))
            {
                try { BangQueue.Enqueue(_settings?.Api, _clickAction); }
                catch (Exception ex) { Logger.Log($"NotificationForm: click action error -- {ex.Message}"); }
            }
            StartClose();
        }

        //  Paint

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Background
            g.Clear(_t.Background);

            // Top accent bar (blue)
            using (var b = new SolidBrush(_t.AccentBlue))
                g.FillRectangle(b, 0, 0, W, AccentH);

            // Outer border
            using (var p = new Pen(_t.Border, 1))
                g.DrawRectangle(p, 0, 0, W - 1, H - 1);

            var coverRect = new Rectangle(CoverX, CoverY, CoverSize, CoverSize);
            if (_cover != null)
            {
                g.DrawImage(_cover, coverRect);
                using (var p = new Pen(_t.Border, 1))
                    g.DrawRectangle(p, coverRect);
            }
            else
            {
                // Placeholder: filled rect with camera icon hint
                using (var b = new SolidBrush(_t.CardBg))
                    g.FillRectangle(b, coverRect);
                using (var p = new Pen(_t.Border, 1))
                    g.DrawRectangle(p, coverRect);
                using (var b = new SolidBrush(_t.TextSecondary))
                using (var f = new Font("Segoe UI", 8f))
                {
                    var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("No\nPreview", f, b, coverRect, sf);
                }
            }

            int ty = AccentH + 10;
            using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var b = new SolidBrush(_t.AccentBlue))
                g.DrawString("FinalShot", f, b, TextX, ty);

            ty += 18;
            using (var f = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            using (var b = new SolidBrush(_t.TextPrimary))
            {
                var r = new RectangleF(TextX, ty, TextW, 22);
                g.DrawString("Saved!", f, b, r);
            }

            ty += 22;
            using (var f = new Font("Segoe UI", 8.5f))
            using (var b = new SolidBrush(_t.TextSecondary))
            {
                var r = new RectangleF(TextX, ty, TextW, 18);
                var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter,
                                            FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(_captureType, f, b, r, sf);
            }

            var cr = CloseRect;
            using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var b = new SolidBrush(_closeHover ? _t.CloseHover : _t.CloseNormal))
            {
                var sf = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("\u2715", f, b, cr, sf);
            }
        }

        //  No-activate topmost + WS_EX_NOACTIVATE

        protected override CreateParams CreateParams
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
                _cover?.Dispose();
                _fadeInTimer?.Dispose();
                _holdTimer?.Dispose();
                _fadeOutTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

using WinTimer = System.Windows.Forms.Timer;

namespace PluginScreenshot
{
    /// <summary>
    /// Borderless dark-theme modal dialog shown when a GIF command is issued
    /// in an invalid state (already recording / not recording).
    ///
    /// Two variants:
    ///   GifStateDialog.ShowAlreadyRecording()
    ///   GifStateDialog.ShowNotRecording()
    ///
    /// The dialog is modal (blocks the calling thread) but runs its own STA
    /// message loop so it never blocks the Rainmeter plugin thread.
    /// It closes when the user clicks OK or presses Enter/Escape.
    /// </summary>
    internal static class GifStateDialog
    {
        private static UITheme _theme = UITheme.Dark;

        /// <summary>Call from Settings load to keep the dialog themed.</summary>
        public static void SetTheme(UITheme theme) => _theme = theme;
        // ------------------------------------------------------------------ //
        //  Public helpers
        // ------------------------------------------------------------------ //

        public static void ShowAlreadyRecording()
        {
            Show(
                DialogKind.Warning,
                "Already Recording",
                "A GIF recording is already in progress.\nStop or cancel the current recording first."
            );
        }

        public static void ShowNotRecording(string action)
        {
            Show(
                DialogKind.Info,
                "Not Recording",
                $"There is no active GIF recording to {action}."
            );
        }

        public static void ShowEncoding()
        {
            Show(
                DialogKind.Info,
                "Encoding in Progress",
                "FinalShot is currently encoding the GIF.\nPlease wait until encoding finishes."
            );
        }

        // ------------------------------------------------------------------ //
        //  Core — spawns an STA thread so the dialog has its own message loop
        // ------------------------------------------------------------------ //

        private enum DialogKind { Warning, Info }

        private static void Show(DialogKind kind, string title, string message)
        {
            var theme = _theme;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var dlg = new StateDialogForm(kind, title, message, theme))
                        Application.Run(dlg);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifStateDialog: error — {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "FinalShot-StateDialog";
            thread.Start();
        }

        // ================================================================== //
        //  StateDialogForm
        // ================================================================== //

        private sealed class StateDialogForm : Form
        {
            private readonly ThemeColors _t;

            private const int W       = 360;
            private const int Pad     = 18;
            private const int AccentH = 3;
            private const int IconSize= 22;
            private const int BtnW    = 80;
            private const int BtnH    = 28;

            private readonly DialogKind _kind;
            private readonly string     _title;
            private readonly string     _message;

            private Rectangle _btnRect;
            private bool      _btnHover;
            private double    _opacity;

            private readonly WinTimer _fadeInTimer;
            private readonly WinTimer _fadeOutTimer;

            public StateDialogForm(DialogKind kind, string title, string message, UITheme theme)
            {
                _kind    = kind;
                _title   = title;
                _message = message;
                _t       = ThemeColors.Resolve(theme);

                int msgLines = message.Split('\n').Length;
                int msgH     = msgLines * 18 + (msgLines > 1 ? 4 : 0);
                int H        = AccentH + Pad + IconSize + 8 + msgH + Pad + BtnH + Pad;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = false;
                DoubleBuffered  = true;
                Width           = W;
                Height          = H;
                BackColor       = _t.Background;
                Opacity         = 0;
                StartPosition   = FormStartPosition.Manual;
                KeyPreview      = true;

                var wa   = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Left + (wa.Width - W) / 2, wa.Top + (wa.Height - H) / 2);
                _btnRect = new Rectangle(W - Pad - BtnW, H - Pad - BtnH, BtnW, BtnH);

                _fadeInTimer        = new WinTimer { Interval = 12 };
                _fadeInTimer.Tick  += FadeInTick;
                _fadeOutTimer       = new WinTimer { Interval = 12 };
                _fadeOutTimer.Tick += FadeOutTick;

                MouseMove  += OnMouseMove;
                MouseLeave += (s, e) => { _btnHover = false; Invalidate(_btnRect); };
                MouseDown  += OnMouseDown;
                KeyDown    += OnKeyDown;
                Load       += (s, e) => _fadeInTimer.Start();
            }

            private void FadeInTick(object s, EventArgs e)
            {
                _opacity += 0.08;
                if (_opacity >= 0.97) { _opacity = 0.97; _fadeInTimer.Stop(); }
                Opacity = _opacity;
            }

            private void FadeOutTick(object s, EventArgs e)
            {
                _opacity -= 0.08;
                if (_opacity <= 0) { _opacity = 0; Opacity = 0; _fadeOutTimer.Stop(); Close(); }
                else Opacity = _opacity;
            }

            private void StartClose() { _fadeInTimer.Stop(); _fadeOutTimer.Start(); }

            private void OnMouseMove(object s, MouseEventArgs e)
            {
                bool over = _btnRect.Contains(e.Location);
                if (over != _btnHover) { _btnHover = over; Invalidate(_btnRect); }
            }

            private void OnMouseDown(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && _btnRect.Contains(e.Location))
                    StartClose();
            }

            private void OnKeyDown(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) StartClose();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Color accent = _kind == DialogKind.Warning ? _t.AccentAmber : _t.AccentBlue;

                g.Clear(_t.Background);

                using (var b = new SolidBrush(accent))
                    g.FillRectangle(b, 0, 0, W, AccentH);

                using (var p = new Pen(_t.Border, 1))
                    g.DrawRectangle(p, 0, 0, W - 1, Height - 1);

                int iconX = Pad;
                int iconY = AccentH + Pad;
                DrawIcon(g, _kind, iconX, iconY, IconSize, accent);

                int textX = iconX + IconSize + 10;
                using (var font = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var brush = new SolidBrush(_t.TextPrimary))
                    g.DrawString(_title, font, brush, textX, iconY);

                int msgY = iconY + IconSize + 8;
                using (var font = new Font("Segoe UI", 9f))
                using (var brush = new SolidBrush(_t.TextSecondary))
                    g.DrawString(_message, font, brush, new RectangleF(Pad, msgY, W - Pad * 2, Height));

                using (var b = new SolidBrush(_btnHover ? _t.BtnHover : _t.BtnBg))
                    g.FillRectangle(b, _btnRect);
                using (var p = new Pen(_t.BtnBorder, 1))
                    g.DrawRectangle(p, _btnRect.X, _btnRect.Y, _btnRect.Width - 1, _btnRect.Height - 1);
                using (var font = new Font("Segoe UI", 9f))
                using (var brush = new SolidBrush(_t.BtnText))
                {
                    var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("OK", font, brush, _btnRect, sf);
                }
            }

            private static void DrawIcon(Graphics g, DialogKind kind, int x, int y, int size, Color color)
            {
                using (var p = new Pen(color, 2f))
                    g.DrawEllipse(p, x, y, size, size);
                float cx = x + size / 2f;
                float cy = y + size / 2f;
                if (kind == DialogKind.Warning)
                {
                    using (var p = new Pen(color, 2f)
                           { StartCap = System.Drawing.Drawing2D.LineCap.Round,
                             EndCap   = System.Drawing.Drawing2D.LineCap.Round })
                    {
                        g.DrawLine(p, cx, cy - size * 0.22f, cx, cy + size * 0.08f);
                        g.DrawLine(p, cx, cy + size * 0.22f, cx, cy + size * 0.24f);
                    }
                }
                else
                {
                    using (var p = new Pen(color, 2f)
                           { StartCap = System.Drawing.Drawing2D.LineCap.Round,
                             EndCap   = System.Drawing.Drawing2D.LineCap.Round })
                    {
                        g.DrawLine(p, cx, cy - size * 0.04f, cx, cy + size * 0.28f);
                        g.DrawLine(p, cx, cy - size * 0.26f, cx, cy - size * 0.24f);
                    }
                }
            }

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

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _fadeInTimer?.Dispose();
                    _fadeOutTimer?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}

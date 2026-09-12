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
            var thread = new Thread(() =>
            {
                try
                {
                    using (var dlg = new StateDialogForm(kind, title, message))
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
            // ---- colours ----
            private static readonly Color BgColor       = Color.FromArgb(255,  16,  18,  22);
            private static readonly Color BorderColor   = Color.FromArgb(255,  45,  48,  55);
            private static readonly Color TextPrimary   = Color.FromArgb(255, 220, 220, 220);
            private static readonly Color TextSecondary = Color.FromArgb(255, 150, 158, 170);
            private static readonly Color AccentWarn    = Color.FromArgb(255, 255, 160,  30);  // amber
            private static readonly Color AccentInfo    = Color.FromArgb(255,   0, 120, 212);  // blue
            private static readonly Color BtnBg         = Color.FromArgb(255,  35,  38,  45);
            private static readonly Color BtnHover      = Color.FromArgb(255,  50,  54,  64);
            private static readonly Color BtnText       = Color.FromArgb(255, 220, 220, 220);
            private static readonly Color BtnBorder     = Color.FromArgb(255,  60,  64,  74);

            // ---- layout ----
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

            // ---------------------------------------------------------------- //
            //  Constructor
            // ---------------------------------------------------------------- //

            public StateDialogForm(DialogKind kind, string title, string message)
            {
                _kind    = kind;
                _title   = title;
                _message = message;

                // Measure required height: AccentH + Pad + icon row + message lines + Pad + BtnH + Pad
                int msgLines   = message.Split('\n').Length;
                int msgH       = msgLines * 18 + (msgLines > 1 ? 4 : 0);
                int H          = AccentH + Pad + IconSize + 8 + msgH + Pad + BtnH + Pad;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = true;
                DoubleBuffered  = true;
                Width           = W;
                Height          = H;
                BackColor       = BgColor;
                Opacity         = 0;
                StartPosition   = FormStartPosition.Manual;
                KeyPreview      = true;

                // Centre on primary screen
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(
                    wa.Left + (wa.Width  - W) / 2,
                    wa.Top  + (wa.Height - H) / 2);

                // OK button rect (bottom-right)
                _btnRect = new Rectangle(W - Pad - BtnW, H - Pad - BtnH, BtnW, BtnH);

                // Fade in
                _fadeInTimer        = new WinTimer { Interval = 12 };
                _fadeInTimer.Tick  += FadeInTick;

                _fadeOutTimer       = new WinTimer { Interval = 12 };
                _fadeOutTimer.Tick += FadeOutTick;

                MouseMove  += OnMouseMove;
                MouseLeave += (s, e) => { _btnHover = false; Invalidate(_btnRect); };
                MouseDown  += OnMouseDown;
                KeyDown    += OnKeyDown;

                Load += (s, e) => _fadeInTimer.Start();
            }

            // ---------------------------------------------------------------- //
            //  Fade
            // ---------------------------------------------------------------- //

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

            private void StartClose()
            {
                _fadeInTimer.Stop();
                _fadeOutTimer.Start();
            }

            // ---------------------------------------------------------------- //
            //  Input
            // ---------------------------------------------------------------- //

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
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                    StartClose();
            }

            // ---------------------------------------------------------------- //
            //  Paint
            // ---------------------------------------------------------------- //

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Color accent = _kind == DialogKind.Warning ? AccentWarn : AccentInfo;

                // Background
                g.Clear(BgColor);

                // Top accent bar
                using (var b = new SolidBrush(accent))
                    g.FillRectangle(b, 0, 0, W, AccentH);

                // Outer border (subtle dark)
                using (var p = new Pen(BorderColor, 1))
                    g.DrawRectangle(p, 0, 0, W - 1, Height - 1);

                // ---- Icon ----
                int iconX = Pad;
                int iconY = AccentH + Pad;
                DrawIcon(g, _kind, iconX, iconY, IconSize, accent);

                // ---- Title ----
                int textX = iconX + IconSize + 10;
                using (var font = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var brush = new SolidBrush(TextPrimary))
                    g.DrawString(_title, font, brush, textX, iconY);

                // ---- Message ----
                int msgY = iconY + IconSize + 8;
                using (var font = new Font("Segoe UI", 9f))
                using (var brush = new SolidBrush(TextSecondary))
                {
                    var rect = new RectangleF(Pad, msgY, W - Pad * 2, Height);
                    g.DrawString(_message, font, brush, rect);
                }

                // ---- OK button ----
                var btnFill = _btnHover ? BtnHover : BtnBg;
                using (var b = new SolidBrush(btnFill))
                    g.FillRectangle(b, _btnRect);
                using (var p = new Pen(BtnBorder, 1))
                    g.DrawRectangle(p, _btnRect.X, _btnRect.Y, _btnRect.Width - 1, _btnRect.Height - 1);
                using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (var brush = new SolidBrush(BtnText))
                {
                    var sf = new StringFormat
                    {
                        Alignment     = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString("OK", font, brush, _btnRect, sf);
                }
            }

            private static void DrawIcon(Graphics g, DialogKind kind,
                                         int x, int y, int size, Color color)
            {
                var rect = new Rectangle(x, y, size, size);

                // Circle outline
                using (var p = new Pen(color, 2f))
                    g.DrawEllipse(p, rect);

                float cx = x + size / 2f;
                float cy = y + size / 2f;

                if (kind == DialogKind.Warning)
                {
                    // Exclamation mark  !
                    using (var p = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(p, cx, cy - size * 0.22f, cx, cy + size * 0.08f);  // stem
                        g.DrawLine(p, cx, cy + size * 0.22f, cx, cy + size * 0.24f);  // dot (short line)
                    }
                }
                else
                {
                    // Info  i
                    using (var p = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(p, cx, cy - size * 0.04f, cx, cy + size * 0.28f);  // stem
                        g.DrawLine(p, cx, cy - size * 0.26f, cx, cy - size * 0.24f);  // dot
                    }
                }
            }

            // ---------------------------------------------------------------- //
            //  Cleanup
            // ---------------------------------------------------------------- //

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

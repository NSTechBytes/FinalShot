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
using System.Windows.Forms;

namespace PluginScreenshot
{
    // Borderless overlay shown during GIF recording.
    //
    // Layout:
    //   Dashed blue border drawn OUTSIDE the capture region (no pixel inside).
    //   Transparent hole over the capture area, fully click-through.
    //   Full-width flat toolbar flush below the border:
    //       [  Stop  |  Pause  |  Abort  |  00:00:00  ]
    //     Each column is equal width, separated by 1px dividers.
    //     A thin blue accent line runs across the top of the toolbar.
    internal static class GifRecordingOverlay
    {
        private static OverlayForm _form;

        //  Public API

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
                    Logger.Log($"GifRecordingOverlay: thread error -- {ex.Message}");
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "FinalShot-GifOverlay";
            thread.Start();
        }

        // Closes the overlay from any thread.
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

        // Syncs pause state (button label + timer freeze) from any thread.
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

        //  OverlayForm

        internal sealed class OverlayForm : Form
        {
            //  Style -- matches the screenshot exactly

            // Border
            private static readonly Color BorderColor  = Color.FromArgb(255,  0, 120, 212); // #0078D4
            private const int BorderW   = 2;
            private const int BorderGap = 1; // pixels between capture edge and innermost border pixel

            // Toolbar
            private static readonly Color ToolbarBg      = Color.FromArgb(255, 16,  18,  22);  // near-black
            private static readonly Color AccentLine      = Color.FromArgb(255,  0, 120, 212);  // blue top line
            private static readonly Color DividerColor    = Color.FromArgb(255, 40,  42,  48);  // column divider
            private static readonly Color TextNormal      = Color.FromArgb(255, 220, 220, 220); // off-white
            private static readonly Color TextHover       = Color.White;
            private static readonly Color ColHoverBg      = Color.FromArgb(255, 35,  38,  45);  // subtle highlight
            private static readonly Color TimerText       = Color.FromArgb(255, 180, 215, 255); // light blue

            private const int ToolbarH    = 32;  // height of the toolbar
            private const int AccentLineH = 2;   // blue line at top of toolbar
            private const int Cols        = 4;   // Stop | Pause | Abort | Timer

            //  State

            private readonly Rectangle _region;
            private readonly Action    _onStop, _onPause, _onAbort;

            private readonly Timer    _ticker;
            private readonly DateTime _startUtc      = DateTime.UtcNow;
            private TimeSpan          _pausedTotal    = TimeSpan.Zero;
            private DateTime?         _pauseStartUtc  = null;
            private bool              _paused         = false;

            private int       _offsetX, _offsetY;   // where the capture hole starts in form coords
            private int       _tbY;                 // toolbar top Y
            private int       _colW;                // width of each column
            private int       _hovered = -1;        // 0=Stop 1=Pause 2=Abort 3=Timer(no action)

            //  Constructor

            public OverlayForm(Rectangle region, Action onStop, Action onPause, Action onAbort)
            {
                _region  = region;
                _onStop  = onStop;
                _onPause = onPause;
                _onAbort = onAbort;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar   = false;
                TopMost         = false; // Z-order set via SetWindowPos in OnHandleCreated
                DoubleBuffered  = true;

                // Prevent this window from ever stealing focus from Rainmeter.
                // WS_EX_NOACTIVATE ensures clicks and show events don't activate us.
                // MakeTopMostNoActivate() in OnHandleCreated sets HWND_TOPMOST without
                // sending WM_ACTIVATE to any other window, so Rainmeter's Z-pos is safe.

                // Form extends outside the region by formPad on top/left/right.
                // Bottom extends to include the toolbar.
                // formPad must be large enough to contain the border without
                // any pixel falling inside the capture rect.
                int formPad = BorderGap + BorderW + 2; // = 5px

                _offsetX = formPad;
                _offsetY = formPad;

                // Toolbar sits flush below the border -- no gap.
                // The border's outermost bottom pixel is at:
                //   _offsetY + region.Height + BorderGap + BorderW/2 - 1
                // We place the toolbar immediately after.
                _tbY = _offsetY + region.Height + BorderGap + BorderW;

                int formW = region.Width  + formPad * 2;
                int formH = _tbY + ToolbarH;

                Bounds        = new Rectangle(region.X - formPad, region.Y - formPad, formW, formH);
                StartPosition = FormStartPosition.Manual;

                BackColor       = Color.Lime;
                TransparencyKey = Color.Lime;

                // Punch a transparent hole exactly over the capture region.
                using (var outerPath = new GraphicsPath())
                using (var holePath  = new GraphicsPath())
                {
                    outerPath.AddRectangle(new Rectangle(0, 0, formW, formH));
                    holePath.AddRectangle(new Rectangle(_offsetX, _offsetY,
                                                        region.Width, region.Height));
                    var rgn = new System.Drawing.Region(outerPath);
                    rgn.Exclude(holePath);
                    // Control.Region clones the region; dispose our local copy.
                    Region = rgn;
                    rgn.Dispose();
                }

                // Column width = toolbar spans exactly region.Width, starting at _offsetX.
                _colW = region.Width / Cols;

                // Timer redraws every 100 ms.
                _ticker       = new Timer { Interval = 100 };
                _ticker.Tick += (s, e) => InvalidateToolbar();
                _ticker.Start();

                MouseMove  += OnMouseMove;
                MouseDown  += OnMouseDown;
                MouseLeave += (s, e) => { _hovered = -1; InvalidateToolbar(); };
            }

            private void InvalidateToolbar()
            {
                if (!IsDisposed)
                    Invalidate(new Rectangle(0, _tbY, Width, ToolbarH));
            }

            //  Pause state

            public void UpdatePauseState(bool paused)
            {
                _paused = paused;
                if (paused)
                {
                    _pauseStartUtc = DateTime.UtcNow;
                    _ticker.Stop();
                }
                else
                {
                    if (_pauseStartUtc.HasValue)
                    {
                        _pausedTotal  += DateTime.UtcNow - _pauseStartUtc.Value;
                        _pauseStartUtc = null;
                    }
                    _ticker.Start();
                }
                InvalidateToolbar();
            }

            //  Paint

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None; // crisp pixel-aligned rendering

                // Pen centre is BorderGap px outside the hole edge.
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

                PaintToolbar(g);
            }

            private void PaintToolbar(Graphics g)
            {
                int tbX = _offsetX;               // toolbar left = same as capture region left
                int tbW = _region.Width;           // toolbar width = exactly capture region width

                // Background
                using (var bg = new SolidBrush(ToolbarBg))
                    g.FillRectangle(bg, tbX, _tbY, tbW, ToolbarH);

                // Blue accent line at top
                using (var accent = new SolidBrush(AccentLine))
                    g.FillRectangle(accent, tbX, _tbY, tbW, AccentLineH);

                // Column labels
                string[] labels = { "Stop", _paused ? "Resume" : "Pause", "Abort", GetTimerText() };

                for (int i = 0; i < Cols; i++)
                {
                    int cx = tbX + i * _colW;
                    int cw = (i == Cols - 1)
                        ? tbW - i * _colW   // last column takes remaining width
                        : _colW;

                    var colRect = new Rectangle(cx, _tbY + AccentLineH, cw, ToolbarH - AccentLineH);

                    // Hover highlight (not on timer column)
                    if (i == _hovered && i < 3)
                    {
                        using (var hov = new SolidBrush(ColHoverBg))
                            g.FillRectangle(hov, colRect);
                    }

                    // Divider (1px right edge of each column except last)
                    if (i < Cols - 1)
                    {
                        using (var div = new Pen(DividerColor, 1))
                            g.DrawLine(div, cx + cw, _tbY + AccentLineH + 4,
                                            cx + cw, _tbY + ToolbarH - 4);
                    }

                    // Text
                    Color tc = (i == 3) ? TimerText
                             : (_hovered == i) ? TextHover
                             : TextNormal;

                    using (var font = new Font(i == 3 ? "Consolas" : "Segoe UI", 9.5f,
                                               i == 3 ? FontStyle.Bold : FontStyle.Regular))
                    using (var brush = new SolidBrush(tc))
                    {
                        var sf = new StringFormat
                        {
                            Alignment     = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center,
                            FormatFlags   = StringFormatFlags.NoWrap
                        };
                        g.DrawString(labels[i], font, brush, colRect, sf);
                    }
                }
            }

            private string GetTimerText()
            {
                TimeSpan rec = (DateTime.UtcNow - _startUtc) - _pausedTotal;
                if (rec < TimeSpan.Zero) rec = TimeSpan.Zero;
                return $"{(int)rec.TotalMinutes:D2}:{rec.Seconds:D2}:{rec.Milliseconds / 10:D2}";
            }

            //  Mouse

            private int HitTestToolbar(Point p)
            {
                if (p.Y < _tbY || p.Y >= _tbY + ToolbarH) return -1;
                int tbX = _offsetX;
                int rel = p.X - tbX;
                if (rel < 0 || rel >= _region.Width) return -1;
                int col = rel / _colW;
                if (col >= Cols) col = Cols - 1;
                return col < 3 ? col : -1; // timer column has no action
            }

            private void OnMouseMove(object s, MouseEventArgs e)
            {
                int hit = HitTestToolbar(e.Location);
                if (hit != _hovered) { _hovered = hit; InvalidateToolbar(); }
            }

            private void OnMouseDown(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                switch (HitTestToolbar(e.Location))
                {
                    case 0: _onStop?.Invoke();  break;
                    case 1: _onPause?.Invoke(); break;
                    case 2: _onAbort?.Invoke(); break;
                }
            }

            //  No-activate topmost -- keeps Rainmeter skin Z-order intact

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
                if (disposing) _ticker?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

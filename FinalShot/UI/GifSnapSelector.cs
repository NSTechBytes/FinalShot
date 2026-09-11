using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// Shows a full-screen translucent overlay that lets the user drag a
    /// rubber-band rectangle to select a region for GIF recording.
    ///
    /// Usage (must be called on an STA thread):
    ///   Rectangle? region = GifSnapSelector.SelectRegion();
    ///   if (region != null) { /* start recording region.Value */ }
    /// </summary>
    internal static class GifSnapSelector
    {
        /// <summary>
        /// Blocks until the user confirms or cancels a region selection.
        /// Returns the selected rectangle in screen coordinates, or null if cancelled.
        /// Must be called on an STA thread.
        /// </summary>
        public static Rectangle? SelectRegion()
        {
            Rectangle? result = null;
            using (var form = new SnapOverlayForm())
            {
                if (form.ShowDialog() == DialogResult.OK)
                    result = form.SelectedRegion;
            }
            return result;
        }

        // ================================================================== //
        //  SnapOverlayForm — full-screen transparent selection overlay
        // ================================================================== //

        private sealed class SnapOverlayForm : Form
        {
            public Rectangle SelectedRegion { get; private set; }

            private Point   _dragStart;
            private Point   _dragEnd;
            private bool    _dragging;

            // Semi-transparent screenshot of the desktop shown behind the overlay.
            private Bitmap  _desktopSnapshot;

            public SnapOverlayForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState     = FormWindowState.Normal;
                ShowInTaskbar   = false;
                TopMost         = true;
                Cursor          = Cursors.Cross;
                DoubleBuffered  = true;
                KeyPreview      = true;

                // Cover the full virtual screen.
                Rectangle screen = SystemInformation.VirtualScreen;
                Bounds = screen;

                // Take a snapshot of the desktop to show dimmed behind the selection.
                _desktopSnapshot = new Bitmap(screen.Width, screen.Height);
                using (Graphics g = Graphics.FromImage(_desktopSnapshot))
                    g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                // Make background fully transparent — we paint everything in OnPaint.
                BackColor        = Color.Black;
                Opacity          = 0.01; // near-zero opacity to keep TopMost click-through off
                // Actually use a layered window approach: keep opaque but paint manually.
                Opacity          = 1.0;
                BackColor        = Color.FromArgb(1, 1, 1); // won't flicker with TransparencyKey
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragStart = e.Location;
                    _dragEnd   = e.Location;
                    _dragging  = true;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_dragging)
                {
                    _dragEnd = e.Location;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && _dragging)
                {
                    _dragging = false;
                    _dragEnd  = e.Location;

                    Rectangle sel = GetSelectionRect();
                    if (sel.Width > 4 && sel.Height > 4)
                    {
                        // Convert from form-local coords to screen coords.
                        Rectangle screen = SystemInformation.VirtualScreen;
                        SelectedRegion = new Rectangle(
                            sel.X + screen.X,
                            sel.Y + screen.Y,
                            sel.Width,
                            sel.Height);
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        // Too small — reset and let user try again.
                        _dragStart = Point.Empty;
                        _dragEnd   = Point.Empty;
                        Invalidate();
                    }
                }
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;

                // 1. Draw dimmed desktop snapshot.
                using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    g.DrawImage(_desktopSnapshot, 0, 0);
                    g.FillRectangle(dim, ClientRectangle);
                }

                Rectangle sel = GetSelectionRect();

                if (sel.Width > 0 && sel.Height > 0)
                {
                    // 2. Cut out the selected region (show it undimmed).
                    g.DrawImage(_desktopSnapshot,
                                new Rectangle(sel.X, sel.Y, sel.Width, sel.Height),
                                sel, GraphicsUnit.Pixel);

                    // 3. Draw selection border.
                    using (var pen = new Pen(Color.FromArgb(255, 255, 80, 0), 2))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
                    }

                    // 4. Size label.
                    string label = $"{sel.Width} × {sel.Height}";
                    using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
                    using (var bg   = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    using (var fg   = new SolidBrush(Color.White))
                    {
                        SizeF ts  = g.MeasureString(label, font);
                        int   lx  = sel.Right - (int)ts.Width - 6;
                        int   ly  = sel.Bottom + 4;
                        if (ly + ts.Height > ClientRectangle.Height)
                            ly = sel.Top - (int)ts.Height - 4;

                        g.FillRectangle(bg, lx - 2, ly - 2, ts.Width + 4, ts.Height + 4);
                        g.DrawString(label, font, fg, lx, ly);
                    }
                }
                else
                {
                    // Instruction text before any drag.
                    string hint = "Drag to select region  •  Right-click or Esc to cancel";
                    using (var font = new Font("Segoe UI", 12f))
                    using (var fg   = new SolidBrush(Color.White))
                    {
                        SizeF ts = g.MeasureString(hint, font);
                        g.DrawString(hint, font, fg,
                            (ClientRectangle.Width  - ts.Width)  / 2f,
                            (ClientRectangle.Height - ts.Height) / 2f);
                    }
                }
            }

            private Rectangle GetSelectionRect()
            {
                int x = Math.Min(_dragStart.X, _dragEnd.X);
                int y = Math.Min(_dragStart.Y, _dragEnd.Y);
                int w = Math.Abs(_dragStart.X - _dragEnd.X);
                int h = Math.Abs(_dragStart.Y - _dragEnd.Y);
                return new Rectangle(x, y, w, h);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _desktopSnapshot?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

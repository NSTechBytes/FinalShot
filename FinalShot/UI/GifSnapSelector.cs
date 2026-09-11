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
        //  Shared style constants — keep in sync with CustomScreenshotForm
        // ================================================================== //

        internal static readonly Color SelectionBorderColor = Color.FromArgb(255, 0, 120, 212); // Windows blue #0078D4
        internal static readonly Color SelectionFillColor   = Color.FromArgb(30,  0, 120, 212);
        internal static readonly Color LabelBackColor       = Color.FromArgb(220, 0,  80, 160);
        internal static readonly Color LabelForeColor       = Color.White;
        internal static readonly Color DimColor             = Color.FromArgb(130, 0,   0,   0);

        // ================================================================== //
        //  SnapOverlayForm — full-screen transparent selection overlay
        // ================================================================== //

        private sealed class SnapOverlayForm : Form
        {
            public Rectangle SelectedRegion { get; private set; }

            private Point  _dragStart;
            private Point  _dragEnd;
            private bool   _dragging;

            // Desktop snapshot shown dimmed behind the overlay.
            private Bitmap _desktopSnapshot;

            public SnapOverlayForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState     = FormWindowState.Normal;
                ShowInTaskbar   = false;
                TopMost         = true;
                Cursor          = Cursors.Cross;
                DoubleBuffered  = true;
                KeyPreview      = true;

                Rectangle screen = SystemInformation.VirtualScreen;
                Bounds = screen;

                _desktopSnapshot = new Bitmap(screen.Width, screen.Height);
                using (Graphics g = Graphics.FromImage(_desktopSnapshot))
                    g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                Opacity   = 1.0;
                BackColor = Color.FromArgb(1, 1, 1);
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

                // 1. Dimmed desktop snapshot.
                g.DrawImage(_desktopSnapshot, 0, 0);
                using (var dim = new SolidBrush(DimColor))
                    g.FillRectangle(dim, ClientRectangle);

                Rectangle sel = GetSelectionRect();

                if (sel.Width > 1 && sel.Height > 1)
                {
                    // 2. Show undimmed desktop content inside the selection.
                    g.DrawImage(_desktopSnapshot,
                                new Rectangle(sel.X, sel.Y, sel.Width, sel.Height),
                                sel, GraphicsUnit.Pixel);

                    // 3. Semi-transparent blue fill.
                    using (var fill = new SolidBrush(SelectionFillColor))
                        g.FillRectangle(fill, sel);

                    // 4. Solid blue border.
                    using (var pen = new Pen(SelectionBorderColor, 2))
                        g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);

                    // 5. Corner handles.
                    DrawCornerHandles(g, sel);

                    // 6. Size label.
                    DrawSizeLabel(g, sel, ClientRectangle);
                }
                // No instruction text — overlay speaks for itself.
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

        // ------------------------------------------------------------------ //
        //  Shared paint helpers (used by both GifSnapSelector and
        //  CustomScreenshotForm via internal access)
        // ------------------------------------------------------------------ //

        /// <summary>Draws small square handles at each corner of the selection.</summary>
        internal static void DrawCornerHandles(Graphics g, Rectangle sel)
        {
            const int sz = 6;
            using (var fill = new SolidBrush(SelectionBorderColor))
            {
                Point[] corners =
                {
                    new Point(sel.Left,       sel.Top),
                    new Point(sel.Right - sz, sel.Top),
                    new Point(sel.Left,       sel.Bottom - sz),
                    new Point(sel.Right - sz, sel.Bottom - sz),
                };
                foreach (var c in corners)
                    g.FillRectangle(fill, c.X, c.Y, sz, sz);
            }
        }

        /// <summary>
        /// Draws the "W × H" size label in a blue pill below (or above) the selection.
        /// </summary>
        internal static void DrawSizeLabel(Graphics g, Rectangle sel, Rectangle clientBounds)
        {
            string label = $"{sel.Width} × {sel.Height}";
            using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var bg   = new SolidBrush(LabelBackColor))
            using (var fg   = new SolidBrush(LabelForeColor))
            {
                SizeF ts      = g.MeasureString(label, font);
                int   padX    = 8;
                int   padY    = 4;
                int   boxW    = (int)ts.Width  + padX * 2;
                int   boxH    = (int)ts.Height + padY * 2;

                // Position: centred under the selection, flip above if too close to bottom.
                int bx = sel.Left + (sel.Width - boxW) / 2;
                int by = sel.Bottom + 6;
                if (by + boxH > clientBounds.Height - 4)
                    by = sel.Top - boxH - 6;
                // Clamp horizontally.
                if (bx < 4) bx = 4;
                if (bx + boxW > clientBounds.Width - 4)
                    bx = clientBounds.Width - boxW - 4;

                // Rounded rectangle background.
                using (var path = RoundedRect(bx, by, boxW, boxH, 5))
                    g.FillPath(bg, path);

                g.DrawString(label, font, fg, bx + padX, by + padY);
            }
        }

        /// <summary>Returns a rounded rectangle GraphicsPath.</summary>
        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x,         y,         r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y,     r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x,         y + h - r * 2,      r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

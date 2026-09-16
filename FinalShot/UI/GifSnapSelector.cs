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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    // Shows a full-screen translucent overlay that lets the user:
    //   hover over a window/control to snap to it (same logic as CustomScreenshotForm), or
    //   drag a rubber-band rectangle to select an arbitrary region.
    //
    // Usage (must be called on an STA thread):
    //   Rectangle? region = GifSnapSelector.SelectRegion(settings);
    //   if (region != null) { /* start recording region.Value */ }
    internal static class GifSnapSelector
    {
        // Blocks until the user confirms or cancels a region selection.
        // Returns the selected rectangle in screen coordinates, or null if cancelled.
        // Must be called on an STA thread.
        public static Rectangle? SelectRegion(Settings settings)
        {
            Rectangle? result = null;
            using (var form = new SnapOverlayForm(settings))
            {
                if (form.ShowDialog() == DialogResult.OK)
                    result = form.SelectedRegion;
            }
            return result;
        }

        //  Shared overlay style -- resolved from UITheme on each form

        private sealed class SnapOverlayForm : Form
        {
            public Rectangle SelectedRegion { get; private set; }

            private readonly Settings    _settings;
            private readonly ThemeColors _theme;

            // Drag state
            private Point     _dragStart;
            private Point     _dragEnd;
            private bool      _dragging;

            // Window-detection state (mirrors CustomScreenshotForm exactly)
            private List<WindowInfo> _windows             = new List<WindowInfo>();
            private WindowInfo       _hoveredWindow        = null;
            private bool             _windowsLoaded        = false;
            private bool             _pendingWindowCapture = false;

            // Desktop snapshot shown dimmed behind the overlay.
            private Bitmap _desktopSnapshot;

            public SnapOverlayForm(Settings settings)
            {
                _settings = settings;
                _theme    = ThemeColors.Resolve(settings?.UITheme ?? UITheme.Dark);

                NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_PER_MONITOR_AWARE_V2);

                FormBorderStyle = FormBorderStyle.None;
                WindowState     = FormWindowState.Normal;
                ShowInTaskbar   = false;
                TopMost         = true;
                Cursor          = Cursors.Cross;
                DoubleBuffered  = true;
                KeyPreview      = true;
                StartPosition   = FormStartPosition.Manual;

                Rectangle screen = SystemInformation.VirtualScreen;
                Bounds   = screen;
                Location = screen.Location;

                // ShareX-style freeze when SnapFreezeCursor=1 (Cross = secondary cursor).
                // Stamp cursor only when ShowCursor=1.
                _desktopSnapshot = ScreenshotManager.CaptureDesktopSnapshot(
                    _settings != null && _settings.SnapFreezeCursor && _settings.ShowCursor);
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                Opacity   = 1.0;
                BackColor = Color.FromArgb(1, 1, 1);

                // Enumerate windows on a background thread, same as CustomScreenshotForm.
                if (!_settings.DetectWindows) return;

                var form = this;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var detector = new WindowsDetector
                        {
                            IncludeChildWindows = _settings.DetectControls
                        };
                        detector.IgnoreHandles.Add(form.Handle);

                        List<WindowInfo> result = detector.GetWindowList();

                        if (!form.IsDisposed)
                        {
                            form.BeginInvoke(new Action(() =>
                            {
                                if (!form.IsDisposed && !_dragging)
                                {
                                    _windows       = result;
                                    _windowsLoaded = true;
                                    Logger.Log($"GifSnapSelector: loaded {result.Count} window entries.");
                                    form.Invalidate();
                                }
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("GifSnapSelector WindowsDetector error: " + ex.Message);
                    }
                });
            }

            //  Keyboard

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            }

            //  Mouse down

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }

                if (e.Button != MouseButtons.Left) return;

                _dragStart = e.Location;
                _dragEnd   = e.Location;

                if (_windowsLoaded && _hoveredWindow != null && _settings.DetectWindows)
                {
                    // A window is highlighted -- confirm on mouse-up unless user drags > 4px.
                    _pendingWindowCapture = true;
                    _dragging             = false;
                }
                else
                {
                    _pendingWindowCapture = false;
                    _dragging             = true;
                }
            }

            //  Mouse move

            protected override void OnMouseMove(MouseEventArgs e)
            {
                // If user started on a highlighted window but then dragged, switch to free-select.
                if (_pendingWindowCapture)
                {
                    double dist = Math.Sqrt(
                        Math.Pow(e.X - _dragStart.X, 2) +
                        Math.Pow(e.Y - _dragStart.Y, 2));

                    if (dist >= 4)
                    {
                        _pendingWindowCapture = false;
                        _hoveredWindow        = null;
                        _dragging             = true;
                    }
                    else
                    {
                        return;
                    }
                }

                if (_dragging)
                {
                    _dragEnd = e.Location;
                    Invalidate();
                    return;
                }

                // Idle hover -- find topmost window under cursor.
                if (_windowsLoaded && _settings.DetectWindows)
                {
                    Point      screenPt = PointToScreen(e.Location);
                    WindowInfo found    = null;

                    for (int i = 0; i < _windows.Count; i++)
                    {
                        if (_windows[i].Rectangle.Contains(screenPt))
                        {
                            found = _windows[i];
                            break;
                        }
                    }

                    if (found != _hoveredWindow)
                    {
                        _hoveredWindow = found;
                        Invalidate();
                    }
                }
            }

            //  Mouse up

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;

                // Window-snap confirm.
                if (_pendingWindowCapture && _hoveredWindow != null)
                {
                    _pendingWindowCapture = false;
                    SelectedRegion = _hoveredWindow.Rectangle;
                    Logger.Log($"GifSnapSelector: window snap -> {SelectedRegion}");
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                // Free-drag confirm.
                if (_dragging)
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
                        Logger.Log($"GifSnapSelector: free-drag -> {SelectedRegion}");
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        // Too small -- reset, let user try again.
                        _dragStart = Point.Empty;
                        _dragEnd   = Point.Empty;
                        Invalidate();
                    }
                }
            }

            //  Paint

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics  g      = e.Graphics;
                Rectangle client = ClientRectangle;

                // 1. Dimmed desktop snapshot as background.
                g.DrawImage(_desktopSnapshot, 0, 0);
                using (var dim = new SolidBrush(_theme.SnapDim))
                    g.FillRectangle(dim, client);

                if (_dragging)
                {
                    Rectangle sel = GetSelectionRect();
                    if (sel.Width > 1 && sel.Height > 1)
                    {
                        // Show undimmed desktop inside selection.
                        g.DrawImage(_desktopSnapshot,
                                    new Rectangle(sel.X, sel.Y, sel.Width, sel.Height),
                                    sel, GraphicsUnit.Pixel);

                        // Themed fill + border + handles + label.
                        using (var fill = new SolidBrush(_theme.SnapFill))
                            g.FillRectangle(fill, sel);
                        using (var pen = new Pen(_theme.SnapBorder, 2))
                            g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
                        DrawCornerHandles(g, sel, _theme);
                        DrawSizeLabel(g, sel, client, _theme);
                    }
                    return;
                }

                if (!_windowsLoaded || _hoveredWindow == null) return;

                // Convert hovered rect to form-local coords.
                Rectangle screen2 = SystemInformation.VirtualScreen;
                Rectangle formRect = new Rectangle(
                    _hoveredWindow.Rectangle.X - screen2.X,
                    _hoveredWindow.Rectangle.Y - screen2.Y,
                    _hoveredWindow.Rectangle.Width,
                    _hoveredWindow.Rectangle.Height);

                Rectangle active = Rectangle.Intersect(formRect, client);
                if (active.Width <= 0 || active.Height <= 0) return;

                // Show undimmed desktop inside the hovered region.
                g.DrawImage(_desktopSnapshot,
                            new Rectangle(active.X, active.Y, active.Width, active.Height),
                            active, GraphicsUnit.Pixel);

                // Extra dim on the four surrounding bands.
                using (var band = new SolidBrush(_theme.SnapDim))
                {
                    if (active.Top    > 0)       g.FillRectangle(band, 0,            0,             client.Width,              active.Top);
                    if (active.Left   > 0)        g.FillRectangle(band, 0,            active.Top,    active.Left,               active.Height);
                    if (active.Right  < client.Width)  g.FillRectangle(band, active.Right, active.Top,    client.Width - active.Right,  active.Height);
                    if (active.Bottom < client.Height) g.FillRectangle(band, 0,            active.Bottom, client.Width,              client.Height - active.Bottom);
                }

                // Themed fill + border + handles.
                using (var fill = new SolidBrush(_theme.SnapFill))
                    g.FillRectangle(fill, active);
                using (var pen = new Pen(_theme.SnapBorder, 2))
                    g.DrawRectangle(pen, active.X, active.Y, active.Width - 1, active.Height - 1);
                DrawCornerHandles(g, active, _theme);

                // Size label uses the actual window pixel dimensions.
                Rectangle sizeRect = new Rectangle(active.X, active.Y,
                                                   _hoveredWindow.Rectangle.Width,
                                                   _hoveredWindow.Rectangle.Height);
                DrawSizeLabel(g, sizeRect, client, _theme);
            }

            //  Helpers

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

        //  Shared paint helpers -- used by GifSnapSelector and CustomScreenshotForm

        // Draws small square handles at each corner of the selection.
        internal static void DrawCornerHandles(Graphics g, Rectangle sel, ThemeColors theme)
        {
            if (theme == null) theme = ThemeColors.Resolve(UITheme.Dark);
            const int sz = 6;
            using (var fill = new SolidBrush(theme.SnapBorder))
            {
                Point[] corners =
                {
                    new Point(sel.Left,           sel.Top),
                    new Point(sel.Right - sz,      sel.Top),
                    new Point(sel.Left,            sel.Bottom - sz),
                    new Point(sel.Right - sz,      sel.Bottom - sz),
                };
                foreach (var c in corners)
                    g.FillRectangle(fill, c.X, c.Y, sz, sz);
            }
        }

        // Draws the "W x H" size label in a rounded themed pill below (or above) the selection.
        internal static void DrawSizeLabel(Graphics g, Rectangle sel, Rectangle clientBounds,
                                          ThemeColors theme)
        {
            if (theme == null) theme = ThemeColors.Resolve(UITheme.Dark);
            string label = $"{sel.Width} × {sel.Height}";
            using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var bg   = new SolidBrush(theme.SnapLabelBack))
            using (var fg   = new SolidBrush(theme.SnapLabelFore))
            {
                SizeF ts   = g.MeasureString(label, font);
                int   padX = 8, padY = 4;
                int   boxW = (int)ts.Width  + padX * 2;
                int   boxH = (int)ts.Height + padY * 2;

                // Centre below the selection; flip above if near the bottom edge.
                int bx = sel.Left + (sel.Width  - boxW) / 2;
                int by = sel.Bottom + 6;
                if (by + boxH > clientBounds.Height - 4)
                    by = sel.Top - boxH - 6;

                // Clamp horizontally.
                if (bx < 4) bx = 4;
                if (bx + boxW > clientBounds.Width - 4)
                    bx = clientBounds.Width - boxW - 4;

                using (var path = RoundedRect(bx, by, boxW, boxH, 5))
                    g.FillPath(bg, path);

                g.DrawString(label, font, fg, bx + padX, by + padY);
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
    }
}

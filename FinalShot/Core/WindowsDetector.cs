using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace PluginScreenshot
{
    /// <summary>
    /// Enumerates all visible, non-cloaked top-level windows (and optionally their child
    /// controls) using Win32 EnumWindows / EnumChildWindows.
    ///
    /// This is a self-contained port of ShareX's WindowsRectangleList adapted for
    /// FinalShot's WinForms / .NET 4.8 environment.
    /// </summary>
    internal class WindowsDetector
    {
        // ──────────────────────────────────────────────────────────────
        // Configuration
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Window class names to skip entirely (e.g. NVIDIA GeForce Overlay).
        /// </summary>
        public List<string> IgnoreClassNames { get; } = new List<string>
        {
            "CEF-OSC-WIDGET"   // NVIDIA GeForce Overlay DT
        };

        /// <summary>
        /// Handles to skip (e.g. the FinalShot overlay form itself).
        /// </summary>
        public List<IntPtr> IgnoreHandles { get; } = new List<IntPtr>();

        /// <summary>
        /// When true, child controls are enumerated inside each top-level window.
        /// Matches ShareX's RegionCaptureOptions.DetectControls.
        /// </summary>
        public bool IncludeChildWindows { get; set; }

        // ──────────────────────────────────────────────────────────────
        // Private state (reset per call)
        // ──────────────────────────────────────────────────────────────

        private List<WindowInfo> _results;
        private HashSet<IntPtr> _parentHandles;

        // ──────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a filtered, z-ordered list of visible window / control rectangles.
        /// Safe to call on a background thread.
        /// </summary>
        public List<WindowInfo> GetWindowList()
        {
            _results      = new List<WindowInfo>();
            _parentHandles = new HashSet<IntPtr>();

            try
            {
                NativeMethods.EnumWindows(CheckTopLevelWindow, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Log("WindowsDetector.GetWindowList exception: " + ex.Message);
            }

            // ── Post-process: remove child rects fully covered by a parent ──
            // This mirrors ShareX's final filter loop.
            var visible = new List<WindowInfo>(_results.Count);
            foreach (WindowInfo w in _results)
            {
                if (w.IsTopLevel)
                {
                    visible.Add(w);
                    continue;
                }

                bool covered = false;
                foreach (WindowInfo parent in visible)
                {
                    if (parent.Rectangle.Contains(w.Rectangle))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                    visible.Add(w);
            }

            return visible;
        }

        // ──────────────────────────────────────────────────────────────
        // EnumWindows callback — top-level windows
        // ──────────────────────────────────────────────────────────────

        private bool CheckTopLevelWindow(IntPtr hWnd, IntPtr _)
        {
            return CheckHandle(hWnd, clipRect: null, isTopLevel: true);
        }

        // ──────────────────────────────────────────────────────────────
        // Core handle processor (shared for top-level and child windows)
        // ──────────────────────────────────────────────────────────────

        private bool CheckHandle(IntPtr hWnd, Rectangle? clipRect, bool isTopLevel)
        {
            // Skip explicitly ignored handles (e.g. our own overlay form)
            if (IgnoreHandles.Contains(hWnd))
                return true;

            // Skip invisible windows
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            if (isTopLevel)
            {
                // Skip cloaked windows (windows on other virtual desktops, etc.)
                if (IsCloaked(hWnd))
                    return true;

                // Skip windows that are both tool-windows AND non-activatable
                // (tiling manager overlays, system auxiliaries — same filter as ShareX)
                int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isToolWindow  = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                bool isNoActivate  = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
                if (isToolWindow && isNoActivate)
                    return true;

                // Skip ignored class names
                string className = GetClassName(hWnd);
                if (!string.IsNullOrEmpty(className))
                {
                    foreach (string ignored in IgnoreClassNames)
                    {
                        if (string.Equals(className, ignored, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            // ── Compute rectangle ──
            Rectangle rect;
            if (isTopLevel)
            {
                if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                    return true;
                rect = RectFromNative(r);
            }
            else
            {
                if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                    return true;
                rect = Rectangle.Intersect(RectFromNative(r), clipRect.Value);
            }

            if (rect.Width <= 0 || rect.Height <= 0)
                return true;

            // ── For top-level windows: enumerate children and add client rect ──
            if (isTopLevel)
            {
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle capture = rect;   // capture for lambda

                    NativeMethods.EnumChildWindows(hWnd, (childHwnd, _) =>
                        CheckHandle(childHwnd, capture, isTopLevel: false), IntPtr.Zero);
                }

                // Also add the client rect as a finer-grained entry (same as ShareX)
                Rectangle clientRect = GetClientRect(hWnd);
                if (clientRect.Width > 0 && clientRect.Height > 0 && clientRect != rect)
                {
                    _results.Add(new WindowInfo
                    {
                        Handle    = hWnd,
                        Rectangle = clientRect,
                        IsTopLevel = false          // treated as sub-entry for filtering
                    });
                }
            }

            _results.Add(new WindowInfo
            {
                Handle    = hWnd,
                Rectangle = rect,
                IsTopLevel = isTopLevel
            });

            return true;
        }

        // ──────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────

        private static bool IsCloaked(IntPtr hWnd)
        {
            try
            {
                int result = NativeMethods.DwmGetWindowAttribute(
                    hWnd,
                    NativeMethods.DWMWA_CLOAKED,
                    out int cloaked,
                    sizeof(int));
                return result == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string GetClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            return NativeMethods.GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }

        private static Rectangle GetClientRect(IntPtr hWnd)
        {
            if (!NativeMethods.GetClientRect(hWnd, out NativeMethods.RECT r))
                return Rectangle.Empty;

            // GetClientRect returns coords relative to the window; convert to screen
            var pt = new NativeMethods.POINT { x = r.Left, y = r.Top };
            NativeMethods.ClientToScreen(hWnd, ref pt);
            return new Rectangle(pt.x, pt.y, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static Rectangle RectFromNative(NativeMethods.RECT r)
        {
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }
}

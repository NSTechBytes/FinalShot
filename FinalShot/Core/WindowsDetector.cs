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
    /// Port of ShareX's WindowsRectangleList for FinalShot (.NET 4.8 / WinForms).
    /// </summary>
    internal class WindowsDetector
    {
        // ── Configuration ────────────────────────────────────────────

        /// <summary>Window class names to skip (e.g. NVIDIA GeForce Overlay).</summary>
        public List<string> IgnoreClassNames { get; } = new List<string>
        {
            "CEF-OSC-WIDGET"
        };

        /// <summary>Handles to skip (add the overlay form's own handle here).</summary>
        public List<IntPtr> IgnoreHandles { get; } = new List<IntPtr>();

        /// <summary>When true, child controls inside each top-level window are included.</summary>
        public bool IncludeChildWindows { get; set; }

        // ── Private state (reset per GetWindowList call) ─────────────

        private List<WindowInfo>  _results;
        private HashSet<IntPtr>   _parentHandles;

        // ── Public API ───────────────────────────────────────────────

        /// <summary>
        /// Returns a z-ordered list of visible window / control rectangles (screen coords).
        /// Safe to call on a background thread.
        /// </summary>
        public List<WindowInfo> GetWindowList()
        {
            _results       = new List<WindowInfo>();
            _parentHandles = new HashSet<IntPtr>();

            try
            {
                // Hold a strong reference to the delegate so GC cannot collect it
                // while EnumWindows is running.
                NativeMethods.EnumWindowsProc topLevelCallback = CheckTopLevelWindow;
                NativeMethods.EnumWindows(topLevelCallback, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Log("WindowsDetector.GetWindowList exception: " + ex.Message);
            }

            // ── Post-process filter ──────────────────────────────────
            // Mirror ShareX's final loop:
            //   • Top-level windows are always kept.
            //   • A child/client-rect entry is dropped only when another entry already
            //     in the output list fully contains it (deduplication).
            //
            // Key ordering invariant (guaranteed by CheckHandle):
            //   Child controls and the client-rect sub-entry for a window are appended
            //   to _results BEFORE their owning top-level window.  That means when we
            //   reach a child in the loop its parent top-level is NOT yet in `visible`,
            //   so the child won't be incorrectly blocked by its own parent.
            var visible = new List<WindowInfo>(_results.Count);

            foreach (WindowInfo w in _results)
            {
                if (w.IsTopLevel)
                {
                    visible.Add(w);
                    continue;
                }

                // Child / client-rect entry: keep unless another visible entry already
                // fully covers this rectangle (avoids redundant sub-regions).
                bool covered = false;
                foreach (WindowInfo existing in visible)
                {
                    if (existing.Rectangle.Contains(w.Rectangle))
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

        // ── EnumWindows callback ─────────────────────────────────────

        private bool CheckTopLevelWindow(IntPtr hWnd, IntPtr lParam)
        {
            return CheckHandle(hWnd, clipRect: null, isTopLevel: true);
        }

        // ── Core handler (shared for top-level and child windows) ────

        private bool CheckHandle(IntPtr hWnd, Rectangle? clipRect, bool isTopLevel)
        {
            if (IgnoreHandles.Contains(hWnd))
                return true;

            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            if (isTopLevel)
            {
                if (IsCloaked(hWnd))
                    return true;

                int exStyle       = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                bool isNoActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE)  != 0;
                if (isToolWindow && isNoActivate)
                    return true;

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

            // ── Compute rectangle ────────────────────────────────────

            if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                return true;

            Rectangle rect = RectFromNative(r);

            if (!isTopLevel)
            {
                // Clip child rect to its parent's bounds
                rect = Rectangle.Intersect(rect, clipRect.Value);
            }

            if (rect.Width <= 0 || rect.Height <= 0)
                return true;

            // ── Children and client-rect first, then the top-level entry ────
            // IMPORTANT: children must land in _results BEFORE their parent so the
            // post-filter loop encounters them before the parent is in `visible`.

            if (isTopLevel)
            {
                // 1. Enumerate child controls (added to _results recursively here)
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle parentRect = rect;    // local copy for lambda capture

                    // Store delegate in a named local — prevents premature GC during
                    // the P/Invoke call (anonymous lambda has no strong reference).
                    NativeMethods.EnumWindowsProc childCallback =
                        (childHwnd, _lp) => CheckHandle(childHwnd, parentRect, isTopLevel: false);

                    NativeMethods.EnumChildWindows(hWnd, childCallback, IntPtr.Zero);
                }

                // 2. Add the client-rect sub-entry when it differs from the window rect.
                //    This gives finer-grained snapping (e.g. Chrome toolbar vs page area).
                //    Mark it IsTopLevel = false so the post-filter can deduplicate it.
                Rectangle clientRect = GetClientRect(hWnd);
                if (clientRect.Width > 0 && clientRect.Height > 0 && clientRect != rect)
                {
                    _results.Add(new WindowInfo
                    {
                        Handle     = hWnd,
                        Rectangle  = clientRect,
                        IsTopLevel = false   // subject to duplication filter
                    });
                }
            }

            // 3. Add the window / control itself (ALWAYS last for this hWnd so children
            //    appear before parent in _results — required by the post-filter).
            _results.Add(new WindowInfo
            {
                Handle     = hWnd,
                Rectangle  = rect,
                IsTopLevel = isTopLevel
            });

            return true;
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static bool IsCloaked(IntPtr hWnd)
        {
            try
            {
                int hr = NativeMethods.DwmGetWindowAttribute(
                    hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
                return hr == 0 && cloaked != 0;
            }
            catch { return false; }
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

            // GetClientRect returns window-relative coords; map to screen.
            var pt = new NativeMethods.POINT { x = r.Left, y = r.Top };
            NativeMethods.ClientToScreen(hWnd, ref pt);
            return new Rectangle(pt.x, pt.y, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static Rectangle RectFromNative(NativeMethods.RECT r)
            => new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

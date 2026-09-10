using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PluginScreenshot
{
    // Enumerates visible windows and child controls using EnumWindows / EnumChildWindows.
    // Exact port of ShareX's WindowsRectangleList for FinalShot (.NET 4.8 / WinForms).
    internal class WindowsDetector
    {
        // Configuration

        // Window class names to skip entirely (e.g. NVIDIA GeForce Overlay)
        public List<string> IgnoreClassNames { get; } = new List<string>
        {
            "CEF-OSC-WIDGET"
        };

        // Handles to skip — add the overlay form's own handle before calling GetWindowList
        public List<IntPtr> IgnoreHandles { get; } = new List<IntPtr>();

        // When true, child controls inside each top-level window are included
        public bool IncludeChildWindows { get; set; }

        // Private state — reset on each GetWindowList call

        private List<WindowInfo> _results;
        private HashSet<IntPtr>  _parentHandles;

        // Public API

        // Returns a z-ordered list of visible window and control rectangles in screen coords.
        // Safe to call on a background thread.
        public List<WindowInfo> GetWindowList()
        {
            _results       = new List<WindowInfo>();
            _parentHandles = new HashSet<IntPtr>();

            try
            {
                NativeMethods.EnumWindowsProc topLevel = CheckTopLevelWindow;
                NativeMethods.EnumWindows(topLevel, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Log("WindowsDetector.GetWindowList exception: " + ex.Message);
            }

            // Post-filter: children appear before their parent in _results (guaranteed by
            // CheckHandle ordering), so when a child is evaluated its parent top-level is
            // not yet in result — children are only blocked by earlier children, never by
            // their own parent. Top-level entries are always kept.
            var result = new List<WindowInfo>(_results.Count);

            foreach (WindowInfo w in _results)
            {
                bool keep = true;

                if (!w.IsTopLevel)
                {
                    foreach (WindowInfo prev in result)
                    {
                        if (prev.Rectangle.Contains(w.Rectangle))
                        {
                            keep = false;
                            break;
                        }
                    }
                }

                if (keep)
                    result.Add(w);
            }

            return result;
        }

        // EnumWindows callback

        private bool CheckTopLevelWindow(IntPtr hWnd, IntPtr lParam)
        {
            return CheckHandle(hWnd, clipRect: null);
        }

        // Core handler — shared for top-level and child windows

        private bool CheckHandle(IntPtr hWnd, Rectangle? clipRect)
        {
            if (IgnoreHandles.Contains(hWnd))
                return true;

            bool isTopLevel = clipRect == null;

            // Only call IsWindowVisible for top-level windows.
            // Many valid child controls (Explorer sidebar, DirectUIHWND panels) fail
            // IsWindowVisible because it walks the parent chain — skip this for children.
            if (isTopLevel && !NativeMethods.IsWindowVisible(hWnd))
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

                string cls = GetClassName(hWnd);
                if (!string.IsNullOrEmpty(cls))
                {
                    foreach (string ignored in IgnoreClassNames)
                    {
                        if (string.Equals(cls, ignored, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            // Compute bounding rectangle

            Rectangle rect;
            if (isTopLevel)
            {
                // Prefer DWM extended frame bounds — the visually rendered rect without shadow pixels
                rect = GetWindowRectangle(hWnd);
            }
            else
            {
                if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                    return true;
                rect = RectFromNative(r);
                rect = Rectangle.Intersect(rect, clipRect.Value);
            }

            if (rect.Width <= 0 || rect.Height <= 0)
                return true;

            // Add children before self so they precede the parent in _results.
            // This is required by the post-filter (children must be seen before parent).

            if (isTopLevel)
            {
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle parentRect = rect;

                    // Store delegate in a named local to prevent GC during P/Invoke
                    NativeMethods.EnumWindowsProc childCb =
                        (childHwnd, _lp) => CheckHandle(childHwnd, parentRect);
                    NativeMethods.EnumChildWindows(hWnd, childCb, IntPtr.Zero);
                }

                // Add a client-rect sub-entry for finer snapping (e.g. Chrome toolbar vs page area)
                Rectangle clientRect = GetClientRect(hWnd);
                if (clientRect.Width > 0 && clientRect.Height > 0 && clientRect != rect)
                {
                    _results.Add(new WindowInfo
                    {
                        Handle     = hWnd,
                        Rectangle  = clientRect,
                        IsTopLevel = false
                    });
                }
            }
            else
            {
                // Track child containers so we don't recurse into the same handle twice
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle childRect = rect;

                    NativeMethods.EnumWindowsProc grandChildCb =
                        (grandChild, _lp) => CheckHandle(grandChild, childRect);
                    NativeMethods.EnumChildWindows(hWnd, grandChildCb, IntPtr.Zero);
                }
            }

            // Add self last so all children appear before this entry in _results
            _results.Add(new WindowInfo
            {
                Handle     = hWnd,
                Rectangle  = rect,
                IsTopLevel = isTopLevel
            });

            return true;
        }

        // Helpers
        private static Rectangle GetWindowRectangle(IntPtr hWnd)
        {
            int hr = NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT dwmRect,
                Marshal.SizeOf(typeof(NativeMethods.RECT)));

            if (hr == 0)
            {
                Rectangle dwm = RectFromNative(dwmRect);
                if (dwm.Width > 0 && dwm.Height > 0)
                    return dwm;
            }

            if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                return RectFromNative(r);

            return Rectangle.Empty;
        }

        private static Rectangle GetClientRect(IntPtr hWnd)
        {
            if (!NativeMethods.GetClientRect(hWnd, out NativeMethods.RECT r))
                return Rectangle.Empty;

            var pt = new NativeMethods.POINT { x = r.Left, y = r.Top };
            NativeMethods.ClientToScreen(hWnd, ref pt);
            return new Rectangle(pt.x, pt.y, r.Right - r.Left, r.Bottom - r.Top);
        }

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

        private static Rectangle RectFromNative(NativeMethods.RECT r)
            => new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

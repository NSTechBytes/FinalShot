using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PluginScreenshot
{
    /// <summary>
    /// Exact port of ShareX's WindowsRectangleList for FinalShot (.NET 4.8 / WinForms).
    ///
    /// Four fixes vs previous version to match ShareX behaviour
    /// ──────────────────────────────────────────────────────────
    /// Fix A — Top-level rect uses DWM extended frame bounds first (DWMWA_EXTENDED_FRAME_BOUNDS)
    ///   Plain GetWindowRect includes invisible shadow/border pixels on modern Windows.
    ///   ShareX calls CaptureHelpers.GetWindowRectangle which tries DWM first.
    ///   Without this, oversized shadow rects eclipse child-control entries in the
    ///   post-filter, and hover detection snaps to wrong positions.
    ///
    /// Fix B — IsWindowVisible NOT called on child controls
    ///   IsWindowVisible walks the entire parent chain. Many valid visible controls
    ///   (Explorer sidebar, DirectUIHWND panels) return false because a parent in the
    ///   chain is transiently considered invisible by the OS during enumeration.
    ///   ShareX only calls windowInfo.IsVisible (== IsWindowVisible) for top-level
    ///   windows (clipRect == null). Child handles get their rect clipped and checked
    ///   for validity — no visibility call.
    ///
    /// Fix C — parentHandles tracks ALL enumerated handles, not just top-level ones
    ///   EnumChildWindows can enumerate container controls whose own children should
    ///   also be enumerated. parentHandles prevents re-entering the same handle.
    ///   We now add every handle to parentHandles when recursing, not just top-level.
    ///
    /// Fix D — Post-filter: children vs children only (never vs parent top-level)
    ///   In _results, children always appear BEFORE their parent top-level entry
    ///   (guaranteed by the order of _results.Add calls in CheckHandle).
    ///   The filter loop builds the visible list in _results order.  When a child
    ///   is evaluated, its parent top-level entry has NOT yet been added to visible —
    ///   so the child is only blocked by earlier children/client-rects, never by its
    ///   own parent.  This matches ShareX exactly.
    /// </summary>
    internal class WindowsDetector
    {
        // ── Configuration ────────────────────────────────────────────

        public List<string> IgnoreClassNames { get; } = new List<string>
        {
            "CEF-OSC-WIDGET"   // NVIDIA GeForce Overlay DT
        };

        public List<IntPtr> IgnoreHandles { get; } = new List<IntPtr>();

        public bool IncludeChildWindows { get; set; }

        // ── Private state ────────────────────────────────────────────

        private List<WindowInfo> _results;
        private HashSet<IntPtr>  _parentHandles;

        // ── Public API ───────────────────────────────────────────────

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

            // ── Post-filter (Fix D) ──────────────────────────────────
            // Exact port of ShareX's final loop.
            // _results ordering:  child entries appear BEFORE their parent top-level
            // entry (because EnumChildWindows + client-rect adds happen before the
            // final _results.Add for the top-level window in CheckHandle).
            //
            // The sequential build of `result` means:
            //   • Top-level windows → always kept.
            //   • Non-top-level entries → kept unless a PREVIOUSLY added entry
            //     (which is an earlier child or client-rect, NOT yet the parent
            //     top-level) already fully contains this rect.
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

        // ── EnumWindows callback ─────────────────────────────────────

        private bool CheckTopLevelWindow(IntPtr hWnd, IntPtr lParam)
        {
            return CheckHandle(hWnd, clipRect: null);
        }

        // ── Core handler ─────────────────────────────────────────────

        private bool CheckHandle(IntPtr hWnd, Rectangle? clipRect)
        {
            if (IgnoreHandles.Contains(hWnd))
                return true;

            bool isTopLevel = clipRect == null;

            // Fix B: only call IsWindowVisible for top-level windows.
            // Child controls (clipRect != null) skip this check — many valid controls
            // (Explorer sidebar etc.) fail IsWindowVisible due to parent-chain walking.
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

            // ── Rectangle ────────────────────────────────────────────

            Rectangle rect;
            if (isTopLevel)
            {
                // Fix A: prefer DWM extended frame bounds (visible rect, no shadow)
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

            // ── Children first, then self (Fix D ordering) ───────────
            if (isTopLevel)
            {
                // Enumerate child controls
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle parentRect = rect;

                    // Named delegate — prevents GC during P/Invoke
                    NativeMethods.EnumWindowsProc childCb =
                        (childHwnd, _lp) => CheckHandle(childHwnd, parentRect);
                    NativeMethods.EnumChildWindows(hWnd, childCb, IntPtr.Zero);
                }

                // Client-rect sub-entry (finer snapping, e.g. Chrome toolbar vs page)
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
                // Fix C: track child containers too so we don't re-enter them
                if (IncludeChildWindows && !_parentHandles.Contains(hWnd))
                {
                    _parentHandles.Add(hWnd);
                    Rectangle childRect = rect;

                    NativeMethods.EnumWindowsProc grandChildCb =
                        (grandChild, _lp) => CheckHandle(grandChild, childRect);
                    NativeMethods.EnumChildWindows(hWnd, grandChildCb, IntPtr.Zero);
                }
            }

            // Self — always last so children precede parent in _results
            _results.Add(new WindowInfo
            {
                Handle     = hWnd,
                Rectangle  = rect,
                IsTopLevel = isTopLevel
            });

            return true;
        }

        // ── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Fix A: mirrors ShareX's CaptureHelpers.GetWindowRectangle.
        /// Tries DWMWA_EXTENDED_FRAME_BOUNDS first (visible frame, no shadow pixels);
        /// falls back to plain GetWindowRect.
        /// </summary>
        private static Rectangle GetWindowRectangle(IntPtr hWnd)
        {
            // Try DWM extended frame bounds — gives the visually rendered rect
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

            // Fallback
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

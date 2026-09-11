using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// Determines which region of the screen is captured during a GIF recording session.
    /// </summary>
    public enum GifCaptureMode
    {
        /// <summary>Capture the full virtual screen (all monitors combined).</summary>
        FullScreen,

        /// <summary>
        /// Show a rubber-band region selector before recording starts.
        /// Recording begins only after the user confirms the selection.
        /// </summary>
        Snap,

        /// <summary>
        /// Capture the fixed region defined by GifPredefX/Y/Width/Height in the
        /// skin INI (falls back to PredefX/Y/Width/Height when the GIF-specific
        /// keys are absent).
        /// </summary>
        Predefined,

        /// <summary>
        /// Capture the bounding rectangle of a named window.
        /// The window rect is re-queried each frame so a moving or resizing
        /// window is tracked throughout the recording.
        /// </summary>
        Window,
    }

    /// <summary>
    /// Controls the full lifecycle of a GIF screen recording session.
    ///
    /// State machine:
    ///
    ///   Idle ──[StartRecording / ToggleRecording]──► Recording
    ///                                                    │
    ///                              [StopAndSave / ToggleRecording]
    ///                                                    │
    ///                                                 Encoding ──► Idle
    ///                                                    │
    ///                              [CancelRecording] ────┘ (frames discarded, no file)
    ///
    /// Thread model:
    ///   • StartRecording() returns immediately; a background thread captures frames.
    ///   • StopAndSave() signals the capture thread to stop, then encodes on a new
    ///     background thread so the Rainmeter plugin thread is never blocked.
    ///   • CancelRecording() signals the capture thread to stop and discards all
    ///     frames — no file is written.
    ///   • ToggleRecording() calls StartRecording() when Idle, StopAndSave() when
    ///     Recording. Does nothing while Encoding is in progress.
    ///
    /// Concurrency safety:
    ///   All public methods are guarded by _stateLock and a volatile state flag.
    ///   Only one recording session can be active at a time.
    /// </summary>
    public static class GifCaptureManager
    {
        // ------------------------------------------------------------------ //
        //  State
        // ------------------------------------------------------------------ //

        private enum State { Idle, Recording, Encoding }

        private static readonly object _stateLock = new object();
        private static volatile State  _state      = State.Idle;

        private static Thread          _captureThread;
        private static GifFrameBuffer  _buffer;

        // Signals the capture loop to exit.
        private static volatile bool   _stopRequested;

        // Streaming-mode fields (only used when GifEncodeWhileRecord = true).
        private static GifStreamEncoder _streamEncoder;
        private static Thread           _encoderThread;

        // ------------------------------------------------------------------ //
        //  Capture-mode state (set once in StartRecording, read by CaptureLoop)
        // ------------------------------------------------------------------ //

        private static GifCaptureMode _captureMode;

        // Resolved capture region (all modes except Window use this directly).
        // For Window mode this is the initial bounds; each frame re-queries the hwnd.
        private static Rectangle _captureRegion;

        // Window-mode fields.
        private static IntPtr _captureHwnd;
        private static string _captureWindowTitle;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Begin a new GIF recording session.
        ///
        /// <para><b>Mode behaviour:</b></para>
        /// <list type="bullet">
        ///   <item><see cref="GifCaptureMode.FullScreen"/> — captures the full virtual screen.</item>
        ///   <item><see cref="GifCaptureMode.Predefined"/> — captures <c>settings.GifPredefinedRegion</c>.</item>
        ///   <item><see cref="GifCaptureMode.Window"/> — captures the window matching
        ///     <paramref name="windowTitle"/>; re-queries the rect each frame.</item>
        ///   <item><see cref="GifCaptureMode.Snap"/> — shows a selection UI on an STA thread;
        ///     recording starts only after the user confirms the region.</item>
        /// </list>
        ///
        /// Does nothing if a recording is already in progress.
        /// </summary>
        public static void StartRecording(Settings settings,
                                          GifCaptureMode mode = GifCaptureMode.FullScreen,
                                          string windowTitle  = null)
        {
            if (string.IsNullOrWhiteSpace(settings.GifSavePath))
            {
                Logger.Log("GifCaptureManager.StartRecording: GifSavePath is empty, aborting.");
                return;
            }

            // ----- Snap mode: show selection UI on STA thread first -----
            if (mode == GifCaptureMode.Snap)
            {
                lock (_stateLock)
                {
                    if (_state != State.Idle)
                    {
                        Logger.Log("GifCaptureManager.StartRecording(Snap): already active, ignoring.");
                        return;
                    }
                    // Reserve state while the UI is open so a second call is rejected.
                    _state = State.Recording;
                }

                var snapThread = new Thread(() =>
                {
                    try
                    {
                        Rectangle? region = GifSnapSelector.SelectRegion(settings);
                        if (region == null || region.Value.Width <= 0 || region.Value.Height <= 0)
                        {
                            Logger.Log("GifCaptureManager.StartRecording(Snap): selection cancelled.");
                            lock (_stateLock) { _state = State.Idle; }
                            return;
                        }
                        Logger.Log($"GifCaptureManager.StartRecording(Snap): region={region.Value}");
                        StartRecordingInternal(settings, GifCaptureMode.Predefined,
                                               region.Value, IntPtr.Zero, null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GifCaptureManager.StartRecording(Snap): error — {ex.Message}");
                        lock (_stateLock) { _state = State.Idle; }
                    }
                });
                snapThread.SetApartmentState(ApartmentState.STA);
                snapThread.IsBackground = true;
                snapThread.Name = "FinalShot-GifSnap";
                snapThread.Start();
                return;
            }

            // ----- All other modes: resolve region, then start -----
            Rectangle captureRegion = Rectangle.Empty;
            IntPtr    captureHwnd   = IntPtr.Zero;

            if (mode == GifCaptureMode.Predefined)
            {
                captureRegion = settings.GifPredefinedRegion;
                if (captureRegion.Width <= 0 || captureRegion.Height <= 0)
                {
                    Logger.Log("GifCaptureManager.StartRecording(Predefined): GifPredefinedRegion is empty, aborting.");
                    return;
                }
            }
            else if (mode == GifCaptureMode.Window)
            {
                if (string.IsNullOrWhiteSpace(windowTitle))
                {
                    Logger.Log("GifCaptureManager.StartRecording(Window): windowTitle is empty, aborting.");
                    return;
                }
                captureHwnd = FindWindowByTitle(windowTitle);
                if (captureHwnd == IntPtr.Zero)
                {
                    Logger.Log($"GifCaptureManager.StartRecording(Window): window '{windowTitle}' not found.");
                    return;
                }
                captureRegion = GetWindowBounds(captureHwnd);
                if (captureRegion.Width <= 0 || captureRegion.Height <= 0)
                {
                    Logger.Log($"GifCaptureManager.StartRecording(Window): window '{windowTitle}' has zero size.");
                    return;
                }
            }
            else // FullScreen
            {
                captureRegion = SystemInformation.VirtualScreen;
            }

            lock (_stateLock)
            {
                if (_state != State.Idle)
                {
                    Logger.Log("GifCaptureManager.StartRecording: already recording or encoding, ignoring.");
                    return;
                }
                _state = State.Recording;
            }

            StartRecordingInternal(settings, mode, captureRegion, captureHwnd, windowTitle);
        }

        // Called once the capture region is confirmed (either directly or post-snap).
        private static void StartRecordingInternal(Settings settings,
                                                   GifCaptureMode mode,
                                                   Rectangle captureRegion,
                                                   IntPtr captureHwnd,
                                                   string windowTitle)
        {
            lock (_stateLock)
            {
                _stopRequested      = false;
                _buffer             = new GifFrameBuffer();
                _streamEncoder      = null;
                _encoderThread      = null;
                _captureMode        = mode;
                _captureRegion      = captureRegion;
                _captureHwnd        = captureHwnd;
                _captureWindowTitle = windowTitle;

                Logger.Log($"GifCaptureManager.StartRecordingInternal: mode={mode}, " +
                           $"region={captureRegion}, fps={settings.GifFPS}, " +
                           $"duration={settings.GifDuration}s, " +
                           $"encodeWhileRecord={settings.GifEncodeWhileRecord}, " +
                           $"path={settings.GifSavePath}");

                if (settings.GifEncodeWhileRecord)
                {
                    var enc = new GifStreamEncoder();
                    enc.Open(settings.GifSavePath, captureRegion.Width, captureRegion.Height);
                    _streamEncoder = enc;

                    _encoderThread = new Thread(() => StreamEncodeLoop(enc, _buffer));
                    _encoderThread.IsBackground = true;
                    _encoderThread.Name = "FinalShot-GifStreamEncode";
                    _encoderThread.Start();
                }

                _captureThread = new Thread(() => CaptureLoop(settings));
                _captureThread.IsBackground = true;
                _captureThread.Name = "FinalShot-GifCapture";
                _captureThread.Start();
            }

            // Fire outside the lock to avoid holding _stateLock while calling into Rainmeter.
            ExecuteAction(settings, settings.GifStartAction, "GifStartAction");
        }

        /// <summary>
        /// Stop the active recording session and save the result as an animated GIF.
        /// Returns immediately; encoding runs on a background thread.
        /// Does nothing if no recording is in progress.
        /// </summary>
        public static void StopAndSave(Settings settings)
        {
            Thread           captureThreadSnapshot;
            Thread           encoderThreadSnapshot;
            GifFrameBuffer   bufferSnapshot;
            GifStreamEncoder streamEncSnapshot;

            lock (_stateLock)
            {
                if (_state != State.Recording)
                {
                    Logger.Log("GifCaptureManager.StopAndSave: not currently recording, ignoring.");
                    return;
                }

                _state         = State.Encoding;
                _stopRequested = true;

                captureThreadSnapshot = _captureThread;
                encoderThreadSnapshot = _encoderThread;
                bufferSnapshot        = _buffer;
                streamEncSnapshot     = _streamEncoder;
            }

            Logger.Log("GifCaptureManager.StopAndSave: signalled capture thread to stop.");

            var finishThread = new Thread(() =>
                EncodeAndFinish(settings, captureThreadSnapshot, encoderThreadSnapshot,
                                bufferSnapshot, streamEncSnapshot));
            finishThread.IsBackground = true;
            finishThread.Name = "FinalShot-GifEncode";
            finishThread.Start();
        }

        /// <summary>Returns true while a recording or encoding session is active.</summary>
        public static bool IsActive => _state != State.Idle;

        /// <summary>
        /// Toggles recording:
        ///   Idle → StartRecording  |  Recording → StopAndSave  |  Encoding → ignored.
        /// </summary>
        public static void ToggleRecording(Settings settings,
                                           GifCaptureMode mode = GifCaptureMode.FullScreen,
                                           string windowTitle  = null)
        {
            State current;
            lock (_stateLock) { current = _state; }

            switch (current)
            {
                case State.Idle:
                    Logger.Log("GifCaptureManager.ToggleRecording: Idle → starting recording.");
                    StartRecording(settings, mode, windowTitle);
                    break;
                case State.Recording:
                    Logger.Log("GifCaptureManager.ToggleRecording: Recording → stopping and saving.");
                    StopAndSave(settings);
                    break;
                case State.Encoding:
                    Logger.Log("GifCaptureManager.ToggleRecording: Encoding in progress — ignored.");
                    break;
            }
        }

        /// <summary>
        /// Cancels the active recording and discards all frames.
        /// In streaming mode the partial GIF file is deleted.
        /// Safe to call at any time; ignored when Idle or Encoding.
        /// </summary>
        public static void CancelRecording(Settings settings)
        {
            Thread           captureThreadSnapshot;
            Thread           encoderThreadSnapshot;
            GifFrameBuffer   bufferSnapshot;
            GifStreamEncoder streamEncSnapshot;

            lock (_stateLock)
            {
                if (_state != State.Recording)
                {
                    Logger.Log($"GifCaptureManager.CancelRecording: state is {_state}, nothing to cancel.");
                    return;
                }

                _state         = State.Idle;
                _stopRequested = true;

                captureThreadSnapshot = _captureThread;
                encoderThreadSnapshot = _encoderThread;
                bufferSnapshot        = _buffer;
                streamEncSnapshot     = _streamEncoder;

                _captureThread = null;
                _encoderThread = null;
                _buffer        = null;
                _streamEncoder = null;
            }

            Logger.Log("GifCaptureManager.CancelRecording: signalled stop, discarding frames.");

            var cleanupThread = new Thread(() =>
            {
                try
                {
                    captureThreadSnapshot?.Join();
                    Logger.Log("GifCaptureManager.CancelRecording: capture thread joined.");

                    if (streamEncSnapshot != null)
                    {
                        encoderThreadSnapshot?.Join();
                        Logger.Log("GifCaptureManager.CancelRecording: encoder thread joined.");
                        streamEncSnapshot.Cancel(); // deletes partial file
                    }
                    else
                    {
                        bufferSnapshot?.Dispose();
                    }

                    Logger.Log("GifCaptureManager.CancelRecording: done, state is Idle.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifCaptureManager.CancelRecording: error — {ex.Message}");
                }
                finally
                {
                    ExecuteAction(settings, settings.GifCancelAction, "GifCancelAction");
                }
            });
            cleanupThread.IsBackground = true;
            cleanupThread.Name = "FinalShot-GifCancel";
            cleanupThread.Start();
        }

        // ------------------------------------------------------------------ //
        //  Capture loop (runs on _captureThread)
        // ------------------------------------------------------------------ //

        private static void CaptureLoop(Settings settings)
        {
            int fps        = settings.GifFPS;
            int maxSeconds = settings.GifDuration; // 0 = unlimited
            int targetMs   = 1000 / fps;
            int maxFrames  = maxSeconds > 0 ? fps * maxSeconds : int.MaxValue;

            Logger.Log($"GifCaptureManager.CaptureLoop: starting, mode={_captureMode}, fps={fps}, " +
                       $"maxFrames={maxFrames}, targetMs={targetMs}");

            IntPtr oldCtx = NativeMethods.SetThreadDpiAwarenessContext(
                                NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            try
            {
                var frameClock = Stopwatch.StartNew();

                for (int i = 0; !_stopRequested && i < maxFrames; i++)
                {
                    var captureSw = Stopwatch.StartNew();

                    try
                    {
                        Bitmap bmp = CaptureFrame(settings.ShowCursor);

                        // Stamp the frame with the real elapsed time since the last
                        // capture so playback speed matches recording speed exactly.
                        int elapsedMs = (int)frameClock.ElapsedMilliseconds;
                        frameClock.Restart();

                        int delayCs = (int)Math.Round(elapsedMs / 10.0);
                        if (delayCs < 2) delayCs = 2;

                        _buffer.Add(new GifFrame(bmp, delayCs));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GifCaptureManager.CaptureLoop: frame {i} error — {ex.Message}");
                        frameClock.Restart();
                    }

                    int sleep = targetMs - (int)captureSw.ElapsedMilliseconds;
                    if (sleep > 0)
                        Thread.Sleep(sleep);
                }
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(oldCtx);
                _buffer.Complete();
                Logger.Log("GifCaptureManager.CaptureLoop: finished.");
            }
        }

        // ------------------------------------------------------------------ //
        //  Frame capture — dispatches to the correct mode
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Captures one frame according to the current <see cref="_captureMode"/>.
        /// </summary>
        private static Bitmap CaptureFrame(bool showCursor)
        {
            switch (_captureMode)
            {
                case GifCaptureMode.Window:
                    return CaptureWindow(showCursor);

                case GifCaptureMode.Predefined:
                    return CaptureRegion(_captureRegion, showCursor);

                default: // FullScreen (Snap resolves to Predefined after selection)
                    return CaptureRegion(SystemInformation.VirtualScreen, showCursor);
            }
        }

        /// <summary>Captures a fixed rectangular region of the screen.</summary>
        private static Bitmap CaptureRegion(Rectangle region, bool showCursor)
        {
            var bmp = new Bitmap(region.Width, region.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);
                if (showCursor)
                    ScreenshotManager.DrawCursor(g, region);
            }
            return bmp;
        }

        /// <summary>
        /// Captures the current bounding rect of <see cref="_captureHwnd"/>.
        /// Re-queries GetWindowRect every frame so a moving/resizing window is followed.
        /// Falls back to the initially resolved region if the window is no longer valid.
        /// </summary>
        private static Bitmap CaptureWindow(bool showCursor)
        {
            Rectangle bounds = GetWindowBounds(_captureHwnd);

            // If the window has closed or moved off-screen, use the last known bounds.
            if (bounds.Width <= 0 || bounds.Height <= 0)
                bounds = _captureRegion;

            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                if (showCursor)
                    ScreenshotManager.DrawCursor(g, bounds);
            }
            return bmp;
        }

        // ------------------------------------------------------------------ //
        //  Window-finding helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Finds the first visible, non-cloaked top-level window whose title
        /// contains <paramref name="title"/> (case-insensitive).
        /// Returns <see cref="IntPtr.Zero"/> if no matching window is found.
        /// </summary>
        private static IntPtr FindWindowByTitle(string title)
        {
            IntPtr found = IntPtr.Zero;
            string lower = title.ToLowerInvariant();

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;

                // Skip cloaked windows (virtual-desktop background etc.)
                NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                                                    out int cloaked, sizeof(int));
                if (cloaked != 0)
                    return true;

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().ToLowerInvariant().Contains(lower))
                {
                    found = hWnd;
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Returns the visible DWM frame bounds of <paramref name="hWnd"/>,
        /// falling back to GetWindowRect if DWM is unavailable.
        /// </summary>
        private static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            // Prefer DWMWA_EXTENDED_FRAME_BOUNDS — excludes invisible shadow pixels.
            if (NativeMethods.DwmGetWindowAttribute(hWnd,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out NativeMethods.RECT r, Marshal.SizeOf(typeof(NativeMethods.RECT))) == 0)
            {
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }

            // Fallback
            if (NativeMethods.GetWindowRect(hWnd, out r))
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

            return Rectangle.Empty;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // ------------------------------------------------------------------ //
        //  Streaming encoder loop (runs on _encoderThread)
        // ------------------------------------------------------------------ //

        private static void StreamEncodeLoop(GifStreamEncoder enc, GifFrameBuffer buffer)
        {
            Logger.Log("GifCaptureManager.StreamEncodeLoop: starting.");
            try
            {
                foreach (GifFrame frame in buffer.Drain())
                    enc.AddFrame(frame);
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.StreamEncodeLoop: error — {ex.Message}");
            }
            finally
            {
                buffer.Dispose();
                Logger.Log("GifCaptureManager.StreamEncodeLoop: finished.");
            }
        }

        // ------------------------------------------------------------------ //
        //  Encode + finish (runs on finishThread)
        // ------------------------------------------------------------------ //

        private static void EncodeAndFinish(
            Settings         settings,
            Thread           captureThread,
            Thread           encoderThread,
            GifFrameBuffer   buffer,
            GifStreamEncoder streamEncoder)
        {
            captureThread?.Join();
            Logger.Log("GifCaptureManager.EncodeAndFinish: capture thread joined.");

            ExecuteAction(settings, settings.OnGifEncodingAction, "OnGifEncodingAction");

            bool success = false;
            try
            {
                if (streamEncoder != null)
                {
                    // Streaming mode: wait for encoder to drain the last frames.
                    encoderThread?.Join();
                    Logger.Log("GifCaptureManager.EncodeAndFinish: stream encoder thread joined.");
                    streamEncoder.Close();
                    success = File.Exists(settings.GifSavePath);
                }
                else
                {
                    // Batch mode: encode all buffered frames now.
                    var frames = new List<GifFrame>();
                    try
                    {
                        foreach (GifFrame frame in buffer.Drain())
                            frames.Add(frame);

                        Logger.Log($"GifCaptureManager.EncodeAndFinish: {frames.Count} frames collected.");

                        if (frames.Count == 0)
                        {
                            Logger.Log("GifCaptureManager.EncodeAndFinish: no frames, skipping save.");
                            return;
                        }

                        AnimatedGifEncoder.Encode(frames, settings.GifSavePath);
                        success = true;
                    }
                    finally
                    {
                        foreach (GifFrame f in frames) f?.Bitmap?.Dispose();
                        buffer.Dispose();
                    }
                }

                if (success)
                {
                    Logger.Log($"GifCaptureManager.EncodeAndFinish: GIF saved → {settings.GifSavePath}");

                    if (settings.ShowNotification && File.Exists(settings.GifSavePath))
                        ShowGifNotification(settings.GifSavePath);

                    ScreenshotManager.ExecuteFinishAction(settings);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.EncodeAndFinish: error — {ex}");
            }
            finally
            {
                streamEncoder?.Dispose();

                lock (_stateLock)
                {
                    _state         = State.Idle;
                    _captureThread = null;
                    _encoderThread = null;
                    _buffer        = null;
                    _streamEncoder = null;
                }

                Logger.Log("GifCaptureManager.EncodeAndFinish: state reset to Idle.");
            }
        }

        // ------------------------------------------------------------------ //
        //  Notification helper
        // ------------------------------------------------------------------ //

        private static void ShowGifNotification(string gifPath)
        {
            try
            {
                var thread = new Thread(() =>
                {
                    try { Application.Run(new NotificationForm(gifPath, "GIF Recording")); }
                    catch (Exception ex)
                    { Logger.Log($"GifCaptureManager: notification error — {ex.Message}"); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.ShowGifNotification: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ //
        //  Action executor helper
        // ------------------------------------------------------------------ //

        private static void ExecuteAction(Settings settings, string action, string actionName)
        {
            if (string.IsNullOrEmpty(action)) return;
            try
            {
                Logger.Log($"GifCaptureManager: executing {actionName}.");
                settings.Api.Execute(action);
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager: error executing {actionName} — {ex.Message}");
            }
        }
    }
}

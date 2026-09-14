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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    public enum GifCaptureMode
    {
        FullScreen,
        Snap,
        Predefined,
        Window,
    }

    public static class GifCaptureManager
    {
        private enum State { Idle, Recording, Encoding }

        private static readonly object _stateLock = new object();
        private static volatile State  _state      = State.Idle;
        private static Thread          _captureThread;
        private static GifDiskCache    _cache;
        private static volatile bool   _stopRequested;
        private static volatile bool   _pauseRequested;
        private static Settings        _activeSettings;
        private static GifCaptureMode  _captureMode;
        private static Rectangle       _captureRegion;
        private static IntPtr          _captureHwnd;
        private static string          _captureWindowTitle;

        //  Tracking fields -- updated during recording lifecycle

        private static DateTime  _recordingStart   = DateTime.MinValue;  // set when capture begins
        private static TimeSpan  _pausedDuration   = TimeSpan.Zero;      // accumulated pause time
        private static DateTime  _pauseStartUtc    = DateTime.MinValue;  // when current pause began
        private static int       _framesCaptured   = 0;                  // live frame counter
        private static string    _lastSavedPath    = "";                 // path of last saved GIF
        private static long      _lastFileSizeBytes= 0;                  // size of last saved GIF

        //  Public state properties

        public static bool IsActive    => _state != State.Idle;
        public static bool IsRecording => _state == State.Recording;
        public static bool IsEncoding  => _state == State.Encoding;
        public static bool IsIdle      => _state == State.Idle;

        // True when the recording is currently paused.
        public static bool IsPaused
        {
            get { lock (_stateLock) return _pauseRequested; }
        }

        // Elapsed recording time (excluding paused intervals).
        // Returns TimeSpan.Zero when not recording.
        public static TimeSpan RecordingElapsed
        {
            get
            {
                lock (_stateLock)
                {
                    if (_state != State.Recording || _recordingStart == DateTime.MinValue)
                        return TimeSpan.Zero;
                    TimeSpan total = DateTime.UtcNow - _recordingStart - _pausedDuration;
                    if (_pauseRequested && _pauseStartUtc != DateTime.MinValue)
                        total -= DateTime.UtcNow - _pauseStartUtc;
                    return total < TimeSpan.Zero ? TimeSpan.Zero : total;
                }
            }
        }

        // Number of frames captured so far in the current recording.
        public static int FramesCaptured => Interlocked.CompareExchange(ref _framesCaptured, 0, 0);

        // Full path of the last successfully saved GIF file.
        public static string LastSavedPath
        {
            get { lock (_stateLock) return _lastSavedPath ?? ""; }
        }

        // File size in bytes of the last saved GIF. 0 if none saved yet.
        public static long LastFileSizeBytes
        {
            get { lock (_stateLock) return _lastFileSizeBytes; }
        }

        public static void OpenLastSaved()
        {
            string path;
            lock (_stateLock) { path = _lastSavedPath; }
            ScreenshotManager.OpenFile(path);
        }

        public static void StartRecording(Settings settings,
                                          GifCaptureMode mode = GifCaptureMode.FullScreen,
                                          string windowTitle  = null)
        {
            if (string.IsNullOrWhiteSpace(settings.GifSavePath))
            {
                Logger.Log("GifCaptureManager.StartRecording: GifSavePath is empty, aborting.");
                return;
            }

            if (mode == GifCaptureMode.Snap)
            {
                lock (_stateLock)
                {
                    if (_state != State.Idle)
                    {
                        Logger.Log("GifCaptureManager.StartRecording(Snap): already active, ignoring.");
                        return;
                    }
                    // Recording during region pick blocks double-start, but do not
                    // keep the previous session's start time (elapsed would jump).
                    _state = State.Recording;
                    ClearLiveStatsLocked();
                }
                var snapThread = new Thread(() =>
                {
                    try
                    {
                        Rectangle? region = GifSnapSelector.SelectRegion(settings);
                        if (region == null || region.Value.Width <= 0 || region.Value.Height <= 0)
                        {
                            Logger.Log("GifCaptureManager.StartRecording(Snap): selection cancelled.");
                            lock (_stateLock)
                            {
                                _state = State.Idle;
                                ClearLiveStatsLocked();
                            }
                            return;
                        }
                        Logger.Log($"GifCaptureManager.StartRecording(Snap): region={region.Value}");
                        StartRecordingInternal(settings, GifCaptureMode.Predefined,
                                               region.Value, IntPtr.Zero, null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GifCaptureManager.StartRecording(Snap): error -- {ex.Message}");
                        lock (_stateLock)
                        {
                            _state = State.Idle;
                            ClearLiveStatsLocked();
                        }
                    }
                });
                snapThread.SetApartmentState(ApartmentState.STA);
                snapThread.IsBackground = true;
                snapThread.Name = "FinalShot-GifSnap";
                snapThread.Start();
                return;
            }

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
            else
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

        public static void StopAndSave(Settings settings)
        {
            Thread       captureThreadSnapshot;
            GifDiskCache cacheSnapshot;

            lock (_stateLock)
            {
                if (_state != State.Recording)
                {
                    Logger.Log("GifCaptureManager.StopAndSave: not currently recording, ignoring.");
                    return;
                }
                _state                = State.Encoding;
                _stopRequested        = true;
                captureThreadSnapshot = _captureThread;
                cacheSnapshot         = _cache;
            }

            Logger.Log("GifCaptureManager.StopAndSave: signalled capture thread to stop.");
            GifRecordingOverlay.CloseOverlay();
            ExecuteAction(settings, settings.GifStopAction, "GifStopAction");

            var finishThread = new Thread(() =>
                EncodeAndFinish(settings, captureThreadSnapshot, cacheSnapshot));
            finishThread.IsBackground = true;
            finishThread.Name = "FinalShot-GifEncode";
            finishThread.Start();
        }

        public static void PauseRecording()
        {
            bool paused;
            Settings settings;
            lock (_stateLock)
            {
                if (_state != State.Recording) return;
                _pauseRequested = !_pauseRequested;
                if (_pauseRequested)
                {
                    _pauseStartUtc = DateTime.UtcNow;
                }
                else if (_pauseStartUtc != DateTime.MinValue)
                {
                    _pausedDuration += DateTime.UtcNow - _pauseStartUtc;
                    _pauseStartUtc   = DateTime.MinValue;
                }
                paused = _pauseRequested;
                settings = _activeSettings;
                Logger.Log($"GifCaptureManager.PauseRecording: paused={paused}");
            }
            GifRecordingOverlay.SetPaused(paused);
            if (settings == null) return;
            if (paused)
                ExecuteAction(settings, settings.GifPauseAction,  "GifPauseAction");
            else
                ExecuteAction(settings, settings.GifResumeAction, "GifResumeAction");
        }

        public static void ToggleRecording(Settings settings,
                                           GifCaptureMode mode = GifCaptureMode.FullScreen,
                                           string windowTitle  = null)
        {
            State current;
            lock (_stateLock) { current = _state; }
            switch (current)
            {
                case State.Idle:
                    Logger.Log("GifCaptureManager.ToggleRecording: Idle -> starting recording.");
                    StartRecording(settings, mode, windowTitle);
                    break;
                case State.Recording:
                    Logger.Log("GifCaptureManager.ToggleRecording: Recording -> stopping and saving.");
                    StopAndSave(settings);
                    break;
                case State.Encoding:
                    Logger.Log("GifCaptureManager.ToggleRecording: Encoding in progress -- ignored.");
                    break;
            }
        }

        public static void CancelRecording(Settings settings)
        {
            Thread       captureThreadSnapshot;
            GifDiskCache cacheSnapshot;

            lock (_stateLock)
            {
                if (_state != State.Recording)
                {
                    Logger.Log($"GifCaptureManager.CancelRecording: state is {_state}, nothing to cancel.");
                    return;
                }
                _state                = State.Idle;
                _stopRequested        = true;
                captureThreadSnapshot = _captureThread;
                cacheSnapshot         = _cache;
                _captureThread        = null;
                _cache                = null;
                ClearLiveStatsLocked();
            }

            Logger.Log("GifCaptureManager.CancelRecording: signalled stop, discarding frames.");
            GifRecordingOverlay.CloseOverlay();

            var cleanupThread = new Thread(() =>
            {
                try
                {
                    captureThreadSnapshot?.Join();
                    Logger.Log("GifCaptureManager.CancelRecording: capture thread joined.");
                    cacheSnapshot?.Dispose(); // deletes the temp file
                    Logger.Log("GifCaptureManager.CancelRecording: done, state is Idle.");
                }
                catch (Exception ex)
                {
                    Logger.Log($"GifCaptureManager.CancelRecording: error -- {ex.Message}");
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

        //  Internal -- start

        // Clears elapsed/frame counters. Caller must hold _stateLock.
        // Sets _recordingStart to MinValue so RecordingElapsed stays 0 until
        // StartRecordingInternal stamps a real start time.
        private static void ClearLiveStatsLocked()
        {
            _recordingStart  = DateTime.MinValue;
            _pausedDuration  = TimeSpan.Zero;
            _pauseStartUtc   = DateTime.MinValue;
            _pauseRequested  = false;
            _framesCaptured  = 0;
        }

        private static void StartRecordingInternal(Settings settings,
                                                   GifCaptureMode mode,
                                                   Rectangle captureRegion,
                                                   IntPtr captureHwnd,
                                                   string windowTitle)
        {
            lock (_stateLock)
            {
                _stopRequested      = false;
                _pauseRequested     = false;
                _cache              = new GifDiskCache(); // opens temp file on disk
                _captureMode        = mode;
                _captureRegion      = captureRegion;
                _captureHwnd        = captureHwnd;
                _captureWindowTitle = windowTitle;
                _activeSettings     = settings;

                // Reset tracking
                _recordingStart    = DateTime.UtcNow;
                _pausedDuration    = TimeSpan.Zero;
                _pauseStartUtc     = DateTime.MinValue;
                _framesCaptured    = 0;

                Logger.Log($"GifCaptureManager.StartRecordingInternal: mode={mode}, " +
                           $"region={captureRegion}, fps={settings.GifFPS}, " +
                           $"quality={settings.GifQuality}, " +
                           $"compression={settings.GifCompression}, " +
                           $"duration={settings.GifDuration}s, " +
                           $"path={settings.GifSavePath}");

                _captureThread = new Thread(() => CaptureLoop(settings));
                _captureThread.IsBackground = true;
                _captureThread.Name = "FinalShot-GifCapture";
                _captureThread.Start();
            }

            if (settings.GifShowOverlay)
            {
                GifRecordingOverlay.Show(
                    captureRegion,
                    onStop:  () => ThreadPool.QueueUserWorkItem(_ => StopAndSave(_activeSettings)),
                    onPause: () => ThreadPool.QueueUserWorkItem(_ => PauseRecording()),
                    onAbort: () => ThreadPool.QueueUserWorkItem(_ => CancelRecording(_activeSettings)),
                    theme:   settings.UITheme);
            }

            ExecuteAction(settings, settings.GifStartAction, "GifStartAction");
        }

        //  Capture loop -- runs on FinalShot-GifCapture thread
        //  Each frame is handed to GifDiskCache which writes it to disk on a
        //  background thread and disposes the Bitmap immediately.
        //  Memory stays flat: only one Bitmap is ever live at a time.

        private static void CaptureLoop(Settings settings)
        {
            int fps        = settings.GifFPS;
            int maxSeconds = settings.GifDuration;
            int targetMs   = Math.Max(1, 1000 / Math.Max(1, fps));
            bool skipIdentical = settings.GifSkipIdenticalFrames;

            GifDiskCache localCache = _cache;

            Logger.Log($"GifCaptureManager.CaptureLoop: starting, mode={_captureMode}, fps={fps}, " +
                       $"maxSeconds={maxSeconds}, targetMs={targetMs}, skipIdentical={skipIdentical}");

            if (localCache == null)
            {
                Logger.Log("GifCaptureManager.CaptureLoop: cache is null at start, aborting.");
                return;
            }

            IntPtr oldCtx = NativeMethods.SetThreadDpiAwarenessContext(
                                NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            bool durationReached = false;
            try
            {
                var frameClock = Stopwatch.StartNew();
                // Wall-clock limit (paused time excluded). Frame-count limits drifted
                // when capture was slower than FPS and never triggered StopAndSave.
                var limitWatch = Stopwatch.StartNew();
                ulong prevHash = 0;
                bool  havePrev = false;
                int   pendingDelayCs = 0;
                int   stillSkipped = 0;

                for (int i = 0; !_stopRequested; i++)
                {
                    if (maxSeconds > 0 && limitWatch.Elapsed.TotalSeconds >= maxSeconds)
                    {
                        durationReached = true;
                        Logger.Log($"GifCaptureManager.CaptureLoop: GifDuration={maxSeconds}s reached.");
                        break;
                    }

                    if (_pauseRequested)
                    {
                        limitWatch.Stop();
                        while (_pauseRequested && !_stopRequested)
                            Thread.Sleep(50);
                        limitWatch.Start();
                        frameClock.Restart();
                        if (_stopRequested) break;
                        if (maxSeconds > 0 && limitWatch.Elapsed.TotalSeconds >= maxSeconds)
                        {
                            durationReached = true;
                            Logger.Log($"GifCaptureManager.CaptureLoop: GifDuration={maxSeconds}s reached after resume.");
                            break;
                        }
                    }

                    var captureSw = Stopwatch.StartNew();
                    Bitmap bmp = null;
                    try
                    {
                        bmp = CaptureFrame(settings.ShowCursor);
                        int elapsedMs   = (int)frameClock.ElapsedMilliseconds;
                        frameClock.Restart();
                        int delayCs     = (int)Math.Round(elapsedMs / 10.0);
                        if (delayCs < 2) delayCs = 2;

                        // Still-frame merge: identical captures fold into the next
                        // frame's delay -- same timing, fewer frames. Disable with
                        // GifSkipIdenticalFrames=0 to keep every FPS tick.
                        if (skipIdentical)
                        {
                            ulong hash = HashBitmapPixels(bmp);
                            if (havePrev && hash == prevHash)
                            {
                                pendingDelayCs += delayCs;
                                stillSkipped++;
                                bmp.Dispose();
                                bmp = null;
                            }
                            else
                            {
                                delayCs += pendingDelayCs;
                                pendingDelayCs = 0;
                                prevHash = hash;
                                havePrev = true;
                                localCache.Add(new GifFrame(bmp, delayCs));
                                bmp = null;
                                Interlocked.Increment(ref _framesCaptured);
                            }
                        }
                        else
                        {
                            delayCs += pendingDelayCs;
                            pendingDelayCs = 0;
                            localCache.Add(new GifFrame(bmp, delayCs));
                            bmp = null;
                            Interlocked.Increment(ref _framesCaptured);
                        }
                    }
                    catch (Exception ex)
                    {
                        bmp?.Dispose();
                        Logger.Log($"GifCaptureManager.CaptureLoop: frame {i} error -- {ex.Message}");
                        frameClock.Restart();
                    }

                    int sleep = targetMs - (int)captureSw.ElapsedMilliseconds;
                    if (sleep > 0)
                        Thread.Sleep(sleep);
                }

                if (stillSkipped > 0)
                    Logger.Log($"GifCaptureManager.CaptureLoop: coalesced {stillSkipped} still frame(s).");
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(oldCtx);
                localCache.Complete();
                Logger.Log("GifCaptureManager.CaptureLoop: finished.");
            }

            // Duration limit must go through StopAndSave (encode + overlay close).
            // Queue after this thread finishes Complete() so EncodeAndFinish can Join safely.
            if (durationReached)
            {
                Settings s = settings ?? _activeSettings;
                ThreadPool.QueueUserWorkItem(_ => StopAndSave(s));
            }
        }

        // Sampled FNV-1a for still-frame coalesce. Full-pixel hash dominated CPU vs
        // CopyFromScreen; a ~48×48 grid + corners is enough to catch motionless content.
        private static ulong HashBitmapPixels(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                  ImageLockMode.ReadOnly,
                                  PixelFormat.Format32bppArgb);
            try
            {
                ulong hash = 14695981039346656037UL;
                IntPtr scan0 = bd.Scan0;
                int stride = bd.Stride;
                int stepX = Math.Max(1, w / 48);
                int stepY = Math.Max(1, h / 48);

                for (int y = 0; y < h; y += stepY)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += stepX)
                    {
                        hash ^= (uint)Marshal.ReadInt32(scan0, row + x * 4);
                        hash *= 1099511628211UL;
                    }
                }

                // Corners + center catch chrome/cursor the coarse grid might miss
                hash ^= (uint)Marshal.ReadInt32(scan0, 0);
                hash *= 1099511628211UL;
                hash ^= (uint)Marshal.ReadInt32(scan0, (w - 1) * 4);
                hash *= 1099511628211UL;
                hash ^= (uint)Marshal.ReadInt32(scan0, (h - 1) * stride);
                hash *= 1099511628211UL;
                hash ^= (uint)Marshal.ReadInt32(scan0, (h - 1) * stride + (w - 1) * 4);
                hash *= 1099511628211UL;
                hash ^= (uint)Marshal.ReadInt32(scan0, (h / 2) * stride + (w / 2) * 4);
                hash *= 1099511628211UL;

                hash ^= (uint)w;
                hash *= 1099511628211UL;
                hash ^= (uint)h;
                hash *= 1099511628211UL;
                return hash;
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
        }

        //  Encode -- runs on FinalShot-GifEncode thread
        //  Passes the disk cache directly to the encoder. The encoder reads
        //  frames back from disk one at a time via GetFrameEnumerator().

        private static void EncodeAndFinish(Settings     settings,
                                            Thread       captureThread,
                                            GifDiskCache cache)
        {
            captureThread?.Join();
            Logger.Log("GifCaptureManager.EncodeAndFinish: capture thread joined.");
            ExecuteAction(settings, settings.GifEncodingAction, "GifEncodingAction");

            // Apply theme to all popup windows
            GifEncodingWindow.SetTheme(settings.UITheme);
            GifStateDialog.SetTheme(settings.UITheme);

            bool success = false;
            try
            {
                int frameCount = cache.Count;
                Logger.Log($"GifCaptureManager.EncodeAndFinish: {frameCount} frames on disk.");

                if (frameCount == 0)
                {
                    Logger.Log("GifCaptureManager.EncodeAndFinish: no frames, skipping save.");
                    return;
                }

                // Show encoding progress window if enabled
                if (settings.GifShowEncodingWindow)
                    GifEncodingWindow.Show(frameCount);

                // Encoder reads frames from disk via two streaming passes --
                // no frame list ever lives in RAM simultaneously.
                AnimatedGifEncoder.Encode(cache, settings.GifSavePath,
                    settings.GifQuality, settings.GifCompression,
                    settings.GifSkipIdenticalFrames);
                success = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.EncodeAndFinish: error -- {ex}");
            }
            finally
            {
                // Close encoding window (triggers fill + fade-out animation)
                GifEncodingWindow.Close();

                cache?.Dispose(); // deletes the temp file
                lock (_stateLock)
                {
                    _state         = State.Idle;
                    _captureThread = null;
                    _cache         = null;
                    ClearLiveStatsLocked();
                }
                Logger.Log("GifCaptureManager.EncodeAndFinish: state reset to Idle.");
            }

            if (success)
            {
                Logger.Log($"GifCaptureManager.EncodeAndFinish: GIF saved -> {settings.GifSavePath}");
                try
                {
                    lock (_stateLock)
                    {
                        _lastSavedPath     = settings.GifSavePath;
                        _lastFileSizeBytes = new FileInfo(settings.GifSavePath).Length;
                    }
                }
                catch { }
                if (settings.ShowNotification && File.Exists(settings.GifSavePath))
                    ShowGifNotification(settings.GifSavePath, settings);
                ExecuteAction(settings, settings.GifFinishAction, "GifFinishAction");
            }
        }

        //  Frame capture helpers

        private static Bitmap CaptureFrame(bool showCursor)
        {
            switch (_captureMode)
            {
                case GifCaptureMode.Window:
                    return CaptureWindow(showCursor);
                case GifCaptureMode.Predefined:
                    return CaptureRegion(_captureRegion, showCursor);
                default:
                    return CaptureRegion(SystemInformation.VirtualScreen, showCursor);
            }
        }

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

        private static Bitmap CaptureWindow(bool showCursor)
        {
            // Lock canvas to the size chosen when recording started so mid-record
            // resize/maximize cannot change frame dimensions (encoder assumes fixed w×h).
            int fixedW = _captureRegion.Width;
            int fixedH = _captureRegion.Height;

            Rectangle live = GetWindowBounds(_captureHwnd);
            if (live.Width <= 0 || live.Height <= 0)
                live = _captureRegion;

            if (fixedW <= 0 || fixedH <= 0)
            {
                fixedW = live.Width;
                fixedH = live.Height;
            }

            if (fixedW <= 0 || fixedH <= 0)
                return new Bitmap(1, 1);

            var bmp = new Bitmap(fixedW, fixedH);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                int copyW = Math.Min(fixedW, live.Width);
                int copyH = Math.Min(fixedH, live.Height);
                if (copyW > 0 && copyH > 0)
                {
                    using (var part = new Bitmap(copyW, copyH))
                    using (var pg = Graphics.FromImage(part))
                    {
                        pg.CopyFromScreen(live.Location, Point.Empty, new Size(copyW, copyH));
                        if (showCursor)
                            ScreenshotManager.DrawCursor(pg,
                                new Rectangle(live.X, live.Y, copyW, copyH));
                        g.DrawImageUnscaled(part, 0, 0);
                    }
                }
            }

            // Rounded corners only when live size still matches the fixed canvas.
            if (_activeSettings != null && _activeSettings.RoundWindowCorners
                && live.Width == fixedW && live.Height == fixedH)
            {
                bmp = WindowCornerHelper.ApplyRoundedCornersIfNeeded(bmp, _captureHwnd);

                if (bmp != null &&
                    (bmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb ||
                     bmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    var flat = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(flat))
                    {
                        g.Clear(Color.Black);
                        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    }
                    bmp.Dispose();
                    return flat;
                }
            }

            return bmp;
        }

        //  Window helpers

        // Prefer exact title (same as -ws), then largest visible contains-match.
        // First-contains-wins previously grabbed tiny helper HWNDs (e.g. 384x45).
        private static IntPtr FindWindowByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return IntPtr.Zero;

            IntPtr exact = NativeMethods.FindWindow(null, title);
            if (exact != IntPtr.Zero && NativeMethods.IsWindowVisible(exact))
            {
                NativeMethods.DwmGetWindowAttribute(exact, NativeMethods.DWMWA_CLOAKED,
                    out int cloakedExact, sizeof(int));
                if (cloakedExact == 0)
                {
                    Logger.Log("GifCaptureManager.FindWindowByTitle: exact match HWND="
                        + exact.ToInt64() + " title='" + title + "' bounds=" + GetWindowBounds(exact));
                    return exact;
                }
            }

            string lower = title.ToLowerInvariant();
            IntPtr best = IntPtr.Zero;
            string bestTitle = "";
            long bestArea = 0;
            IntPtr exactEnum = IntPtr.Zero;
            Rectangle exactEnumBounds = Rectangle.Empty;

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;
                NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                                                    out int cloaked, sizeof(int));
                if (cloaked != 0)
                    return true;

                // Skip tooltips / tool windows (Rainmeter ToolTipText can contain the
                // search title and was matching first via Contains — e.g. 384x45).
                int ex = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                    return true;

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                string winTitle = sb.ToString();
                if (string.IsNullOrEmpty(winTitle))
                    return true;

                string winLower = winTitle.ToLowerInvariant();
                Rectangle bounds = GetWindowBounds(hWnd);
                long area = (long)bounds.Width * bounds.Height;
                if (bounds.Width < 80 || bounds.Height < 80)
                    return true; // skip toolbars / ghosts / title-bar scraps

                if (string.Equals(winTitle, title, StringComparison.OrdinalIgnoreCase))
                {
                    if (area > (long)exactEnumBounds.Width * exactEnumBounds.Height)
                    {
                        exactEnum = hWnd;
                        exactEnumBounds = bounds;
                    }
                }

                if (winLower.Contains(lower) && area > bestArea)
                {
                    best = hWnd;
                    bestTitle = winTitle;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);

            if (exactEnum != IntPtr.Zero)
            {
                Logger.Log("GifCaptureManager.FindWindowByTitle: enum exact HWND="
                    + exactEnum.ToInt64() + " title='" + title + "' bounds=" + exactEnumBounds);
                return exactEnum;
            }

            if (best != IntPtr.Zero)
            {
                Logger.Log("GifCaptureManager.FindWindowByTitle: contains match HWND="
                    + best.ToInt64() + " title='" + bestTitle + "' area=" + bestArea
                    + " bounds=" + GetWindowBounds(best));
                return best;
            }

            Logger.Log("GifCaptureManager.FindWindowByTitle: no window matched '" + title + "'");
            return IntPtr.Zero;
        }

        private static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out NativeMethods.RECT r,
                    Marshal.SizeOf(typeof(NativeMethods.RECT))) == 0)
            {
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
            if (NativeMethods.GetWindowRect(hWnd, out r))
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return Rectangle.Empty;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        //  Notification + action helpers

        private static void ShowGifNotification(string gifPath, Settings settings)
        {
            try
            {
                if (settings.PlayNotificationSound)
                    NotificationSound.Play();

                var thread = new System.Threading.Thread(() =>
                {
                    try { Application.Run(new NotificationForm(gifPath, "GIF Recording", settings)); }
                    catch (Exception ex)
                    { Logger.Log($"GifCaptureManager: notification error -- {ex.Message}"); }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.ShowGifNotification: {ex.Message}");
            }
        }

        private static void ExecuteAction(Settings settings, string action, string actionName)
        {
            if (string.IsNullOrEmpty(action) || settings?.Api == null) return;
            try
            {
                Logger.Log($"GifCaptureManager: queueing {actionName}.");
                BangQueue.Enqueue(settings.Api, action);
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager: error queueing {actionName} -- {ex.Message}");
            }
        }
    }
}

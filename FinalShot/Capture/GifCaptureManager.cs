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
        private static GifFrameBuffer  _buffer;
        private static volatile bool   _stopRequested;
        private static volatile bool   _pauseRequested;
        private static GifStreamEncoder _streamEncoder;
        private static Thread           _encoderThread;
        private static Settings _activeSettings;
        private static GifCaptureMode _captureMode;
        private static Rectangle _captureRegion;
        private static IntPtr _captureHwnd;
        private static string _captureWindowTitle;
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
                _buffer             = new GifFrameBuffer();
                _streamEncoder      = null;
                _encoderThread      = null;
                _captureMode        = mode;
                _captureRegion      = captureRegion;
                _captureHwnd        = captureHwnd;
                _captureWindowTitle = windowTitle;
                _activeSettings     = settings;
                Logger.Log($"GifCaptureManager.StartRecordingInternal: mode={mode}, " +
                           $"region={captureRegion}, fps={settings.GifFPS}, " +
                           $"duration={settings.GifDuration}s, " +
                           $"encodeWhileRecord={settings.GifEncodeWhileRecord}, " +
                           $"path={settings.GifSavePath}");
                if (settings.GifEncodeWhileRecord)
                {
                    var enc = new GifStreamEncoder();
                    enc.Open(settings.GifSavePath, captureRegion.Width, captureRegion.Height,
                             settings.EncoderQuality, settings.GifCompression);
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
            if (settings.GifShowOverlay)
            {
                GifRecordingOverlay.Show(
                    captureRegion,
                    onStop:  () => ThreadPool.QueueUserWorkItem(_ => StopAndSave(_activeSettings)),
                    onPause: () => ThreadPool.QueueUserWorkItem(_ => PauseRecording()),
                    onAbort: () => ThreadPool.QueueUserWorkItem(_ => CancelRecording(_activeSettings)));
            }
            ExecuteAction(settings, settings.GifStartAction, "GifStartAction");
        }
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
            GifRecordingOverlay.CloseOverlay();
            var finishThread = new Thread(() =>
                EncodeAndFinish(settings, captureThreadSnapshot, encoderThreadSnapshot,
                                bufferSnapshot, streamEncSnapshot));
            finishThread.IsBackground = true;
            finishThread.Name = "FinalShot-GifEncode";
            finishThread.Start();
        }
        public static bool IsActive => _state != State.Idle;
        public static void PauseRecording()
        {
            lock (_stateLock)
            {
                if (_state != State.Recording) return;
                _pauseRequested = !_pauseRequested;
                Logger.Log($"GifCaptureManager.PauseRecording: paused={_pauseRequested}");
            }
            GifRecordingOverlay.SetPaused(_pauseRequested);
            if (_pauseRequested)
                ExecuteAction(_activeSettings, _activeSettings.GifPauseAction,  "GifPauseAction");
            else
                ExecuteAction(_activeSettings, _activeSettings.GifResumeAction, "GifResumeAction");
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
            GifRecordingOverlay.CloseOverlay();
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
                        streamEncSnapshot.Cancel(); 
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
        private static void CaptureLoop(Settings settings)
        {
            int fps        = settings.GifFPS;
            int maxSeconds = settings.GifDuration; 
            int targetMs   = 1000 / fps;
            int maxFrames  = maxSeconds > 0 ? fps * maxSeconds : int.MaxValue;
            GifFrameBuffer localBuffer = _buffer;
            Logger.Log($"GifCaptureManager.CaptureLoop: starting, mode={_captureMode}, fps={fps}, " +
                       $"maxFrames={maxFrames}, targetMs={targetMs}");
            if (localBuffer == null)
            {
                Logger.Log("GifCaptureManager.CaptureLoop: buffer is null at start, aborting.");
                return;
            }
            IntPtr oldCtx = NativeMethods.SetThreadDpiAwarenessContext(
                                NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            try
            {
                var frameClock = Stopwatch.StartNew();
                for (int i = 0; !_stopRequested && i < maxFrames; i++)
                {
                    if (_pauseRequested)
                    {
                        while (_pauseRequested && !_stopRequested)
                            Thread.Sleep(50);
                        frameClock.Restart();
                        if (_stopRequested) break;
                    }
                    var captureSw = Stopwatch.StartNew();
                    try
                    {
                        Bitmap bmp = CaptureFrame(settings.ShowCursor);
                        int elapsedMs = (int)frameClock.ElapsedMilliseconds;
                        frameClock.Restart();
                        int delayCs = (int)Math.Round(elapsedMs / 10.0);
                        if (delayCs < 2) delayCs = 2;
                        localBuffer.Add(new GifFrame(bmp, delayCs));
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
                localBuffer.Complete();   
                Logger.Log("GifCaptureManager.CaptureLoop: finished.");
            }
        }
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
            Rectangle bounds = GetWindowBounds(_captureHwnd);
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
        private static IntPtr FindWindowByTitle(string title)
        {
            IntPtr found = IntPtr.Zero;
            string lower = title.ToLowerInvariant();
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;
                NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                                                    out int cloaked, sizeof(int));
                if (cloaked != 0)
                    return true;
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().ToLowerInvariant().Contains(lower))
                {
                    found = hWnd;
                    return false; 
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        private static Rectangle GetWindowBounds(IntPtr hWnd)
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out NativeMethods.RECT r, Marshal.SizeOf(typeof(NativeMethods.RECT))) == 0)
            {
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
            if (NativeMethods.GetWindowRect(hWnd, out r))
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return Rectangle.Empty;
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
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
                    encoderThread?.Join();
                    Logger.Log("GifCaptureManager.EncodeAndFinish: stream encoder thread joined.");
                    streamEncoder.Close();
                    success = File.Exists(settings.GifSavePath);
                }
                else
                {
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
                        AnimatedGifEncoder.Encode(frames, settings.GifSavePath,
                                                  settings.EncoderQuality,
                                                  settings.GifCompression);
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
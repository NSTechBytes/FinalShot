using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
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
    ///   • StopAndSave() signals the capture thread to stop, waits for it to finish,
    ///     then encodes all frames on a new background thread so the Rainmeter plugin
    ///     thread is never blocked.
    ///   • CancelRecording() signals the capture thread to stop and discards all
    ///     frames without encoding — no file is written.
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

        // Signals the capture loop to exit when set to true.
        private static volatile bool   _stopRequested;

        // Streaming-mode fields (only used when GifEncodeWhileRecord = true).
        // _streamEncoder is opened in StartRecording and closed/cancelled on stop/cancel.
        // _encoderThread runs concurrently with _captureThread, draining _buffer live.
        private static GifStreamEncoder _streamEncoder;
        private static Thread           _encoderThread;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Begin a new GIF recording session.
        /// Captures the full virtual screen at <c>settings.GifFPS</c> frames per second.
        /// If <c>settings.GifDuration &gt; 0</c> the recording stops automatically
        /// after that many seconds; otherwise it runs until <see cref="StopAndSave"/>.
        ///
        /// When <c>settings.GifEncodeWhileRecord</c> is true, frames are encoded
        /// and written to disk as they are captured (streaming mode).  This keeps
        /// memory usage flat but uses more CPU during recording.
        ///
        /// Does nothing if a recording is already in progress.
        /// </summary>
        public static void StartRecording(Settings settings)
        {
            lock (_stateLock)
            {
                if (_state != State.Idle)
                {
                    Logger.Log("GifCaptureManager.StartRecording: already recording or encoding, ignoring.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(settings.GifSavePath))
                {
                    Logger.Log("GifCaptureManager.StartRecording: GifSavePath is empty, aborting.");
                    return;
                }

                _state         = State.Recording;
                _stopRequested = false;
                _buffer        = new GifFrameBuffer();
                _streamEncoder = null;
                _encoderThread = null;

                Logger.Log($"GifCaptureManager.StartRecording: fps={settings.GifFPS}, " +
                           $"duration={settings.GifDuration}s, " +
                           $"encodeWhileRecord={settings.GifEncodeWhileRecord}, " +
                           $"path={settings.GifSavePath}");

                if (settings.GifEncodeWhileRecord)
                {
                    // --- Streaming mode ---
                    // Open the GIF stream immediately so the encoder thread can
                    // start writing frames the moment they arrive in the buffer.
                    var enc = new GifStreamEncoder(settings.GifMaxWidth);
                    Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                    enc.Open(settings.GifSavePath, bounds.Width, bounds.Height,
                             1000 / settings.GifFPS);
                    _streamEncoder = enc;

                    // Encoder thread: drain buffer → AddFrame live during recording.
                    _encoderThread = new Thread(() => StreamEncodeLoop(enc, _buffer));
                    _encoderThread.IsBackground = true;
                    _encoderThread.Name = "FinalShot-GifStreamEncode";
                    _encoderThread.Start();
                }

                // Capture thread — same for both modes.
                _captureThread = new Thread(() => CaptureLoop(settings));
                _captureThread.IsBackground = true;
                _captureThread.Name = "FinalShot-GifCapture";
                _captureThread.Start();
            }

            // Fire GifStartAction outside the lock so it never holds _stateLock
            // while calling back into Rainmeter (avoids potential deadlock).
            ExecuteAction(settings, settings.GifStartAction, "GifStartAction");
        }

        /// <summary>
        /// Stop the active recording session and save the result as an animated GIF.
        ///
        /// In batch mode: encoding runs on a background thread after capture stops.
        /// In streaming mode: the encoder thread has been writing frames live; this
        /// just waits for it to finish draining the last frames and calls Close().
        ///
        /// The method returns immediately in both modes.
        /// Does nothing if no recording is in progress.
        /// </summary>
        public static void StopAndSave(Settings settings)
        {
            Thread          captureThreadSnapshot;
            Thread          encoderThreadSnapshot;
            GifFrameBuffer  bufferSnapshot;
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

        /// <summary>
        /// Returns true while a recording or encoding session is active.
        /// Useful for the skin to conditionally enable/disable buttons.
        /// </summary>
        public static bool IsActive => _state != State.Idle;

        /// <summary>
        /// Toggles recording state:
        ///   • Idle      → calls <see cref="StartRecording"/> (begin capturing).
        ///   • Recording → calls <see cref="StopAndSave"/> (stop and encode).
        ///   • Encoding  → ignored (encoding already in progress, do nothing).
        ///
        /// This lets a single skin button act as both Start and Stop.
        /// </summary>
        public static void ToggleRecording(Settings settings)
        {
            State current;
            lock (_stateLock) { current = _state; }

            switch (current)
            {
                case State.Idle:
                    Logger.Log("GifCaptureManager.ToggleRecording: Idle → starting recording.");
                    StartRecording(settings);
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
        /// Cancels the active recording session and discards all captured frames.
        /// No file is written and <c>FinishAction</c> is NOT executed.
        /// <c>GifCancelAction</c> IS executed once the frames have been discarded.
        ///
        /// In streaming mode the partially-written GIF file is deleted.
        ///
        /// Safe to call at any time:
        ///   • Idle      → ignored.
        ///   • Recording → capture thread is stopped, all frames are discarded.
        ///   • Encoding  → ignored (cannot interrupt safely).
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
                    // Stop capture first
                    captureThreadSnapshot?.Join();
                    Logger.Log("GifCaptureManager.CancelRecording: capture thread joined.");

                    if (streamEncSnapshot != null)
                    {
                        // Streaming mode: the encoder thread is blocked on Drain().
                        // The capture thread already called buffer.Complete() in its
                        // finally block, so the encoder thread will exit naturally.
                        encoderThreadSnapshot?.Join();
                        Logger.Log("GifCaptureManager.CancelRecording: encoder thread joined.");
                        // Cancel deletes the partial file.
                        streamEncSnapshot.Cancel();
                    }
                    else
                    {
                        // Batch mode: just discard all buffered frames.
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
            int fps         = settings.GifFPS;
            int maxSeconds  = settings.GifDuration;   // 0 = unlimited
            int delayMs     = 1000 / fps;
            int maxFrames   = maxSeconds > 0 ? fps * maxSeconds : int.MaxValue;

            Logger.Log($"GifCaptureManager.CaptureLoop: starting, fps={fps}, " +
                       $"maxFrames={maxFrames}, delayMs={delayMs}");

            // Elevate DPI awareness so CopyFromScreen captures physical pixels
            // rather than logical ones on high-DPI displays.
            IntPtr oldCtx = NativeMethods.SetThreadDpiAwarenessContext(
                                NativeMethods.DPI_PER_MONITOR_AWARE_V2);
            try
            {
                for (int i = 0; !_stopRequested && i < maxFrames; i++)
                {
                    var sw = Stopwatch.StartNew();

                    try
                    {
                        Bitmap frame = CaptureVirtualScreen(settings.ShowCursor);
                        _buffer.Add(frame);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GifCaptureManager.CaptureLoop: frame {i} error — {ex.Message}");
                    }

                    // Sleep for the remainder of this frame's time budget.
                    int elapsed = (int)sw.ElapsedMilliseconds;
                    int sleep   = delayMs - elapsed;
                    if (sleep > 0)
                        Thread.Sleep(sleep);
                    // If we're behind schedule we just proceed immediately.
                }
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(oldCtx);
                _buffer.Complete();   // signal: no more frames will be added
                Logger.Log("GifCaptureManager.CaptureLoop: finished.");
            }
        }

        // ------------------------------------------------------------------ //
        //  Full-screen capture helper
        // ------------------------------------------------------------------ //

        private static Bitmap CaptureVirtualScreen(bool showCursor)
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
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
        //  Streaming encoder loop (runs on _encoderThread, streaming mode only)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Drains the frame buffer and feeds each frame to the stream encoder
        /// as it arrives.  Blocks until <see cref="GifFrameBuffer.Complete"/>
        /// has been called and the queue is empty, then returns — the caller
        /// (<see cref="EncodeAndFinish"/>) is responsible for calling Close().
        /// </summary>
        private static void StreamEncodeLoop(GifStreamEncoder enc, GifFrameBuffer buffer)
        {
            Logger.Log("GifCaptureManager.StreamEncodeLoop: starting.");
            try
            {
                foreach (Bitmap frame in buffer.Drain())
                {
                    // AddFrame encodes+writes the frame and disposes the Bitmap.
                    enc.AddFrame(frame);
                }
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
            // 1. Wait for capture to finish.
            captureThread?.Join();
            Logger.Log("GifCaptureManager.EncodeAndFinish: capture thread joined.");

            // Notify the skin that encoding phase is beginning.
            ExecuteAction(settings, settings.OnGifEncodingAction, "OnGifEncodingAction");

            bool success = false;
            try
            {
                if (streamEncoder != null)
                {
                    // --- Streaming mode ---
                    // The encoder thread has been writing frames live.
                    // Wait for it to drain the last frames, then close the stream.
                    encoderThread?.Join();
                    Logger.Log("GifCaptureManager.EncodeAndFinish: stream encoder thread joined.");
                    streamEncoder.Close();
                    success = File.Exists(settings.GifSavePath);
                }
                else
                {
                    // --- Batch mode ---
                    var frames = new List<Bitmap>();
                    try
                    {
                        foreach (Bitmap frame in buffer.Drain())
                            frames.Add(frame);

                        Logger.Log($"GifCaptureManager.EncodeAndFinish: {frames.Count} frames collected.");

                        if (frames.Count == 0)
                        {
                            Logger.Log("GifCaptureManager.EncodeAndFinish: no frames, skipping save.");
                            return;
                        }

                        AnimatedGifEncoder.GifMaxWidth = settings.GifMaxWidth;
                        AnimatedGifEncoder.Encode(frames, 1000 / settings.GifFPS, settings.GifSavePath);
                        success = true;
                    }
                    finally
                    {
                        foreach (Bitmap f in frames) f?.Dispose();
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
                streamEncoder?.Dispose(); // no-op if already closed/cancelled

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

        /// <summary>
        /// Executes a Rainmeter bang string via the API.
        /// Does nothing if <paramref name="action"/> is null or empty.
        /// Logs the action name on success and any exception on failure.
        /// </summary>
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

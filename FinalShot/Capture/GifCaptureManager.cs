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
    ///   Idle ──[StartRecording]──► Recording ──[StopAndSave]──► Encoding ──► Idle
    ///
    /// Thread model:
    ///   • StartRecording() returns immediately; a background thread captures frames.
    ///   • StopAndSave() signals the capture thread to stop, waits for it to finish,
    ///     then encodes all frames synchronously on a new background thread so that
    ///     the Rainmeter plugin thread is never blocked.
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

        private static Thread         _captureThread;
        private static GifFrameBuffer _buffer;

        // Signals the capture loop to exit when set to true.
        private static volatile bool _stopRequested;

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Begin a new GIF recording session.
        /// Captures the full virtual screen at <c>settings.GifFPS</c> frames per second.
        /// If <c>settings.GifDuration &gt; 0</c> the recording stops automatically
        /// after that many seconds; otherwise it runs until <see cref="StopAndSave"/>.
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

                Logger.Log($"GifCaptureManager.StartRecording: fps={settings.GifFPS}, " +
                           $"duration={settings.GifDuration}s, path={settings.GifSavePath}");

                _captureThread = new Thread(() => CaptureLoop(settings));
                _captureThread.IsBackground = true;
                _captureThread.Name = "FinalShot-GifCapture";
                _captureThread.Start();
            }
        }

        /// <summary>
        /// Stop the active recording session and save the result as an animated GIF.
        ///
        /// The method returns immediately; encoding runs on a separate background
        /// thread.  <c>settings.FinishAction</c> is executed once encoding is done.
        ///
        /// Does nothing if no recording is in progress.
        /// </summary>
        public static void StopAndSave(Settings settings)
        {
            Thread captureThreadSnapshot;
            GifFrameBuffer bufferSnapshot;

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
                bufferSnapshot        = _buffer;
            }

            Logger.Log("GifCaptureManager.StopAndSave: signalled capture thread to stop.");

            // Run the encode on a separate thread so the Rainmeter plugin call
            // returns quickly (encoding a long GIF can take several seconds).
            var encodeThread = new Thread(() =>
                EncodeAndFinish(settings, captureThreadSnapshot, bufferSnapshot));
            encodeThread.IsBackground = true;
            encodeThread.Name = "FinalShot-GifEncode";
            encodeThread.Start();
        }

        /// <summary>
        /// Returns true while a recording or encoding session is active.
        /// Useful for the skin to conditionally enable/disable buttons.
        /// </summary>
        public static bool IsActive => _state != State.Idle;

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
        //  Encode + finish (runs on encodeThread)
        // ------------------------------------------------------------------ //

        private static void EncodeAndFinish(
            Settings       settings,
            Thread         captureThread,
            GifFrameBuffer buffer)
        {
            // Wait for the capture thread to finish draining the frame buffer.
            captureThread?.Join();
            Logger.Log("GifCaptureManager.EncodeAndFinish: capture thread joined, starting encode.");

            var frames = new List<Bitmap>();
            try
            {
                // Drain the buffer — this returns immediately because capture
                // thread already called Complete() in its finally block.
                foreach (Bitmap frame in buffer.Drain())
                    frames.Add(frame);

                Logger.Log($"GifCaptureManager.EncodeAndFinish: {frames.Count} frames collected.");

                if (frames.Count == 0)
                {
                    Logger.Log("GifCaptureManager.EncodeAndFinish: no frames captured, skipping save.");
                    return;
                }

                int frameDelayMs = 1000 / settings.GifFPS;
                AnimatedGifEncoder.GifMaxWidth = settings.GifMaxWidth;
                AnimatedGifEncoder.Encode(frames, frameDelayMs, settings.GifSavePath);

                Logger.Log($"GifCaptureManager.EncodeAndFinish: GIF saved to {settings.GifSavePath}");

                // Show notification (reuse existing notification system).
                if (settings.ShowNotification && File.Exists(settings.GifSavePath))
                {
                    // NotificationForm expects an image path; GIF is a valid image.
                    ShowGifNotification(settings.GifSavePath);
                }

                // Execute the configured finish action bang.
                ScreenshotManager.ExecuteFinishAction(settings);
            }
            catch (Exception ex)
            {
                Logger.Log($"GifCaptureManager.EncodeAndFinish: error — {ex}");
            }
            finally
            {
                // Dispose all frames regardless of success/failure.
                foreach (Bitmap f in frames)
                    f?.Dispose();

                buffer.Dispose();

                lock (_stateLock)
                {
                    _state         = State.Idle;
                    _captureThread = null;
                    _buffer        = null;
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
    }
}

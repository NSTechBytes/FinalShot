using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;

namespace PluginScreenshot
{
    /// <summary>
    /// A captured frame paired with its actual display duration in centiseconds.
    /// The delay is measured from the real elapsed time between captures so that
    /// the GIF plays back at the correct speed regardless of FPS setting or
    /// timing jitter.
    /// </summary>
    internal sealed class GifFrame
    {
        public readonly Bitmap Bitmap;
        /// <summary>GIF delay in centiseconds (1cs = 10ms). Minimum 2cs (20ms).</summary>
        public readonly int DelayCs;

        public GifFrame(Bitmap bitmap, int delayCs)
        {
            Bitmap  = bitmap;
            DelayCs = delayCs;
        }
    }

    /// <summary>
    /// Thread-safe producer/consumer frame queue for GIF recording.
    ///
    /// The capture thread calls Add() at the desired FPS.
    /// After recording is stopped, the encoder calls Drain() to retrieve
    /// all collected frames in capture order.
    ///
    /// Ownership: each GifFrame added belongs to this buffer.
    /// The consumer (encoder) is responsible for disposing each frame
    /// after it has been encoded.
    /// </summary>
    internal sealed class GifFrameBuffer : IDisposable
    {
        // BlockingCollection gives us a thread-safe bounded/unbounded queue.
        // We leave it unbounded here; memory pressure is handled by GifDuration
        // and the caller's FPS cap.
        private readonly BlockingCollection<GifFrame> _queue
            = new BlockingCollection<GifFrame>();

        private bool _disposed;

        // ------------------------------------------------------------------ //
        //  Producer side (capture thread)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Enqueue a captured frame with its real elapsed delay.
        /// Must not be called after Complete().
        /// The buffer takes ownership of <paramref name="frame"/>.
        /// </summary>
        public void Add(GifFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (_disposed) return;
            _queue.Add(frame);
        }

        /// <summary>
        /// Signal that no more frames will be added.
        /// Must be called exactly once, from the capture thread, after the
        /// last Add(). Unblocks any pending Drain() call.
        /// </summary>
        public void Complete()
        {
            if (!_disposed)
                _queue.CompleteAdding();
        }

        // ------------------------------------------------------------------ //
        //  Consumer side (encoder / main GifCaptureManager)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Blocking enumeration of all frames in order.
        /// Blocks until Complete() has been called and the queue is empty.
        /// Caller must dispose each yielded GifFrame's Bitmap after use.
        /// </summary>
        public IEnumerable<GifFrame> Drain()
        {
            // GetConsumingEnumerable blocks on each Take() until an item
            // is available or the collection is marked complete.
            foreach (GifFrame frame in _queue.GetConsumingEnumerable())
            {
                yield return frame;
            }
        }

        /// <summary>Number of frames currently in the queue (approximate).</summary>
        public int Count => _queue.Count;

        // ------------------------------------------------------------------ //
        //  Cleanup
        // ------------------------------------------------------------------ //

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drain and discard any frames that were never consumed.
            _queue.CompleteAdding();
            while (_queue.TryTake(out GifFrame leftover))
                leftover?.Bitmap?.Dispose();

            _queue.Dispose();
        }
    }
}

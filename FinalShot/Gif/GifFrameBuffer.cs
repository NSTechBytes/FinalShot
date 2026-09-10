using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;

namespace PluginScreenshot
{
    /// <summary>
    /// Thread-safe producer/consumer frame queue for GIF recording.
    ///
    /// The capture thread calls Add() at the desired FPS.
    /// After recording is stopped, the encoder calls Drain() to retrieve
    /// all collected frames in capture order.
    ///
    /// Ownership: each Bitmap added belongs to this buffer.
    /// The consumer (encoder) is responsible for disposing each frame
    /// after it has been encoded.
    /// </summary>
    internal sealed class GifFrameBuffer : IDisposable
    {
        // BlockingCollection gives us a thread-safe bounded/unbounded queue.
        // We leave it unbounded here; memory pressure is handled by GifDuration
        // and the caller's FPS cap.
        private readonly BlockingCollection<Bitmap> _queue
            = new BlockingCollection<Bitmap>();

        private bool _disposed;

        // ------------------------------------------------------------------ //
        //  Producer side (capture thread)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Enqueue a captured frame. Must not be called after Complete().
        /// The buffer takes ownership of <paramref name="frame"/>.
        /// </summary>
        public void Add(Bitmap frame)
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
        /// Caller must dispose each yielded Bitmap after use.
        /// </summary>
        public IEnumerable<Bitmap> Drain()
        {
            // GetConsumingEnumerable blocks on each Take() until an item
            // is available or the collection is marked complete.
            foreach (Bitmap frame in _queue.GetConsumingEnumerable())
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
            while (_queue.TryTake(out Bitmap leftover))
                leftover?.Dispose();

            _queue.Dispose();
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
namespace PluginScreenshot
{
    internal sealed class GifFrame
    {
        public readonly Bitmap Bitmap;
        public readonly int DelayCs;
        public GifFrame(Bitmap bitmap, int delayCs)
        {
            Bitmap  = bitmap;
            DelayCs = delayCs;
        }
    }
    internal sealed class GifFrameBuffer : IDisposable
    {
        private readonly BlockingCollection<GifFrame> _queue
            = new BlockingCollection<GifFrame>();
        private bool _disposed;
        public void Add(GifFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (_disposed) return;
            _queue.Add(frame);
        }
        public void Complete()
        {
            if (!_disposed)
                _queue.CompleteAdding();
        }
        public IEnumerable<GifFrame> Drain()
        {
            foreach (GifFrame frame in _queue.GetConsumingEnumerable())
            {
                yield return frame;
            }
        }
        public int Count => _queue.Count;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            while (_queue.TryTake(out GifFrame leftover))
                leftover?.Bitmap?.Dispose();
            _queue.Dispose();
        }
    }
}
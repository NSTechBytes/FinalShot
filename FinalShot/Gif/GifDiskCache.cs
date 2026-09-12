using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PluginScreenshot
{
    // GifFrame is defined here now that GifFrameBuffer.cs has been removed.
    internal sealed class GifFrame
    {
        public readonly Bitmap Bitmap;
        public readonly int    DelayCs;
        public GifFrame(Bitmap bitmap, int delayCs)
        {
            Bitmap  = bitmap;
            DelayCs = delayCs;
        }
    }
    // ======================================================================
    //  GifDiskCache  —  ShareX-style disk-backed frame store.
    //
    //  During recording each captured Bitmap is serialised as a raw BMP into
    //  a single temporary file on disk and then immediately disposed.  Memory
    //  consumption is therefore constant regardless of recording length — only
    //  one frame is ever in RAM at a time.
    //
    //  During encoding the caller iterates GetFrameEnumerator() which opens
    //  the cache file and yields one GifFrame at a time.  Because the encoder
    //  needs two passes (palette sampling + quantise/write), the enumerator
    //  can be called twice; it simply seeks back to the start of the file.
    //
    //  The temp file is deleted when Dispose() is called.
    // ======================================================================
    internal sealed class GifDiskCache : IDisposable
    {
        // ------------------------------------------------------------------ //
        //  Index entry: byte offset + length of one BMP frame in the cache file
        // ------------------------------------------------------------------ //
        private struct FrameEntry
        {
            public readonly long   Offset;
            public readonly long   Length;
            public readonly int    DelayCs;

            public FrameEntry(long offset, long length, int delayCs)
            {
                Offset  = offset;
                Length  = length;
                DelayCs = delayCs;
            }
        }

        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //

        private readonly string             _cachePath;
        private readonly FileStream         _writeStream;
        private readonly List<FrameEntry>   _index = new List<FrameEntry>();
        private readonly object             _lock  = new object();

        // Background consumer — receives bitmaps from the capture thread and
        // writes them to disk so the capture loop is never blocked by I/O.
        private readonly System.Collections.Concurrent.BlockingCollection<GifFrame>
                                            _queue
            = new System.Collections.Concurrent.BlockingCollection<GifFrame>();
        private readonly Thread             _writerThread;
        private bool                        _disposed;

        public int Count { get { lock (_lock) { return _index.Count; } } }

        // ------------------------------------------------------------------ //
        //  Construction — opens the cache file and starts the writer thread
        // ------------------------------------------------------------------ //

        public GifDiskCache()
        {
            _cachePath   = Path.Combine(Path.GetTempPath(),
                               "FinalShot_" + Guid.NewGuid().ToString("N") + ".tmp");
            _writeStream = new FileStream(_cachePath, FileMode.Create,
                               FileAccess.Write, FileShare.Read,
                               bufferSize: 1 << 20 /* 1 MB write buffer */);

            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name         = "FinalShot-DiskCacheWriter"
            };
            _writerThread.Start();
        }

        // ------------------------------------------------------------------ //
        //  Add — called from capture thread (non-blocking)
        //  The bitmap is owned by the queue from this point; the writer thread
        //  disposes it after writing.
        // ------------------------------------------------------------------ //

        public void Add(GifFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (_disposed) { frame.Bitmap?.Dispose(); return; }
            _queue.Add(frame);
        }

        // ------------------------------------------------------------------ //
        //  Complete — signals end of capture; blocks until all queued frames
        //  have been written to disk and the write stream is flushed/closed.
        // ------------------------------------------------------------------ //

        public void Complete()
        {
            _queue.CompleteAdding();
            _writerThread.Join();   // wait for every frame to land on disk
            _writeStream.Flush();
            _writeStream.Dispose();
        }

        // ------------------------------------------------------------------ //
        //  GetFrameEnumerator — streams frames back from disk one at a time.
        //  Safe to call multiple times (each call opens a fresh read handle).
        //  Must only be called after Complete().
        // ------------------------------------------------------------------ //

        public IEnumerable<GifFrame> GetFrameEnumerator()
        {
            if (_index.Count == 0 || !File.Exists(_cachePath))
                yield break;

            using (var fs = new FileStream(_cachePath, FileMode.Open,
                                FileAccess.Read, FileShare.Read,
                                bufferSize: 1 << 20))
            {
                foreach (FrameEntry entry in _index)
                {
                    // Read the BMP bytes for this frame
                    byte[] buf = new byte[entry.Length];
                    fs.Seek(entry.Offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n == 0) break;
                        read += n;
                    }

                    // Decode BMP → Bitmap, yield, then dispose immediately
                    Bitmap bmp;
                    using (var ms = new MemoryStream(buf))
                        bmp = new Bitmap(ms);

                    yield return new GifFrame(bmp, entry.DelayCs);
                    // Caller disposes the bitmap after use
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Writer loop (background thread)
        // ------------------------------------------------------------------ //

        private void WriterLoop()
        {
            try
            {
                foreach (GifFrame frame in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        WriteFrame(frame);
                    }
                    finally
                    {
                        frame.Bitmap?.Dispose(); // free GDI bitmap immediately
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GifDiskCache.WriterLoop: error — {ex.Message}");
            }
        }

        private void WriteFrame(GifFrame frame)
        {
            using (var ms = new MemoryStream())
            {
                // Save as BMP — lossless and very fast to encode/decode
                frame.Bitmap.Save(ms, ImageFormat.Bmp);
                long offset = _writeStream.Position;
                byte[] buf  = ms.ToArray();
                _writeStream.Write(buf, 0, buf.Length);
                lock (_lock)
                    _index.Add(new FrameEntry(offset, buf.Length, frame.DelayCs));
            }
        }

        // ------------------------------------------------------------------ //
        //  Dispose — deletes the temp file
        // ------------------------------------------------------------------ //

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drain any remaining queued frames (cancelled recording path)
            _queue.CompleteAdding();
            while (_queue.TryTake(out GifFrame leftover))
                leftover?.Bitmap?.Dispose();
            _queue.Dispose();

            try { _writeStream.Dispose(); } catch { }

            if (File.Exists(_cachePath))
            {
                try { File.Delete(_cachePath); }
                catch (Exception ex)
                { Logger.Log($"GifDiskCache.Dispose: could not delete temp file — {ex.Message}"); }
            }
        }
    }
}

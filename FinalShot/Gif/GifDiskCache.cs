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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

    //  GifDiskCache  --  ShareX-style disk-backed frame store.
    //
    //  During recording each captured Bitmap is serialised as a raw BMP into
    //  a single temporary file on disk and then immediately disposed.  Memory
    //  consumption is therefore constant regardless of recording length -- only
    //  one frame is ever in RAM at a time.
    //
    //  During encoding the caller iterates GetFrameEnumerator() which opens
    //  the cache file and yields one GifFrame at a time.  Because the encoder
    //  needs two passes (palette sampling + quantise/write), the enumerator
    //  can be called twice; it simply seeks back to the start of the file.
    //
    //  The temp file is deleted when Dispose() is called.
    internal sealed class GifDiskCache : IDisposable
    {
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

        private readonly string             _cachePath;
        private readonly FileStream         _writeStream;
        private readonly List<FrameEntry>   _index = new List<FrameEntry>();
        private readonly object             _lock  = new object();

        private readonly System.Collections.Concurrent.BlockingCollection<GifFrame>
                                            _queue
            = new System.Collections.Concurrent.BlockingCollection<GifFrame>();
        private readonly Thread             _writerThread;
        private bool                        _disposed;
        private bool                        _completed;

        public int Count { get { lock (_lock) { return _index.Count; } } }

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

        public void Add(GifFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (_disposed || _completed) { frame.Bitmap?.Dispose(); return; }
            try
            {
                _queue.Add(frame);
            }
            catch (InvalidOperationException)
            {
                // Queue completed between check and Add
                frame.Bitmap?.Dispose();
            }
        }

        // Signals end of capture; blocks until all queued frames are on disk.
        public void Complete()
        {
            lock (_lock)
            {
                if (_completed || _disposed) return;
                _completed = true;
            }

            try
            {
                if (!_queue.IsAddingCompleted)
                    _queue.CompleteAdding();
            }
            catch (InvalidOperationException) { /* already completed */ }

            _writerThread.Join();
            try
            {
                _writeStream.Flush();
                _writeStream.Dispose();
            }
            catch { }
        }

        // Streams frames back from disk. Safe to call multiple times after Complete().
        // Yields independent Bitmaps (deep-copied) so callers are not tied to a stream.
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
                    byte[] buf = new byte[entry.Length];
                    fs.Seek(entry.Offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n == 0) break;
                        read += n;
                    }

                    // new Bitmap(stream) keeps a reference to the stream — clone
                    // into an independent bitmap before disposing the MemoryStream.
                    Bitmap bmp;
                    using (var ms = new MemoryStream(buf))
                    using (var temp = new Bitmap(ms))
                    {
                        bmp = new Bitmap(temp);
                    }

                    yield return new GifFrame(bmp, entry.DelayCs);
                }
            }
        }

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
                        frame.Bitmap?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GifDiskCache.WriterLoop: error -- {ex.Message}");
            }
        }

        private void WriteFrame(GifFrame frame)
        {
            using (var ms = new MemoryStream())
            {
                frame.Bitmap.Save(ms, ImageFormat.Bmp);
                long offset = _writeStream.Position;
                byte[] buf  = ms.ToArray();
                _writeStream.Write(buf, 0, buf.Length);
                lock (_lock)
                    _index.Add(new FrameEntry(offset, buf.Length, frame.DelayCs));
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            // Cancel path: Complete() was never called — drain and join writer.
            if (!_completed)
            {
                try
                {
                    if (!_queue.IsAddingCompleted)
                        _queue.CompleteAdding();
                }
                catch (InvalidOperationException) { }

                while (_queue.TryTake(out GifFrame leftover))
                    leftover?.Bitmap?.Dispose();

                try
                {
                    if (_writerThread.IsAlive)
                        _writerThread.Join(5000);
                }
                catch { }

                try { _writeStream.Dispose(); } catch { }
            }

            try { _queue.Dispose(); } catch { }

            if (File.Exists(_cachePath))
            {
                try { File.Delete(_cachePath); }
                catch (Exception ex)
                { Logger.Log($"GifDiskCache.Dispose: could not delete temp file -- {ex.Message}"); }
            }
        }
    }
}

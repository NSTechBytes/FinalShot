using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    // ======================================================================
    //  AnimatedGifEncoder
    //  Pure .NET 4.8 animated GIF encoder.
    //
    //  Pipeline per frame:
    //    1. Scale frame down to at most GifMaxWidth (default 800 px).
    //       GIF's 256-colour limit makes large frames look bad; scaling down
    //       also massively reduces encoding time and file size.
    //    2. Median-cut quantisation — builds an optimal 256-colour palette
    //       from the actual pixels (no fixed Windows palette).
    //    3. Build a 32×32×32 colour-lookup-cube for O(1) nearest-colour
    //       lookup (replaces the O(256) linear scan that caused the hang).
    //    4. Floyd-Steinberg error-diffusion dithering for smooth gradients.
    //    5. GIF-compatible LZW compression.
    //    6. Write raw GIF89a bytes directly (full control over every field).
    // ======================================================================
    internal static class AnimatedGifEncoder
    {
        /// <summary>
        /// Maximum output width in pixels.  Frames wider than this are scaled
        /// down proportionally before encoding.  Reduces file size and encoding
        /// time dramatically without visible quality loss at GIF colour depth.
        /// </summary>
        public static int GifMaxWidth = 800;

        // ------------------------------------------------------------------ //
        //  Public entry point
        // ------------------------------------------------------------------ //

        public static void Encode(IList<Bitmap> frames, int frameDelayMs, string outputPath)
        {
            if (frames == null || frames.Count == 0)
                throw new ArgumentException("No frames to encode.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentNullException("outputPath");

            if (frameDelayMs < 20) frameDelayMs = 20;
            int delayCs = frameDelayMs / 10; // GIF delay unit = centiseconds

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Determine output dimensions (scale first frame, use same for all).
            int srcW = frames[0].Width;
            int srcH = frames[0].Height;
            int outW = srcW, outH = srcH;
            if (outW > GifMaxWidth)
            {
                outW = GifMaxWidth;
                outH = (int)Math.Round(srcH * ((double)GifMaxWidth / srcW));
                if (outH < 1) outH = 1;
            }

            Logger.Log($"AnimatedGifEncoder: {frames.Count} frames, {srcW}x{srcH} -> {outW}x{outH}, " +
                       $"delay={frameDelayMs}ms, out={outputPath}");

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                // GIF89a header
                bw.Write(new byte[] { 0x47,0x49,0x46,0x38,0x39,0x61 }); // "GIF89a"

                // Logical Screen Descriptor — no global colour table
                WriteU16Internal(bw, (ushort)outW);
                WriteU16Internal(bw, (ushort)outH);
                bw.Write((byte)0x00); // packed: no GCT
                bw.Write((byte)0x00); // background colour index
                bw.Write((byte)0x00); // pixel aspect ratio

                // Netscape loop extension (loop = 0 → infinite)
                WriteNetscapeLoopInternal(bw, 0);

                for (int i = 0; i < frames.Count; i++)
                {
                    Logger.Log($"AnimatedGifEncoder: encoding frame {i + 1}/{frames.Count}");
                    EncodeFrame(bw, frames[i], outW, outH, delayCs);
                }
                bw.Write((byte)0x3B); // GIF trailer
            }

            Logger.Log($"AnimatedGifEncoder: done — {outputPath}");
        }

        // ------------------------------------------------------------------ //
        //  Per-frame encode
        // ------------------------------------------------------------------ //

        private static void EncodeFrame(BinaryWriter bw, Bitmap src, int outW, int outH, int delayCs)
        {
            // 1. Scale (or use as-is if already the right size)
            Bitmap scaled = ScaleFrameInternal(src, outW, outH);
            try
            {
                // 2. Read pixels — GDI+ Format32bppArgb is stored in memory
                //    as [B, G, R, A] per 4-byte pixel (little-endian DWORD).
                byte[] bgra = ReadBgraInternal(scaled, outW, outH);

                // 3. Build optimal 256-colour palette via median-cut
                Color[] palette = MediaCutQuantizer.Build(bgra, outW * outH, 256);

                // 4. Build 32³ lookup cube for fast nearest-colour queries
                byte[] cube = BuildLookupCubeInternal(palette);

                // 5. Floyd-Steinberg dither → palette indices
                byte[] indices = DitherInternal(bgra, outW, outH, palette, cube);

                // 6. Write GIF frame
                WriteGifFrameInternal(bw, indices, palette, outW, outH, delayCs);
            }
            finally
            {
                if (!ReferenceEquals(scaled, src))
                    scaled.Dispose();
            }
        }

        // ------------------------------------------------------------------ //
        //  Frame scaling
        // ------------------------------------------------------------------ //

        internal static Bitmap ScaleFrameInternal(Bitmap src, int w, int h)
        {
            if (src.Width == w && src.Height == h)
                return src; // no scaling needed

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode  = InterpolationMode.Bilinear;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.SmoothingMode      = SmoothingMode.None;
                g.DrawImage(src, 0, 0, w, h);
            }
            return dst;
        }

        // ------------------------------------------------------------------ //
        //  Read raw BGRA bytes from bitmap
        // ------------------------------------------------------------------ //

        internal static byte[] ReadBgraInternal(Bitmap bmp, int w, int h)
        {
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                  ImageLockMode.ReadOnly,
                                  PixelFormat.Format32bppArgb);
            try
            {
                // Stride may be padded; copy row by row if needed
                byte[] buf = new byte[w * h * 4];
                if (bd.Stride == w * 4)
                {
                    Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                }
                else
                {
                    IntPtr ptr = bd.Scan0;
                    int rowBytes = w * 4;
                    for (int row = 0; row < h; row++)
                    {
                        Marshal.Copy(IntPtr.Add(ptr, row * bd.Stride),
                                     buf, row * rowBytes, rowBytes);
                    }
                }
                return buf;
            }
            finally { bmp.UnlockBits(bd); }
        }

        // ------------------------------------------------------------------ //
        //  Colour-lookup cube  (32 × 32 × 32 = 32768 entries)
        //
        //  Maps a quantized (R>>3, G>>3, B>>3) triple to the nearest palette
        //  index.  Built once per frame; lookups are O(1).
        // ------------------------------------------------------------------ //

        internal static byte[] BuildLookupCubeInternal(Color[] palette)
        {
            const int BITS = 5;           // 32 levels per channel
            const int SIZE = 32;
            byte[] cube = new byte[SIZE * SIZE * SIZE];

            for (int ri = 0; ri < SIZE; ri++)
            for (int gi = 0; gi < SIZE; gi++)
            for (int bi = 0; bi < SIZE; bi++)
            {
                // Representative colour for this cube cell (mid-point of the 8-value band)
                byte r = (byte)((ri << 3) | 4);
                byte g = (byte)((gi << 3) | 4);
                byte b = (byte)((bi << 3) | 4);
                cube[(ri << (BITS * 2)) | (gi << BITS) | bi] = (byte)FindNearest(palette, r, g, b);
            }
            return cube;
        }

        /// <summary>Exact O(palette.Length) nearest search — used only when building the cube.</summary>
        private static int FindNearest(Color[] palette, byte r, byte g, byte b)
        {
            int best = 0;
            long bestD = long.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                long dr = r - palette[i].R;
                long dg = g - palette[i].G;
                long db = b - palette[i].B;
                long d  = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Fast O(1) nearest-colour lookup via the prebuilt cube.</summary>
        private static int CubeLookup(byte[] cube, byte r, byte g, byte b)
        {
            return cube[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
        }

        // ------------------------------------------------------------------ //
        //  Floyd-Steinberg dithering
        // ------------------------------------------------------------------ //

        internal static byte[] DitherInternal(byte[] bgra, int w, int h, Color[] palette, byte[] cube)
        {
            // Two-row error buffers — only need current and next row in memory.
            // Indexed [channel * rowLen + x], channel: 0=R 1=G 2=B
            int rowLen = w;
            float[] errCurR = new float[rowLen];
            float[] errCurG = new float[rowLen];
            float[] errCurB = new float[rowLen];
            float[] errNxtR = new float[rowLen];
            float[] errNxtG = new float[rowLen];
            float[] errNxtB = new float[rowLen];

            byte[] indices = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                // Swap error rows
                float[] tmpR = errCurR; errCurR = errNxtR; errNxtR = tmpR;
                float[] tmpG = errCurG; errCurG = errNxtG; errNxtG = tmpG;
                float[] tmpB = errCurB; errCurB = errNxtB; errNxtB = tmpB;
                // Clear next row
                Array.Clear(errNxtR, 0, rowLen);
                Array.Clear(errNxtG, 0, rowLen);
                Array.Clear(errNxtB, 0, rowLen);

                for (int x = 0; x < w; x++)
                {
                    int bi = (y * w + x) * 4;
                    // GDI+ BGRA memory layout: bi+0=B, bi+1=G, bi+2=R, bi+3=A
                    float r = Clamp(bgra[bi + 2] + errCurR[x]);
                    float g = Clamp(bgra[bi + 1] + errCurG[x]);
                    float b = Clamp(bgra[bi + 0] + errCurB[x]);

                    // Palette lookup via cube (O(1))
                    int pidx = CubeLookup(cube, (byte)r, (byte)g, (byte)b);
                    indices[y * w + x] = (byte)pidx;

                    // Quantization error
                    float er = r - palette[pidx].R;
                    float eg = g - palette[pidx].G;
                    float eb = b - palette[pidx].B;

                    // Distribute error: Floyd-Steinberg weights 7/3/5/1 /16
                    if (x + 1 < w)
                    {
                        errCurR[x + 1] += er * (7f / 16f);
                        errCurG[x + 1] += eg * (7f / 16f);
                        errCurB[x + 1] += eb * (7f / 16f);
                    }
                    if (x > 0)
                    {
                        errNxtR[x - 1] += er * (3f / 16f);
                        errNxtG[x - 1] += eg * (3f / 16f);
                        errNxtB[x - 1] += eb * (3f / 16f);
                    }
                    errNxtR[x] += er * (5f / 16f);
                    errNxtG[x] += eg * (5f / 16f);
                    errNxtB[x] += eb * (5f / 16f);
                    if (x + 1 < w)
                    {
                        errNxtR[x + 1] += er * (1f / 16f);
                        errNxtG[x + 1] += eg * (1f / 16f);
                        errNxtB[x + 1] += eb * (1f / 16f);
                    }
                }
            }
            return indices;
        }

        private static float Clamp(float v) => v < 0f ? 0f : v > 255f ? 255f : v;

        // ------------------------------------------------------------------ //
        //  GIF binary output
        // ------------------------------------------------------------------ //

        internal static void WriteU16Internal(BinaryWriter bw, ushort v)
        {
            bw.Write((byte)(v & 0xFF));
            bw.Write((byte)(v >> 8));
        }

        internal static void WriteNetscapeLoopInternal(BinaryWriter bw, ushort loopCount)
        {
            bw.Write((byte)0x21); bw.Write((byte)0xFF); // Ext introducer + App label
            bw.Write((byte)11);                         // Block size
            bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write((byte)3); bw.Write((byte)1);       // Sub-block ID
            WriteU16Internal(bw, loopCount);
            bw.Write((byte)0);                          // Block terminator
        }

        internal static void WriteGifFrameInternal(BinaryWriter bw, byte[] indices,
                                          Color[] palette, int w, int h, int delayCs)
        {
            // Graphic Control Extension
            bw.Write((byte)0x21); bw.Write((byte)0xF9);
            bw.Write((byte)4);
            bw.Write((byte)0x00);          // packed: dispose=0, no transparency
            WriteU16Internal(bw, (ushort)delayCs);
            bw.Write((byte)0);             // transparent colour index (unused)
            bw.Write((byte)0);             // block terminator

            // Image Descriptor
            bw.Write((byte)0x2C);
            WriteU16Internal(bw, 0); WriteU16Internal(bw, 0);   // left, top
            WriteU16Internal(bw, (ushort)w);
            WriteU16Internal(bw, (ushort)h);
            // Packed byte: Local CT flag=1, not interlaced, size field=7 → 2^(7+1)=256 entries
            bw.Write((byte)0x87);

            // Local Colour Table — exactly 256 × 3 bytes
            for (int i = 0; i < 256; i++)
            {
                Color c = (i < palette.Length) ? palette[i] : Color.Black;
                bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
            }

            // LZW minimum code size
            bw.Write((byte)8);

            // LZW-encode and write in 255-byte sub-blocks
            byte[] lzw = LzwEncoder.Encode(indices, 8);
            int pos = 0;
            while (pos < lzw.Length)
            {
                int blockLen = Math.Min(255, lzw.Length - pos);
                bw.Write((byte)blockLen);
                bw.Write(lzw, pos, blockLen);
                pos += blockLen;
            }
            bw.Write((byte)0); // block terminator
        }
    }

    // ======================================================================
    //  MediaCutQuantizer
    //  Median-cut colour quantization.
    //
    //  Uses flat int[] storage (no heap-allocated int[3] per sample) to avoid
    //  GC pressure. Samples are stored interleaved: [R0,G0,B0, R1,G1,B1, …].
    // ======================================================================
    internal static class MediaCutQuantizer
    {
        private const int MaxSamples = 40_000;

        /// <summary>
        /// Returns an optimal palette of <paramref name="maxColors"/> colours
        /// for the given raw BGRA pixel data.
        /// </summary>
        public static Color[] Build(byte[] bgra, int pixelCount, int maxColors)
        {
            // --- Sample pixels into a flat int[] [R,G,B, R,G,B, …] ---
            int sampleCount = Math.Min(pixelCount, MaxSamples);
            int step = pixelCount / sampleCount;
            if (step < 1) step = 1;

            // Pre-allocate flat storage
            int[] flat = new int[sampleCount * 3];
            int si = 0;
            for (int i = 0; i < pixelCount && si < sampleCount * 3; i += step)
            {
                int bi = i * 4;
                flat[si++] = bgra[bi + 2]; // R
                flat[si++] = bgra[bi + 1]; // G
                flat[si++] = bgra[bi + 0]; // B
            }
            int actualSamples = si / 3;

            // Each "bucket" is a (start, length) range into the flat array.
            // We avoid sub-arrays by sorting in-place within ranges.
            var buckets = new List<(int start, int len)> { (0, actualSamples) };

            while (buckets.Count < maxColors)
            {
                int splitIdx = LargestRangeIdx(flat, buckets);
                var bkt = buckets[splitIdx];
                if (bkt.len <= 1) break;

                buckets.RemoveAt(splitIdx);
                SplitBucket(flat, bkt.start, bkt.len, buckets);
            }

            // Compute mean per bucket → palette entry
            var palette = new Color[maxColors];
            for (int i = 0; i < maxColors; i++)
            {
                if (i < buckets.Count)
                    palette[i] = Mean(flat, buckets[i].start, buckets[i].len);
                else
                    palette[i] = Color.Black;
            }
            return palette;
        }

        private static int LargestRangeIdx(int[] flat, List<(int start, int len)> buckets)
        {
            int best = 0, bestRange = -1;
            for (int i = 0; i < buckets.Count; i++)
            {
                int range = Range(flat, buckets[i].start, buckets[i].len);
                if (range > bestRange) { bestRange = range; best = i; }
            }
            return best;
        }

        private static int Range(int[] flat, int start, int len)
        {
            int minR=255,maxR=0,minG=255,maxG=0,minB=255,maxB=0;
            for (int i = start * 3, end = (start + len) * 3; i < end; i += 3)
            {
                int r = flat[i], g = flat[i+1], b = flat[i+2];
                if (r < minR) minR=r; if (r > maxR) maxR=r;
                if (g < minG) minG=g; if (g > maxG) maxG=g;
                if (b < minB) minB=b; if (b > maxB) maxB=b;
            }
            return Math.Max(maxR-minR, Math.Max(maxG-minG, maxB-minB));
        }

        private static void SplitBucket(int[] flat, int start, int len,
                                        List<(int start, int len)> output)
        {
            // Find widest channel
            int minR=255,maxR=0,minG=255,maxG=0,minB=255,maxB=0;
            for (int i = start*3, end = (start+len)*3; i < end; i += 3)
            {
                int r=flat[i],g=flat[i+1],b=flat[i+2];
                if (r<minR)minR=r; if (r>maxR)maxR=r;
                if (g<minG)minG=g; if (g>maxG)maxG=g;
                if (b<minB)minB=b; if (b>maxB)maxB=b;
            }
            int rR=maxR-minR, rG=maxG-minG, rB=maxB-minB;
            int ch = (rG>=rR && rG>=rB) ? 1 : (rB>=rR && rB>=rG) ? 2 : 0;

            // Insertion-sort a small array view (bucket sizes stay manageable)
            // For large buckets, use a simple partition (faster than full sort)
            int s = start * 3, e = (start + len) * 3;

            // Collect channel values + original positions for partial sort
            // Use in-place selection of median by partial pivot sort
            PartialSort(flat, s, e, ch, len);

            int mid = len / 2;
            output.Add((start, mid));
            output.Add((start + mid, len - mid));
        }

        /// <summary>
        /// Rearranges the RGB triples in flat[s..e] so that the first half
        /// contains the triples with the smaller values in channel <c>ch</c>.
        /// Uses a simple insertion sort (acceptable because median-cut buckets
        /// shrink quickly and rarely exceed a few thousand entries).
        /// </summary>
        private static void PartialSort(int[] flat, int s, int e, int ch, int len)
        {
            // We only need a partition at the median, not a full sort.
            // For simplicity (and because buckets are small after a few splits)
            // we do a full in-place sort via a compact selection approach.
            // Store only the channel value + index to sort without extra allocation.
            // Use Knuth's shell-sort — O(n log n) without heap allocation.
            int n = (e - s) / 3;
            int gap = 1;
            while (gap < n / 3) gap = gap * 3 + 1; // Knuth sequence
            while (gap >= 1)
            {
                for (int i = gap; i < n; i++)
                {
                    int iBase = s + i * 3;
                    int iVal = flat[iBase + ch];
                    int ir = flat[iBase], ig = flat[iBase+1], ib = flat[iBase+2];
                    int j = i;
                    while (j >= gap && flat[s + (j-gap)*3 + ch] > iVal)
                    {
                        int jBase = s + j * 3;
                        int pBase = s + (j - gap) * 3;
                        flat[jBase]   = flat[pBase];
                        flat[jBase+1] = flat[pBase+1];
                        flat[jBase+2] = flat[pBase+2];
                        j -= gap;
                    }
                    int tBase = s + j * 3;
                    flat[tBase] = ir; flat[tBase+1] = ig; flat[tBase+2] = ib;
                }
                gap /= 3;
            }
        }

        private static Color Mean(int[] flat, int start, int len)
        {
            if (len == 0) return Color.Black;
            long r=0,g=0,b=0;
            for (int i=start*3, end=(start+len)*3; i<end; i+=3)
            { r+=flat[i]; g+=flat[i+1]; b+=flat[i+2]; }
            return Color.FromArgb((int)(r/len),(int)(g/len),(int)(b/len));
        }
    }

    // ======================================================================
    //  GifStreamEncoder
    //  Streaming (encode-while-record) variant.
    //
    //  Usage pattern:
    //    1. var enc = new GifStreamEncoder(maxWidth);
    //    2. enc.Open(outputPath, sourceWidth, sourceHeight, frameDelayMs);
    //    3. foreach frame: enc.AddFrame(bitmap);        ← called from encoder thread
    //    4a. enc.Close();    ← normal finish, writes GIF trailer
    //    4b. enc.Cancel();   ← discard, deletes the partial file
    //
    //  Thread safety:
    //    Open/Close/Cancel are called from the encoder/cancel thread.
    //    AddFrame is called from the same encoder thread (never concurrently).
    //    The object is not shared across threads simultaneously.
    // ======================================================================
    internal sealed class GifStreamEncoder : IDisposable
    {
        private readonly int     _maxWidth;
        private FileStream       _fs;
        private BinaryWriter     _bw;
        private string           _outputPath;
        private int              _outW, _outH, _delayCs;
        private int              _frameCount;
        private bool             _closed;
        private bool             _cancelled;

        public bool IsOpen => _fs != null && !_closed && !_cancelled;

        public GifStreamEncoder(int maxWidth)
        {
            _maxWidth = maxWidth > 0 ? maxWidth : 800;
        }

        // ------------------------------------------------------------------ //
        //  Open — write GIF89a header and Netscape loop extension
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Opens the output file and writes the GIF89a header.
        /// Must be called once before any <see cref="AddFrame"/> calls.
        /// </summary>
        public void Open(string outputPath, int sourceWidth, int sourceHeight, int frameDelayMs)
        {
            if (_fs != null) throw new InvalidOperationException("GifStreamEncoder already open.");

            if (frameDelayMs < 20) frameDelayMs = 20;
            _delayCs    = frameDelayMs / 10;
            _outputPath = outputPath;
            _frameCount = 0;
            _closed     = false;
            _cancelled  = false;

            // Compute scaled output dimensions
            _outW = sourceWidth;
            _outH = sourceHeight;
            if (_outW > _maxWidth)
            {
                _outH = (int)Math.Round(_outH * ((double)_maxWidth / _outW));
                if (_outH < 1) _outH = 1;
                _outW = _maxWidth;
            }

            // Ensure output directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_fs);

            // GIF89a header
            _bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }); // "GIF89a"

            // Logical Screen Descriptor — no global colour table
            AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_outW);
            AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_outH);
            _bw.Write((byte)0x00); // packed: no GCT
            _bw.Write((byte)0x00); // background colour index
            _bw.Write((byte)0x00); // pixel aspect ratio

            // Netscape loop extension (loop = 0 → infinite)
            AnimatedGifEncoder.WriteNetscapeLoopInternal(_bw, 0);

            Logger.Log($"GifStreamEncoder.Open: {sourceWidth}x{sourceHeight} → {_outW}x{_outH}, " +
                       $"delay={frameDelayMs}ms, path={outputPath}");
        }

        // ------------------------------------------------------------------ //
        //  AddFrame — quantise, dither, LZW-encode, and write one frame
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Encodes one captured frame and appends it to the open GIF stream.
        /// Disposes the source bitmap when done.
        /// </summary>
        public void AddFrame(Bitmap src)
        {
            if (!IsOpen)
            {
                src?.Dispose();
                return;
            }

            Bitmap scaled = AnimatedGifEncoder.ScaleFrameInternal(src, _outW, _outH);
            try
            {
                byte[]  bgra    = AnimatedGifEncoder.ReadBgraInternal(scaled, _outW, _outH);
                Color[] palette = MediaCutQuantizer.Build(bgra, _outW * _outH, 256);
                byte[]  cube    = AnimatedGifEncoder.BuildLookupCubeInternal(palette);
                byte[]  indices = AnimatedGifEncoder.DitherInternal(bgra, _outW, _outH, palette, cube);
                AnimatedGifEncoder.WriteGifFrameInternal(_bw, indices, palette, _outW, _outH, _delayCs);
                _bw.Flush();
                _frameCount++;
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.AddFrame: error on frame {_frameCount} — {ex.Message}");
            }
            finally
            {
                if (!ReferenceEquals(scaled, src)) scaled.Dispose();
                src.Dispose();
            }
        }

        // ------------------------------------------------------------------ //
        //  Close — write GIF trailer and flush
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Finalises the GIF file by writing the trailer byte and closing
        /// the stream.  The file is now a valid animated GIF.
        /// </summary>
        public void Close()
        {
            if (_closed || _cancelled || _fs == null) return;
            _closed = true;
            try
            {
                _bw.Write((byte)0x3B); // GIF trailer
                _bw.Flush();
                Logger.Log($"GifStreamEncoder.Close: {_frameCount} frames written → {_outputPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.Close: error — {ex.Message}");
            }
            finally
            {
                DisposeStream();
            }
        }

        // ------------------------------------------------------------------ //
        //  Cancel — close and delete the partial file
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Aborts the recording.  The partial GIF file is closed and deleted.
        /// </summary>
        public void Cancel()
        {
            if (_closed || _cancelled || _fs == null) return;
            _cancelled = true;
            Logger.Log($"GifStreamEncoder.Cancel: discarding partial file ({_frameCount} frames) → {_outputPath}");
            DisposeStream();
            try
            {
                if (File.Exists(_outputPath))
                    File.Delete(_outputPath);
                Logger.Log("GifStreamEncoder.Cancel: partial file deleted.");
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.Cancel: delete failed — {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ //
        //  Dispose
        // ------------------------------------------------------------------ //

        public void Dispose()
        {
            // If neither Close() nor Cancel() was called, treat as cancel
            // to avoid leaving an unfinished (untrailered) file on disk.
            if (!_closed && !_cancelled)
                Cancel();
            else
                DisposeStream();
        }

        private void DisposeStream()
        {
            try { _bw?.Dispose(); } catch { }
            try { _fs?.Dispose(); } catch { }
            _bw = null;
            _fs = null;
        }
    }

    // ======================================================================
    //  LzwEncoder  —  GIF-compatible LZW (LSB-first, 8-bit minimum code size)
    // ======================================================================
    internal static class LzwEncoder
    {
        public static byte[] Encode(byte[] indices, int minCodeSize)
        {
            if (minCodeSize < 2) minCodeSize = 2;

            int clearCode = 1 << minCodeSize;
            int eoiCode   = clearCode + 1;

            var  output  = new List<byte>(indices.Length / 2 + 64);
            int  bitBuf  = 0, bitLen = 0;
            int  codeSize = minCodeSize + 1;
            int  nextCode = eoiCode + 1;
            int  maxCode  = 1 << codeSize;

            // Hash table: key = (prefix << 8) | byte → code
            // Use open-addressing with a fixed 16-bit range (max codes = 4096)
            const int TABLE_SIZE = 5003; // prime > 4096
            int[] hashKey  = new int[TABLE_SIZE];
            int[] hashCode = new int[TABLE_SIZE];
            for (int i = 0; i < TABLE_SIZE; i++) hashKey[i] = -1;

            void Emit(int code)
            {
                bitBuf |= code << bitLen;
                bitLen += codeSize;
                while (bitLen >= 8)
                {
                    output.Add((byte)(bitBuf & 0xFF));
                    bitBuf >>= 8;
                    bitLen -= 8;
                }
            }

            void ResetTable()
            {
                for (int i = 0; i < TABLE_SIZE; i++) hashKey[i] = -1;
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
                maxCode  = 1 << codeSize;
            }

            Emit(clearCode);

            if (indices.Length == 0) { Emit(eoiCode); FlushBits(output, bitBuf, bitLen); return output.ToArray(); }

            int prefix = indices[0];

            for (int i = 1; i < indices.Length; i++)
            {
                int suffix = indices[i];
                int key    = (prefix << 8) | suffix;
                int slot   = key % TABLE_SIZE;

                // Linear probe
                while (hashKey[slot] != -1 && hashKey[slot] != key)
                    slot = (slot + 1) % TABLE_SIZE;

                if (hashKey[slot] == key)
                {
                    prefix = hashCode[slot];
                }
                else
                {
                    Emit(prefix);

                    if (nextCode < 4096)
                    {
                        hashKey[slot]  = key;
                        hashCode[slot] = nextCode++;
                        if (nextCode > maxCode && codeSize < 12)
                        { codeSize++; maxCode <<= 1; }
                    }
                    else
                    {
                        Emit(clearCode);
                        ResetTable();
                    }

                    prefix = suffix;
                }
            }

            Emit(prefix);
            Emit(eoiCode);
            FlushBits(output, bitBuf, bitLen);
            return output.ToArray();
        }

        private static void FlushBits(List<byte> output, int bitBuf, int bitLen)
        {
            while (bitLen > 0)
            { output.Add((byte)(bitBuf & 0xFF)); bitBuf >>= 8; bitLen -= 8; }
        }
    }
}

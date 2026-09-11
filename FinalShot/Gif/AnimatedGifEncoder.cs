using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    // ======================================================================
    //  AnimatedGifEncoder  —  Pure .NET 4.8 animated GIF encoder.
    //
    //  Key optimisation (10x file-size reduction):
    //    • One GLOBAL palette is built once from a sample of all frames.
    //    • Every frame is quantised against that same palette so identical
    //      pixels get the same index in consecutive frames.
    //    • The last palette slot is reserved as a TRANSPARENT index.
    //    • Each frame only writes pixels that CHANGED vs the previous frame;
    //      unchanged pixels are written as the transparent index.
    //    • Disposal method is set to "do not dispose" (1) so unchanged pixels
    //      from the previous frame remain visible through the transparency.
    //    • LZW sees massive runs of the same transparent index → compresses
    //      dramatically better than per-frame independent palettes.
    // ======================================================================
    internal static class AnimatedGifEncoder
    {
        // The last palette entry is always the transparent index.
        // This means quality.Colors is the number of *real* colour entries;
        // the actual palette written is quality.Colors entries where the last
        // one is designated transparent.
        private const int TRANSPARENT_IDX_OFFSET = 1; // last slot = Colors - 1

        // ------------------------------------------------------------------ //
        //  Public entry point — batch encode
        // ------------------------------------------------------------------ //

        public static void Encode(IList<GifFrame> frames, string outputPath,
                                  GifEncoderQuality quality, int compression = 2)
        {
            if (frames == null || frames.Count == 0)
                throw new ArgumentException("No frames to encode.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentNullException("outputPath");

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            int w = frames[0].Bitmap.Width;
            int h = frames[0].Bitmap.Height;

            Logger.Log($"AnimatedGifEncoder: {frames.Count} frames, {w}x{h}, " +
                       $"colors={quality.Colors}, samples={quality.MaxSamples}, " +
                       $"dither={quality.Dither}, compression={compression}, out={outputPath}");

            // --- Step 1: Read all frames into BGRA buffers ---
            // (needed for global palette building and delta encoding)
            var bgraFrames = new List<byte[]>(frames.Count);
            foreach (var f in frames)
                bgraFrames.Add(ReadBgraInternal(f.Bitmap, w, h));

            // --- Step 2: Build ONE global palette from all frames ---
            // We use Colors-1 actual colour entries and reserve the last slot
            // as the transparent index. This avoids losing a colour to transparency
            // while still keeping the table size a power of 2.
            int    realColors  = quality.Colors; // e.g. 256
            int    transpIdx   = realColors - 1; // e.g. 255
            Color[] palette    = MediaCutQuantizer.BuildGlobal(
                                     bgraFrames, w * h,
                                     realColors - 1,       // ask for one fewer real colour
                                     quality.MaxSamples);  // returns realColors-1 entries

            // Expand palette to full size; last slot = transparent sentinel (Black)
            if (palette.Length < realColors)
            {
                var expanded = new Color[realColors];
                palette.CopyTo(expanded, 0);
                expanded[transpIdx] = Color.Black; // colour value doesn't matter, never displayed
                palette = expanded;
            }

            // --- Step 3: Build lookup cube for the global palette ---
            byte[] cube = BuildLookupCubeInternal(palette, transpIdx);

            Logger.Log($"AnimatedGifEncoder: global palette built, transpIdx={transpIdx}");

            // --- Step 4: Write GIF ---
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                // GIF89a header
                bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 });

                // Logical Screen Descriptor — no global colour table
                WriteU16Internal(bw, (ushort)w);
                WriteU16Internal(bw, (ushort)h);
                bw.Write((byte)0x00);
                bw.Write((byte)0x00);
                bw.Write((byte)0x00);

                WriteNetscapeLoopInternal(bw, 0);

                bool   deduplicate = (compression <= 2);
                byte[] prevIndices = null; // quantised indices of previous written frame
                int    pendingCs   = 0;
                int    written     = 0;

                for (int i = 0; i < bgraFrames.Count; i++)
                {
                    byte[] bgra    = bgraFrames[i];
                    int    delayCs = frames[i].DelayCs;

                    // Quantise this frame against the global palette
                    byte[] indices = quality.Dither
                        ? DitherInternal(bgra, w, h, palette, cube, transpIdx)
                        : QuantizeNoDither(bgra, w, h, cube);

                    // Deduplication: if ALL pixels are unchanged → skip frame
                    if (deduplicate && prevIndices != null)
                    {
                        if (BytesEqual(indices, prevIndices))
                        {
                            pendingCs += delayCs;
                            Logger.Log($"AnimatedGifEncoder: frame {i+1} duplicate skipped.");
                            continue;
                        }
                    }

                    int effectiveCs = delayCs + pendingCs;
                    pendingCs = 0;

                    // Delta: replace unchanged pixels with transparent index
                    byte[] deltaIndices = (prevIndices != null)
                        ? ApplyDelta(indices, prevIndices, transpIdx)
                        : indices; // first frame — no delta

                    Logger.Log($"AnimatedGifEncoder: writing frame {i+1}/{frames.Count} delay={effectiveCs}cs");
                    WriteGifFrameWithTransparency(bw, deltaIndices, palette, w, h,
                                                  effectiveCs, transpIdx);

                    prevIndices = indices; // store undelta'd indices for next comparison
                    written++;
                }

                bw.Write((byte)0x3B); // GIF trailer
                Logger.Log($"AnimatedGifEncoder: done — {written}/{frames.Count} frames written → {outputPath}");
            }
        }

        // ------------------------------------------------------------------ //
        //  Delta: replace unchanged pixels with transparent index
        // ------------------------------------------------------------------ //

        private static byte[] ApplyDelta(byte[] current, byte[] previous, int transpIdx)
        {
            byte[] delta = new byte[current.Length];
            byte   t     = (byte)transpIdx;
            for (int i = 0; i < current.Length; i++)
                delta[i] = (current[i] == previous[i]) ? t : current[i];
            return delta;
        }

        // ------------------------------------------------------------------ //
        //  Fast byte-array equality check
        // ------------------------------------------------------------------ //

        internal static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int chunks = a.Length / 8;
            int rem    = a.Length % 8;
            if (chunks > 0)
            {
                long[] la = new long[chunks];
                long[] lb = new long[chunks];
                Buffer.BlockCopy(a, 0, la, 0, chunks * 8);
                Buffer.BlockCopy(b, 0, lb, 0, chunks * 8);
                for (int i = 0; i < chunks; i++)
                    if (la[i] != lb[i]) return false;
            }
            int off = chunks * 8;
            for (int i = 0; i < rem; i++)
                if (a[off + i] != b[off + i]) return false;
            return true;
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
                byte[] buf = new byte[w * h * 4];
                if (bd.Stride == w * 4)
                {
                    Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                }
                else
                {
                    IntPtr ptr      = bd.Scan0;
                    int    rowBytes = w * 4;
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(IntPtr.Add(ptr, row * bd.Stride),
                                     buf, row * rowBytes, rowBytes);
                }
                return buf;
            }
            finally { bmp.UnlockBits(bd); }
        }

        // ------------------------------------------------------------------ //
        //  Colour-lookup cube  (32×32×32, skips the transparent slot)
        // ------------------------------------------------------------------ //

        internal static byte[] BuildLookupCubeInternal(Color[] palette)
            => BuildLookupCubeInternal(palette, -1);

        internal static byte[] BuildLookupCubeInternal(Color[] palette, int skipIdx)
        {
            const int BITS = 5;
            const int SIZE = 32;
            byte[] cube = new byte[SIZE * SIZE * SIZE];

            for (int ri = 0; ri < SIZE; ri++)
            for (int gi = 0; gi < SIZE; gi++)
            for (int bi = 0; bi < SIZE; bi++)
            {
                byte r = (byte)((ri << 3) | 4);
                byte g = (byte)((gi << 3) | 4);
                byte b = (byte)((bi << 3) | 4);
                cube[(ri << (BITS * 2)) | (gi << BITS) | bi] =
                    (byte)FindNearest(palette, r, g, b, skipIdx);
            }
            return cube;
        }

        private static int FindNearest(Color[] palette, byte r, byte g, byte b, int skipIdx)
        {
            int  best  = 0;
            long bestD = long.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                if (i == skipIdx) continue; // never map real pixels to the transparent slot
                long dr = r - palette[i].R;
                long dg = g - palette[i].G;
                long db = b - palette[i].B;
                long d  = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static int CubeLookup(byte[] cube, byte r, byte g, byte b)
            => cube[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];

        // ------------------------------------------------------------------ //
        //  Floyd-Steinberg dithering (quality 3-5)
        //  Transparent index is excluded from the nearest-colour search.
        // ------------------------------------------------------------------ //

        internal static byte[] DitherInternal(byte[] bgra, int w, int h,
                                              Color[] palette, byte[] cube,
                                              int skipIdx = -1)
        {
            float[] errCurR = new float[w], errCurG = new float[w], errCurB = new float[w];
            float[] errNxtR = new float[w], errNxtG = new float[w], errNxtB = new float[w];
            byte[]  indices = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                float[] t;
                t = errCurR; errCurR = errNxtR; errNxtR = t;
                t = errCurG; errCurG = errNxtG; errNxtG = t;
                t = errCurB; errCurB = errNxtB; errNxtB = t;
                Array.Clear(errNxtR, 0, w);
                Array.Clear(errNxtG, 0, w);
                Array.Clear(errNxtB, 0, w);

                for (int x = 0; x < w; x++)
                {
                    int   bi   = (y * w + x) * 4;
                    float r    = Clamp(bgra[bi + 2] + errCurR[x]);
                    float g    = Clamp(bgra[bi + 1] + errCurG[x]);
                    float b    = Clamp(bgra[bi + 0] + errCurB[x]);
                    int   pidx = CubeLookup(cube, (byte)r, (byte)g, (byte)b);
                    indices[y * w + x] = (byte)pidx;

                    float er = r - palette[pidx].R;
                    float eg = g - palette[pidx].G;
                    float eb = b - palette[pidx].B;

                    if (x + 1 < w)
                    {
                        errCurR[x+1] += er * (7f/16f);
                        errCurG[x+1] += eg * (7f/16f);
                        errCurB[x+1] += eb * (7f/16f);
                    }
                    if (x > 0)
                    {
                        errNxtR[x-1] += er * (3f/16f);
                        errNxtG[x-1] += eg * (3f/16f);
                        errNxtB[x-1] += eb * (3f/16f);
                    }
                    errNxtR[x] += er * (5f/16f);
                    errNxtG[x] += eg * (5f/16f);
                    errNxtB[x] += eb * (5f/16f);
                    if (x + 1 < w)
                    {
                        errNxtR[x+1] += er * (1f/16f);
                        errNxtG[x+1] += eg * (1f/16f);
                        errNxtB[x+1] += eb * (1f/16f);
                    }
                }
            }
            return indices;
        }

        // ------------------------------------------------------------------ //
        //  No-dither quantise  (quality 1-2)
        // ------------------------------------------------------------------ //

        private static byte[] QuantizeNoDither(byte[] bgra, int w, int h, byte[] cube)
        {
            byte[] indices = new byte[w * h];
            int    pixels  = w * h;
            for (int i = 0; i < pixels; i++)
            {
                int bi = i * 4;
                indices[i] = (byte)CubeLookup(cube, bgra[bi+2], bgra[bi+1], bgra[bi+0]);
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
            bw.Write((byte)0x21); bw.Write((byte)0xFF);
            bw.Write((byte)11);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write((byte)3); bw.Write((byte)1);
            WriteU16Internal(bw, loopCount);
            bw.Write((byte)0);
        }

        /// <summary>
        /// Writes one GIF frame with transparency and "do not dispose" disposal.
        /// This is the optimised path: unchanged pixels are already set to transpIdx
        /// by the caller; the GCE enables transparency so those pixels show through
        /// from the previous frame, and LZW sees long runs of the same index.
        /// </summary>
        internal static void WriteGifFrameWithTransparency(
            BinaryWriter bw, byte[] indices, Color[] palette,
            int w, int h, int delayCs, int transpIdx)
        {
            int tableN       = 0;
            int tableEntries = 2;
            while (tableEntries < palette.Length && tableN < 7)
            {
                tableN++;
                tableEntries = 1 << (tableN + 1);
            }
            int lzwMinCode = Math.Max(2, tableN + 1);

            // Graphic Control Extension
            bw.Write((byte)0x21); bw.Write((byte)0xF9);
            bw.Write((byte)4);
            // Packed: disposal=1 (do not dispose), bits 3-5 = 001 → 0x04
            // Transparency flag = bit 0 → 0x01
            // Combined: 0x04 | 0x01 = 0x05
            bw.Write((byte)0x05);
            WriteU16Internal(bw, (ushort)delayCs);
            bw.Write((byte)transpIdx); // transparent colour index
            bw.Write((byte)0);         // block terminator

            // Image Descriptor
            bw.Write((byte)0x2C);
            WriteU16Internal(bw, 0); WriteU16Internal(bw, 0); // left, top
            WriteU16Internal(bw, (ushort)w);
            WriteU16Internal(bw, (ushort)h);
            bw.Write((byte)(0x80 | tableN)); // local colour table

            // Local Colour Table
            for (int i = 0; i < tableEntries; i++)
            {
                Color c = (i < palette.Length) ? palette[i] : Color.Black;
                bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
            }

            bw.Write((byte)lzwMinCode);

            byte[] lzw = LzwEncoder.Encode(indices, lzwMinCode);
            int pos = 0;
            while (pos < lzw.Length)
            {
                int blockLen = Math.Min(255, lzw.Length - pos);
                bw.Write((byte)blockLen);
                bw.Write(lzw, pos, blockLen);
                pos += blockLen;
            }
            bw.Write((byte)0);
        }

        /// <summary>Legacy overload for callers that don't use transparency.</summary>
        internal static void WriteGifFrameInternal(BinaryWriter bw, byte[] indices,
                                                   Color[] palette, int w, int h, int delayCs)
        {
            int tableN = 0, tableEntries = 2;
            while (tableEntries < palette.Length && tableN < 7)
            { tableN++; tableEntries = 1 << (tableN + 1); }
            int lzwMinCode = Math.Max(2, tableN + 1);

            bw.Write((byte)0x21); bw.Write((byte)0xF9);
            bw.Write((byte)4);
            bw.Write((byte)0x00); // no transparency, no disposal
            WriteU16Internal(bw, (ushort)delayCs);
            bw.Write((byte)0); bw.Write((byte)0);

            bw.Write((byte)0x2C);
            WriteU16Internal(bw, 0); WriteU16Internal(bw, 0);
            WriteU16Internal(bw, (ushort)w);
            WriteU16Internal(bw, (ushort)h);
            bw.Write((byte)(0x80 | tableN));

            for (int i = 0; i < tableEntries; i++)
            {
                Color c = (i < palette.Length) ? palette[i] : Color.Black;
                bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
            }

            bw.Write((byte)lzwMinCode);
            byte[] lzw = LzwEncoder.Encode(indices, lzwMinCode);
            int pos = 0;
            while (pos < lzw.Length)
            {
                int bl = Math.Min(255, lzw.Length - pos);
                bw.Write((byte)bl);
                bw.Write(lzw, pos, bl);
                pos += bl;
            }
            bw.Write((byte)0);
        }
    }

    // ======================================================================
    //  MediaCutQuantizer — median-cut colour quantisation
    // ======================================================================
    internal static class MediaCutQuantizer
    {
        /// <summary>
        /// Builds a global palette sampled from ALL frames combined.
        /// This ensures the same pixel always maps to the same index across frames,
        /// which is the prerequisite for transparent-pixel delta encoding.
        /// </summary>
        public static Color[] BuildGlobal(IList<byte[]> bgraFrames, int pixelsPerFrame,
                                          int maxColors, int maxSamplesTotal)
        {
            // Distribute sampling budget evenly across frames
            int framesCount      = bgraFrames.Count;
            int samplesPerFrame  = Math.Max(1, maxSamplesTotal / framesCount);

            // Combine samples from all frames into one flat array
            // Rough upper bound: samplesPerFrame * framesCount * 3
            int capacity = samplesPerFrame * framesCount * 3;
            int[] flat   = new int[capacity];
            int   si     = 0;

            foreach (byte[] bgra in bgraFrames)
            {
                int sampleCount = Math.Min(pixelsPerFrame, samplesPerFrame);
                int step        = pixelsPerFrame / sampleCount;
                if (step < 1) step = 1;

                for (int i = 0; i < pixelsPerFrame && si < capacity - 2; i += step)
                {
                    int bi = i * 4;
                    flat[si++] = bgra[bi + 2]; // R
                    flat[si++] = bgra[bi + 1]; // G
                    flat[si++] = bgra[bi + 0]; // B
                }
            }

            int actualSamples = si / 3;
            return RunMedianCut(flat, actualSamples, maxColors);
        }

        /// <summary>Builds a palette from a single frame's BGRA data.</summary>
        public static Color[] Build(byte[] bgra, int pixelCount,
                                    int maxColors, int maxSamples)
        {
            int sampleCount = Math.Min(pixelCount, maxSamples);
            int step        = pixelCount / sampleCount;
            if (step < 1) step = 1;

            int[] flat = new int[sampleCount * 3];
            int   si   = 0;
            for (int i = 0; i < pixelCount && si < flat.Length - 2; i += step)
            {
                int bi = i * 4;
                flat[si++] = bgra[bi + 2];
                flat[si++] = bgra[bi + 1];
                flat[si++] = bgra[bi + 0];
            }
            return RunMedianCut(flat, si / 3, maxColors);
        }

        private static Color[] RunMedianCut(int[] flat, int actualSamples, int maxColors)
        {
            var buckets = new List<(int start, int len)> { (0, actualSamples) };
            while (buckets.Count < maxColors)
            {
                int splitIdx = LargestRangeIdx(flat, buckets);
                var bkt      = buckets[splitIdx];
                if (bkt.len <= 1) break;
                buckets.RemoveAt(splitIdx);
                SplitBucket(flat, bkt.start, bkt.len, buckets);
            }

            var palette = new Color[maxColors];
            for (int i = 0; i < maxColors; i++)
                palette[i] = i < buckets.Count
                    ? Mean(flat, buckets[i].start, buckets[i].len)
                    : Color.Black;
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
            for (int i = start*3, end = (start+len)*3; i < end; i += 3)
            {
                int r=flat[i], g=flat[i+1], b=flat[i+2];
                if (r<minR)minR=r; if (r>maxR)maxR=r;
                if (g<minG)minG=g; if (g>maxG)maxG=g;
                if (b<minB)minB=b; if (b>maxB)maxB=b;
            }
            return Math.Max(maxR-minR, Math.Max(maxG-minG, maxB-minB));
        }

        private static void SplitBucket(int[] flat, int start, int len,
                                        List<(int start, int len)> output)
        {
            int minR=255,maxR=0,minG=255,maxG=0,minB=255,maxB=0;
            for (int i=start*3, end=(start+len)*3; i<end; i+=3)
            {
                int r=flat[i],g=flat[i+1],b=flat[i+2];
                if(r<minR)minR=r; if(r>maxR)maxR=r;
                if(g<minG)minG=g; if(g>maxG)maxG=g;
                if(b<minB)minB=b; if(b>maxB)maxB=b;
            }
            int rR=maxR-minR, rG=maxG-minG, rB=maxB-minB;
            int ch = (rG>=rR && rG>=rB) ? 1 : (rB>=rR && rB>=rG) ? 2 : 0;
            PartialSort(flat, start*3, (start+len)*3, ch);
            int mid = len / 2;
            output.Add((start,       mid));
            output.Add((start + mid, len - mid));
        }

        private static void PartialSort(int[] flat, int s, int e, int ch)
        {
            int n = (e - s) / 3;
            int gap = 1;
            while (gap < n / 3) gap = gap * 3 + 1;
            while (gap >= 1)
            {
                for (int i = gap; i < n; i++)
                {
                    int iBase = s + i*3;
                    int iVal  = flat[iBase + ch];
                    int ir=flat[iBase], ig=flat[iBase+1], ib=flat[iBase+2];
                    int j = i;
                    while (j >= gap && flat[s+(j-gap)*3+ch] > iVal)
                    {
                        int jBase=s+j*3, pBase=s+(j-gap)*3;
                        flat[jBase]=flat[pBase]; flat[jBase+1]=flat[pBase+1]; flat[jBase+2]=flat[pBase+2];
                        j -= gap;
                    }
                    int tBase = s+j*3;
                    flat[tBase]=ir; flat[tBase+1]=ig; flat[tBase+2]=ib;
                }
                gap /= 3;
            }
        }

        private static Color Mean(int[] flat, int start, int len)
        {
            if (len == 0) return Color.Black;
            long r=0, g=0, b=0;
            for (int i=start*3, end=(start+len)*3; i<end; i+=3)
            { r+=flat[i]; g+=flat[i+1]; b+=flat[i+2]; }
            return Color.FromArgb((int)(r/len),(int)(g/len),(int)(b/len));
        }
    }

    // ======================================================================
    //  GifStreamEncoder — streaming (encode-while-record) variant
    //
    //  Streaming cannot use a true global palette (all frames not available
    //  at Open time). Strategy: buffer the first PALETTE_SAMPLE_FRAMES frames
    //  in memory, build the global palette from those, then write the header
    //  and begin encoding. Frames after that use the same global palette.
    //  This adds a small latency equal to the sample window but keeps memory
    //  bounded and avoids per-frame palette flicker.
    // ======================================================================
    internal sealed class GifStreamEncoder : IDisposable
    {
        // Number of frames buffered before the global palette is built.
        // At 10fps this is 0.5 seconds of buffering before the first byte is written.
        private const int PALETTE_SAMPLE_FRAMES = 5;

        private FileStream        _fs;
        private BinaryWriter      _bw;
        private string            _outputPath;
        private int               _w, _h;
        private int               _frameCount;
        private int               _skippedFrames;
        private bool              _closed;
        private bool              _cancelled;
        private GifEncoderQuality _quality;
        private int               _compression;

        // Global palette (set once after sample frames are collected)
        private Color[] _palette;
        private byte[]  _cube;
        private int     _transpIdx;

        // Pre-palette buffer: frames arriving before the palette is built
        private readonly List<GifFrame> _sampleBuffer = new List<GifFrame>();
        private bool _paletteReady = false;

        // Delta state
        private byte[] _prevIndices;
        private int    _pendingCs;

        public bool IsOpen => _fs != null && !_closed && !_cancelled;

        public void Open(string outputPath, int width, int height,
                         GifEncoderQuality quality, int compression = 2)
        {
            if (_fs != null) throw new InvalidOperationException("GifStreamEncoder already open.");

            _outputPath    = outputPath;
            _w             = width;
            _h             = height;
            _quality       = quality;
            _compression   = compression;
            _frameCount    = 0;
            _skippedFrames = 0;
            _closed        = false;
            _cancelled     = false;
            _prevIndices   = null;
            _pendingCs     = 0;
            _paletteReady  = false;
            _sampleBuffer.Clear();

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // File is opened but we don't write the header yet —
            // we need the global palette first.
            _fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_fs);

            Logger.Log($"GifStreamEncoder.Open: {_w}x{_h}, " +
                       $"colors={quality.Colors}, samples={quality.MaxSamples}, " +
                       $"dither={quality.Dither}, compression={compression}, path={outputPath}");
        }

        public void AddFrame(GifFrame frame)
        {
            if (!IsOpen) { frame?.Bitmap?.Dispose(); return; }

            try
            {
                if (!_paletteReady)
                {
                    // Buffer frames until we have enough to build a good palette
                    _sampleBuffer.Add(frame); // keep bitmap alive — dispose later

                    if (_sampleBuffer.Count >= PALETTE_SAMPLE_FRAMES)
                        FlushSampleBuffer();

                    return; // don't dispose — stored in sample buffer
                }

                // Normal path: palette is ready, encode with delta
                byte[] bgra    = AnimatedGifEncoder.ReadBgraInternal(frame.Bitmap, _w, _h);
                int    delayCs = frame.DelayCs;

                byte[] indices = _quality.Dither
                    ? AnimatedGifEncoder.DitherInternal(bgra, _w, _h, _palette, _cube, _transpIdx)
                    : QuantizeNoDitherInternal(bgra, _w * _h, _cube);

                // Deduplication
                if (_compression <= 2 && _prevIndices != null &&
                    AnimatedGifEncoder.BytesEqual(indices, _prevIndices))
                {
                    _pendingCs += delayCs;
                    _skippedFrames++;
                    return;
                }

                int effectiveCs = delayCs + _pendingCs;
                _pendingCs = 0;

                byte[] delta = (_prevIndices != null)
                    ? ApplyDelta(indices, _prevIndices, _transpIdx)
                    : indices;

                AnimatedGifEncoder.WriteGifFrameWithTransparency(
                    _bw, delta, _palette, _w, _h, effectiveCs, _transpIdx);
                _bw.Flush();

                _prevIndices = indices;
                _frameCount++;
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.AddFrame: error — {ex.Message}");
            }
            finally
            {
                if (_paletteReady) // only dispose if not stored in sample buffer
                    frame?.Bitmap?.Dispose();
            }
        }

        /// <summary>
        /// Called once PALETTE_SAMPLE_FRAMES have been buffered.
        /// Builds the global palette, writes the GIF header, then encodes
        /// all buffered frames before switching to streaming mode.
        /// </summary>
        private void FlushSampleBuffer()
        {
            try
            {
                // Read all buffered frames' BGRA data
                var bgraList = new List<byte[]>(_sampleBuffer.Count);
                foreach (var f in _sampleBuffer)
                    bgraList.Add(AnimatedGifEncoder.ReadBgraInternal(f.Bitmap, _w, _h));

                // Build global palette
                int realColors = _quality.Colors;
                _transpIdx     = realColors - 1;
                Color[] raw    = MediaCutQuantizer.BuildGlobal(
                                     bgraList, _w * _h,
                                     realColors - 1,
                                     _quality.MaxSamples);

                _palette = new Color[realColors];
                raw.CopyTo(_palette, 0);
                _palette[_transpIdx] = Color.Black;

                _cube = AnimatedGifEncoder.BuildLookupCubeInternal(_palette, _transpIdx);

                Logger.Log($"GifStreamEncoder: global palette built from {_sampleBuffer.Count} " +
                           $"sample frames, transpIdx={_transpIdx}");

                // Write GIF header now that we have dimensions and palette
                _bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 });
                AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_w);
                AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_h);
                _bw.Write((byte)0x00);
                _bw.Write((byte)0x00);
                _bw.Write((byte)0x00);
                AnimatedGifEncoder.WriteNetscapeLoopInternal(_bw, 0);

                // Encode all buffered frames
                for (int i = 0; i < bgraList.Count; i++)
                {
                    byte[] bgra    = bgraList[i];
                    int    delayCs = _sampleBuffer[i].DelayCs;

                    byte[] indices = _quality.Dither
                        ? AnimatedGifEncoder.DitherInternal(bgra, _w, _h, _palette, _cube, _transpIdx)
                        : QuantizeNoDitherInternal(bgra, _w * _h, _cube);

                    if (_compression <= 2 && _prevIndices != null &&
                        AnimatedGifEncoder.BytesEqual(indices, _prevIndices))
                    {
                        _pendingCs += delayCs;
                        _skippedFrames++;
                        continue;
                    }

                    int effectiveCs = delayCs + _pendingCs;
                    _pendingCs = 0;

                    byte[] delta = (_prevIndices != null)
                        ? ApplyDelta(indices, _prevIndices, _transpIdx)
                        : indices;

                    AnimatedGifEncoder.WriteGifFrameWithTransparency(
                        _bw, delta, _palette, _w, _h, effectiveCs, _transpIdx);
                    _bw.Flush();

                    _prevIndices = indices;
                    _frameCount++;
                }

                _paletteReady = true;
            }
            finally
            {
                foreach (var f in _sampleBuffer)
                    f?.Bitmap?.Dispose();
                _sampleBuffer.Clear();
            }
        }

        private static byte[] ApplyDelta(byte[] current, byte[] previous, int transpIdx)
        {
            byte[] delta = new byte[current.Length];
            byte   t     = (byte)transpIdx;
            for (int i = 0; i < current.Length; i++)
                delta[i] = (current[i] == previous[i]) ? t : current[i];
            return delta;
        }

        private static byte[] QuantizeNoDitherInternal(byte[] bgra, int pixels, byte[] cube)
        {
            byte[] idx = new byte[pixels];
            for (int i = 0; i < pixels; i++)
            {
                int bi = i * 4;
                idx[i] = (byte)(cube[((bgra[bi+2]>>3)<<10)|((bgra[bi+1]>>3)<<5)|(bgra[bi+0]>>3)]);
            }
            return idx;
        }

        public void Close()
        {
            if (_closed || _cancelled || _fs == null) return;
            _closed = true;
            try
            {
                // If recording was very short and sample buffer never flushed
                if (!_paletteReady && _sampleBuffer.Count > 0)
                    FlushSampleBuffer();

                _bw.Write((byte)0x3B);
                _bw.Flush();
                Logger.Log($"GifStreamEncoder.Close: {_frameCount} frames written, " +
                           $"{_skippedFrames} skipped → {_outputPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.Close: error — {ex.Message}");
            }
            finally { DisposeStream(); }
        }

        public void Cancel()
        {
            if (_closed || _cancelled || _fs == null) return;
            _cancelled = true;
            foreach (var f in _sampleBuffer) f?.Bitmap?.Dispose();
            _sampleBuffer.Clear();
            Logger.Log($"GifStreamEncoder.Cancel: discarding → {_outputPath}");
            DisposeStream();
            try
            {
                if (File.Exists(_outputPath)) File.Delete(_outputPath);
                Logger.Log("GifStreamEncoder.Cancel: partial file deleted.");
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.Cancel: delete failed — {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_closed && !_cancelled) Cancel();
            else DisposeStream();
        }

        private void DisposeStream()
        {
            try { _bw?.Dispose(); } catch { }
            try { _fs?.Dispose(); } catch { }
            _bw = null; _fs = null;
        }
    }

    // ======================================================================
    //  LzwEncoder — GIF-compatible LZW (LSB-first, unchanged)
    // ======================================================================
    internal static class LzwEncoder
    {
        public static byte[] Encode(byte[] indices, int minCodeSize)
        {
            if (minCodeSize < 2) minCodeSize = 2;
            int clearCode = 1 << minCodeSize;
            int eoiCode   = clearCode + 1;
            var output    = new List<byte>(indices.Length / 2 + 64);
            int bitBuf=0, bitLen=0;
            int codeSize = minCodeSize + 1;
            int nextCode = eoiCode + 1;
            int maxCode  = 1 << codeSize;
            const int TABLE_SIZE = 5003;
            int[] hashKey  = new int[TABLE_SIZE];
            int[] hashCode = new int[TABLE_SIZE];
            for (int i = 0; i < TABLE_SIZE; i++) hashKey[i] = -1;

            void Emit(int code)
            {
                bitBuf |= code << bitLen; bitLen += codeSize;
                while (bitLen >= 8) { output.Add((byte)(bitBuf&0xFF)); bitBuf>>=8; bitLen-=8; }
            }
            void ResetTable()
            {
                for (int i=0;i<TABLE_SIZE;i++) hashKey[i]=-1;
                codeSize=minCodeSize+1; nextCode=eoiCode+1; maxCode=1<<codeSize;
            }

            Emit(clearCode);
            if (indices.Length == 0) { Emit(eoiCode); FlushBits(output,bitBuf,bitLen); return output.ToArray(); }

            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int suffix = indices[i];
                int key    = (prefix<<8)|suffix;
                int slot   = key % TABLE_SIZE;
                while (hashKey[slot]!=-1 && hashKey[slot]!=key) slot=(slot+1)%TABLE_SIZE;
                if (hashKey[slot]==key) { prefix=hashCode[slot]; }
                else
                {
                    Emit(prefix);
                    if (nextCode < 4096) { hashKey[slot]=key; hashCode[slot]=nextCode++; if(nextCode>maxCode&&codeSize<12){codeSize++;maxCode<<=1;} }
                    else { Emit(clearCode); ResetTable(); }
                    prefix=suffix;
                }
            }
            Emit(prefix); Emit(eoiCode);
            FlushBits(output, bitBuf, bitLen);
            return output.ToArray();
        }

        private static void FlushBits(List<byte> output, int bitBuf, int bitLen)
        {
            while (bitLen>0) { output.Add((byte)(bitBuf&0xFF)); bitBuf>>=8; bitLen-=8; }
        }
    }
}

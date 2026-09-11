using System;
using System.Collections.Generic;
using System.Drawing;
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
    //    1. Median-cut quantisation — builds an optimal palette from actual pixels.
    //    2. Build a 32×32×32 colour-lookup-cube for O(1) nearest-colour lookup.
    //    3. Floyd-Steinberg error-diffusion dithering (optional, controlled by quality).
    //    4. GIF-compatible LZW compression.
    //    5. Write raw GIF89a bytes directly.
    // ======================================================================
    internal static class AnimatedGifEncoder
    {
        // ------------------------------------------------------------------ //
        //  Public entry point — batch encode
        // ------------------------------------------------------------------ //

        /// <summary>Encodes a list of frames into an animated GIF at the given quality.</summary>
        public static void Encode(IList<GifFrame> frames, string outputPath,
                                  GifEncoderQuality quality)
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
                       $"dither={quality.Dither}, out={outputPath}");

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                // GIF89a header
                bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 });

                // Logical Screen Descriptor — no global colour table
                WriteU16Internal(bw, (ushort)w);
                WriteU16Internal(bw, (ushort)h);
                bw.Write((byte)0x00); // no GCT
                bw.Write((byte)0x00);
                bw.Write((byte)0x00);

                WriteNetscapeLoopInternal(bw, 0);

                for (int i = 0; i < frames.Count; i++)
                {
                    Logger.Log($"AnimatedGifEncoder: encoding frame {i + 1}/{frames.Count}");
                    EncodeFrame(bw, frames[i].Bitmap, frames[i].DelayCs, quality);
                }

                bw.Write((byte)0x3B); // GIF trailer
            }

            Logger.Log($"AnimatedGifEncoder: done — {outputPath}");
        }

        // ------------------------------------------------------------------ //
        //  Per-frame encode
        // ------------------------------------------------------------------ //

        private static void EncodeFrame(BinaryWriter bw, Bitmap src, int delayCs,
                                        GifEncoderQuality quality)
        {
            int w = src.Width;
            int h = src.Height;

            byte[]  bgra    = ReadBgraInternal(src, w, h);
            Color[] palette = MediaCutQuantizer.Build(bgra, w * h,
                                                      quality.Colors,
                                                      quality.MaxSamples);
            byte[]  cube    = BuildLookupCubeInternal(palette);
            byte[]  indices = quality.Dither
                ? DitherInternal(bgra, w, h, palette, cube)
                : QuantizeNoDither(bgra, w, h, cube);

            WriteGifFrameInternal(bw, indices, palette, w, h, delayCs);
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
        //  Colour-lookup cube  (32 × 32 × 32 = 32 768 entries)
        // ------------------------------------------------------------------ //

        internal static byte[] BuildLookupCubeInternal(Color[] palette)
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
                    (byte)FindNearest(palette, r, g, b);
            }
            return cube;
        }

        private static int FindNearest(Color[] palette, byte r, byte g, byte b)
        {
            int  best  = 0;
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

        private static int CubeLookup(byte[] cube, byte r, byte g, byte b)
            => cube[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];

        // ------------------------------------------------------------------ //
        //  Floyd-Steinberg dithering  (quality 4-5)
        // ------------------------------------------------------------------ //

        internal static byte[] DitherInternal(byte[] bgra, int w, int h,
                                              Color[] palette, byte[] cube)
        {
            float[] errCurR = new float[w], errCurG = new float[w], errCurB = new float[w];
            float[] errNxtR = new float[w], errNxtG = new float[w], errNxtB = new float[w];
            byte[]  indices = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                // Swap error rows
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
        //  No-dither quantise  (quality 1-3, faster)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Maps each pixel to the nearest palette entry using the lookup cube only —
        /// no error diffusion. Faster but produces more visible banding on gradients.
        /// </summary>
        private static byte[] QuantizeNoDither(byte[] bgra, int w, int h, byte[] cube)
        {
            byte[] indices = new byte[w * h];
            int    pixels  = w * h;
            for (int i = 0; i < pixels; i++)
            {
                int bi = i * 4;
                indices[i] = (byte)CubeLookup(cube,
                                              bgra[bi + 2],   // R
                                              bgra[bi + 1],   // G
                                              bgra[bi + 0]);  // B
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
        /// Writes one GIF frame with a local colour table sized to match the palette.
        /// The table size, packed byte, and LZW minimum code size are all derived
        /// from <c>palette.Length</c> so reduced palettes (e.g. 64 colours) produce
        /// correctly-formed, smaller GIF blocks.
        /// </summary>
        internal static void WriteGifFrameInternal(BinaryWriter bw, byte[] indices,
                                                   Color[] palette, int w, int h, int delayCs)
        {
            // Determine the smallest power-of-two colour table that fits the palette.
            // GIF spec: color table size N means 2^(N+1) entries; N is 0-7 (2 to 256 entries).
            int paletteSize  = palette.Length;          // e.g. 64, 128, 256
            int tableN       = 0;                       // color table size field (0..7)
            int tableEntries = 2;                       // actual entries written = 2^(tableN+1)
            while (tableEntries < paletteSize && tableN < 7)
            {
                tableN++;
                tableEntries = 1 << (tableN + 1);
            }
            int lzwMinCode = tableN + 1;                // LZW minimum code size
            if (lzwMinCode < 2) lzwMinCode = 2;         // GIF spec minimum

            // Graphic Control Extension
            bw.Write((byte)0x21); bw.Write((byte)0xF9);
            bw.Write((byte)4);
            bw.Write((byte)0x00);                       // dispose=0, no transparency
            WriteU16Internal(bw, (ushort)delayCs);
            bw.Write((byte)0);                          // transparent colour index (unused)
            bw.Write((byte)0);                          // block terminator

            // Image Descriptor
            bw.Write((byte)0x2C);
            WriteU16Internal(bw, 0); WriteU16Internal(bw, 0); // left, top
            WriteU16Internal(bw, (ushort)w);
            WriteU16Internal(bw, (ushort)h);
            // Packed byte: Local CT flag=1, not interlaced, size field = tableN
            bw.Write((byte)(0x80 | tableN));

            // Local Colour Table — exactly tableEntries × 3 bytes
            for (int i = 0; i < tableEntries; i++)
            {
                Color c = (i < palette.Length) ? palette[i] : Color.Black;
                bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
            }

            // LZW minimum code size
            bw.Write((byte)lzwMinCode);

            // LZW-encode and write in 255-byte sub-blocks
            byte[] lzw = LzwEncoder.Encode(indices, lzwMinCode);
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
    //  MediaCutQuantizer — median-cut colour quantisation
    // ======================================================================
    internal static class MediaCutQuantizer
    {
        /// <summary>
        /// Builds an optimal palette of <paramref name="maxColors"/> colours.
        /// </summary>
        /// <param name="bgra">Raw BGRA pixel data.</param>
        /// <param name="pixelCount">Total number of pixels.</param>
        /// <param name="maxColors">Palette size (should be a power of 2, 2–256).</param>
        /// <param name="maxSamples">Maximum pixels to sample for accuracy vs speed.</param>
        public static Color[] Build(byte[] bgra, int pixelCount,
                                    int maxColors, int maxSamples)
        {
            int sampleCount = Math.Min(pixelCount, maxSamples);
            int step        = pixelCount / sampleCount;
            if (step < 1) step = 1;

            int[] flat = new int[sampleCount * 3];
            int   si   = 0;
            for (int i = 0; i < pixelCount && si < sampleCount * 3; i += step)
            {
                int bi = i * 4;
                flat[si++] = bgra[bi + 2]; // R
                flat[si++] = bgra[bi + 1]; // G
                flat[si++] = bgra[bi + 0]; // B
            }
            int actualSamples = si / 3;

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
                palette[i] = i < buckets.Count ? Mean(flat, buckets[i].start, buckets[i].len)
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
            for (int i = start * 3, end = (start + len) * 3; i < end; i += 3)
            {
                int r=flat[i], g=flat[i+1], b=flat[i+2];
                if (r < minR) minR=r; if (r > maxR) maxR=r;
                if (g < minG) minG=g; if (g > maxG) maxG=g;
                if (b < minB) minB=b; if (b > maxB) maxB=b;
            }
            return Math.Max(maxR-minR, Math.Max(maxG-minG, maxB-minB));
        }

        private static void SplitBucket(int[] flat, int start, int len,
                                        List<(int start, int len)> output)
        {
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
            PartialSort(flat, start*3, (start+len)*3, ch, len);
            int mid = len / 2;
            output.Add((start,       mid));
            output.Add((start + mid, len - mid));
        }

        private static void PartialSort(int[] flat, int s, int e, int ch, int len)
        {
            int n   = (e - s) / 3;
            int gap = 1;
            while (gap < n / 3) gap = gap * 3 + 1;
            while (gap >= 1)
            {
                for (int i = gap; i < n; i++)
                {
                    int iBase = s + i * 3;
                    int iVal  = flat[iBase + ch];
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
            long r=0, g=0, b=0;
            for (int i = start*3, end = (start+len)*3; i < end; i += 3)
            { r += flat[i]; g += flat[i+1]; b += flat[i+2]; }
            return Color.FromArgb((int)(r/len), (int)(g/len), (int)(b/len));
        }
    }

    // ======================================================================
    //  GifStreamEncoder — streaming (encode-while-record) variant
    // ======================================================================
    internal sealed class GifStreamEncoder : IDisposable
    {
        private FileStream        _fs;
        private BinaryWriter      _bw;
        private string            _outputPath;
        private int               _w, _h;
        private int               _frameCount;
        private bool              _closed;
        private bool              _cancelled;
        private GifEncoderQuality _quality;

        public bool IsOpen => _fs != null && !_closed && !_cancelled;

        public void Open(string outputPath, int width, int height,
                         GifEncoderQuality quality)
        {
            if (_fs != null) throw new InvalidOperationException("GifStreamEncoder already open.");

            _outputPath = outputPath;
            _w          = width;
            _h          = height;
            _quality    = quality;
            _frameCount = 0;
            _closed     = false;
            _cancelled  = false;

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_fs);

            _bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 });
            AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_w);
            AnimatedGifEncoder.WriteU16Internal(_bw, (ushort)_h);
            _bw.Write((byte)0x00);
            _bw.Write((byte)0x00);
            _bw.Write((byte)0x00);
            AnimatedGifEncoder.WriteNetscapeLoopInternal(_bw, 0);

            Logger.Log($"GifStreamEncoder.Open: {_w}x{_h}, " +
                       $"colors={quality.Colors}, samples={quality.MaxSamples}, " +
                       $"dither={quality.Dither}, path={outputPath}");
        }

        public void AddFrame(GifFrame frame)
        {
            if (!IsOpen) { frame?.Bitmap?.Dispose(); return; }

            try
            {
                Bitmap  src     = frame.Bitmap;
                byte[]  bgra    = AnimatedGifEncoder.ReadBgraInternal(src, _w, _h);
                Color[] palette = MediaCutQuantizer.Build(bgra, _w * _h,
                                                          _quality.Colors,
                                                          _quality.MaxSamples);
                byte[]  cube    = AnimatedGifEncoder.BuildLookupCubeInternal(palette);
                byte[]  indices = _quality.Dither
                    ? AnimatedGifEncoder.DitherInternal(bgra, _w, _h, palette, cube)
                    : QuantizeNoDitherInternal(bgra, _w * _h, cube);

                AnimatedGifEncoder.WriteGifFrameInternal(_bw, indices, palette,
                                                         _w, _h, frame.DelayCs);
                _bw.Flush();
                _frameCount++;
            }
            catch (Exception ex)
            {
                Logger.Log($"GifStreamEncoder.AddFrame: error on frame {_frameCount} — {ex.Message}");
            }
            finally
            {
                frame?.Bitmap?.Dispose();
            }
        }

        // Inline no-dither quantise used by AddFrame to avoid a static call.
        private static byte[] QuantizeNoDitherInternal(byte[] bgra, int pixels, byte[] cube)
        {
            byte[] idx = new byte[pixels];
            for (int i = 0; i < pixels; i++)
            {
                int bi = i * 4;
                idx[i] = (byte)(cube[((bgra[bi+2] >> 3) << 10) |
                                     ((bgra[bi+1] >> 3) << 5)  |
                                      (bgra[bi+0] >> 3)]);
            }
            return idx;
        }

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
            finally { DisposeStream(); }
        }

        public void Cancel()
        {
            if (_closed || _cancelled || _fs == null) return;
            _cancelled = true;
            Logger.Log($"GifStreamEncoder.Cancel: discarding partial file ({_frameCount} frames) → {_outputPath}");
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
            _bw = null;
            _fs = null;
        }
    }

    // ======================================================================
    //  LzwEncoder — GIF-compatible LZW (LSB-first)
    // ======================================================================
    internal static class LzwEncoder
    {
        public static byte[] Encode(byte[] indices, int minCodeSize)
        {
            if (minCodeSize < 2) minCodeSize = 2;

            int clearCode = 1 << minCodeSize;
            int eoiCode   = clearCode + 1;

            var output   = new List<byte>(indices.Length / 2 + 64);
            int bitBuf   = 0, bitLen = 0;
            int codeSize = minCodeSize + 1;
            int nextCode = eoiCode + 1;
            int maxCode  = 1 << codeSize;

            const int TABLE_SIZE = 5003;
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
                    bitLen  -= 8;
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

            if (indices.Length == 0)
            {
                Emit(eoiCode);
                FlushBits(output, bitBuf, bitLen);
                return output.ToArray();
            }

            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int suffix = indices[i];
                int key    = (prefix << 8) | suffix;
                int slot   = key % TABLE_SIZE;

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

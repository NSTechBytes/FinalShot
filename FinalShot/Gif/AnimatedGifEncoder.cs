/*
 * Copyright (c) 2025 nstechbytes
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
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    //  AnimatedGifEncoder  --  Pure .NET 4.8 animated GIF encoder.
    //
    //  Key optimisations (file-size reduction, same resolution):
    //    - Global palette sampled evenly across ALL frames (FFmpeg palettegen-style).
    //    - Bayer ordered dither on every pixel (smooth gradients; frame-stable for deltas).
    //    - Dirty horizontal bands instead of one huge AABB when updates are distant.
    //    - Exact + near-duplicate frame coalesce (sub-pixel shimmer).
    //    - Capture-time still-frame merge (identical bitmaps fold into next delay).
    //    - Disposal=1 + transparent index for unchanged pixels (gifsicle-style).
    internal static class AnimatedGifEncoder
    {
        // The last palette entry is always the transparent index.
        // This means quality.Colors is the number of *real* colour entries;
        // the actual palette written is quality.Colors entries where the last
        // one is designated transparent.
        private const int TRANSPARENT_IDX_OFFSET = 1; // last slot = Colors - 1

        //  Public entry point - two-pass streaming encode from disk cache
        //
        //  Pass 1 - palette sampling:
        //    Streams frames from disk one at a time, reads pixels into a
        //    temporary BGRA buffer, samples them for the global palette, then
        //    disposes the buffer immediately.  Peak memory = one BGRA frame.
        //
        //  Pass 2 - quantise + write:
        //    Streams frames from disk again one at a time, quantises each
        //    against the global palette, writes the GIF frame, then disposes
        //    the BGRA buffer.  Peak memory = one BGRA frame + one index array
        //    (previous frame) for delta encoding.
        //
        //  Total memory at any point ~= 2 x frameBytes regardless of recording
        //  length - identical to ShareX HardDiskCache approach.

        public static void Encode(GifDiskCache cache, string outputPath,
                                  int quality = 100, int compression = 50)
        {
            if (cache == null || cache.Count == 0)
                throw new ArgumentException("No frames to encode.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentNullException("outputPath");

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Read dimensions from first frame only
            int w = 0, h = 0;
            foreach (GifFrame first in cache.GetFrameEnumerator())
            {
                w = first.Bitmap.Width;
                h = first.Bitmap.Height;
                first.Bitmap.Dispose();
                break;
            }
            if (w == 0 || h == 0)
                throw new InvalidOperationException("Could not determine frame dimensions.");

            ResolveGifQuality(quality, out int realColors, out int bayerStrength, out bool useDither);
            ResolveGifCompression(compression,
                out double nearDupeFraction, out int bandAreaPercent, out int maxBands);

            const int MAX_SAMPLES = 500_000;

            Logger.Log($"AnimatedGifEncoder: {cache.Count} frames, {w}x{h}, " +
                       $"quality={quality}, compression={compression}, colors={realColors}, dither=" +
                       (useDither ? ("bayer/" + bayerStrength) : "off") +
                       $", nearDupe={nearDupeFraction:P3}, out={outputPath}");

            // -- Pass 1: build global palette (streaming, even across all frames)
            int     transpIdx  = realColors - 1;
            Color[] palette    = MediaCutQuantizer.BuildGlobalStreaming(
                                     cache.GetFrameEnumerator(),
                                     w, h,
                                     realColors - 1,
                                     MAX_SAMPLES,
                                     cache.Count);

            if (palette.Length < realColors)
            {
                var expanded = new Color[realColors];
                palette.CopyTo(expanded, 0);
                expanded[transpIdx] = Color.Black;
                palette = expanded;
            }

            byte[] cube = BuildLookupCubeInternal(palette, transpIdx);
            Logger.Log($"AnimatedGifEncoder: global palette built, transpIdx={transpIdx}");

            // -- Pass 2: quantise + write GIF (streaming) ------------------
            int tableN       = 0;
            int tableEntries = 2;
            while (tableEntries < palette.Length && tableN < 7)
            { tableN++; tableEntries = 1 << (tableN + 1); }
            int lzwMinCode = Math.Max(2, tableN + 1);

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                // GIF89a header
                bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 });

                // Logical Screen Descriptor with Global Color Table
                WriteU16Internal(bw, (ushort)w);
                WriteU16Internal(bw, (ushort)h);
                bw.Write((byte)(0x80 | (tableN << 4) | tableN));
                bw.Write((byte)0x00);
                bw.Write((byte)0x00);

                for (int i = 0; i < tableEntries; i++)
                {
                    Color c = (i < palette.Length) ? palette[i] : Color.Black;
                    bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
                }

                WriteNetscapeLoopInternal(bw, 0);

                byte[] prevIndices = null;
                int    pendingCs   = 0;
                int    written     = 0;
                int    frameNum    = 0;
                int    nearDupes   = 0;
                int    multiBandFrames = 0;
                long   nearDupeThreshold = nearDupeFraction <= 0
                    ? 0
                    : Math.Max(4L, (long)(w * (long)h * nearDupeFraction));

                foreach (GifFrame frame in cache.GetFrameEnumerator())
                {
                    frameNum++;
                    int delayCs = frame.DelayCs;

                    byte[] bgra = ReadBgraInternal(frame.Bitmap, w, h);
                    frame.Bitmap.Dispose();

                    byte[] indices = useDither
                        ? DitherInternal(bgra, w, h, palette, cube, transpIdx, bayerStrength)
                        : QuantizeNoDither(bgra, w, h, cube);
                    bgra = null;

                    if (prevIndices != null)
                    {
                        // One pass: exact dupe + near-dupe (was BytesEqual then CountChangedPixels)
                        long changedCount = CountChangedPixels(indices, prevIndices);
                        if (changedCount == 0)
                        {
                            pendingCs += delayCs;
                            Logger.Log($"AnimatedGifEncoder: frame {frameNum} duplicate skipped.");
                            continue;
                        }
                        if (nearDupeThreshold > 0 && changedCount < nearDupeThreshold)
                        {
                            pendingCs += delayCs;
                            nearDupes++;
                            Logger.Log($"AnimatedGifEncoder: frame {frameNum} near-duplicate " +
                                       $"({changedCount} px) skipped.");
                            continue;
                        }
                    }

                    int effectiveCs = delayCs + pendingCs;
                    pendingCs = 0;

                    if (prevIndices != null)
                    {
                        List<Rectangle> bands = ComputeDirtyBands(
                            indices, prevIndices, w, h, bandAreaPercent, maxBands);
                        if (bands == null || bands.Count == 0)
                        {
                            pendingCs += effectiveCs;
                            continue;
                        }

                        byte[] deltaFull = ApplyDelta(indices, prevIndices, transpIdx);
                        if (bands.Count > 1)
                            multiBandFrames++;

                        Logger.Log($"AnimatedGifEncoder: writing frame {frameNum}/{cache.Count} " +
                                   $"delay={effectiveCs}cs bands={bands.Count}");

                        for (int b = 0; b < bands.Count; b++)
                        {
                            Rectangle cr = bands[b];
                            int bandDelay = (b == bands.Count - 1) ? effectiveCs : 0;
                            byte[] sub = ExtractSubRect(deltaFull, w, cr);
                            WriteGifFrameWithTransparency(bw, sub,
                                cr.Left, cr.Top, cr.Width, cr.Height,
                                bandDelay, transpIdx, lzwMinCode);
                        }
                    }
                    else
                    {
                        Logger.Log($"AnimatedGifEncoder: writing frame {frameNum}/{cache.Count} " +
                                   $"delay={effectiveCs}cs (first frame, full canvas)");
                        WriteGifFrameWithTransparency(bw, indices,
                            0, 0, w, h, effectiveCs, transpIdx, lzwMinCode);
                    }

                    prevIndices = indices;
                    written++;
                    GifEncodingWindow.UpdateProgress(written, cache.Count);
                }

                // Trailing coalesce delay: GIF delay is per-frame GCE, so append a
                // 1x1 transparent keep-previous patch carrying leftover pendingCs.
                if (pendingCs > 0 && prevIndices != null && transpIdx >= 0)
                {
                    byte[] one = { (byte)transpIdx };
                    WriteGifFrameWithTransparency(bw, one,
                        0, 0, 1, 1, pendingCs, transpIdx, lzwMinCode);
                    written++;
                    Logger.Log("AnimatedGifEncoder: applied trailing pending delay "
                        + pendingCs + "cs as transparent keep frame.");
                    pendingCs = 0;
                }

                bw.Write((byte)0x3B); // GIF trailer
                Logger.Log($"AnimatedGifEncoder: done - {written}/{cache.Count} frames written, " +
                           $"nearDupes={nearDupes}, multiBandFrames={multiBandFrames} -> {outputPath}");
            }
        }

        //  Delta: replace unchanged pixels with transparent index

        private static byte[] ApplyDelta(byte[] current, byte[] previous, int transpIdx)
        {
            byte[] delta = new byte[current.Length];
            byte   t     = (byte)transpIdx;
            for (int i = 0; i < current.Length; i++)
                delta[i] = (current[i] == previous[i]) ? t : current[i];
            return delta;
        }

        //  Changed-rect helpers

        private static long CountChangedPixels(byte[] current, byte[] previous)
        {
            long n = 0;
            int len = current.Length;
            for (int i = 0; i < len; i++)
                if (current[i] != previous[i]) n++;
            return n;
        }

        // Splits dirty pixels into horizontal bands so distant updates (e.g. cursor
        // + toast) do not force one huge AABB. Returns null/empty if nothing changed.
        // Falls back to a single AABB when banding would not shrink encoded area.
        // bandAreaPercent / maxBands come from GifCompression.
        private static List<Rectangle> ComputeDirtyBands(byte[] current, byte[] previous,
                                                         int w, int h,
                                                         int bandAreaPercent = 85,
                                                         int maxBands = 6)
        {
            const int MergeGapRows = 8;
            if (bandAreaPercent < 50) bandAreaPercent = 50;
            if (bandAreaPercent > 100) bandAreaPercent = 100;
            if (maxBands < 1) maxBands = 1;

            var dirtyRows = new bool[h];
            int dirtyRowCount = 0;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (current[rowBase + x] != previous[rowBase + x])
                    {
                        dirtyRows[y] = true;
                        dirtyRowCount++;
                        break;
                    }
                }
            }

            if (dirtyRowCount == 0)
                return null;

            var rawBands = new List<(int y0, int y1)>();
            int i = 0;
            while (i < h)
            {
                while (i < h && !dirtyRows[i]) i++;
                if (i >= h) break;
                int y0 = i;
                while (i < h && dirtyRows[i]) i++;
                int y1 = i - 1;

                if (rawBands.Count > 0 && y0 - rawBands[rawBands.Count - 1].y1 - 1 <= MergeGapRows)
                {
                    var last = rawBands[rawBands.Count - 1];
                    rawBands[rawBands.Count - 1] = (last.y0, y1);
                }
                else
                {
                    rawBands.Add((y0, y1));
                }
            }

            var bands = new List<Rectangle>(rawBands.Count);
            long bandArea = 0;
            int aabbMinX = w, aabbMaxX = -1, aabbMinY = h, aabbMaxY = -1;
            foreach (var (y0, y1) in rawBands)
            {
                int minX = w, maxX = -1;
                for (int y = y0; y <= y1; y++)
                {
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (current[rowBase + x] == previous[rowBase + x]) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
                if (maxX < 0) continue;
                var r = new Rectangle(minX, y0, maxX - minX + 1, y1 - y0 + 1);
                bands.Add(r);
                bandArea += (long)r.Width * r.Height;
                if (minX < aabbMinX) aabbMinX = minX;
                if (maxX > aabbMaxX) aabbMaxX = maxX;
                if (y0 < aabbMinY) aabbMinY = y0;
                if (y1 > aabbMaxY) aabbMaxY = y1;
            }

            if (bands.Count == 0)
                return null;

            // AABB built during band scan — no second full-frame ComputeChangedRect pass
            var aabb = new Rectangle(aabbMinX, aabbMinY,
                aabbMaxX - aabbMinX + 1, aabbMaxY - aabbMinY + 1);

            if (bands.Count > 1)
            {
                long aabbArea = (long)aabb.Width * aabb.Height;
                if (bandArea >= aabbArea * bandAreaPercent / 100)
                    return new List<Rectangle> { aabb };
            }
            else if (bands.Count == 1)
            {
                return bands;
            }

            if (bands.Count > maxBands)
                return new List<Rectangle> { aabb };

            return bands;
        }

        private static byte[] ExtractSubRect(byte[] indices, int fullW, Rectangle rect)
        {
            byte[] sub = new byte[rect.Width * rect.Height];
            for (int row = 0; row < rect.Height; row++)
                Buffer.BlockCopy(indices,
                                 (rect.Top + row) * fullW + rect.Left,
                                 sub,
                                 row * rect.Width,
                                 rect.Width);
            return sub;
        }

        // Allocation-free equality (kept for callers outside the encode hot path)
        internal static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return CountChangedPixels(a, b) == 0;
        }

        //  Read raw BGRA bytes from bitmap

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

        //  Colour-lookup cube  (32x32x32, skips the transparent slot)

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

        //  Bayer ordered dithering (quality 3-5)
        //
        //  Uses an 8x8 Bayer threshold matrix.  The threshold for pixel (x,y)
        //  depends only on its position, NOT on the values of adjacent pixels.
        //  This makes the dither pattern completely deterministic and
        //  frame-independent:  the same source pixel at position (x,y) always
        //  produces the same palette index across every frame.
        //
        //  Why this matters for GIF compression
        //  
        //  Sierra-2-4A (error diffusion) propagates quantisation error forward
        //  through the scan line.  A tiny difference in one pixel's colour
        //  (e.g. due to animation, anti-aliasing, or video noise) cascades into
        //  a different error distribution for all subsequent pixels in the row,
        //  so pixels that look identical in the source may get different indices
        //  in consecutive frames.  The delta encoder therefore cannot mark them
        //  as transparent, and LZW must encode the full pixel value.
        //
        //  With Bayer dithering the threshold at (x,y) is constant, so if a
        //  source pixel is unchanged between frames it gets the same index and
        //  the delta encoder marks it transparent.  LZW then sees long runs of
        //  the same transparent index, compressing dramatically better.
        //
        //  Strength is scaled to +/-strength/2 around 0.  Typical value: 24
        //  (~ +/-12 out of 255, noticeable but not harsh).  The transparent
        //  index is excluded from the nearest-colour search.

        // 8x8 Bayer matrix -- values 0..63, row-major
        private static readonly int[] _bayer8x8 =
        {
             0, 32,  8, 40,  2, 34, 10, 42,
            48, 16, 56, 24, 50, 18, 58, 26,
            12, 44,  4, 36, 14, 46,  6, 38,
            60, 28, 52, 20, 62, 30, 54, 22,
             3, 35, 11, 43,  1, 33,  9, 41,
            51, 19, 59, 27, 49, 17, 57, 25,
            15, 47,  7, 39, 13, 45,  5, 37,
            63, 31, 55, 23, 61, 29, 53, 21
        };

        // Bayer dither strength default (quality 100). Offset is in [-strength/2, +strength/2].
        private const int DefaultBayerStrength = 24;

        // Maps GifQuality 0–100 → palette size (power of 2), Bayer strength, dither on/off.
        // 100 = 256 colors + full dither (previous default behaviour).
        private static void ResolveGifQuality(int quality,
            out int paletteSize, out int bayerStrength, out bool useDither)
        {
            if (quality < 0) quality = 0;
            if (quality > 100) quality = 100;

            // Palette size includes the reserved transparent slot.
            if (quality >= 85)      paletteSize = 256;
            else if (quality >= 65) paletteSize = 128;
            else if (quality >= 45) paletteSize = 64;
            else if (quality >= 25) paletteSize = 32;
            else                    paletteSize = 16;

            useDither = quality >= 15;
            // Scale dither 8…24 with quality so gradients stay smooth at high settings.
            bayerStrength = useDither
                ? Math.Max(8, DefaultBayerStrength * quality / 100)
                : 0;
        }

        // Maps GifCompression 0–100 = near-duplicate threshold and dirty-band aggressiveness.
        // Higher compression → smaller files (more frame skipping / multi-band patches).
        // Does not change resolution or palette (use GifQuality for that).
        private static void ResolveGifCompression(int compression,
            out double nearDupeFraction, out int bandAreaPercent, out int maxBands)
        {
            if (compression < 0) compression = 0;
            if (compression > 100) compression = 100;

            // 0 : exact duplicates only; 50 → ~0.1%; 100 → ~0.2%
            nearDupeFraction = compression / 100.0 * 0.002;

            // 0 " never prefer bands (100%); 50 → 85%; 100 → 70%
            bandAreaPercent = 100 - compression * 30 / 100;

            // 0 : single rect; 50 → ~4; 100 → 8
            maxBands = Math.Max(1, 1 + compression * 7 / 100);
        }

        internal static byte[] DitherInternal(byte[] bgra, int w, int h,
                                              Color[] palette, byte[] cube,
                                              int skipIdx = -1, int bayerStrength = DefaultBayerStrength)
        {
            if (bayerStrength < 1)
                return QuantizeNoDither(bgra, w, h, cube);

            byte[] indices = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int   threshold = _bayer8x8[(y & 7) * 8 + (x & 7)];
                    int   offset    = (threshold * bayerStrength + 32) / 64 - bayerStrength / 2;

                    int   bi = (rowBase + x) * 4;
                    byte  r  = (byte)Math.Max(0, Math.Min(255, bgra[bi + 2] + offset));
                    byte  g  = (byte)Math.Max(0, Math.Min(255, bgra[bi + 1] + offset));
                    byte  b  = (byte)Math.Max(0, Math.Min(255, bgra[bi + 0] + offset));

                    indices[rowBase + x] = (byte)CubeLookup(cube, r, g, b);
                }
            }
            return indices;
        }

        //  No-dither quantise  (quality 1-2)

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

        //  GIF binary output

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

        // Writes one GIF frame referencing the file-level Global Color Table.
        // left/top position the patch within the logical screen (used for the
        // changed-rect optimisation). No local colour table is written, saving
        // palette.Length*3 bytes per frame. Unchanged pixels are already set to
        // transpIdx by the caller; the GCE enables transparency so those pixels
        // show through from the previous frame, and LZW sees long runs of the
        // same index.
        internal static void WriteGifFrameWithTransparency(
            BinaryWriter bw, byte[] indices,
            int left, int top, int w, int h,
            int delayCs, int transpIdx, int lzwMinCode)
        {
            // Graphic Control Extension
            bw.Write((byte)0x21); bw.Write((byte)0xF9);
            bw.Write((byte)4);
            // Packed: disposal=1 (do not dispose) = bits3-5 -> 0x04
            // Transparency flag = bit0 -> 0x01   Combined: 0x05
            bw.Write((byte)0x05);
            WriteU16Internal(bw, (ushort)delayCs);
            bw.Write((byte)transpIdx);
            bw.Write((byte)0); // block terminator

            // Image Descriptor -- no local colour table (bit7 = 0)
            bw.Write((byte)0x2C);
            WriteU16Internal(bw, (ushort)left);
            WriteU16Internal(bw, (ushort)top);
            WriteU16Internal(bw, (ushort)w);
            WriteU16Internal(bw, (ushort)h);
            bw.Write((byte)0x00); // packed: no local table, not interlaced

            // LZW image data
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

        // Convenience overload: full canvas frame at (0,0).
        internal static void WriteGifFrameWithTransparency(
            BinaryWriter bw, byte[] indices,
            int w, int h, int delayCs, int transpIdx, int lzwMinCode)
            => WriteGifFrameWithTransparency(bw, indices, 0, 0, w, h, delayCs, transpIdx, lzwMinCode);

        // Overload kept for call sites that still pass palette + per-frame sizing
        // (used by legacy WriteGifFrameInternal path). Computes lzwMinCode from
        // the palette length and delegates to the primary overload.
        internal static void WriteGifFrameWithTransparency(
            BinaryWriter bw, byte[] indices, Color[] palette,
            int w, int h, int delayCs, int transpIdx)
        {
            int tableN       = 0;
            int tableEntries = 2;
            while (tableEntries < palette.Length && tableN < 7)
            { tableN++; tableEntries = 1 << (tableN + 1); }
            WriteGifFrameWithTransparency(bw, indices, w, h, delayCs, transpIdx,
                                          Math.Max(2, tableN + 1));
        }

        // Legacy overload for callers that don't use transparency.
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

    //  MediaCutQuantizer -- median-cut colour quantisation
    internal static class MediaCutQuantizer
    {
        // Streaming palette builder: samples evenly across all frames (like
        // FFmpeg palettegen=stats_mode=full) so late frames are not starved.
        // Never holds more than one frame's pixels in memory at once.
        public static Color[] BuildGlobalStreaming(IEnumerable<GifFrame> frames,
                                                   int w, int h,
                                                   int maxColors, int maxSamplesTotal,
                                                   int frameCount = 0)
        {
            int pixelsPerFrame = w * h;
            if (frameCount < 1) frameCount = 1;

            // Even budget per frame so long recordings still represent late colors.
            int samplesPerFrame = Math.Max(1, maxSamplesTotal / frameCount);
            int step = Math.Max(1, pixelsPerFrame / samplesPerFrame);

            int capacity = Math.Min(maxSamplesTotal, samplesPerFrame * frameCount);
            int[] flat = new int[capacity * 3];
            int   si   = 0;
            int   framesSeen = 0;

            foreach (GifFrame frame in frames)
            {
                framesSeen++;
                if (si >= flat.Length - 2) break;

                byte[] bgra = AnimatedGifEncoder.ReadBgraInternal(frame.Bitmap, w, h);
                frame.Bitmap.Dispose();

                int frameBudget = samplesPerFrame;
                int taken = 0;
                // Phase offset per frame so we don't always sample the same grid cells
                int phase = ((framesSeen - 1) * 7) % step;

                for (int i = phase; i < pixelsPerFrame && taken < frameBudget && si < flat.Length - 2; i += step)
                {
                    int bi = i * 4;
                    flat[si++] = bgra[bi + 2]; // R
                    flat[si++] = bgra[bi + 1]; // G
                    flat[si++] = bgra[bi + 0]; // B
                    taken++;
                }
            }

            int actualSamples = si / 3;
            Logger.Log($"MediaCutQuantizer.BuildGlobalStreaming: " +
                       $"samples={actualSamples}, step={step}, frames={framesSeen}, " +
                       $"samplesPerFrame~={samplesPerFrame}, maxColors={maxColors}");
            return RunMedianCut(flat, actualSamples, maxColors);
        }

        // Builds a global palette sampled from ALL frames combined using a single
        // uniform stride across the entire pixel population.
        public static Color[] BuildGlobal(IList<byte[]> bgraFrames, int pixelsPerFrame,
                                          int maxColors, int maxSamplesTotal)
        {
            int framesCount  = bgraFrames.Count;
            int totalPixels  = pixelsPerFrame * framesCount;

            // One global step across all frames combined -- never drops below 1
            int globalStep   = Math.Max(1, totalPixels / maxSamplesTotal);

            // Upper bound on samples we can actually collect -- capped at totalPixels
            // to guard against overflow when maxSamplesTotal is very large.
            int capacity     = (int)Math.Min((long)maxSamplesTotal, (long)(totalPixels / globalStep) + 1);
            int[] flat       = new int[capacity * 3];
            int   si         = 0;
            int   globalIdx  = 0; // absolute pixel index across all frames

            foreach (byte[] bgra in bgraFrames)
            {
                for (int i = 0; i < pixelsPerFrame; i++, globalIdx++)
                {
                    // Sample every globalStep-th pixel in the combined sequence
                    if (globalIdx % globalStep != 0) continue;
                    if (si >= flat.Length - 2) break;

                    int bi = i * 4;
                    flat[si++] = bgra[bi + 2]; // R
                    flat[si++] = bgra[bi + 1]; // G
                    flat[si++] = bgra[bi + 0]; // B
                }
            }

            int actualSamples = si / 3;
            Logger.Log($"MediaCutQuantizer.BuildGlobal: {framesCount} frames, " +
                       $"totalPixels={totalPixels}, step={globalStep}, " +
                       $"samples={actualSamples}, maxColors={maxColors}");
            return RunMedianCut(flat, actualSamples, maxColors);
        }

        // Builds a palette from a single frame's BGRA data.
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

    //  LzwEncoder -- GIF-compatible LZW (LSB-first, unchanged)
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

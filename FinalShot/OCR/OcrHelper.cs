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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PluginScreenshot
{
    // Windows.Media.Ocr wrapper (ShareX-compatible pipeline) for .NET Framework 4.8.
    // Requires Windows 10 1903+ and an installed OCR language pack.
    internal static class OcrHelper
    {
        private static readonly Version SupportedVersion = new Version(10, 0, 18362, 0);

        public static bool IsSupported
        {
            get
            {
                try
                {
                    // Prefer probing the WinRT API -- Environment.OSVersion is often capped at 6.2
                    // without an app compatibility manifest.
                    var languages = OcrEngine.AvailableRecognizerLanguages;
                    return languages != null;
                }
                catch
                {
                    try
                    {
                        return Environment.OSVersion.Version >= SupportedVersion;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public static void ThrowIfNotSupported()
        {
            if (!IsSupported)
            {
                throw new InvalidOperationException(
                    "OCR requires Windows 10 version 1903 (10.0.18362) or later.");
            }
        }

        public static async Task<string> RecognizeAsync(Bitmap bitmap, string languageTag,
            float scaleFactor, bool singleLine)
        {
            ThrowIfNotSupported();
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            scaleFactor = Math.Max(scaleFactor, 1f);
            languageTag = string.IsNullOrWhiteSpace(languageTag) ? "en" : languageTag.Trim();

            using (Bitmap scaled = ScaleBitmap(bitmap, scaleFactor))
            {
                return await RecognizeInternalAsync(scaled, languageTag, singleLine)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<string> RecognizeInternalAsync(Bitmap bitmap,
            string languageTag, bool singleLine)
        {
            Language language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language))
            {
                throw new InvalidOperationException(
                    "OCR language unavailable: " + language.DisplayName +
                    " (" + language.LanguageTag + "). Install the language pack in Windows Settings.");
            }

            OcrEngine engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine == null)
            {
                throw new InvalidOperationException(
                    "Could not create OCR engine for language: " + language.LanguageTag);
            }

            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                bitmap.Save(stream.AsStream(), ImageFormat.Bmp);
                stream.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream)
                    .AsTask().ConfigureAwait(false);
                using (SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync()
                    .AsTask().ConfigureAwait(false))
                {
                    OcrResult result = await engine.RecognizeAsync(softwareBitmap)
                        .AsTask().ConfigureAwait(false);

                    IEnumerable<string> lines;
                    if (language.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                        language.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                    {
                        lines = result.Lines.Select(line =>
                            string.Concat(line.Words.Select(word => word.Text)));
                    }
                    else if (language.LayoutDirection == LanguageLayoutDirection.Rtl)
                    {
                        lines = result.Lines.Select(line =>
                            string.Join(" ", line.Words.Reverse().Select(word => word.Text)));
                    }
                    else
                    {
                        lines = result.Lines.Select(line => line.Text);
                    }

                    return string.Join(singleLine ? " " : Environment.NewLine, lines);
                }
            }
        }

        private static Bitmap ScaleBitmap(Bitmap source, float scaleFactor)
        {
            if (scaleFactor <= 1.01f)
                return new Bitmap(source);

            int w = Math.Max(1, (int)Math.Round(source.Width * scaleFactor));
            int h = Math.Max(1, (int)Math.Round(source.Height * scaleFactor));
            var scaled = new Bitmap(w, h);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }
            return scaled;
        }
    }
}

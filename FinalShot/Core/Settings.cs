using System.Drawing;
using Rainmeter;

namespace PluginScreenshot
{
    /// <summary>
    /// Resolved encoder parameters derived from the GifQualityLevel level (1–5).
    /// Passed through the encoding pipeline so every call site uses consistent values.
    /// </summary>
    public sealed class GifEncoderQuality
    {
        /// <summary>Number of palette entries (must be a power of 2, 2–256).</summary>
        public int Colors { get; }

        /// <summary>Maximum pixels sampled by MediaCutQuantizer per frame.</summary>
        public int MaxSamples { get; }

        /// <summary>When true, Floyd-Steinberg error diffusion is applied after quantisation.</summary>
        public bool Dither { get; }

        public GifEncoderQuality(int colors, int maxSamples, bool dither)
        {
            Colors     = colors;
            MaxSamples = maxSamples;
            Dither     = dither;
        }

        /// <summary>
        /// Maps a quality level (1–5) to concrete encoder parameters.
        ///
        /// | Level | Name         | Colors | MaxSamples | Dither |
        /// |-------|--------------|--------|------------|--------|
        /// |   1   | Low          |    64  |   5,000    | false  |
        /// |   2   | Medium-Low   |   128  |  15,000    | false  |
        /// |   3   | Medium (def) |   256  |  40,000    | true   |
        /// |   4   | High         |   256  |  80,000    | true   |
        /// |   5   | Ultra        |   256  | 150,000    | true   |
        /// </summary>
        public static GifEncoderQuality FromLevel(int level)
        {
            switch (level)
            {
                case 1:  return new GifEncoderQuality( 64,   5_000, false);
                case 2:  return new GifEncoderQuality(128,  15_000, false);
                case 4:  return new GifEncoderQuality(256,  80_000, true);
                case 5:  return new GifEncoderQuality(256, 150_000, true);
                default: return new GifEncoderQuality(256,  40_000, true); // 3 = default
            }
        }
    }

    public class Settings
    {
        public API Api { get; }
        public string SavePath { get; private set; }
        public string FinishAction { get; private set; }
        public bool ShowCursor { get; private set; }
        public int JpegQuality { get; private set; }
        public Rectangle PredefinedRegion { get; private set; }
        public bool ShowNotification { get; private set; }
        public bool UsePrintWindow { get; private set; }
        public bool DetectWindows { get; private set; }
        public bool DetectControls { get; private set; }

        public string GifSavePath { get; private set; }
        public int GifFPS { get; private set; }
        public int GifDuration { get; private set; }
        public Rectangle GifPredefinedRegion { get; private set; }

        /// <summary>
        /// GIF encoding quality level, 1–5. Default 3.
        ///   1 = Low      (64 colors, fast, no dither)
        ///   2 = Med-Low  (128 colors, no dither)
        ///   3 = Medium   (256 colors, 40K samples, no dither)  ← default
        ///   4 = High     (256 colors, 80K samples, dither)
        ///   5 = Ultra    (256 colors, 150K samples, dither)
        /// </summary>
        public int GifQualityLevel { get; private set; }

        /// <summary>Resolved encoder parameters for the current GifQualityLevel.</summary>
        public GifEncoderQuality EncoderQuality { get; private set; }

        /// <summary>
        /// GIF compression level, 1–3. Default 2.
        ///   1 = Aggressive — duplicate frames are dropped and identical consecutive
        ///       frames are merged (their delays are accumulated). Best file size for
        ///       low-motion recordings (e.g. screen with static background).
        ///   2 = Normal (default) — duplicate frames are dropped. Good balance.
        ///   3 = Off — every captured frame is written regardless of content.
        ///       Use when every frame must be preserved (fast motion, smooth animation).
        ///
        /// Compression is independent of Quality — you can have high quality + high
        /// compression, or low quality + no compression.
        /// </summary>
        public int GifCompressionLevel { get; private set; }

        public string GifStartAction { get; private set; }
        public string GifCancelAction { get; private set; }
        public string OnGifEncodingAction { get; private set; }
        public string GifPauseAction { get; private set; }
        public string GifResumeAction { get; private set; }
        public bool GifShowOverlay { get; private set; }

        public Settings(API api)
        {
            Api              = api;
            SavePath         = api.ReadString("SavePath", "");
            FinishAction     = api.ReadString("ScreenshotFinishAction", "");
            ShowCursor       = api.ReadInt("ShowCursor", 0) > 0;
            JpegQuality      = api.ReadInt("JpgQuality", 70);
            ShowNotification = api.ReadInt("ShowNotification", 0) > 0;
            UsePrintWindow   = api.ReadInt("UsePrintWindow", 0) > 0;

            int x = api.ReadInt("PredefX", 0);
            int y = api.ReadInt("PredefY", 0);
            int w = api.ReadInt("PredefWidth", 0);
            int h = api.ReadInt("PredefHeight", 0);
            PredefinedRegion = new Rectangle(x, y, w, h);

            DetectWindows  = api.ReadInt("DetectWindows",  1) > 0;
            DetectControls = api.ReadInt("DetectControls", 1) > 0;

            GifSavePath = api.ReadString("GifSavePath", "");
            GifFPS      = api.ReadInt("GifFPS", 10);
            if (GifFPS < 1)  GifFPS = 1;
            if (GifFPS > 30) GifFPS = 30;
            GifDuration = api.ReadInt("GifDuration", 0);
            if (GifDuration < 0) GifDuration = 0;

            int gx = api.ReadInt("GifPredefX",      x);
            int gy = api.ReadInt("GifPredefY",      y);
            int gw = api.ReadInt("GifPredefWidth",  w);
            int gh = api.ReadInt("GifPredefHeight", h);
            GifPredefinedRegion = new Rectangle(gx, gy, gw, gh);

            GifQualityLevel     = api.ReadInt("GifQualityLevel", 3);
            if (GifQualityLevel < 1) GifQualityLevel = 1;
            if (GifQualityLevel > 5) GifQualityLevel = 5;
            EncoderQuality = GifEncoderQuality.FromLevel(GifQualityLevel);

            GifCompressionLevel = api.ReadInt("GifCompressionLevel", 2);
            if (GifCompressionLevel < 1) GifCompressionLevel = 1;
            if (GifCompressionLevel > 3) GifCompressionLevel = 3;

            GifStartAction       = api.ReadString("GifStartAction",      "");
            GifCancelAction      = api.ReadString("GifCancelAction",     "");
            GifPauseAction       = api.ReadString("GifPauseAction",      "");
            GifResumeAction      = api.ReadString("GifResumeAction",     "");
            OnGifEncodingAction  = api.ReadString("OnGifEncodingAction", "");
            GifShowOverlay       = api.ReadInt("GifShowOverlay",       1) > 0;

            Logger.DebugEnabled = api.ReadInt("DebugLog", 0) == 1;
            string dbg = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(dbg))
                Logger.LogFilePath = dbg;

            Logger.Log("Settings reloaded. SavePath=" + SavePath
                + "  DetectWindows=" + DetectWindows
                + "  DetectControls=" + DetectControls
                + "  GifSavePath=" + GifSavePath
                + "  GifFPS=" + GifFPS
                + "  GifDuration=" + GifDuration
                + "  GifQualityLevel=" + GifQualityLevel
                    + " (colors=" + EncoderQuality.Colors
                    + " samples=" + EncoderQuality.MaxSamples
                    + " dither="  + EncoderQuality.Dither + ")"
                + "  GifCompressionLevel=" + GifCompressionLevel
                + "  GifPredefinedRegion=" + GifPredefinedRegion
                + "  GifStartAction="      + (string.IsNullOrEmpty(GifStartAction)      ? "(none)" : "(set)")
                + "  GifCancelAction="     + (string.IsNullOrEmpty(GifCancelAction)     ? "(none)" : "(set)")
                + "  GifPauseAction="      + (string.IsNullOrEmpty(GifPauseAction)      ? "(none)" : "(set)")
                + "  GifResumeAction="     + (string.IsNullOrEmpty(GifResumeAction)     ? "(none)" : "(set)")
                + "  OnGifEncodingAction=" + (string.IsNullOrEmpty(OnGifEncodingAction) ? "(none)" : "(set)"));
        }
    }
}

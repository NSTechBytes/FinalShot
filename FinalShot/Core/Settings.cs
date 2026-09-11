using System.Drawing;
using Rainmeter;

namespace PluginScreenshot
{
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

        // When true, CustomScreenshotForm highlights windows under the cursor.
        // Set DetectWindows=0 in the skin ini to use drag-only mode.
        public bool DetectWindows { get; private set; }

        // When true, child controls inside each window are also detected.
        // Requires DetectWindows=1.
        public bool DetectControls { get; private set; }

        // --- GIF capture settings ---

        /// <summary>Full path where the animated GIF will be saved, e.g. #@#Screenshots\Capture.gif</summary>
        public string GifSavePath { get; private set; }

        /// <summary>Frames per second for GIF recording. Default 10.</summary>
        public int GifFPS { get; private set; }

        /// <summary>
        /// Maximum recording duration in seconds.
        /// 0 = unlimited — recording continues until -gif-stop is sent.
        /// </summary>
        public int GifDuration { get; private set; }

        /// <summary>
        /// Bang executed the moment GIF recording starts (capture thread launched).
        /// Useful for turning a skin button red, showing a "● REC" label, etc.
        /// Example: [!SetOption GifToggle_BackGround This "Fill Color 120,30,30,200"][!UpdateMeter *][!Redraw]
        /// </summary>
        public string GifStartAction { get; private set; }

        /// <summary>
        /// Bang executed when a recording is cancelled via -gif-cancel
        /// (frames discarded, no file written). Useful for resetting the skin UI.
        /// Example: [!Log "GIF cancelled"][!SetOption Row2_Label FontColor "255,100,100"][!UpdateMeter Row2_Label][!Redraw]
        /// </summary>
        public string GifCancelAction { get; private set; }

        /// <summary>
        /// Bang executed the moment encoding begins (capture stopped, frames handed
        /// to the encoder). Useful for showing an "Encoding…" spinner or label.
        /// Example: [!SetOption Row2_Label Text "Encoding..."][!UpdateMeter Row2_Label][!Redraw]
        /// </summary>
        public string OnGifEncodingAction { get; private set; }

        /// <summary>
        /// When true, each frame is quantised and written to the GIF file as it is
        /// captured, instead of buffering all raw frames and encoding after stop.
        ///
        /// Benefits:
        ///   • RAM usage stays low (only ~2 frames in memory at a time).
        ///   • StopAndSave() / -gif-stop returns almost immediately — no post-encode wait.
        ///
        /// Trade-off:
        ///   • CPU is higher during recording (quantise + dither + LZW per frame).
        ///   • -gif-cancel deletes the partial file that was already being written.
        ///
        /// Set GifEncodeWhileRecord=1 in the skin INI to enable.
        /// Default: 0 (batch mode — encode after stop, same as before).
        /// </summary>
        public bool GifEncodeWhileRecord { get; private set; }

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

            // GIF
            GifSavePath = api.ReadString("GifSavePath", "");
            GifFPS      = api.ReadInt("GifFPS", 10);
            if (GifFPS < 1)  GifFPS = 1;
            if (GifFPS > 30) GifFPS = 30;
            GifDuration = api.ReadInt("GifDuration", 0);
            if (GifDuration < 0) GifDuration = 0;

            GifStartAction      = api.ReadString("GifStartAction",      "");
            GifCancelAction     = api.ReadString("GifCancelAction",     "");
            OnGifEncodingAction = api.ReadString("OnGifEncodingAction", "");
            GifEncodeWhileRecord = api.ReadInt("GifEncodeWhileRecord", 0) > 0;

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
                + "  GifEncodeWhileRecord=" + GifEncodeWhileRecord
                + "  GifStartAction=" + (string.IsNullOrEmpty(GifStartAction) ? "(none)" : "(set)")
                + "  GifCancelAction=" + (string.IsNullOrEmpty(GifCancelAction) ? "(none)" : "(set)")
                + "  OnGifEncodingAction=" + (string.IsNullOrEmpty(OnGifEncodingAction) ? "(none)" : "(set)"));
        }
    }
}

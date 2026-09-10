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
        /// Maximum output width in pixels. Frames wider than this are scaled down
        /// proportionally before GIF encoding to reduce file size and encode time.
        /// Default 800. Set to a larger value (e.g. 1920) to preserve full resolution.
        /// </summary>
        public int GifMaxWidth { get; private set; }

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
            GifMaxWidth = api.ReadInt("GifMaxWidth", 800);
            if (GifMaxWidth < 100)  GifMaxWidth = 100;
            if (GifMaxWidth > 3840) GifMaxWidth = 3840;

            Logger.DebugEnabled = api.ReadInt("DebugLog", 0) == 1;
            string dbg = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(dbg))
                Logger.LogFilePath = dbg;

            Logger.Log("Settings reloaded. SavePath=" + SavePath
                + "  DetectWindows=" + DetectWindows
                + "  DetectControls=" + DetectControls
                + "  GifSavePath=" + GifSavePath
                + "  GifFPS=" + GifFPS
                + "  GifDuration=" + GifDuration);
        }
    }
}

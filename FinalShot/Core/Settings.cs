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

        // ── Window-detection settings (Task 3) ──────────────────────
        /// <summary>
        /// When true, CustomScreenshotForm highlights windows under the cursor.
        /// Set DetectWindows=0 in the skin ini to restore pure drag-only behaviour.
        /// Default: 1 (on).
        /// </summary>
        public bool DetectWindows { get; private set; }

        /// <summary>
        /// When true, child controls are also enumerated inside each top-level window,
        /// allowing finer-grained snapping. Requires DetectWindows=1.
        /// Default: 1 (on).
        /// </summary>
        public bool DetectControls { get; private set; }

        public Settings(API api)
        {
            Api = api;
            SavePath       = api.ReadString("SavePath", "");
            FinishAction   = api.ReadString("ScreenshotFinishAction", "");
            ShowCursor     = api.ReadInt("ShowCursor", 0) > 0;
            JpegQuality    = api.ReadInt("JpgQuality", 70);
            ShowNotification = api.ReadInt("ShowNotification", 0) > 0;
            UsePrintWindow = api.ReadInt("UsePrintWindow", 0) > 0;

            int x = api.ReadInt("PredefX", 0);
            int y = api.ReadInt("PredefY", 0);
            int w = api.ReadInt("PredefWidth", 0);
            int h = api.ReadInt("PredefHeight", 0);
            PredefinedRegion = new Rectangle(x, y, w, h);

            DetectWindows  = api.ReadInt("DetectWindows",  1) > 0;
            DetectControls = api.ReadInt("DetectControls", 1) > 0;

            Logger.DebugEnabled = api.ReadInt("DebugLog", 0) == 1;
            string dbg = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(dbg))
                Logger.LogFilePath = dbg;

            Logger.Log($"Settings reloaded. SavePath={SavePath}  DetectWindows={DetectWindows}  DetectControls={DetectControls}");
        }
    }
}

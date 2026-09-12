using System;
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
        public bool DetectWindows { get; private set; }
        public bool DetectControls { get; private set; }

        public string GifSavePath { get; private set; }
        public int GifFPS { get; private set; }
        public int GifDuration { get; private set; }
        public Rectangle GifPredefinedRegion { get; private set; }

        /// <summary>
        /// When true, a dialog is shown when the user tries to start a recording
        /// while one is already active, or tries to stop/cancel when not recording.
        /// Default true.
        /// </summary>
        public bool GifShowStateDialogs { get; private set; }

        public string GifStartAction { get; private set; }
        public string GifCancelAction { get; private set; }
        public string OnGifEncodingAction { get; private set; }
        public string GifPauseAction { get; private set; }
        public string GifResumeAction { get; private set; }
        public bool GifShowOverlay { get; private set; }

        /// <summary>
        /// When true, a small encoding-progress window is shown while the GIF
        /// is being written to disk after recording stops. Default true.
        /// </summary>
        public bool GifShowEncodingWindow { get; private set; }

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

            GifStartAction       = api.ReadString("GifStartAction",      "");
            GifCancelAction      = api.ReadString("GifCancelAction",     "");
            GifPauseAction       = api.ReadString("GifPauseAction",      "");
            GifResumeAction      = api.ReadString("GifResumeAction",     "");
            OnGifEncodingAction  = api.ReadString("OnGifEncodingAction", "");
            GifShowOverlay        = api.ReadInt("GifShowOverlay",        1) > 0;
            GifShowEncodingWindow = api.ReadInt("GifShowEncodingWindow", 1) > 0;
            GifShowStateDialogs   = api.ReadInt("GifShowStateDialogs",   1) > 0;

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
                + "  GifPredefinedRegion=" + GifPredefinedRegion
                + "  GifStartAction="      + (string.IsNullOrEmpty(GifStartAction)      ? "(none)" : "(set)")
                + "  GifCancelAction="     + (string.IsNullOrEmpty(GifCancelAction)     ? "(none)" : "(set)")
                + "  GifPauseAction="      + (string.IsNullOrEmpty(GifPauseAction)      ? "(none)" : "(set)")
                + "  GifResumeAction="     + (string.IsNullOrEmpty(GifResumeAction)     ? "(none)" : "(set)")
                + "  OnGifEncodingAction=" + (string.IsNullOrEmpty(OnGifEncodingAction) ? "(none)" : "(set)"));
        }
    }
}

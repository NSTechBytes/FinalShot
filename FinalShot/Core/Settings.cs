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
using System.Drawing;
using Rainmeter;

namespace PluginScreenshot
{
    public class Settings
    {
        // Suppresses spam when DynamicVariables=1 reloads every update.
        private static string _lastDebugSignature;

        public API Api { get; }
        public string SavePath { get; private set; }
        public string FinishAction { get; private set; }
        public bool ShowCursor { get; private set; }
        public int JpegQuality { get; private set; }
        public Rectangle PredefinedRegion { get; private set; }
        public bool ShowNotification { get; private set; }

        // Play a Windows notify WAV when the toast appears. Default true.
        // Independent of the Windows sound-scheme Asterisk event (often silent).
        public bool PlayNotificationSound { get; private set; }
        public bool UsePrintWindow { get; private set; }
        public bool DetectWindows { get; private set; }
        public bool DetectControls { get; private set; }

        // When true, mask Windows 11 rounded corners on top-level window captures
        // so desktop pixels in the sharp rectangle corners are removed.
        // Default false for backward compatibility.
        public bool RoundWindowCorners { get; private set; }

        // Theme for all FinalShot popup windows (Notification, Encoding, Dialog).
        //   0 = Dark (default)
        //   1 = Light
        //   2 = System (follows Windows app theme setting)
        public UITheme UITheme { get; private set; }

        // Bang executed when the user clicks the notification toast.
        // Leave empty for no action.
        public string NotificationClickAction { get; private set; }

        public string GifSavePath { get; private set; }
        public int GifFPS { get; private set; }
        public int GifDuration { get; private set; }

        // GIF encode quality 0–100 (default 100). Higher = more colors / stronger dither.
        public int GifQuality { get; private set; }

        // GIF inter-frame compression 0–100 (default 50). Higher = smaller files via
        // more aggressive dirty-band / near-duplicate optimisation. Does not change resolution.
        public int GifCompression { get; private set; }

        public Rectangle GifPredefinedRegion { get; private set; }

        // When true, a dialog is shown when the user tries to start a recording
        // while one is already active, or tries to stop/cancel when not recording.
        // Default true.
        public bool GifShowStateDialogs { get; private set; }

        public string GifStartAction { get; private set; }
        public string GifCancelAction { get; private set; }
        public string GifStopAction { get; private set; }
        public string GifEncodingAction { get; private set; }
        public string GifPauseAction { get; private set; }
        public string GifResumeAction { get; private set; }

        // Bang after a GIF is successfully saved (not on cancel / encode failure).
        // Replaces ScreenshotFinishAction for the GIF path.
        public string GifFinishAction { get; private set; }
        public bool GifShowOverlay { get; private set; }

        // When true, a small encoding-progress window is shown while the GIF
        // is being written to disk after recording stops. Default true.
        public bool GifShowEncodingWindow { get; private set; }

        // OCR language tag (e.g. en, en-US). Requires installed Windows OCR pack.
        public string OcrLanguage { get; private set; }

        // Upscale factor before OCR (1-4). Default 2.
        public float OcrScaleFactor { get; private set; }

        // When true, OCR lines are joined with spaces instead of newlines.
        public bool OcrSingleLine { get; private set; }

        // Bang executed after successful OCR (not on cancel).
        public string OcrFinishAction { get; private set; }

        // When true, show an OCR result window after recognition (edit / copy / close).
        // Default false (silent clipboard mode).
        public bool ShowOcrWindow { get; private set; }

        // Master switch for global hotkeys. Default true (chords still must be set).
        public bool HotkeysEnabled { get; private set; }

        public string HotkeyFullscreen { get; private set; }
        public string HotkeyPredefined { get; private set; }
        public string HotkeyCustom { get; private set; }
        public string HotkeyWindowHandle { get; private set; }
        public string HotkeyStoredWindow { get; private set; }
        public string HotkeyOcr { get; private set; }
        public string HotkeyGifToggle { get; private set; }
        public string HotkeyGifToggleSnap { get; private set; }

        public Settings(API api)
        {
            Api              = api;
            SavePath         = api.ReadString("SavePath", "");
            FinishAction     = api.ReadString("ScreenshotFinishAction", "");
            ShowCursor       = api.ReadInt("ShowCursor", 0) > 0;
            JpegQuality      = api.ReadInt("JpgQuality", 70);
            ShowNotification = api.ReadInt("ShowNotification", 0) > 0;
            PlayNotificationSound = api.ReadInt("NotificationSound", 1) > 0;
            UsePrintWindow   = api.ReadInt("UsePrintWindow", 0) > 0;

            UITheme = ThemeColors.ParseTheme(api.ReadString("UITheme", "0"));
            NotificationClickAction = api.ReadString("NotificationClickAction", "");

            int x = api.ReadInt("PredefX", 0);
            int y = api.ReadInt("PredefY", 0);
            int w = api.ReadInt("PredefWidth", 0);
            int h = api.ReadInt("PredefHeight", 0);
            PredefinedRegion = new Rectangle(x, y, w, h);

            DetectWindows  = api.ReadInt("DetectWindows",  1) > 0;
            DetectControls = api.ReadInt("DetectControls", 1) > 0;
            RoundWindowCorners = api.ReadInt("RoundWindowCorners", 0) > 0;

            OcrLanguage = api.ReadString("OcrLanguage", "en");
            if (string.IsNullOrWhiteSpace(OcrLanguage))
                OcrLanguage = "en";

            float scale = 2f;
            string scaleRaw = api.ReadString("OcrScaleFactor", "2");
            if (!float.TryParse(scaleRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out scale))
                scale = 2f;
            if (scale < 1f) scale = 1f;
            if (scale > 4f) scale = 4f;
            OcrScaleFactor = scale;

            OcrSingleLine    = api.ReadInt("OcrSingleLine", 0) > 0;
            OcrFinishAction  = api.ReadString("OCRFinishAction", "");
            ShowOcrWindow    = api.ReadInt("ShowOCRWindow", 0) > 0;

            HotkeysEnabled       = api.ReadInt("HotkeysEnabled", 1) > 0;
            HotkeyFullscreen     = api.ReadString("HotkeyFullscreen", "");
            HotkeyPredefined     = api.ReadString("HotkeyPredefined", "");
            HotkeyCustom         = api.ReadString("HotkeyCustom", "");
            HotkeyWindowHandle   = api.ReadString("HotkeyWindowHandle", "");
            HotkeyStoredWindow   = api.ReadString("HotkeyStoredWindow", "");
            HotkeyOcr            = api.ReadString("HotkeyOCR", "");
            HotkeyGifToggle      = api.ReadString("HotkeyGifToggle", "");
            HotkeyGifToggleSnap  = api.ReadString("HotkeyGifToggleSnap", "");

            GifSavePath = api.ReadString("GifSavePath", "");
            GifFPS      = api.ReadInt("GifFPS", 10);
            if (GifFPS < 1)  GifFPS = 1;
            if (GifFPS > 30) GifFPS = 30;
            GifDuration = api.ReadInt("GifDuration", 0);
            if (GifDuration < 0) GifDuration = 0;

            GifQuality = api.ReadInt("GifQuality", 100);
            if (GifQuality < 0)   GifQuality = 0;
            if (GifQuality > 100) GifQuality = 100;

            GifCompression = api.ReadInt("GifCompression", 50);
            if (GifCompression < 0)   GifCompression = 0;
            if (GifCompression > 100) GifCompression = 100;

            int gx = api.ReadInt("GifPredefX",      x);
            int gy = api.ReadInt("GifPredefY",      y);
            int gw = api.ReadInt("GifPredefWidth",  w);
            int gh = api.ReadInt("GifPredefHeight", h);
            GifPredefinedRegion = new Rectangle(gx, gy, gw, gh);

            GifStartAction       = api.ReadString("GifStartAction",      "");
            GifCancelAction      = api.ReadString("GifCancelAction",     "");
            GifStopAction        = api.ReadString("GifStopAction",       "");
            GifPauseAction       = api.ReadString("GifPauseAction",      "");
            GifResumeAction      = api.ReadString("GifResumeAction",     "");
            GifEncodingAction    = api.ReadString("GifEncodingAction", "");
            GifFinishAction      = api.ReadString("GifFinishAction",    "");
            GifShowOverlay        = api.ReadInt("GifShowOverlay",        1) > 0;
            GifShowEncodingWindow = api.ReadInt("GifShowEncodingWindow", 1) > 0;
            GifShowStateDialogs   = api.ReadInt("GifShowStateDialogs",   1) > 0;

            Logger.DebugEnabled = api.ReadInt("DebugLog", 0) == 1;
            string dbg = api.ReadString("DebugLogPath", "");
            if (!string.IsNullOrEmpty(dbg))
                Logger.LogFilePath = dbg;

            // DynamicVariables=1 reloads every update (SavePath often changes each
            // second with a Time measure). Only log when non-path config changes.
            string sig = BuildDebugSignature();
            if (sig != _lastDebugSignature)
            {
                _lastDebugSignature = sig;
                Logger.Log("Settings reloaded. SavePath=" + SavePath
                    + "  DetectWindows=" + DetectWindows
                    + "  DetectControls=" + DetectControls
                    + "  RoundWindowCorners=" + RoundWindowCorners
                    + "  OcrLanguage=" + OcrLanguage
                    + "  OcrScaleFactor=" + OcrScaleFactor
                    + "  OcrSingleLine=" + OcrSingleLine
                    + "  ShowOCRWindow=" + ShowOcrWindow
                    + "  OCRFinishAction=" + (string.IsNullOrEmpty(OcrFinishAction) ? "(none)" : "(set)")
                    + "  HotkeysEnabled=" + HotkeysEnabled
                    + "  HotkeyCustom=" + (string.IsNullOrEmpty(HotkeyCustom) ? "(none)" : HotkeyCustom)
                    + "  HotkeyWindowHandle=" + (string.IsNullOrEmpty(HotkeyWindowHandle) ? "(none)" : HotkeyWindowHandle)
                    + "  HotkeyStoredWindow=" + (string.IsNullOrEmpty(HotkeyStoredWindow) ? "(none)" : HotkeyStoredWindow)
                    + "  HotkeyOCR=" + (string.IsNullOrEmpty(HotkeyOcr) ? "(none)" : HotkeyOcr)
                    + "  GifSavePath=" + GifSavePath
                    + "  GifFPS=" + GifFPS
                    + "  GifQuality=" + GifQuality
                    + "  GifCompression=" + GifCompression
                    + "  GifDuration=" + GifDuration
                    + "  GifPredefinedRegion=" + GifPredefinedRegion
                    + "  GifStartAction="      + (string.IsNullOrEmpty(GifStartAction)      ? "(none)" : "(set)")
                    + "  GifCancelAction="     + (string.IsNullOrEmpty(GifCancelAction)     ? "(none)" : "(set)")
                    + "  GifStopAction="       + (string.IsNullOrEmpty(GifStopAction)       ? "(none)" : "(set)")
                    + "  GifPauseAction="      + (string.IsNullOrEmpty(GifPauseAction)      ? "(none)" : "(set)")
                    + "  GifResumeAction="     + (string.IsNullOrEmpty(GifResumeAction)     ? "(none)" : "(set)")
                    + "  GifEncodingAction=" + (string.IsNullOrEmpty(GifEncodingAction) ? "(none)" : "(set)")
                    + "  GifFinishAction="   + (string.IsNullOrEmpty(GifFinishAction)   ? "(none)" : "(set)"));
            }
        }

        // Hotkey chords + enabled flag — used to skip rebinding every DynamicVariables reload.
        internal string HotkeyConfigKey
        {
            get
            {
                return (HotkeysEnabled ? "1" : "0")
                    + "|" + (HotkeyFullscreen ?? "")
                    + "|" + (HotkeyPredefined ?? "")
                    + "|" + (HotkeyCustom ?? "")
                    + "|" + (HotkeyWindowHandle ?? "")
                    + "|" + (HotkeyStoredWindow ?? "")
                    + "|" + (HotkeyOcr ?? "")
                    + "|" + (HotkeyGifToggle ?? "")
                    + "|" + (HotkeyGifToggleSnap ?? "");
            }
        }

        // Everything except SavePath / GifSavePath (those change every update with stamps).
        private string BuildDebugSignature()
        {
            return HotkeyConfigKey
                + "|" + DetectWindows + "|" + DetectControls + "|" + RoundWindowCorners
                + "|" + ShowCursor + "|" + ShowNotification + "|" + PlayNotificationSound
                + "|" + UsePrintWindow + "|" + JpegQuality + "|" + UITheme
                + "|" + PredefinedRegion
                + "|" + (FinishAction ?? "")
                + "|" + (NotificationClickAction ?? "")
                + "|" + OcrLanguage + "|" + OcrScaleFactor + "|" + OcrSingleLine
                + "|" + ShowOcrWindow + "|" + (OcrFinishAction ?? "")
                + "|" + GifFPS + "|" + GifQuality + "|" + GifCompression + "|" + GifDuration
                + "|" + GifPredefinedRegion
                + "|" + GifShowOverlay + "|" + GifShowEncodingWindow + "|" + GifShowStateDialogs
                + "|" + (GifStartAction ?? "") + "|" + (GifCancelAction ?? "")
                + "|" + (GifStopAction ?? "") + "|" + (GifPauseAction ?? "")
                + "|" + (GifResumeAction ?? "") + "|" + (GifEncodingAction ?? "")
                + "|" + (GifFinishAction ?? "");
        }
    }
}

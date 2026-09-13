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

namespace PluginScreenshot
{
    // Shared command routing for ExecuteBang and global hotkeys.
    internal static class CommandDispatcher
    {
        public static void Execute(Settings settings, string cmd)
        {
            if (settings == null || string.IsNullOrWhiteSpace(cmd))
                return;

            GifEncodingWindow.SetTheme(settings.UITheme);
            GifStateDialog.SetTheme(settings.UITheme);

            Logger.Log("CommandDispatcher: " + cmd);

            if (string.Equals(cmd, "-fs", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakeFullScreen(settings);
            }
            else if (string.Equals(cmd, "-ps", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakePredefined(settings);
            }
            else if (string.Equals(cmd, "-cs", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.TakeCustom(settings, () =>
                {
                    Logger.Log("Custom capture done, calling FinishAction.");
                    ScreenshotManager.ExecuteFinishAction(settings);
                });
            }
            else if (string.Equals(cmd, "-wh", StringComparison.OrdinalIgnoreCase))
            {
                ScreenshotManager.PickWindowHandle(settings);
            }
            else if (string.Equals(cmd, "-ocr", StringComparison.OrdinalIgnoreCase))
            {
                OcrManager.CaptureAndRecognize(settings);
            }
            else if (cmd.StartsWith("-ws|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring(4);
                if (string.IsNullOrWhiteSpace(windowTitle))
                    ScreenshotManager.TakeStoredWindowScreenshot(settings);
                else
                    ScreenshotManager.TakeWindowScreenshot(settings, windowTitle);
            }
            else if (string.Equals(cmd, "-gif-start", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsActive)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowAlreadyRecording(); }
                else
                    GifCaptureManager.StartRecording(settings, GifCaptureMode.FullScreen);
            }
            else if (string.Equals(cmd, "-gif-start-snap", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsActive)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowAlreadyRecording(); }
                else
                    GifCaptureManager.StartRecording(settings, GifCaptureMode.Snap);
            }
            else if (string.Equals(cmd, "-gif-start-predefined", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsActive)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowAlreadyRecording(); }
                else
                    GifCaptureManager.StartRecording(settings, GifCaptureMode.Predefined);
            }
            else if (cmd.StartsWith("-gif-start-window|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring("-gif-start-window|".Length);
                if (GifCaptureManager.IsActive)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowAlreadyRecording(); }
                else
                    GifCaptureManager.StartRecording(settings, GifCaptureMode.Window, windowTitle);
            }
            else if (string.Equals(cmd, "-gif-stop", StringComparison.OrdinalIgnoreCase))
            {
                if (!GifCaptureManager.IsRecording)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowNotRecording("stop"); }
                else
                    GifCaptureManager.StopAndSave(settings);
            }
            else if (string.Equals(cmd, "-gif-cancel", StringComparison.OrdinalIgnoreCase))
            {
                if (!GifCaptureManager.IsRecording)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowNotRecording("cancel"); }
                else
                    GifCaptureManager.CancelRecording(settings);
            }
            else if (string.Equals(cmd, "-gif-pause", StringComparison.OrdinalIgnoreCase))
            {
                if (!GifCaptureManager.IsRecording)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowNotRecording("pause"); }
                else
                    GifCaptureManager.PauseRecording();
            }
            else if (string.Equals(cmd, "-gif-toggle", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsEncoding)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowEncoding(); }
                else
                    GifCaptureManager.ToggleRecording(settings, GifCaptureMode.FullScreen);
            }
            else if (string.Equals(cmd, "-gif-toggle-snap", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsEncoding)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowEncoding(); }
                else
                    GifCaptureManager.ToggleRecording(settings, GifCaptureMode.Snap);
            }
            else if (string.Equals(cmd, "-gif-toggle-predefined", StringComparison.OrdinalIgnoreCase))
            {
                if (GifCaptureManager.IsEncoding)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowEncoding(); }
                else
                    GifCaptureManager.ToggleRecording(settings, GifCaptureMode.Predefined);
            }
            else if (cmd.StartsWith("-gif-toggle-window|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring("-gif-toggle-window|".Length);
                if (GifCaptureManager.IsEncoding)
                { if (settings.GifShowStateDialogs) GifStateDialog.ShowEncoding(); }
                else
                    GifCaptureManager.ToggleRecording(settings, GifCaptureMode.Window, windowTitle);
            }
        }

        public static string CommandFor(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.Fullscreen:      return "-fs";
                case HotkeyAction.Predefined:      return "-ps";
                case HotkeyAction.Custom:          return "-cs";
                case HotkeyAction.WindowHandle:    return "-wh";
                case HotkeyAction.StoredWindow:    return "-ws|";
                case HotkeyAction.Ocr:             return "-ocr";
                case HotkeyAction.GifToggle:       return "-gif-toggle";
                case HotkeyAction.GifToggleSnap:   return "-gif-toggle-snap";
                default: return null;
            }
        }
    }
}

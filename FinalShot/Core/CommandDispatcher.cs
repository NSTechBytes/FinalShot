using System;

namespace PluginScreenshot
{
    /// <summary>
    /// Shared command routing for ExecuteBang and global hotkeys.
    /// </summary>
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
            else if (string.Equals(cmd, "-ocr", StringComparison.OrdinalIgnoreCase))
            {
                OcrManager.CaptureAndRecognize(settings);
            }
            else if (cmd.StartsWith("-ws|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring(4);
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
            else if (cmd.StartsWith("ExecuteBatch ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(cmd.Split(' ')[1], out int code))
                {
                    if (code == 1) ScreenshotManager.TakeFullScreen(settings);
                    if (code == 2) ScreenshotManager.TakeCustom(settings, () => { });
                    if (code == 3) ScreenshotManager.TakePredefined(settings);
                }
            }
        }

        public static string CommandFor(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.Fullscreen:    return "-fs";
                case HotkeyAction.Predefined:    return "-ps";
                case HotkeyAction.Custom:        return "-cs";
                case HotkeyAction.Ocr:           return "-ocr";
                case HotkeyAction.GifToggle:     return "-gif-toggle";
                case HotkeyAction.GifToggleSnap: return "-gif-toggle-snap";
                default: return null;
            }
        }
    }
}

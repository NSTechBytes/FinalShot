using System;
using System.Runtime.InteropServices;
using Rainmeter;

namespace PluginScreenshot
{
    public static class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            var api = new API(rm);
            var settings = new Settings(api);
            data = GCHandle.ToIntPtr(GCHandle.Alloc(settings));
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            var handle = GCHandle.FromIntPtr(data);
            var settings = new Settings(new API(rm));
            handle.Target = settings;
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            return 0.0;
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            string cmd = Marshal.PtrToStringUni(args);
            var settings = (Settings)GCHandle.FromIntPtr(data).Target;

            Logger.Log($"ExecuteBang: {cmd} received.");

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
            else if (cmd.StartsWith("-ws|", StringComparison.OrdinalIgnoreCase))
            {
                string windowTitle = cmd.Substring(4);
                Logger.Log($"ExecuteBang: Window screenshot requested for '{windowTitle}'");
                ScreenshotManager.TakeWindowScreenshot(settings, windowTitle);
            }
            // ----------------------------------------------------------------
            //  GIF start commands — blocked when already recording / encoding
            // ----------------------------------------------------------------
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
            // ----------------------------------------------------------------
            //  GIF stop / cancel / pause — blocked when not recording
            // ----------------------------------------------------------------
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
            // ----------------------------------------------------------------
            //  GIF toggle commands — show dialog only when encoding is in progress
            //  (toggle handles idle↔recording itself; encoding state can't be interrupted)
            // ----------------------------------------------------------------
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
    }
}

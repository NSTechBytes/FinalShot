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

        // ================================================================== //
        //  Section Variables
        //  Usage in skin:  [&Measure_FinalShot:FunctionName()]
        //  Requires DynamicVariables=1 on the meter.
        //
        //  GetString() — default measure string value when referenced as
        //                [&Measure_FinalShot] without a function name.
        //                Returns the current status string (same as GetStatus).
        // ================================================================== //

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            // Default string value of the measure = current status
            if (GifCaptureManager.IsRecording) return Rainmeter.StringBuffer.Update("Recording");
            if (GifCaptureManager.IsEncoding)  return Rainmeter.StringBuffer.Update("Encoding");
            return Rainmeter.StringBuffer.Update("Idle");
        }

        /// <summary>
        /// Returns the numeric recording state.
        ///   1 = Idle
        ///   2 = Recording
        ///   3 = Encoding
        /// Usage: [&Measure_FinalShot:GetStatus()]
        /// </summary>
        [DllExport]
        public static IntPtr GetStatus(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            if (GifCaptureManager.IsRecording) return Rainmeter.StringBuffer.Update("2");
            if (GifCaptureManager.IsEncoding)  return Rainmeter.StringBuffer.Update("3");
            return Rainmeter.StringBuffer.Update("1");
        }

        /// <summary>
        /// Returns 1 if currently recording, -1 otherwise.
        /// Usage: [&Measure_FinalShot:IsRecording()]
        /// </summary>
        [DllExport]
        public static IntPtr IsRecording(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsRecording ? "1" : "-1");
        }

        /// <summary>
        /// Returns 1 if currently encoding, -1 otherwise.
        /// Usage: [&Measure_FinalShot:IsEncoding()]
        /// </summary>
        [DllExport]
        public static IntPtr IsEncoding(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsEncoding ? "1" : "-1");
        }

        /// <summary>
        /// Returns 1 if idle (not recording or encoding), -1 otherwise.
        /// Usage: [&Measure_FinalShot:IsIdle()]
        /// </summary>
        [DllExport]
        public static IntPtr IsIdle(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsIdle ? "1" : "-1");
        }

        /// <summary>
        /// Returns 1 if recording is paused, -1 otherwise.
        /// Usage: [&Measure_FinalShot:IsPaused()]
        /// </summary>
        [DllExport]
        public static IntPtr IsPaused(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsPaused ? "1" : "-1");
        }

        /// <summary>
        /// Returns the number of frames captured so far in the current recording.
        /// Returns "0" when not recording.
        /// Usage: [&Measure_FinalShot:GetFrameCount()]
        /// </summary>
        [DllExport]
        public static IntPtr GetFrameCount(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.FramesCaptured.ToString());
        }

        /// <summary>
        /// Returns the elapsed recording time as MM:SS (excludes paused time).
        /// Returns "00:00" when not recording.
        /// Usage: [&Measure_FinalShot:GetElapsedTime()]
        /// </summary>
        [DllExport]
        public static IntPtr GetElapsedTime(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            TimeSpan elapsed = GifCaptureManager.RecordingElapsed;
            return Rainmeter.StringBuffer.Update(
                $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}");
        }

        /// <summary>
        /// Returns the elapsed recording time in whole seconds (excludes paused time).
        /// Returns "0" when not recording.
        /// Usage: [&Measure_FinalShot:GetElapsedSeconds()]
        /// </summary>
        [DllExport]
        public static IntPtr GetElapsedSeconds(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(
                ((int)GifCaptureManager.RecordingElapsed.TotalSeconds).ToString());
        }

        /// <summary>
        /// Returns the full path of the last successfully saved GIF file.
        /// Returns "" if no GIF has been saved yet this session.
        /// Usage: [&Measure_FinalShot:GetSavePath()]
        /// </summary>
        [DllExport]
        public static IntPtr GetSavePath(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.LastSavedPath);
        }

        /// <summary>
        /// Returns the file size of the last saved GIF as a human-readable string
        /// e.g. "2.4 MB", "512 KB", "980 B".
        /// Returns "" if no GIF has been saved yet this session.
        /// Usage: [&Measure_FinalShot:GetLastFileSize()]
        /// </summary>
        [DllExport]
        public static IntPtr GetLastFileSize(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            long bytes = GifCaptureManager.LastFileSizeBytes;
            if (bytes <= 0)
                return Rainmeter.StringBuffer.Update("");
            string size;
            if      (bytes >= 1024L * 1024 * 1024) size = $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            else if (bytes >= 1024L * 1024)         size = $"{bytes / (1024.0 * 1024):F1} MB";
            else if (bytes >= 1024L)                size = $"{bytes / 1024.0:F1} KB";
            else                                    size = $"{bytes} B";
            return Rainmeter.StringBuffer.Update(size);
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

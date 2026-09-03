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

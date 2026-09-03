using System;
using System.IO;

namespace PluginScreenshot
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        public static bool DebugEnabled = false;
        public static string LogFilePath = "FinalShotDebug.log";
        public static long MaxLogFileSize = 5 * 1024 * 1024;

        public static void Log(string message)
        {
            if (!DebugEnabled) return;
            try
            {
                lock (_lock)
                {
                    RotateIfNeeded();
                    string logMessage = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                        " " + message + Environment.NewLine;
                    File.AppendAllText(LogFilePath, logMessage);
                }
            }
            catch {}
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Length >= MaxLogFileSize)
                    {
                        string name = Path.GetFileNameWithoutExtension(LogFilePath);
                        string ext = Path.GetExtension(LogFilePath);
                        string archive = string.Format("{0}_{1:yyyyMMddHHmmss}{2}",
                                                       name, DateTime.Now, ext);
                        File.Move(LogFilePath, archive);
                    }
                }
            }
            catch {}
        }
    }
}

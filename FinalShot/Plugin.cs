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
            HotkeyManager.UpdateBindings(data, settings);
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            HotkeyManager.RemoveBindings(data);
            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            var handle = GCHandle.FromIntPtr(data);
            var settings = new Settings(new API(rm));
            handle.Target = settings;
            HotkeyManager.UpdateBindings(data, settings);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            BangQueue.Flush();
            return 0.0;
        }

        //  Section Variables
        //  Usage in skin:  [&Measure_FinalShot:FunctionName()]
        //  Requires DynamicVariables=1 on the meter.
        //
        //  GetString() -- default measure string value when referenced as
        //                [&Measure_FinalShot] without a function name.
        //                Returns the current status string (same as GetStatus).

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            // Default string value of the measure = current status
            if (GifCaptureManager.IsRecording) return Rainmeter.StringBuffer.Update("Recording");
            if (GifCaptureManager.IsEncoding)  return Rainmeter.StringBuffer.Update("Encoding");
            return Rainmeter.StringBuffer.Update("Idle");
        }

        // Returns the numeric recording state.
        //   1 = Idle
        //   2 = Recording
        //   3 = Encoding
        // Usage: [&Measure_FinalShot:GetStatus()]
        [DllExport]
        public static IntPtr GetStatus(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            if (GifCaptureManager.IsRecording) return Rainmeter.StringBuffer.Update("2");
            if (GifCaptureManager.IsEncoding)  return Rainmeter.StringBuffer.Update("3");
            return Rainmeter.StringBuffer.Update("1");
        }

        // Returns 1 if currently recording, -1 otherwise.
        // Usage: [&Measure_FinalShot:IsRecording()]
        [DllExport]
        public static IntPtr IsRecording(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsRecording ? "1" : "-1");
        }

        // Returns 1 if currently encoding, -1 otherwise.
        // Usage: [&Measure_FinalShot:IsEncoding()]
        [DllExport]
        public static IntPtr IsEncoding(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsEncoding ? "1" : "-1");
        }

        // Returns 1 if idle (not recording or encoding), -1 otherwise.
        // Usage: [&Measure_FinalShot:IsIdle()]
        [DllExport]
        public static IntPtr IsIdle(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsIdle ? "1" : "-1");
        }

        // Returns 1 if recording is paused, -1 otherwise.
        // Usage: [&Measure_FinalShot:IsPaused()]
        [DllExport]
        public static IntPtr IsPaused(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.IsPaused ? "1" : "-1");
        }

        // Returns the number of frames captured so far in the current recording.
        // Returns "0" when not recording.
        // Usage: [&Measure_FinalShot:GetFrameCount()]
        [DllExport]
        public static IntPtr GetFrameCount(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.FramesCaptured.ToString());
        }

        // Returns the elapsed recording time as MM:SS (excludes paused time).
        // Returns "00:00" when not recording.
        // Usage: [&Measure_FinalShot:GetElapsedTime()]
        [DllExport]
        public static IntPtr GetElapsedTime(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            TimeSpan elapsed = GifCaptureManager.RecordingElapsed;
            return Rainmeter.StringBuffer.Update(
                $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}");
        }

        // Returns the elapsed recording time in whole seconds (excludes paused time).
        // Returns "0" when not recording.
        // Usage: [&Measure_FinalShot:GetElapsedSeconds()]
        [DllExport]
        public static IntPtr GetElapsedSeconds(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(
                ((int)GifCaptureManager.RecordingElapsed.TotalSeconds).ToString());
        }

        // Returns the full path of the last successfully saved GIF file.
        // Returns "" if no GIF has been saved yet this session.
        // Usage: [&Measure_FinalShot:GetSavePath()]
        [DllExport]
        public static IntPtr GetSavePath(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(GifCaptureManager.LastSavedPath);
        }

        // Returns the file size of the last saved GIF as a human-readable string
        // e.g. "2.4 MB", "512 KB", "980 B".
        // Returns "" if no GIF has been saved yet this session.
        // Usage: [&Measure_FinalShot:GetLastFileSize()]
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

        // Returns the last OCR result text (empty if none / cancelled / failed).
        // After ShowOCRWindow=1, this reflects any edits made in the result window.
        // Usage: [&Measure_FinalShot:GetLastOCRText()]
        [DllExport]
        public static IntPtr GetLastOCRText(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(OcrManager.LastOcrText);
        }

        // Returns the title of the window stored by -wh (empty if none / cleared / invalid).
        // Usage: [&Measure_FinalShot:GetLastStoredWindow()]
        [DllExport]
        public static IntPtr GetLastStoredWindow(IntPtr data, int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            return Rainmeter.StringBuffer.Update(StoredWindowTarget.Title);
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            string cmd = Marshal.PtrToStringUni(args);
            var settings = (Settings)GCHandle.FromIntPtr(data).Target;
            Logger.Log($"ExecuteBang: {cmd} received.");
            CommandDispatcher.Execute(settings, cmd);
        }
    }
}

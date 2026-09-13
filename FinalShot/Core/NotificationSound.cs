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
using System.IO;
using System.Runtime.InteropServices;

namespace PluginScreenshot
{
    // Plays a real Windows Media WAV for capture toasts.
    // Avoids SystemSounds / MessageBeep, which stay silent when the Asterisk
    // scheme event is set to "(None)" (common on Windows 11).
    internal static class NotificationSound
    {
        private const uint SND_ASYNC = 0x0001;
        private const uint SND_FILENAME = 0x00020000;
        private const uint SND_NODEFAULT = 0x0002;

        private static readonly string[] CandidateFiles =
        {
            "Windows Notify System Generic.wav",
            "Windows Notify.wav",
            "notify.wav",
            "Windows Ding.wav",
            "ding.wav"
        };

        [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        public static void Play()
        {
            try
            {
                string media = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Media");

                foreach (string name in CandidateFiles)
                {
                    string path = Path.Combine(media, name);
                    if (!File.Exists(path))
                        continue;

                    if (PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT))
                        return;
                }

                Logger.Log("NotificationSound: no playable Windows Media notify WAV found.");
            }
            catch (Exception ex)
            {
                Logger.Log("NotificationSound error: " + ex.Message);
            }
        }
    }
}

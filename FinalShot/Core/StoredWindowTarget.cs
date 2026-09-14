/*
 * Copyright (c) 2026 nstechbytes
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
    // Process-wide HWND selected by -wh for later capture via -ws| (empty title).
    internal static class StoredWindowTarget
    {
        private static readonly object Sync = new object();
        private static IntPtr _handle = IntPtr.Zero;
        private static string _title = "";

        public static string Title
        {
            get { lock (Sync) return _title ?? ""; }
        }

        public static void Set(IntPtr hWnd, string title)
        {
            lock (Sync)
            {
                _handle = hWnd;
                _title = title ?? "";
                Logger.Log("StoredWindowTarget: stored HWND=" + hWnd.ToInt64()
                    + " title='" + _title + "'");
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                if (_handle != IntPtr.Zero)
                    Logger.Log("StoredWindowTarget: cleared HWND=" + _handle.ToInt64());
                _handle = IntPtr.Zero;
                _title = "";
            }
        }

        // Returns true if a stored handle exists and IsWindow succeeds.
        // On failure, clears the store.
        public static bool TryGetValid(out IntPtr hWnd, out string title)
        {
            lock (Sync)
            {
                hWnd = _handle;
                title = _title ?? "";

                if (hWnd == IntPtr.Zero)
                {
                    Logger.Log("StoredWindowTarget: no stored window handle.");
                    return false;
                }

                if (!NativeMethods.IsWindow(hWnd))
                {
                    Logger.Log("StoredWindowTarget: stored HWND=" + hWnd.ToInt64()
                        + " is no longer valid; clearing.");
                    _handle = IntPtr.Zero;
                    _title = "";
                    hWnd = IntPtr.Zero;
                    title = "";
                    return false;
                }

                return true;
            }
        }
    }
}

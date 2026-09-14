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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    // Process-wide WH_KEYBOARD_LL hotkey manager for FinalShot.
    // Installs the hook only while at least one measure has bindings.
    internal static class HotkeyManager
    {
        private static readonly object Sync = new object();

        // measureId (GCHandle IntPtr) -> bindings for that measure
        private static readonly Dictionary<IntPtr, MeasureBindings> Measures =
            new Dictionary<IntPtr, MeasureBindings>();

        // Flat lookup rebuilt on each UpdateBindings / RemoveBindings
        private static List<BindingEntry> _flat = new List<BindingEntry>();

        private static NativeMethods.LowLevelKeyboardProc _hookProc;
        private static IntPtr _hook = IntPtr.Zero;
        private static Thread _pumpThread;
        private static uint _pumpThreadId;
        private static volatile bool _pumpRunning;
        private static int _dispatchBusy;
        private static int _lastVk = -1;
        private static int _lastTick;

        private sealed class MeasureBindings
        {
            public Settings Settings;
            public string ConfigKey;
            public List<BindingEntry> Entries = new List<BindingEntry>();
        }

        private sealed class BindingEntry
        {
            public HotkeyChord Chord;
            public HotkeyAction Action;
            public Settings Settings;
        }

        // Replace hotkey bindings for a measure. Pass settings with HotkeysEnabled
        // and Hotkey* chords. Empty / disabled removes that measure from the hook.
        public static void UpdateBindings(IntPtr measureId, Settings settings)
        {
            if (measureId == IntPtr.Zero)
                return;

            lock (Sync)
            {
                string hotkeyKey = settings != null ? settings.HotkeyConfigKey : "";

                // DynamicVariables=1 calls Reload every update. Refresh Settings
                // pointers only when hotkey chords did not change — avoid rebind spam.
                MeasureBindings existing;
                if (Measures.TryGetValue(measureId, out existing) &&
                    existing.ConfigKey == hotkeyKey)
                {
                    existing.Settings = settings;
                    foreach (var e in existing.Entries)
                        e.Settings = settings;
                    RefreshFlatSettingsLocked(measureId, settings);
                    return;
                }

                Measures.Remove(measureId);

                if (settings != null && settings.HotkeysEnabled)
                {
                    var entries = BuildEntries(settings);
                    if (entries.Count > 0)
                    {
                        Measures[measureId] = new MeasureBindings
                        {
                            Settings = settings,
                            ConfigKey = hotkeyKey,
                            Entries = entries
                        };
                        Logger.Log("HotkeyManager: measure " + measureId.ToInt64()
                            + " registered " + entries.Count + " hotkey(s).");
                    }
                    else
                    {
                        // Enabled but no valid chords — keep key so we skip rebuilds.
                        Measures[measureId] = new MeasureBindings
                        {
                            Settings = settings,
                            ConfigKey = hotkeyKey,
                            Entries = new List<BindingEntry>()
                        };
                    }
                }
                else if (settings != null)
                {
                    Measures[measureId] = new MeasureBindings
                    {
                        Settings = settings,
                        ConfigKey = hotkeyKey,
                        Entries = new List<BindingEntry>()
                    };
                }

                RebuildFlatLocked();
                EnsureHookStateLocked();
            }
        }

        private static void RefreshFlatSettingsLocked(IntPtr measureId, Settings settings)
        {
            MeasureBindings mb;
            if (!Measures.TryGetValue(measureId, out mb) || mb.Entries.Count == 0)
                return;

            var set = new HashSet<BindingEntry>(mb.Entries);
            for (int i = 0; i < _flat.Count; i++)
            {
                if (set.Contains(_flat[i]))
                    _flat[i].Settings = settings;
            }
        }

        public static void RemoveBindings(IntPtr measureId)
        {
            if (measureId == IntPtr.Zero)
                return;

            lock (Sync)
            {
                if (Measures.Remove(measureId))
                    Logger.Log("HotkeyManager: measure " + measureId.ToInt64() + " removed.");
                RebuildFlatLocked();
                EnsureHookStateLocked();
            }
        }

        private static List<BindingEntry> BuildEntries(Settings settings)
        {
            var list = new List<BindingEntry>();
            TryAdd(list, settings, settings.HotkeyFullscreen, HotkeyAction.Fullscreen);
            TryAdd(list, settings, settings.HotkeyPredefined, HotkeyAction.Predefined);
            TryAdd(list, settings, settings.HotkeyCustom, HotkeyAction.Custom);
            TryAdd(list, settings, settings.HotkeyWindowHandle, HotkeyAction.WindowHandle);
            TryAdd(list, settings, settings.HotkeyStoredWindow, HotkeyAction.StoredWindow);
            TryAdd(list, settings, settings.HotkeyOcr, HotkeyAction.Ocr);
            TryAdd(list, settings, settings.HotkeyGifToggle, HotkeyAction.GifToggle);
            TryAdd(list, settings, settings.HotkeyGifToggleSnap, HotkeyAction.GifToggleSnap);
            return list;
        }

        private static void TryAdd(List<BindingEntry> list, Settings settings,
            string chordText, HotkeyAction action)
        {
            if (string.IsNullOrWhiteSpace(chordText))
                return;

            HotkeyChord chord = HotkeyChord.TryParse(chordText, out string error);
            if (chord == null)
            {
                if (!string.IsNullOrEmpty(error))
                    Logger.Log("HotkeyManager: invalid " + action + " hotkey -- " + error);
                return;
            }

            list.Add(new BindingEntry
            {
                Chord = chord,
                Action = action,
                Settings = settings
            });
        }

        private static void RebuildFlatLocked()
        {
            var flat = new List<BindingEntry>();
            var seen = new Dictionary<HotkeyChord, HotkeyAction>();

            foreach (var kv in Measures)
            {
                foreach (var e in kv.Value.Entries)
                {
                    if (seen.TryGetValue(e.Chord, out HotkeyAction existing) && existing != e.Action)
                    {
                        Logger.Log("HotkeyManager: chord '" + e.Chord.Source
                            + "' conflict -- last measure wins (" + e.Action + " over " + existing + ").");
                    }
                    seen[e.Chord] = e.Action;
                    // Last write wins: remove previous same chord then add
                    flat.RemoveAll(x => x.Chord.Equals(e.Chord));
                    flat.Add(e);
                }
            }

            _flat = flat;
            Logger.Log("HotkeyManager: active bindings=" + _flat.Count);
        }

        private static void EnsureHookStateLocked()
        {
            bool needHook = _flat.Count > 0;
            if (needHook && _hook == IntPtr.Zero)
                StartHookLocked();
            else if (!needHook && _hook != IntPtr.Zero)
                StopHookLocked();
        }

        private static void StartHookLocked()
        {
            if (_pumpRunning)
                return;

            _hookProc = HookCallback; // keep alive
            _pumpRunning = true;
            var ready = new ManualResetEvent(false);
            Exception startError = null;

            _pumpThread = new Thread(() =>
            {
                try
                {
                    _pumpThreadId = NativeMethods.GetCurrentThreadId();
                    _hook = NativeMethods.SetWindowsHookEx(
                        NativeMethods.WH_KEYBOARD_LL,
                        _hookProc,
                        IntPtr.Zero,
                        0);

                    if (_hook == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        startError = new InvalidOperationException(
                            "SetWindowsHookEx failed, error=" + err);
                        _pumpRunning = false;
                        ready.Set();
                        return;
                    }

                    Logger.Log("HotkeyManager: LL keyboard hook installed.");
                    ready.Set();

                    while (_pumpRunning)
                    {
                        int r = NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0);
                        if (r <= 0)
                            break;
                        NativeMethods.TranslateMessage(ref msg);
                        NativeMethods.DispatchMessage(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("HotkeyManager: pump thread error -- " + ex.Message);
                    startError = startError ?? ex;
                    ready.Set();
                }
                finally
                {
                    if (_hook != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(_hook);
                        _hook = IntPtr.Zero;
                        Logger.Log("HotkeyManager: LL keyboard hook removed.");
                    }
                    _pumpRunning = false;
                    _pumpThreadId = 0;
                }
            });

            _pumpThread.IsBackground = true;
            _pumpThread.Name = "FinalShot-HotkeyHook";
            _pumpThread.Start();

            if (!ready.WaitOne(3000))
            {
                Logger.Log("HotkeyManager: hook thread start timed out.");
                _pumpRunning = false;
            }
            else if (startError != null)
            {
                Logger.Log("HotkeyManager: " + startError.Message);
            }
        }

        private static void StopHookLocked()
        {
            if (!_pumpRunning && _hook == IntPtr.Zero)
                return;

            _pumpRunning = false;
            uint tid = _pumpThreadId;
            if (tid != 0)
            {
                try
                {
                    NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Logger.Log("HotkeyManager: PostThreadMessage failed -- " + ex.Message);
                }
            }

            Thread t = _pumpThread;
            _pumpThread = null;
            if (t != null && t.IsAlive)
            {
                if (!t.Join(2000))
                    Logger.Log("HotkeyManager: pump thread did not exit in time.");
            }

            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && lParam != IntPtr.Zero)
                {
                    int msg = wParam.ToInt32();
                    var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    bool isUp = (info.flags & NativeMethods.LLKHF_UP) != 0;
                    int vk = (int)info.vkCode;

                    // PrintScreen often arrives only as key-up; other keys fire on key-down.
                    bool isSnapshot = vk == NativeMethods.VK_SNAPSHOT;
                    bool interested = isSnapshot ? isUp : !isUp;

                    if (interested &&
                        (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN
                         || msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP))
                    {
                        if (TryHandleKey(vk))
                            return (IntPtr)1; // suppress
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("HotkeyManager.HookCallback: " + ex.Message);
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool TryHandleKey(int vk)
        {
            // Debounce auto-repeat / double PrintScreen
            int tick = Environment.TickCount;
            if (vk == _lastVk && (tick - _lastTick) < 350)
                return false;

            bool ctrl = IsDown(NativeMethods.VK_CONTROL);
            bool alt = IsDown(NativeMethods.VK_MENU);
            bool shift = IsDown(NativeMethods.VK_SHIFT);
            bool win = IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);

            // When the chord key is a modifier-only press we already reject at parse time.
            Keys key = (Keys)vk;

            List<BindingEntry> snapshot;
            lock (Sync)
                snapshot = _flat;

            foreach (var entry in snapshot)
            {
                if (!entry.Chord.Matches(ctrl, alt, shift, win, key))
                    continue;

                _lastVk = vk;
                _lastTick = tick;

                Settings settings = entry.Settings;
                HotkeyAction action = entry.Action;
                string cmd = CommandDispatcher.CommandFor(action);
                if (string.IsNullOrEmpty(cmd))
                    return false;

                // Only suppress the key when we actually accept the dispatch.
                // Previously we suppressed first, then Dispatch could no-op if busy.
                if (Interlocked.CompareExchange(ref _dispatchBusy, 1, 0) != 0)
                {
                    Logger.Log("HotkeyManager: '" + cmd + "' not suppressed -- another action is busy.");
                    return false;
                }

                Logger.Log("HotkeyManager: matched '" + entry.Chord.Source + "' -> " + cmd);
                ThreadPool.QueueUserWorkItem(_ => DispatchAccepted(settings, cmd));
                return true;
            }

            return false;
        }

        private static bool IsDown(int vKey)
        {
            return (NativeMethods.GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        // Busy flag already taken by the hook thread before queueing.
        private static void DispatchAccepted(Settings settings, string cmd)
        {
            try
            {
                if (settings != null && !string.IsNullOrEmpty(cmd))
                    CommandDispatcher.Execute(settings, cmd);
            }
            catch (Exception ex)
            {
                Logger.Log("HotkeyManager.Dispatch: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _dispatchBusy, 0);
            }
        }
    }
}

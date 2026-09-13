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
using System.Globalization;
using System.Windows.Forms;

namespace PluginScreenshot
{
    // Parsed hotkey chord, e.g. Ctrl+Shift+PrintScreen.
    internal sealed class HotkeyChord : IEquatable<HotkeyChord>
    {
        public bool Ctrl { get; }
        public bool Alt { get; }
        public bool Shift { get; }
        public bool Win { get; }
        public Keys Key { get; }
        public string Source { get; }

        public HotkeyChord(bool ctrl, bool alt, bool shift, bool win, Keys key, string source)
        {
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Win = win;
            Key = key;
            Source = source ?? "";
        }

        public bool Matches(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            return Ctrl == ctrl && Alt == alt && Shift == shift && Win == win && Key == key;
        }

        public override string ToString() => Source;

        public bool Equals(HotkeyChord other)
        {
            if (other == null) return false;
            return Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift
                && Win == other.Win && Key == other.Key;
        }

        public override bool Equals(object obj) => Equals(obj as HotkeyChord);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Key;
                h = (h * 397) ^ (Ctrl ? 1 : 0);
                h = (h * 397) ^ (Alt ? 2 : 0);
                h = (h * 397) ^ (Shift ? 4 : 0);
                h = (h * 397) ^ (Win ? 8 : 0);
                return h;
            }
        }

        // Parses a chord string. Returns null if empty or invalid.
        public static HotkeyChord TryParse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string raw = text.Trim();
            string[] parts = raw.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "empty chord";
                return null;
            }

            bool ctrl = false, alt = false, shift = false, win = false;
            Keys? key = null;

            foreach (string partRaw in parts)
            {
                string part = partRaw.Trim();
                if (part.Length == 0) continue;

                string p = part.ToLowerInvariant();
                if (p == "ctrl" || p == "control") { ctrl = true; continue; }
                if (p == "alt") { alt = true; continue; }
                if (p == "shift") { shift = true; continue; }
                if (p == "win" || p == "lwin" || p == "rwin" || p == "windows") { win = true; continue; }

                if (key.HasValue)
                {
                    error = "multiple non-modifier keys in: " + raw;
                    return null;
                }

                if (!TryParseKey(part, out Keys parsed))
                {
                    error = "unknown key '" + part + "' in: " + raw;
                    return null;
                }
                key = parsed;
            }

            if (!key.HasValue)
            {
                error = "modifier-only chord not allowed: " + raw;
                return null;
            }

            // Key must not be a pure modifier
            if (IsModifierKey(key.Value))
            {
                error = "cannot use modifier as primary key: " + raw;
                return null;
            }

            return new HotkeyChord(ctrl, alt, shift, win, key.Value, raw);
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey
                || key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey
                || key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu
                || key == Keys.LWin || key == Keys.RWin;
        }

        private static bool TryParseKey(string token, out Keys key)
        {
            key = Keys.None;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            string t = token.Trim();
            string lower = t.ToLowerInvariant();

            // Named aliases
            var aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
            {
                { "printscreen", Keys.PrintScreen },
                { "prtscr", Keys.PrintScreen },
                { "prtsc", Keys.PrintScreen },
                { "snapshot", Keys.PrintScreen },
                { "ins", Keys.Insert },
                { "insert", Keys.Insert },
                { "del", Keys.Delete },
                { "delete", Keys.Delete },
                { "home", Keys.Home },
                { "end", Keys.End },
                { "pgup", Keys.PageUp },
                { "pageup", Keys.PageUp },
                { "pgdn", Keys.PageDown },
                { "pagedown", Keys.PageDown },
                { "up", Keys.Up },
                { "down", Keys.Down },
                { "left", Keys.Left },
                { "right", Keys.Right },
                { "esc", Keys.Escape },
                { "escape", Keys.Escape },
                { "tab", Keys.Tab },
                { "space", Keys.Space },
                { "enter", Keys.Enter },
                { "return", Keys.Enter },
                { "backspace", Keys.Back },
                { "bksp", Keys.Back },
            };

            if (aliases.TryGetValue(lower, out key))
                return true;

            // F1-F24
            if (lower.Length >= 2 && lower[0] == 'f'
                && int.TryParse(lower.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)
                && f >= 1 && f <= 24)
            {
                key = (Keys)((int)Keys.F1 + (f - 1));
                return true;
            }

            // Hex VK: 0x2C
            if (lower.StartsWith("0x")
                && int.TryParse(lower.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vkHex)
                && vkHex > 0 && vkHex <= 0xFF)
            {
                key = (Keys)vkHex;
                return true;
            }

            // Single letter / digit
            if (t.Length == 1)
            {
                char c = char.ToUpperInvariant(t[0]);
                if (c >= 'A' && c <= 'Z') { key = (Keys)c; return true; }
                if (c >= '0' && c <= '9') { key = (Keys)c; return true; }
            }

            // Enum name fallback (e.g. Oemcomma)
            if (Enum.TryParse(t, true, out Keys enumKey) && enumKey != Keys.None)
            {
                key = enumKey;
                return true;
            }

            return false;
        }
    }

    internal enum HotkeyAction
    {
        Fullscreen,
        Predefined,
        Custom,
        WindowHandle,
        StoredWindow,
        Ocr,
        GifToggle,
        GifToggleSnap
    }
}

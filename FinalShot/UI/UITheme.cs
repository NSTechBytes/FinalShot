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
using System.Drawing;
using Microsoft.Win32;

namespace PluginScreenshot
{
    //  UITheme  --  shared palette for Notification, EncodingWindow, Dialog

    // Three theme modes the user can choose in Main.ini / GIF.ini.
    //   0 = Dark   -- always dark (default, matches the plugin's design)
    //   1 = Light  -- always light
    //   2 = System -- follows the Windows "Apps use light theme" setting
    public enum UITheme { Dark = 0, Light = 1, System = 2 }

    // Resolved colour palette for a given UITheme.
    // All UI windows obtain colours from here so a single theme change
    // propagates everywhere.
    internal sealed class ThemeColors
    {
        public Color Background  { get; }   // main window background
        public Color CardBg      { get; }   // card / panel background
        public Color BtnBg       { get; }   // button background
        public Color BtnHover    { get; }   // button hover
        public Color BarTrack    { get; }   // progress bar track

        public Color Border      { get; }   // outer window border
        public Color BtnBorder   { get; }   // button border
        public Color Divider     { get; }   // internal divider lines

        public Color TextPrimary   { get; }
        public Color TextSecondary { get; }
        public Color BtnText       { get; }

        public Color AccentBlue   { get; } = Color.FromArgb(0, 120, 212);
        public Color AccentGreen  { get; } = Color.FromArgb(0, 200, 100);
        public Color AccentAmber  { get; } = Color.FromArgb(255, 160, 30);

        public Color CloseNormal  { get; }
        public Color CloseHover   { get; }

        private ThemeColors(bool dark)
        {
            if (dark)
            {
                Background   = Color.FromArgb(16,  18,  22);
                CardBg       = Color.FromArgb(22,  26,  34);
                BtnBg        = Color.FromArgb(35,  38,  45);
                BtnHover     = Color.FromArgb(50,  54,  64);
                BarTrack     = Color.FromArgb(35,  38,  45);
                Border       = Color.FromArgb(45,  48,  55);
                BtnBorder    = Color.FromArgb(60,  64,  74);
                Divider      = Color.FromArgb(35,  40,  50);
                TextPrimary  = Color.FromArgb(220, 220, 220);
                TextSecondary= Color.FromArgb(140, 150, 165);
                BtnText      = Color.FromArgb(220, 220, 220);
                CloseNormal  = Color.FromArgb(120, 120, 120);
                CloseHover   = Color.White;
            }
            else
            {
                Background   = Color.FromArgb(245, 246, 248);
                CardBg       = Color.FromArgb(255, 255, 255);
                BtnBg        = Color.FromArgb(230, 232, 238);
                BtnHover     = Color.FromArgb(210, 214, 224);
                BarTrack     = Color.FromArgb(210, 214, 224);
                Border       = Color.FromArgb(200, 202, 210);
                BtnBorder    = Color.FromArgb(180, 184, 194);
                Divider      = Color.FromArgb(220, 222, 228);
                TextPrimary  = Color.FromArgb(20,  22,  28);
                TextSecondary= Color.FromArgb(90,  96, 110);
                BtnText      = Color.FromArgb(20,  22,  28);
                CloseNormal  = Color.FromArgb(120, 120, 120);
                CloseHover   = Color.FromArgb(20,  22,  28);
            }
        }

        //  Factory

        public static ThemeColors Resolve(UITheme theme)
        {
            bool dark;
            switch (theme)
            {
                case UITheme.Light:  dark = false; break;
                case UITheme.Dark:   dark = true;  break;
                default:             dark = IsSystemDark(); break;
            }
            return new ThemeColors(dark);
        }

        private static bool IsSystemDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("AppsUseLightTheme");
                        if (v != null) return (int)v == 0;
                    }
                }
            }
            catch { }
            return true; // default dark
        }

        // Convenience: parse "dark"/"light"/"system" or "0"/"1"/"2"
        public static UITheme ParseTheme(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return UITheme.Dark;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "1": case "light":  return UITheme.Light;
                case "2": case "system": return UITheme.System;
                default:                 return UITheme.Dark;
            }
        }
    }
}

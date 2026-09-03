using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PluginScreenshot
{
    public class NotificationForm : Form
    {
        private readonly Timer _autoCloseTimer;
        private readonly Timer _fadeOutTimer;
        private readonly string _imagePath;
        private const int NotificationWidth = 380;
        private const int NotificationHeight = 120;
        private const int DisplayDuration = 4000;
        private const int FadeOutDuration = 500;
        private double _opacity = 1.0;
        private readonly bool _isDarkMode;

        public NotificationForm(string imagePath, string captureType)
        {
            _imagePath = imagePath;
            _isDarkMode = IsWindowsDarkMode();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            Width = NotificationWidth;
            Height = NotificationHeight;
            BackColor = _isDarkMode ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            Opacity = 0;

            var workingArea = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                workingArea.Right - Width - 20,
                workingArea.Bottom - Height - 20
            );

            CreateNotificationUI(captureType);

            _autoCloseTimer = new Timer { Interval = DisplayDuration };
            _autoCloseTimer.Tick += (s, e) =>
            {
                _autoCloseTimer.Stop();
                StartFadeOut();
            };

            _fadeOutTimer = new Timer { Interval = 20 };
            _fadeOutTimer.Tick += FadeOutTick;

            Click += (s, e) => StartFadeOut();

            Load += (s, e) =>
            {
                FadeIn();
                _autoCloseTimer.Start();
            };
        }

        private bool IsWindowsDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value != null)
                        {
                            return (int)value == 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to detect Windows theme: {ex.Message}");
            }
            return true;
        }

        private void CreateNotificationUI(string captureType)
        {
            Color panelBackColor = _isDarkMode ? Color.FromArgb(40, 40, 40) : Color.FromArgb(250, 250, 250);
            Color textColor = _isDarkMode ? Color.White : Color.FromArgb(30, 30, 30);
            Color subtitleColor = _isDarkMode ? Color.FromArgb(180, 180, 180) : Color.FromArgb(100, 100, 100);
            Color closeButtonColor = _isDarkMode ? Color.FromArgb(150, 150, 150) : Color.FromArgb(100, 100, 100);
            Color closeButtonHoverColor = _isDarkMode ? Color.White : Color.Black;
            Color thumbnailBorderColor = _isDarkMode ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 200, 200);

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = panelBackColor
            };

            var thumbnail = new PictureBox
            {
                Location = new Point(10, 10),
                Size = new Size(100, 100),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = thumbnailBorderColor
            };

            try
            {
                using (var img = Image.FromFile(_imagePath))
                {
                    thumbnail.Image = new Bitmap(img, thumbnail.Size);
                }
            }
            catch
            {
                thumbnail.BackColor = thumbnailBorderColor;
            }

            var successLabel = new Label
            {
                Text = "FinalShot",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 200, 100),
                Location = new Point(120, 10),
                Size = new Size(200, 30),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var titleLabel = new Label
            {
                Text = "Screenshot Captured!",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = textColor,
                Location = new Point(120, 45),
                Size = new Size(250, 25),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var subtitleLabel = new Label
            {
                Text = captureType,
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                ForeColor = subtitleColor,
                Location = new Point(120, 70),
                Size = new Size(250, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var closeButton = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = closeButtonColor,
                Location = new Point(NotificationWidth - 35, 5),
                Size = new Size(25, 25),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            closeButton.Click += (s, e) => StartFadeOut();
            closeButton.MouseEnter += (s, e) => closeButton.ForeColor = closeButtonHoverColor;
            closeButton.MouseLeave += (s, e) => closeButton.ForeColor = closeButtonColor;

            panel.Controls.Add(thumbnail);
            panel.Controls.Add(successLabel);
            panel.Controls.Add(titleLabel);
            panel.Controls.Add(subtitleLabel);
            panel.Controls.Add(closeButton);
            Controls.Add(panel);

            foreach (Control ctrl in panel.Controls)
            {
                if (ctrl is Label && ctrl != closeButton)
                {
                    ctrl.Click += (s, e) => StartFadeOut();
                }
            }
        }

        private void FadeIn()
        {
            var fadeInTimer = new Timer { Interval = 10 };
            double targetOpacity = 0.95;
            double step = 0.05;

            fadeInTimer.Tick += (s, e) =>
            {
                _opacity += step;
                if (_opacity >= targetOpacity)
                {
                    _opacity = targetOpacity;
                    Opacity = _opacity;
                    fadeInTimer.Stop();
                    fadeInTimer.Dispose();
                }
                else
                {
                    Opacity = _opacity;
                }
            };
            fadeInTimer.Start();
        }

        private void StartFadeOut()
        {
            if (_fadeOutTimer.Enabled) return;
            _autoCloseTimer.Stop();
            _fadeOutTimer.Start();
        }

        private void FadeOutTick(object sender, EventArgs e)
        {
            _opacity -= 0.05;
            if (_opacity <= 0)
            {
                _fadeOutTimer.Stop();
                Close();
            }
            else
            {
                Opacity = _opacity;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _autoCloseTimer?.Dispose();
                _fadeOutTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

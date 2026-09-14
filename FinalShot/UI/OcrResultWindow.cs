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
using System.Drawing;
using System.Windows.Forms;

namespace PluginScreenshot
{
    // Themed OCR result window (ShareX-style): preview + editable text + Copy / Close.
    // Must be shown on an STA thread. Returns the final text from the editor on close.
    internal sealed class OcrResultWindow : Form
    {
        private readonly ThemeColors _t;
        private readonly TextBox _textBox;
        private readonly PictureBox _preview;
        private readonly Label _status;
        private string _resultText;

        public string ResultText => _resultText ?? "";

        // Shows a modal OCR result dialog. Caller owns preview lifetime
        // after this returns (a clone is used for display).
        public static string ShowDialog(string text, Bitmap preview, UITheme theme)
        {
            using (var form = new OcrResultWindow(text, preview, theme))
            {
                form.ShowDialog();
                return form.ResultText;
            }
        }

        private OcrResultWindow(string text, Bitmap preview, UITheme theme)
        {
            _t = ThemeColors.Resolve(theme);
            _resultText = text ?? "";

            Text = "FinalShot -- OCR";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 560;
            Height = 480;
            BackColor = _t.Background;
            ForeColor = _t.TextPrimary;
            KeyPreview = true;
            TopMost = true;

            var title = new Label
            {
                Text = "OCR Result",
                AutoSize = false,
                Left = 16,
                Top = 12,
                Width = 400,
                Height = 24,
                Font = new Font("Segoe UI Semibold", 12f),
                ForeColor = _t.TextPrimary,
                BackColor = Color.Transparent
            };

            _preview = new PictureBox
            {
                Left = 16,
                Top = 44,
                Width = 160,
                Height = 120,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = _t.CardBg,
                BorderStyle = BorderStyle.FixedSingle
            };
            if (preview != null)
            {
                try { _preview.Image = new Bitmap(preview); }
                catch { /* leave empty */ }
            }

            _status = new Label
            {
                Text = BuildStatus(text),
                AutoSize = false,
                Left = 188,
                Top = 44,
                Width = 340,
                Height = 120,
                Font = new Font("Segoe UI", 9f),
                ForeColor = _t.TextSecondary,
                BackColor = Color.Transparent
            };

            _textBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 16,
                Top = 176,
                Width = 512,
                Height = 200,
                Font = new Font("Consolas", 10f),
                Text = text ?? "",
                BackColor = _t.CardBg,
                ForeColor = _t.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                AcceptsReturn = true,
                AcceptsTab = false
            };

            var btnCopy = CreateButton("Copy", 280, 392);
            btnCopy.Click += (s, e) => CopyCurrent();

            var btnClose = CreateButton("Close", 400, 392);
            btnClose.Click += (s, e) => CloseWithResult();

            Controls.Add(title);
            Controls.Add(_preview);
            Controls.Add(_status);
            Controls.Add(_textBox);
            Controls.Add(btnCopy);
            Controls.Add(btnClose);

            AcceptButton = btnClose;
            CancelButton = btnClose;

            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && !_textBox.Focused)
                {
                    CopyCurrent();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    CloseWithResult();
                    e.Handled = true;
                }
            };

            FormClosing += (s, e) =>
            {
                _resultText = _textBox.Text ?? "";
            };

            Shown += (s, e) =>
            {
                _textBox.SelectAll();
                _textBox.Focus();
            };
        }

        private Button CreateButton(string caption, int x, int y)
        {
            var btn = new Button
            {
                Text = caption,
                Left = x,
                Top = y,
                Width = 100,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = _t.BtnBg,
                ForeColor = _t.BtnText,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = _t.BtnBorder;
            btn.FlatAppearance.MouseOverBackColor = _t.BtnHover;
            return btn;
        }

        private static string BuildStatus(string text)
        {
            int chars = text?.Length ?? 0;
            int lines = 0;
            if (!string.IsNullOrEmpty(text))
                lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Length;
            return "Recognized text is editable.\n\n" +
                   "Characters: " + chars + "\n" +
                   "Lines: " + lines + "\n\n" +
                   "Copy places text on the clipboard.\n" +
                   "Close updates GetLastOCRText().";
        }

        private void CopyCurrent()
        {
            try
            {
                string t = _textBox.Text ?? "";
                if (string.IsNullOrEmpty(t))
                    Clipboard.Clear();
                else
                    Clipboard.SetText(t);
                _status.Text = "Copied to clipboard.";
                _status.ForeColor = _t.AccentGreen;
            }
            catch (Exception ex)
            {
                Logger.Log("OcrResultWindow.Copy: " + ex.Message);
                _status.Text = "Clipboard copy failed.";
                _status.ForeColor = _t.AccentAmber;
            }
        }

        private void CloseWithResult()
        {
            _resultText = _textBox.Text ?? "";
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_preview != null && _preview.Image != null)
                {
                    var img = _preview.Image;
                    _preview.Image = null;
                    img.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PluginScreenshot
{
    /// <summary>
    /// Orchestrates ShareX-style OCR: snap/select region → capture → recognize → clipboard.
    /// </summary>
    public static class OcrManager
    {
        private static readonly object TextLock = new object();
        private static string _lastOcrText = "";

        /// <summary>
        /// Last successfully recognized text (empty after cancel/failure).
        /// Exposed to Rainmeter via GetLastOCRText().
        /// </summary>
        public static string LastOcrText
        {
            get { lock (TextLock) return _lastOcrText ?? ""; }
            private set { lock (TextLock) _lastOcrText = value ?? ""; }
        }

        /// <summary>
        /// Opens the snap overlay, OCRs the selected region, copies text, runs finish action.
        /// Blocks until the STA worker finishes (same pattern as custom screenshot).
        /// </summary>
        public static void CaptureAndRecognize(Settings settings)
        {
            if (settings == null)
            {
                Logger.Log("OcrManager: settings is null.");
                return;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    RunOnStaThread(settings);
                }
                catch (Exception ex)
                {
                    Logger.Log("OcrManager thread error: " + ex);
                    LastOcrText = "";
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "FinalShot-OCR";
            thread.Start();
            thread.Join();
        }

        private static void RunOnStaThread(Settings settings)
        {
            Logger.Log("OcrManager: starting region selection for OCR.");

            if (!OcrHelper.IsSupported)
            {
                Logger.Log("OcrManager: OS does not support Windows.Media.Ocr.");
                LastOcrText = "";
                return;
            }

            Rectangle? region = GifSnapSelector.SelectRegion(settings);
            if (region == null || region.Value.Width <= 0 || region.Value.Height <= 0)
            {
                Logger.Log("OcrManager: selection cancelled.");
                return;
            }

            Logger.Log("OcrManager: selected region=" + region.Value);

            using (Bitmap bmp = ScreenshotManager.CaptureRegionToBitmap(region.Value, settings))
            {
                if (bmp == null)
                {
                    Logger.Log("OcrManager: capture failed.");
                    LastOcrText = "";
                    return;
                }

                string text;
                try
                {
                    text = OcrHelper.RecognizeAsync(
                        bmp,
                        settings.OcrLanguage,
                        settings.OcrScaleFactor,
                        settings.OcrSingleLine).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logger.Log("OcrManager: OCR failed — " + ex.Message);
                    LastOcrText = "";
                    return;
                }

                LastOcrText = text ?? "";
                Logger.Log("OcrManager: recognized " + LastOcrText.Length + " characters.");

                if (!string.IsNullOrEmpty(LastOcrText))
                {
                    try
                    {
                        Clipboard.SetText(LastOcrText);
                        Logger.Log("OcrManager: text copied to clipboard.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("OcrManager: clipboard error — " + ex.Message);
                    }
                }

                if (settings.ShowOcrWindow)
                {
                    Logger.Log("OcrManager: showing OCR result window.");
                    string edited = OcrResultWindow.ShowDialog(LastOcrText, bmp, settings.UITheme);
                    LastOcrText = edited ?? "";
                    Logger.Log("OcrManager: OCR window closed, text length=" + LastOcrText.Length);
                }
                else if (settings.ShowNotification)
                {
                    ShowOcrNotification(bmp, settings);
                }

                ExecuteOcrFinishAction(settings);
            }
        }

        private static void ExecuteOcrFinishAction(Settings settings)
        {
            if (string.IsNullOrEmpty(settings.OcrFinishAction))
                return;
            try
            {
                settings.Api.Execute(settings.OcrFinishAction);
            }
            catch (Exception ex)
            {
                Logger.Log("OcrManager: OCRFinishAction error — " + ex.Message);
            }
        }

        private static void ShowOcrNotification(Bitmap source, Settings settings)
        {
            string tempPath = null;
            try
            {
                try { System.Media.SystemSounds.Asterisk.Play(); }
                catch { /* ignore */ }

                tempPath = Path.Combine(Path.GetTempPath(),
                    "FinalShot_OCR_" + Guid.NewGuid().ToString("N") + ".png");
                source.Save(tempPath, ImageFormat.Png);

                GifEncodingWindow.SetTheme(settings.UITheme);
                GifStateDialog.SetTheme(settings.UITheme);

                string pathForThread = tempPath;
                var notificationThread = new Thread(() =>
                {
                    try
                    {
                        Application.Run(new NotificationForm(pathForThread, "OCR Text", settings));
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("OcrManager notification thread: " + ex.Message);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(pathForThread))
                                File.Delete(pathForThread);
                        }
                        catch { /* ignore */ }
                    }
                });
                notificationThread.SetApartmentState(ApartmentState.STA);
                notificationThread.IsBackground = true;
                notificationThread.Start();
            }
            catch (Exception ex)
            {
                Logger.Log("OcrManager.ShowOcrNotification: " + ex.Message);
                try
                {
                    if (tempPath != null && File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { /* ignore */ }
            }
        }
    }
}

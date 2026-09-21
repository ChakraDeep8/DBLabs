using System;
using System.IO;
using System.Windows;
using DBLabs.Services;
using Microsoft.Win32;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>
    /// Picks a video file (or stream URL) and pulls the first frame out of it that passes an
    /// automatic sharpness check, so a blurred or motion-smeared frame is never calibrated on.
    /// </summary>
    public partial class VideoGrabDialog : Window
    {
        public Mat? CapturedFrame { get; private set; }

        /// <summary>File name suggestion for the grabbed frame, derived from the source.</summary>
        public string SuggestedName { get; private set; } = "video_frame.jpg";

        public VideoGrabDialog()
        {
            InitializeComponent();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.webm|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                SourceBox.Text = dlg.FileName;
        }

        private async void Grab_Click(object sender, RoutedEventArgs e)
        {
            var source = SourceBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                ShowStatus("Choose a video file or enter a stream URL.", isError: true);
                return;
            }

            bool looksLikeUrl = source.Contains("://", StringComparison.Ordinal);
            if (!looksLikeUrl && !File.Exists(source))
            {
                ShowStatus("That file does not exist.", isError: true);
                return;
            }

            GrabButton.IsEnabled = false;
            ShowStatus("Scanning the video for a sharp frame…", isError: false);

            try
            {
                var result = await VideoFrameGrabService.GrabSharpFrameAsync(source);

                if (result.Error != null)
                {
                    ShowStatus(result.Error, isError: true);
                    return;
                }

                if (result.Frame == null)
                {
                    ShowStatus("No frames could be read from this video.", isError: true);
                    return;
                }

                if (!result.MeetsThreshold)
                {
                    // Deliberately does NOT return the frame: the whole point is to avoid
                    // calibrating on a soft frame. Report it and let the user pick another source.
                    result.Frame.Dispose();
                    ShowStatus(
                        $"No sharp frame found — checked {result.CandidatesScored} frame(s), the best scored " +
                        $"{result.Sharpness:0} against a threshold of {VideoFrameGrabService.DefaultSharpnessThreshold:0}. " +
                        "The video may be out of focus or heavily compressed. Try a different video or clip.",
                        isError: true);
                    return;
                }

                CapturedFrame = result.Frame;
                SuggestedName = BuildFrameName(source, looksLikeUrl);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", isError: true);
            }
            finally
            {
                GrabButton.IsEnabled = true;
            }
        }

        private static string BuildFrameName(string source, bool looksLikeUrl)
        {
            var stamp = DateTime.Now.ToString("HHmmss");
            if (looksLikeUrl) return $"video_frame_{stamp}.jpg";

            var baseName = Path.GetFileNameWithoutExtension(source);
            return string.IsNullOrWhiteSpace(baseName)
                ? $"video_frame_{stamp}.jpg"
                : $"{baseName}_frame_{stamp}.jpg";
        }

        private void ShowStatus(string message, bool isError)
        {
            StatusText.Text = message;
            StatusText.Foreground = isError
                ? (System.Windows.Media.Brush)FindResource("ErrorBrush")
                : (System.Windows.Media.Brush)FindResource("TextSecondary");
            StatusText.Visibility = Visibility.Visible;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

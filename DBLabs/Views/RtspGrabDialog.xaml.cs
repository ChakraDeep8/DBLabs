using System.Windows;
using DBLabs.Services;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    public partial class RtspGrabDialog : Window
    {
        public Mat? CapturedFrame { get; private set; }

        public RtspGrabDialog()
        {
            InitializeComponent();

            foreach (var (name, url) in SavedCamerasStore.LoadRtspViewerCameras())
                SavedCamerasCombo.Items.Add(new CameraOption(name, url));

            if (SavedCamerasCombo.Items.Count == 0)
                SavedCamerasCombo.Items.Add(new CameraOption("(none saved)", ""));
        }

        private void SavedCamerasCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SavedCamerasCombo.SelectedItem is CameraOption opt && !string.IsNullOrEmpty(opt.Url))
                UrlBox.Text = opt.Url;
        }

        private void SaveForReuseCheck_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (SaveNameLabel == null || SaveNameBox == null) return;
            var show = SaveForReuseCheck.IsChecked == true;
            SaveNameLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SaveNameBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Grab_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("rtsp://"))
            {
                ShowStatus("Enter a valid rtsp:// URL.");
                return;
            }

            GrabButton.IsEnabled = false;
            ShowStatus("Connecting and grabbing frame…", isError: false);
            try
            {
                var frame = await RtspCaptureService.GrabFrameAsync(url);
                if (frame == null)
                {
                    ShowStatus("Could not grab a frame. Check the URL/network and try again.");
                    return;
                }

                if (SaveForReuseCheck.IsChecked == true)
                {
                    var saveName = SaveNameBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(saveName)) saveName = url;
                    SavedCamerasStore.SaveRtspViewerCamera(saveName, url);
                }

                CapturedFrame = frame;
                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                ShowStatus($"Error: {ex.Message}");
            }
            finally
            {
                GrabButton.IsEnabled = true;
            }
        }

        private void ShowStatus(string message, bool isError = true)
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

        private record CameraOption(string Name, string Url)
        {
            public override string ToString() => string.IsNullOrEmpty(Name) ? Url : $"{Name}  ({Url})";
        }
    }
}

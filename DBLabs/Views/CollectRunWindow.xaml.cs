using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using DBLabs.Models.Roi;
using DBLabs.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace DBLabs.Views
{
    /// <summary>
    /// Drives an unattended collection run. Non-modal on purpose: a run can last hours, and
    /// holding the whole application hostage for that would make the feature unusable for the
    /// exact case it exists to serve.
    /// </summary>
    public partial class CollectRunWindow : Window
    {
        private CancellationTokenSource? _cancellation;
        private Task<CollectionSummary>? _run;

        /// <summary>Most recent crops, newest first. Bounded because a long run would otherwise
        /// accumulate thumbnails for every person it ever saw.</summary>
        private readonly ObservableCollection<BitmapSource> _savedCrops = new();
        private const int MaxShownCrops = 24;

        private bool IsRunning => _run is { IsCompleted: false };

        public CollectRunWindow()
        {
            InitializeComponent();

            SavedCropsList.ItemsSource = _savedCrops;
            LoadSavedCameras();
            LoadSizeOptions();

            UrlBox.Text = DataBuilderSettings.LoadCollectUrl();
            OutputBox.Text = DataBuilderSettings.LoadOutputFolder();
        }

        /// <summary>
        /// Offers the cameras the RTSP Camera Viewer has saved, grouped by store exactly as they
        /// are grouped there. The grouping earns its keep: camera names repeat between stores, so
        /// a flat list cannot tell one site's "Everlite" from another's. The URL box stays
        /// editable for any stream that isn't on the list.
        /// </summary>
        private void LoadSavedCameras()
        {
            var cameras = SavedCamerasStore.LoadRtspViewerCameras();
            if (cameras.Count == 0)
            {
                SavedCamerasCombo.IsEnabled = false;
                SavedCamerasCombo.ToolTip = "No cameras saved by the RTSP Camera Viewer — type a URL below.";
                return;
            }

            var grouped = new CollectionViewSource { Source = cameras };
            grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SavedCamerasStore.SavedCamera.GroupName)));
            SavedCamerasCombo.ItemsSource = grouped.View;

            // Left unselected on purpose: the URL box is prefilled with the last stream used, and
            // auto-selecting the first camera would quietly overwrite it.
            SavedCamerasCombo.SelectedIndex = -1;
        }

        private void SavedCameras_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SavedCamerasCombo.SelectedItem is SavedCamerasStore.SavedCamera camera)
                UrlBox.Text = camera.Url;
        }

        // =====================================================================
        // Output size
        // =====================================================================

        private void LoadSizeOptions()
        {
            SizeCombo.Items.Add(new SizeOption("Original (no resize)", 0));
            foreach (var size in new[] { 224, 320, 416, 640 })
                SizeCombo.Items.Add(new SizeOption($"{size} x {size}", size));
            SizeCombo.SelectedIndex = 0;

            FitCombo.Items.Add(new FitOption("Expand to square — real pixels, more background", CropFit.ExpandToSquare));
            FitCombo.Items.Add(new FitOption("Letterbox — pad to fit, no distortion", CropFit.Letterbox));
            FitCombo.Items.Add(new FitOption("Stretch — fills the frame, distorts the subject", CropFit.Stretch));
            FitCombo.Items.Add(new FitOption("Fixed height — keeps aspect, widths vary", CropFit.FixedHeight));
            FitCombo.SelectedIndex = 0;

            UpdateFitAvailability();
        }

        private void Size_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFitAvailability();

        /// <summary>There is nothing to fit when crops keep their natural size, so the fit choice
        /// is disabled rather than left looking as though it applies.</summary>
        private void UpdateFitAvailability()
        {
            if (FitCombo == null || FitHintText == null) return;

            bool resizing = SizeCombo.SelectedItem is SizeOption { Pixels: > 0 };
            FitCombo.IsEnabled = resizing;

            FitHintText.Text = !resizing
                ? "Original keeps each crop at whatever size it came out of the frame."
                : FitCombo.SelectedItem is FitOption { Fit: CropFit.FixedHeight }
                    ? "Heights match but widths follow the subject, so files won't all be the same size."
                    : "Every crop is written at exactly this size.";
        }

        private CropSizing? SelectedSizing()
        {
            if (SizeCombo.SelectedItem is not SizeOption { Pixels: > 0 } size) return null;
            var fit = FitCombo.SelectedItem is FitOption option ? option.Fit : CropFit.ExpandToSquare;
            return new CropSizing { TargetSize = size.Pixels, Fit = fit };
        }

        private sealed record SizeOption(string Label, int Pixels)
        {
            public override string ToString() => Label;
        }

        private sealed record FitOption(string Label, CropFit Fit)
        {
            public override string ToString() => Label;
        }

        // =====================================================================
        // Starting
        // =====================================================================

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (IsRunning) return;

            // Detection is the whole feature, so there is no useful degraded mode: without the
            // weights an unattended run would quietly write nothing, or rubbish.
            if (!await EnsureModelAsync()) return;

            if (!TryBuildOptions(out var options, out var problem))
            {
                MessageBox.Show(this, problem, "Collect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DataBuilderSettings.SaveCollectUrl(options.RtspUrl);
            DataBuilderSettings.SaveOutputFolder(options.OutputFolder);

            SetRunningState(true);
            RunStatusText.Text = "Connecting to the stream…";
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            var progress = new Progress<CollectionProgress>(OnProgress);
            _cancellation = new CancellationTokenSource();
            _run = PersonCollectorService.RunAsync(
                options, () => new RtspFrameSource(options.RtspUrl), progress, _cancellation.Token);

            CollectionSummary summary;
            try
            {
                summary = await _run;
            }
            catch (Exception ex)
            {
                summary = new CollectionSummary { Error = ex.Message, StoppedBecause = "error" };
            }

            SetRunningState(false);
            ShowSummary(summary);
        }

        /// <summary>
        /// Makes sure the detection weights are on disk, downloading them on first use.
        ///
        /// Asked rather than done silently: it is a ~43MB download that the user may not want on
        /// a metered connection, and an unexplained pause at the moment you press Start is a poor
        /// way to discover it. See ModelStore for why the weights aren't shipped with the app.
        /// </summary>
        private async Task<bool> EnsureModelAsync()
        {
            if (ModelStore.IsPresent) return true;

            var answer = MessageBox.Show(this,
                "Person detection needs the YOLOv8 model, which isn't downloaded yet.\n\n"
                + "It's about 43MB and is fetched once, then kept in your app data folder.\n\n"
                + "Download it now?",
                "One-time download", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK) return false;

            SetRunningState(true);
            StopButton.Visibility = Visibility.Collapsed; // nothing to stop yet; this isn't a run
            RunStatusText.Text = "Downloading the detection model…";

            var progress = new Progress<double>(fraction =>
                RunStatusText.Text = $"Downloading the detection model… {fraction * 100:0}%");

            var (ok, error) = await ModelStore.EnsureAsync(progress);

            SetRunningState(false);

            if (!ok)
            {
                RunStatusText.Text = "Model download failed.";
                MessageBox.Show(this,
                    $"The model could not be downloaded.\n\n{error}\n\n"
                    + $"You can also place yolov8s.onnx manually at:\n{ModelStore.ModelPath}",
                    "Download failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            RunStatusText.Text = "Model ready.";
            return true;
        }

        /// <summary>Reads the form, rejecting anything that would fail later in a less obvious place.</summary>
        private bool TryBuildOptions(out CollectionRunOptions options, out string problem)
        {
            options = new CollectionRunOptions();
            problem = "";

            var url = UrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) { problem = "Enter the RTSP URL of the camera."; return false; }

            var folder = OutputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder)) { problem = "Choose a dataset folder to collect into."; return false; }

            // Checked now rather than discovered twenty minutes into an unattended run.
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                problem = $"That dataset folder can't be written to.\n\n{ex.Message}";
                return false;
            }

            var label = LabelBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = "Person";

            options.RtspUrl = url;
            options.OutputFolder = folder;
            options.LabelName = label;
            options.FramesPerSecond = FpsSlider.Value;
            options.MinConfidence = (float)ConfidenceSlider.Value;
            options.VarietyThreshold = VarietySlider.Value;
            options.CropSizing = SelectedSizing();

            if (UseDuration.IsChecked == true)
            {
                if (!double.TryParse(DurationBox.Text.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var minutes) || minutes <= 0)
                { problem = "Enter a positive number of minutes, or untick that stop condition."; return false; }
                options.MaxDuration = TimeSpan.FromMinutes(minutes);
            }

            if (UseCropTarget.IsChecked == true)
            {
                if (!int.TryParse(CropTargetBox.Text.Trim(), out var crops) || crops <= 0)
                { problem = "Enter a positive number of crops, or untick that stop condition."; return false; }
                options.MaxCrops = crops;
            }

            if (UseStopAt.IsChecked == true)
            {
                if (!TimeSpan.TryParse(StopAtBox.Text.Trim(), CultureInfo.CurrentCulture, out var timeOfDay))
                { problem = "Enter a clock time as HH:mm, or untick that stop condition."; return false; }

                var stopAt = DateTime.Today.Add(timeOfDay);
                // A time already past today plainly means tomorrow — an overnight run is the
                // normal reason to use a clock time at all.
                if (stopAt <= DateTime.Now) stopAt = stopAt.AddDays(1);
                options.StopAtLocalTime = stopAt;
            }

            return true;
        }

        // =====================================================================
        // Live state
        // =====================================================================

        private void OnProgress(CollectionProgress progress)
        {
            SavedText.Text = progress.CropsSaved.ToString();
            SampledText.Text = progress.FramesSampled.ToString();
            DuplicatesText.Text = progress.DuplicatesSkipped.ToString();
            ReconnectsText.Text = progress.Reconnects.ToString();

            if (progress.Preview != null) PreviewImage.Source = progress.Preview;

            if (progress.NewCrops is { Count: > 0 })
            {
                CropsPlaceholder.Visibility = Visibility.Collapsed;
                foreach (var crop in progress.NewCrops)
                {
                    _savedCrops.Insert(0, crop); // newest first, so the latest is always in view
                    while (_savedCrops.Count > MaxShownCrops) _savedCrops.RemoveAt(_savedCrops.Count - 1);
                }
                CropsScroller.ScrollToHome();
            }

            var parts = new System.Collections.Generic.List<string>
            {
                $"running {Describe(progress.Elapsed)}",
                $"{progress.ActualFps:0.0} fps actual"
            };
            if (progress.LowQualitySkipped > 0) parts.Add($"{progress.LowQualitySkipped} too small or uncertain");
            if (progress.Message != null) parts.Add(progress.Message);

            RunStatusText.Text = string.Join("  ·  ", parts);
        }

        private void SetRunningState(bool running)
        {
            SettingsPanel.IsEnabled = !running;
            StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.IsEnabled = !running;
            FooterHintText.Text = running
                ? "Collecting — the main window is still usable."
                : "The main window stays usable while a run is going.";
        }

        private void ShowSummary(CollectionSummary summary)
        {
            if (summary.Failed)
            {
                RunStatusText.Text = $"Stopped: {summary.Error}";
                MessageBox.Show(this,
                    $"{summary.Error}\n\n{summary.CropsSaved} crop(s) were saved before it stopped.",
                    "Collect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RunStatusText.Text =
                $"Finished — {summary.StoppedBecause}. {summary.CropsSaved} crop(s) from {summary.FramesSampled} sampled frame(s), "
                + $"{summary.DuplicatesSkipped} look-alike(s) skipped.";

            var detail = $"{summary.CropsSaved} crop(s) saved in {Describe(summary.Elapsed)}.\n\n"
                + $"Sampled {summary.FramesSampled} frame(s)\n"
                + $"Skipped {summary.DuplicatesSkipped} look-alike(s)\n"
                + $"Skipped {summary.LowQualitySkipped} too small or uncertain\n"
                + (summary.Reconnects > 0 ? $"Reconnected {summary.Reconnects} time(s)\n" : "")
                + (summary.ManifestPath != null ? $"\nIndexed in {Path.GetFileName(summary.ManifestPath)}." : "");

            MessageBox.Show(this, detail, "Collection finished", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string Describe(TimeSpan span) =>
            span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" :
            span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" :
            $"{span.Seconds}s";

        // =====================================================================
        // Stopping
        // =====================================================================

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            RunStatusText.Text = "Stopping…";
            _cancellation?.Cancel();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!IsRunning) return;

            var answer = MessageBox.Show(this,
                "A collection run is still going. Close and stop it?\n\nCrops already saved are kept.",
                "Collect", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
            _cancellation?.Cancel();
        }

        // =====================================================================
        // Form plumbing
        // =====================================================================

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            // The same save-dialog-as-folder-picker stand-in the Data Builder uses.
            var dlg = new SaveFileDialog
            {
                Title = "Choose the dataset folder",
                FileName = "Select this folder",
                Filter = "Folder|*.none",
                CheckPathExists = true
            };
            if (!string.IsNullOrWhiteSpace(OutputBox.Text) && Directory.Exists(OutputBox.Text))
                dlg.InitialDirectory = OutputBox.Text;

            if (dlg.ShowDialog() != true) return;
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(folder)) OutputBox.Text = folder;
        }

        private void Fps_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FpsValueText != null) FpsValueText.Text = $"{e.NewValue:0.0}";
        }

        private void Confidence_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ConfidenceValueText != null) ConfidenceValueText.Text = $"{e.NewValue:0.00}";
        }

        private void Variety_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VarietyValueText != null) VarietyValueText.Text = $"{e.NewValue:0.0}";
        }
    }
}

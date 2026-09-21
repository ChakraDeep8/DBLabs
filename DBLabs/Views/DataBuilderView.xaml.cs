using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DBLabs.Controls;
using DBLabs.Models.Roi;
using DBLabs.Services;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Path = System.IO.Path;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>
    /// Builds an image-classification dataset from camera frames. Same drawing surface as ROI
    /// Calibration, but instead of exporting geometry as JSON it exports the PIXELS: each drawn
    /// region is written out as its own image file, into a folder named after its label.
    ///
    /// Deliberately has no adjustment or filter controls. A training set wants the frame as the
    /// camera saw it; silently baking a brightness curve into some crops and not others is the
    /// kind of thing that quietly poisons a dataset.
    /// </summary>
    public partial class DataBuilderView : UserControl
    {
        private readonly RoiDocument _document = new();
        private readonly RoiHistory _history = new();
        private Mat? _originalMat;

        private readonly ObservableCollection<CalibrationClass> _classLibrary;
        private CalibrationClass? _activeClass;

        /// <summary>Per-label totals for the whole session. These deliberately outlive a batch:
        /// Save Database closes a batch, it does not start a new day's work.</summary>
        private readonly ObservableCollection<ClassCount> _sessionCounts = new();
        private int _sessionTotal;

        /// <summary>Crops written since the last Save Database — the open batch.</summary>
        private readonly List<CropResult> _batchCrops = new();

        /// <summary>Image extensions the folder walk and drag-drop both accept.</summary>
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        /// <summary>Frames in the folder currently being worked through, and where we are in it.
        /// Empty when the frame came from somewhere without a folder to step through, such as an
        /// RTSP or video grab.</summary>
        private List<string> _folderImages = new();
        private int _folderIndex = -1;

        /// <summary>The first nine labels, bound to the number keys and listed in the sidebar.</summary>
        private readonly ObservableCollection<LabelShortcut> _labelShortcuts = new();

        private bool _syncingSelection;

        public DataBuilderView()
        {
            InitializeComponent();

            _classLibrary = ClassLibraryStore.Load();

            Canvas.Document = _document;
            Canvas.History = _history;
            Canvas.ClassColorResolver = GetClassColor;
            Canvas.RequestNaming += OnRequestNaming;
            Canvas.SelectionChanged += OnCanvasSelectionChanged;
            Canvas.Changed += OnCanvasChanged;
            Canvas.ToolChangeRequested += OnCanvasToolChangeRequested;

            RegionList.ItemsSource = _document.Objects;
            SessionCountsList.ItemsSource = _sessionCounts;
            LabelShortcutList.ItemsSource = _labelShortcuts;

            OutputFolderBox.Text = DataBuilderSettings.LoadOutputFolder();
            UpdateActiveClassDisplay();
            RefreshLabelShortcuts();
            RefreshFolderPosition();
            Canvas.Tool = ToolMode.Rectangle;
        }

        // =====================================================================
        // Image sources
        // =====================================================================

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" };
            if (dlg.ShowDialog() != true) return;

            // Opening one frame still seeds the folder it lives in, so N/P work immediately —
            // labelling rarely stops at a single frame, and making the user re-open the folder
            // to get there would be busywork.
            AdoptFolderOf(dlg.FileName);
            LoadImageFile(dlg.FileName);
        }

        /// <summary>
        /// Opens the unattended collector. Non-modal and owned by the main window rather than
        /// this view, so a long run survives switching tabs and the rest of the app stays usable
        /// while it collects.
        /// </summary>
        private void CollectFromRtsp_Click(object sender, RoutedEventArgs e)
        {
            var window = new CollectRunWindow
            {
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Show();
        }

        // =====================================================================
        // Folder navigation — the batch labelling loop
        // =====================================================================

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = PickFolder("Choose a folder of frames to label");
            if (folder == null) return;

            var images = EnumerateImages(folder);
            if (images.Count == 0)
            {
                UpdateStatus($"No images found in {folder}.", isWarning: true);
                return;
            }

            if (!ConfirmLeavingUnsavedRegions()) return;

            _folderImages = images;
            _folderIndex = 0;
            LoadImageFile(images[0]);
        }

        private void PrevImage_Click(object sender, RoutedEventArgs e) => StepFolder(-1);
        private void NextImage_Click(object sender, RoutedEventArgs e) => StepFolder(+1);

        private void StepFolder(int delta)
        {
            if (_folderImages.Count == 0)
            {
                UpdateStatus("Open an image or a folder first — then N and P step through its frames.", isWarning: true);
                return;
            }

            int target = _folderIndex + delta;
            if (target < 0) { UpdateStatus("Already at the first frame in this folder."); return; }
            if (target >= _folderImages.Count) { UpdateStatus("Already at the last frame in this folder."); return; }

            if (!ConfirmLeavingUnsavedRegions()) return;

            _folderIndex = target;
            LoadImageFile(_folderImages[target]);
        }

        /// <summary>
        /// Regions that have not been through Add Data exist only on screen, so moving to another
        /// frame would discard that work silently. Ask first rather than lose it.
        /// </summary>
        private bool ConfirmLeavingUnsavedRegions()
        {
            int pending = _document.Objects.Count(o => o.IsVisible && !o.IsExported);
            if (pending == 0) return true;

            return MessageBox.Show(Window.GetWindow(this),
                $"{pending} region(s) on this frame haven't been saved yet — leaving now discards them.\n\n"
                + "Press Add Data first if you want to keep them.\n\nMove to the other frame anyway?",
                "Unsaved regions", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        /// <summary>Points folder navigation at the directory holding <paramref name="path"/>,
        /// positioned on that file.</summary>
        private void AdoptFolderOf(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder)) { ClearFolderContext(); return; }

            _folderImages = EnumerateImages(folder);
            _folderIndex = _folderImages.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            if (_folderIndex < 0) ClearFolderContext();
        }

        private void ClearFolderContext()
        {
            _folderImages = new List<string>();
            _folderIndex = -1;
        }

        private static List<string> EnumerateImages(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void RefreshFolderPosition() =>
            FolderPositionText.Text = _folderImages.Count == 0 || _folderIndex < 0
                ? "—"
                : $"{_folderIndex + 1} / {_folderImages.Count}";

        /// <summary>A save dialog stands in for a folder picker — see BrowseOutput_Click for why.</summary>
        private string? PickFolder(string title)
        {
            var dlg = new SaveFileDialog
            {
                Title = title,
                FileName = "Select this folder",
                Filter = "Folder|*.none",
                CheckPathExists = true
            };
            if (_folderIndex >= 0 && _folderImages.Count > 0)
                dlg.InitialDirectory = Path.GetDirectoryName(_folderImages[_folderIndex]);

            if (dlg.ShowDialog() != true) return null;
            var folder = Path.GetDirectoryName(dlg.FileName);
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }

        private void LoadImageFile(string path)
        {
            Mat mat;
            try
            {
                mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat.Empty()) throw new InvalidOperationException("Unsupported or corrupt image file.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not open this image.\n\n{ex.Message}",
                    "Open Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadMat(mat, Path.GetFileName(path));
        }

        private void GrabRtsp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RtspGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.CapturedFrame == null) return;

            // A live grab has no folder behind it, so there is nothing to step through.
            ClearFolderContext();
            LoadMat(dlg.CapturedFrame, $"live_{DateTime.Now:HHmmss}.jpg");
        }

        private void GrabVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VideoGrabDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.CapturedFrame == null) return;

            ClearFolderContext();
            LoadMat(dlg.CapturedFrame, dlg.SuggestedName);
        }

        /// <summary>Takes ownership of <paramref name="mat"/>.</summary>
        private void LoadMat(Mat mat, string displayName)
        {
            _originalMat?.Dispose();
            _originalMat = mat;

            _document.ImageFileName = displayName;
            _document.ImageWidth = mat.Width;
            _document.ImageHeight = mat.Height;
            _document.Objects.Clear();
            _history.Clear();

            var bitmap = mat.ToBitmapSource();
            bitmap.Freeze();
            Canvas.LoadImage(bitmap, mat.Width, mat.Height);

            EmptyState.Visibility = Visibility.Collapsed;
            RefreshFolderPosition();

            var position = _folderImages.Count > 0 && _folderIndex >= 0
                ? $"  |  frame {_folderIndex + 1} of {_folderImages.Count}"
                : "";
            UpdateStatus($"{displayName}  |  {mat.Width} x {mat.Height}{position}  |  draw a box around each subject");
        }

        private void EmptyState_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void EmptyState_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var file = files.FirstOrDefault(f =>
                ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (file == null) return;

            AdoptFolderOf(file);
            LoadImageFile(file);
        }

        // =====================================================================
        // Output folder
        // =====================================================================

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            // A save dialog stands in for a folder picker: WPF has no built-in folder browser,
            // and pulling in WinForms for one would drag its whole namespace into a WPF app and
            // collide with types like Application, Point and Rectangle.
            var dlg = new SaveFileDialog
            {
                Title = "Choose the dataset folder",
                FileName = "Select this folder",
                Filter = "Folder|*.none",
                CheckPathExists = true
            };
            if (!string.IsNullOrWhiteSpace(OutputFolderBox.Text) && Directory.Exists(OutputFolderBox.Text))
                dlg.InitialDirectory = OutputFolderBox.Text;

            if (dlg.ShowDialog() != true) return;
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(folder)) OutputFolderBox.Text = folder;
        }

        private void OutputFolder_TextChanged(object sender, TextChangedEventArgs e) =>
            DataBuilderSettings.SaveOutputFolder(OutputFolderBox.Text);

        private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                UpdateStatus("Set an output folder first.", isWarning: true);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }

        // =====================================================================
        // Add Data — the point of this workspace
        // =====================================================================

        private void AddData_Click(object sender, RoutedEventArgs e) => AddData();

        private void AddData()
        {
            if (_originalMat == null || !_document.HasImage)
            {
                UpdateStatus("Open an image or grab a frame first.", isWarning: true);
                return;
            }

            var folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                UpdateStatus("Set an output folder before adding data.", isWarning: true);
                OutputFolderBox.Focus();
                return;
            }

            var drawn = _document.Objects.Where(o => o.IsVisible).ToList();
            if (drawn.Count == 0)
            {
                UpdateStatus("Draw at least one region first.", isWarning: true);
                return;
            }

            // Regions stay on the image after Add Data so the frame keeps showing what has been
            // labelled. That makes a second click a duplicate-export risk, so anything already
            // written out is skipped rather than saved twice.
            var regions = drawn.Where(o => !o.IsExported).ToList();
            if (regions.Count == 0)
            {
                UpdateStatus($"All {drawn.Count} region(s) on this frame are already saved. Draw more, or load the next frame.", isWarning: true);
                return;
            }

            List<CropResult> results;
            try
            {
                Directory.CreateDirectory(folder);
                results = CropExportService.ExportCrops(
                    _originalMat, regions, folder, _document.ImageFileName,
                    ResolveClassName, (int)PaddingSlider.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not write the crops.\n\n{ex.Message}",
                    "Add Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saved = results.Where(r => r.Saved).ToList();
            foreach (var group in saved.GroupBy(r => r.ClassName))
                BumpCount(group.Key, group.Count());
            _sessionTotal += saved.Count;
            _batchCrops.AddRange(saved);

            // Tick the regions that were written; they stay on the canvas.
            var savedNames = saved.Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal);
            foreach (var region in regions.Where(r => savedNames.Contains(r.Name)))
                region.IsExported = true;
            Canvas.RedrawAll();

            var skipped = results.Where(r => !r.Saved).ToList();
            var message = saved.Count == 1 ? "Saved 1 crop" : $"Saved {saved.Count} crops";
            message += $" to {folder}";
            if (skipped.Count > 0)
                message += $"  |  skipped {skipped.Count}: {string.Join("; ", skipped.Select(s => $"{s.RegionName} ({s.Skipped})"))}";

            UpdateStatus(message);
            RefreshSessionTotal();
        }

        // =====================================================================
        // Save Database — closes out the batch
        // =====================================================================

        private void SaveDatabase_Click(object sender, RoutedEventArgs e) => SaveDatabase();

        /// <summary>
        /// Finishes the batch: indexes everything collected since the last save into labels.csv
        /// next to the class folders, then resets so the next batch starts clean. The crops
        /// themselves are already on disk — this is what makes the set self-describing, tying
        /// each one back to the frame and pixel rectangle it came from.
        /// </summary>
        private void SaveDatabase()
        {
            if (_batchCrops.Count == 0)
            {
                UpdateStatus("Nothing to save yet — press Add Data on at least one frame first.", isWarning: true);
                return;
            }

            var folder = OutputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                UpdateStatus("Set an output folder before saving the database.", isWarning: true);
                OutputFolderBox.Focus();
                return;
            }

            string manifest;
            try
            {
                manifest = DatasetManifestService.AppendBatch(folder, _batchCrops);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not write the dataset index.\n\n{ex.Message}",
                    "Save Database", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int crops = _batchCrops.Count;
            int classes = _batchCrops.Select(c => c.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            MessageBox.Show(Window.GetWindow(this),
                $"Batch saved.\n\n{crops} crop(s) across {classes} class(es) indexed in\n{Path.GetFileName(manifest)}.\n\nStarting a new batch.",
                "Save Database", MessageBoxButton.OK, MessageBoxImage.Information);

            // Start the next batch clean: the regions on screen belong to the batch just closed.
            // The session tallies deliberately survive — closing a batch is not the end of the
            // sitting, and zeroing the day's progress every time you index one made the sidebar
            // read as though nothing had been collected.
            _batchCrops.Clear();
            _document.Objects.Clear();
            _history.Clear();
            Canvas.Select(null);
            Canvas.RedrawAll();

            RefreshSessionTotal();
            UpdateStatus($"Batch closed — {crops} crop(s) indexed in {manifest}. Ready for the next batch.");
        }

        // =====================================================================
        // Un-save a crop — a mislabel is fixable here rather than in Explorer
        // =====================================================================

        private void UnExportRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            e.Handled = true;
            UnExportRegion(obj);
        }

        /// <summary>
        /// Deletes the crop this region produced and clears its tick, so it can be relabelled and
        /// added again. Only reaches into the open batch: once Save Database has closed one, its
        /// crops are no longer tracked in memory and the file has to be dealt with on disk.
        /// </summary>
        private void UnExportRegion(CalibrationObjectBase obj)
        {
            // Matched on the source frame as well as the region name: a batch spans many frames
            // and the same label ("Staff") recurs on each, so name alone would happily delete
            // another frame's crop.
            var crop = _batchCrops.FirstOrDefault(c =>
                c.Saved
                && string.Equals(c.RegionName, obj.Name, StringComparison.Ordinal)
                && string.Equals(c.SourceImage, _document.ImageFileName, StringComparison.OrdinalIgnoreCase));

            if (crop?.SavedPath == null)
            {
                UpdateStatus($"\"{obj.Name}\" isn't in the open batch — crops from a saved batch have to be removed on disk.",
                    isWarning: true);
                return;
            }

            try
            {
                if (File.Exists(crop.SavedPath)) File.Delete(crop.SavedPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not delete the crop file.\n\n{ex.Message}",
                    "Un-save crop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _batchCrops.Remove(crop);
            BumpCount(crop.ClassName, -1);
            _sessionTotal = Math.Max(0, _sessionTotal - 1);

            obj.IsExported = false;
            Canvas.RedrawAll();
            RefreshSessionTotal();
            UpdateStatus($"Removed {Path.GetFileName(crop.SavedPath)} — relabel the region and press Add Data again.");
        }

        private void BumpCount(string className, int delta)
        {
            var existing = _sessionCounts.FirstOrDefault(c =>
                string.Equals(c.ClassName, className, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                if (delta > 0) _sessionCounts.Add(new ClassCount { ClassName = className, Count = delta });
                return;
            }

            existing.Count += delta;
            // A label that drops back to nothing should leave the list rather than sit at zero.
            if (existing.Count <= 0) _sessionCounts.Remove(existing);
        }

        private void RefreshSessionTotal()
        {
            if (_sessionTotal == 0) { SessionTotalText.Text = ""; return; }

            var session = $"{_sessionTotal} crop{(_sessionTotal == 1 ? "" : "s")} this session";
            SessionTotalText.Text = _batchCrops.Count == 0
                ? session
                : $"{_batchCrops.Count} in open batch  ·  {session}";
        }

        // =====================================================================
        // Labels (reuses the shared class library)
        // =====================================================================

        private string? ResolveClassName(string? classId) =>
            classId == null ? null : _classLibrary.FirstOrDefault(c => c.Id == classId)?.Name;

        private Color? GetClassColor(CalibrationObjectBase obj)
        {
            if (obj.ClassId == null) return null;
            var cls = _classLibrary.FirstOrDefault(c => c.Id == obj.ClassId);
            if (cls == null) return null;
            try { return (Color)ColorConverter.ConvertFromString(cls.ColorHex); }
            catch { return null; }
        }

        private void UpdateObjectSwatches()
        {
            foreach (var obj in _document.Objects)
            {
                var color = GetClassColor(obj);
                obj.SwatchBrush = color.HasValue ? new SolidColorBrush(color.Value) : Brushes.Gray;
            }
        }

        private void SetActiveClass(CalibrationClass? cls)
        {
            _activeClass = cls;
            UpdateActiveClassDisplay();
        }

        /// <summary>Rebuilds the numbered label list. Nine because that is how many number keys
        /// there are; a library larger than that still works through the picker.</summary>
        private void RefreshLabelShortcuts()
        {
            _labelShortcuts.Clear();
            int number = 1;
            foreach (var cls in _classLibrary.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(9))
                _labelShortcuts.Add(new LabelShortcut { Number = number++, Label = cls });
        }

        private void LabelShortcut_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not LabelShortcut shortcut) return;
            SetActiveClass(shortcut.Label);
            UpdateStatus($"Active label: {shortcut.Label.Name}");
            e.Handled = true;
        }

        private void ApplyLabelShortcut(int number)
        {
            var shortcut = _labelShortcuts.FirstOrDefault(s => s.Number == number);
            if (shortcut == null)
            {
                UpdateStatus($"No label bound to {number} yet — add one with + New Label.", isWarning: true);
                return;
            }
            SetActiveClass(shortcut.Label);
            UpdateStatus($"Active label: {shortcut.Label.Name}");
        }

        private void UpdateActiveClassDisplay()
        {
            if (_activeClass == null)
            {
                ActiveClassText.Text = "No label — name each region as you draw";
                ActiveClassSwatch.Fill = (Brush)FindResource("TextSecondary");
            }
            else
            {
                ActiveClassText.Text = _activeClass.Name;
                ActiveClassSwatch.Fill = _activeClass.SwatchBrush;
            }
        }

        private void ActiveClassButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ClassPickerDialog(_classLibrary, "Set Label") { Owner = Window.GetWindow(this) };
            var ok = dlg.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);
            RefreshLabelShortcuts(); // the picker can create labels, so the numbered list follows it
            if (ok != true) return;

            if (dlg.Outcome == ClassPickerOutcome.Selected) SetActiveClass(dlg.SelectedClass);
            else if (dlg.Outcome == ClassPickerOutcome.Custom) SetActiveClass(null);
        }

        private void NewClass_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NameShapeDialog("New label", "", _classLibrary.Select(c => c.Name).ToList())
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            var newClass = new CalibrationClass
            {
                Name = dlg.ResultName,
                ColorHex = ClassColorPalette.NextColor(_classLibrary.Count)
            };
            _classLibrary.Add(newClass);
            ClassLibraryStore.Save(_classLibrary);
            RefreshLabelShortcuts();
            SetActiveClass(newClass);
        }

        private void ManageClasses_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ManageClassesDialog(_classLibrary, _document.Objects) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.LibraryChanged) return;
            UpdateObjectSwatches();
            UpdateActiveClassDisplay();
            RefreshLabelShortcuts();
            Canvas.RedrawAll();
        }

        // =====================================================================
        // Drawing / naming
        // =====================================================================

        private void OnRequestNaming(CalibrationObjectBase pending)
        {
            // A label already chosen? Apply it with no popup — the fast path when tagging many
            // subjects of the same kind across a set of frames.
            if (_activeClass != null)
            {
                AssignClass(pending, _activeClass);
                Commit();
                return;
            }

            var picker = new ClassPickerDialog(_classLibrary, "Label this region") { Owner = Window.GetWindow(this) };
            var result = picker.ShowDialog();
            ClassLibraryStore.Save(_classLibrary);
            RefreshLabelShortcuts();

            if (result == true && picker.Outcome == ClassPickerOutcome.Selected && picker.SelectedClass != null)
            {
                AssignClass(pending, picker.SelectedClass);
                Commit();
                return;
            }

            if (result == true && picker.Outcome == ClassPickerOutcome.Custom
                && !string.IsNullOrWhiteSpace(picker.TypedName))
            {
                pending.Name = picker.TypedName!;
                Commit();
                return;
            }

            Canvas.DiscardPendingShape();

            void Commit()
            {
                Canvas.CommitPendingShape();
                UpdateObjectSwatches();
                UpdateStatus($"{_document.Objects.Count} region(s) ready — press Add Data to save them.");
            }
        }

        private void AssignClass(CalibrationObjectBase pending, CalibrationClass cls)
        {
            pending.ClassId = cls.Id;
            cls.LastUsedUtc = DateTime.UtcNow;

            int existing = _document.Objects.Count(o => o.ClassId == cls.Id);
            pending.Name = existing == 0 ? cls.Name : $"{cls.Name}_{existing + 1:D2}";
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (Canvas == null) return;
            Canvas.Tool = sender switch
            {
                _ when sender == ToolRectangle => ToolMode.Rectangle,
                _ when sender == ToolSquare => ToolMode.Square,
                _ when sender == ToolPolygon => ToolMode.Polygon,
                _ when sender == ToolPan => ToolMode.Pan,
                _ => ToolMode.Select
            };
        }

        private void OnCanvasToolChangeRequested(ToolMode tool)
        {
            if (tool == ToolMode.Pan) { ToolPan.IsChecked = true; return; }

            // Back to drawing: the rectangle tool is the one this workspace is built around.
            ToolRectangle.IsChecked = true;
        }

        // =====================================================================
        // Region list
        // =====================================================================

        private void OnCanvasChanged()
        {
            UpdateObjectSwatches();
            UpdateStatus($"{_document.Objects.Count} region(s) ready — press Add Data to save them.");
        }

        private void OnCanvasSelectionChanged(CalibrationObjectBase? obj)
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            RegionList.SelectedItem = obj;
            _syncingSelection = false;
        }

        private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            Canvas.Select(RegionList.SelectedItem as CalibrationObjectBase);
            _syncingSelection = false;
        }

        private void RenameRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;

            var dlg = new NameShapeDialog("Rename region", obj.Name,
                _document.Objects.Where(o => o != obj).Select(o => o.Name).ToList())
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true) return;

            _history.Snapshot(_document.Objects);
            obj.Name = dlg.ResultName;
            obj.ClassId = null; // a hand-typed name overrides the label it came from
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            e.Handled = true;
        }

        private void DeleteRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CalibrationObjectBase obj) return;
            _history.Snapshot(_document.Objects);
            _document.Objects.Remove(obj);
            Canvas.Select(null);
            Canvas.RedrawAll();
            e.Handled = true;
        }

        // =====================================================================
        // Toolbar / shortcuts
        // =====================================================================

        private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
        private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

        private void DoUndo()
        {
            var restored = _history.Undo(_document.Objects);
            if (restored == null) return;
            Canvas.ReplaceObjects(restored);
            UpdateObjectSwatches();
        }

        private void DoRedo()
        {
            var restored = _history.Redo(_document.Objects);
            if (restored == null) return;
            Canvas.ReplaceObjects(restored);
            UpdateObjectSwatches();
        }

        private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitToWindow();
        private void Zoom100_Click(object sender, RoutedEventArgs e) => Canvas.SetZoomPercent(100);
        private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
            Canvas.RefitAfterLayoutChange();

        private void Padding_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PaddingValueText == null) return;
            PaddingValueText.Text = ((int)PaddingSlider.Value).ToString();
        }

        private void DataBuilderView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool typing = Keyboard.FocusedElement is TextBox;

            if (ctrl && e.Key == Key.Enter) { AddData(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { SaveDatabase(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z) { DoUndo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.D)
            {
                if (Canvas.Selected != null) DuplicateRegion(Canvas.Selected);
                e.Handled = true;
            }
            // Zoom-to-100% moves to Ctrl+1 here, unlike the rest of the app: in a labelling tool
            // the bare number keys are worth more spent on labels, which get pressed constantly.
            else if (ctrl && (e.Key == Key.D1 || e.Key == Key.NumPad1)) { Canvas.SetZoomPercent(100); e.Handled = true; }
            else if (!typing && e.Key == Key.Delete) { Canvas.DeleteSelected(); e.Handled = true; }
            else if (!typing && e.Key == Key.F) { Canvas.FitToWindow(); e.Handled = true; }
            else if (!typing && !ctrl && e.Key == Key.N) { StepFolder(+1); e.Handled = true; }
            else if (!typing && !ctrl && e.Key == Key.P) { StepFolder(-1); e.Handled = true; }
            else if (!typing && !ctrl && TryGetLabelNumber(e.Key, out int number))
            {
                ApplyLabelShortcut(number);
                e.Handled = true;
            }
        }

        /// <summary>Maps 1-9 (and their numpad twins) to a label slot.</summary>
        private static bool TryGetLabelNumber(Key key, out int number)
        {
            if (key >= Key.D1 && key <= Key.D9) { number = key - Key.D1 + 1; return true; }
            if (key >= Key.NumPad1 && key <= Key.NumPad9) { number = key - Key.NumPad1 + 1; return true; }
            number = 0;
            return false;
        }

        private void DuplicateRegion(CalibrationObjectBase obj)
        {
            var clone = obj.Clone();
            clone.Translate(24, 24);
            clone.Name = NextName(obj.Name);

            _history.Snapshot(_document.Objects);
            _document.Objects.Add(clone);
            UpdateObjectSwatches();
            Canvas.RedrawAll();
            Canvas.Select(clone);
        }

        private string NextName(string baseName)
        {
            for (int n = 2; ; n++)
            {
                var candidate = $"{baseName}_{n:D2}";
                if (_document.Objects.All(o => !string.Equals(o.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }

        private void UpdateStatus(string message, bool isWarning = false)
        {
            StatusText.Text = message;
            StatusText.Foreground = isWarning
                ? (Brush)FindResource("ErrorBrush")
                : (Brush)FindResource("TextSecondary");
        }

        /// <summary>Per-label tally shown in the sidebar. Mutable so the count can tick up in place.</summary>
        public sealed class ClassCount : System.ComponentModel.INotifyPropertyChanged
        {
            private int _count;
            public string ClassName { get; set; } = "";
            public int Count
            {
                get => _count;
                set { _count = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Count))); }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        /// <summary>One numbered entry in the sidebar's label list, bound to a number key.</summary>
        public sealed class LabelShortcut
        {
            public int Number { get; init; }
            public CalibrationClass Label { get; init; } = null!;

            public string Name => Label.Name;
            public Brush SwatchBrush => Label.SwatchBrush;
        }
    }
}

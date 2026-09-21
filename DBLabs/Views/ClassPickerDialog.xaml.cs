using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DBLabs.Models.Roi;
using DBLabs.Services;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>
    /// Cancelled = nothing chosen. Selected = <see cref="ClassPickerDialog.SelectedClass"/> holds a
    /// class from the library. Custom = <see cref="ClassPickerDialog.TypedName"/> holds a name to
    /// use for this one region only, deliberately NOT added to the class library.
    /// </summary>
    public enum ClassPickerOutcome { Cancelled, Selected, Custom }

    /// <summary>
    /// Compact class assignment popup: one textbox to type the class name, one checkbox to decide
    /// whether that name is saved to the reusable class library, and the existing classes listed
    /// below for quick picking. Typing also filters that list.
    /// </summary>
    public partial class ClassPickerDialog : Window
    {
        private readonly ObservableCollection<CalibrationClass> _library;
        private readonly ObservableCollection<CalibrationClass> _view = new();

        public ClassPickerOutcome Outcome { get; private set; } = ClassPickerOutcome.Cancelled;
        public CalibrationClass? SelectedClass { get; private set; }

        /// <summary>Set with Outcome.Custom: a one-off name that must not be persisted.</summary>
        public string? TypedName { get; private set; }

        // WPF can fire a spurious Deactivated on this borderless popup in the same tick it opens —
        // e.g. the mouse-up that completed the click which opened it still bubbling to the owner's
        // custom WindowChrome. Without a guard that self-closes the dialog before the user ever
        // sees it, which is exactly what looked like "the class picker doesn't open". Only treat a
        // Deactivated as real once this window has actually been Activated at least once.
        private bool _hasActivated;

        // Set while the list selection is writing into the name box, so that write does not
        // re-filter the list out from under the click that caused it.
        private bool _syncingNameFromList;

        public ClassPickerDialog(ObservableCollection<CalibrationClass> library, string title = "Assign Calibration Class")
        {
            InitializeComponent();
            _library = library;
            TitleText.Text = title;
            ClassList.ItemsSource = _view;
            RefreshView("");
            Activated += (_, _) => _hasActivated = true;
            Loaded += (_, _) => NameBox.Focus();
        }

        private void RefreshView(string filter)
        {
            _view.Clear();
            var ordered = _library
                .OrderByDescending(c => c.LastUsedUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(c => string.IsNullOrWhiteSpace(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            foreach (var c in ordered) _view.Add(c);
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingNameFromList) return;
            RefreshView(NameBox.Text.Trim());
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && _view.Count > 0)
            {
                ClassList.Focus();
                ClassList.SelectedIndex = 0;
                (ClassList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Assign_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
            }
        }

        /// <summary>Picking a class in the list fills the name box, so Assign uses it as-is.</summary>
        private void ClassList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClassList.SelectedItem is not CalibrationClass c) return;

            _syncingNameFromList = true;
            NameBox.Text = c.Name;
            NameBox.CaretIndex = NameBox.Text.Length;
            _syncingNameFromList = false;
        }

        private void ClassList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ClassList.SelectedItem is CalibrationClass c) ChooseExisting(c);
        }

        private void ClassList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ClassList.SelectedItem is CalibrationClass c) ChooseExisting(c);
        }

        private void Assign_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();

            // Nothing typed: fall back to whatever is highlighted in the list.
            if (string.IsNullOrWhiteSpace(name))
            {
                if (ClassList.SelectedItem is CalibrationClass picked) { ChooseExisting(picked); return; }
                MessageBox.Show(this, "Type a class name, or pick one from the list.",
                    "Assign Calibration Class", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // An exact name match always reuses the existing class rather than creating a
            // duplicate, whether or not the checkbox is ticked.
            var existing = _library.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { ChooseExisting(existing); return; }

            if (AddAsNewClassCheck.IsChecked == true)
            {
                var newClass = new CalibrationClass
                {
                    Name = name,
                    ColorHex = ClassColorPalette.NextColor(_library.Count),
                    LastUsedUtc = DateTime.UtcNow
                };
                _library.Add(newClass);
                ChooseExisting(newClass);
                return;
            }

            // Unchecked: use the name for this region only, leaving the class library untouched.
            TypedName = name;
            Outcome = ClassPickerOutcome.Custom;
            DialogResult = true;
            Close();
        }

        private void ChooseExisting(CalibrationClass c)
        {
            c.LastUsedUtc = DateTime.UtcNow;
            SelectedClass = c;
            Outcome = ClassPickerOutcome.Selected;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Outcome = ClassPickerOutcome.Cancelled;
            DialogResult = false;
            Close();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Losing focus (e.g. clicking elsewhere) closes the popup like a real popup would,
            // rather than leaving an orphaned borderless window behind — but ignore the very first
            // Deactivated if the window was never actually Activated (see _hasActivated above).
            if (_hasActivated && IsVisible && DialogResult == null) Cancel_Click(this, new RoutedEventArgs());
        }
    }
}

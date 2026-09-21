using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DBLabs.Models.Roi;
using DBLabs.Services;
using Microsoft.Win32;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>
    /// Manage the reusable class library: add/rename/duplicate/delete/recolor/reshape, plus
    /// import/export so a technician can carry a standard vocabulary between machines.
    /// Renaming/deleting a class never touches existing calibration-object geometry — only the
    /// currently open document's objects that reference the class have their label updated,
    /// and only when the user explicitly chooses "Rename Everywhere".
    /// </summary>
    public partial class ManageClassesDialog : Window
    {
        private readonly ObservableCollection<CalibrationClass> _library;
        private readonly ObservableCollection<CalibrationObjectBase> _currentDocumentObjects;
        public bool LibraryChanged { get; private set; }

        public ManageClassesDialog(ObservableCollection<CalibrationClass> library, ObservableCollection<CalibrationObjectBase> currentDocumentObjects)
        {
            InitializeComponent();
            _library = library;
            _currentDocumentObjects = currentDocumentObjects;
            Refresh();
        }

        private void Refresh()
        {
            var selectedId = (ClassList.SelectedItem as ClassRow)?.Class.Id;
            var rows = _library
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ClassRow(c, _currentDocumentObjects.Count(o => o.ClassId == c.Id)))
                .ToList();
            ClassList.ItemsSource = rows;
            EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectedId != null)
                ClassList.SelectedItem = rows.FirstOrDefault(r => r.Class.Id == selectedId);
        }

        private ClassRow? Selected => ClassList.SelectedItem as ClassRow;

        private void ClassList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

        private void SaveLibrary()
        {
            ClassLibraryStore.Save(_library);
            LibraryChanged = true;
        }

        private void AddClass_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NameShapeDialog("New class name", "", _library.Select(c => c.Name).ToList()) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var newClass = new CalibrationClass
            {
                Name = dlg.ResultName,
                ColorHex = ClassColorPalette.NextColor(_library.Count)
            };
            _library.Add(newClass);
            SaveLibrary();
            Refresh();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            var others = _library.Where(c => c.Id != row.Class.Id).Select(c => c.Name).ToList();
            var dlg = new NameShapeDialog("Rename class", row.Class.Name, others, isRename: true) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var oldName = row.Class.Name;
            var newName = dlg.ResultName;
            if (oldName == newName) return;

            if (row.UsageCount > 0)
            {
                var choice = MessageBox.Show(this,
                    $"\"{oldName}\" is used by {row.UsageCount} object(s) in the currently open calibration.\n\n" +
                    "Rename it in those objects too? (Objects with a custom instance name different from the class name are left untouched.)\n\n" +
                    "Yes = rename everywhere it matches exactly\nNo = future objects only",
                    "Rename Class", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                {
                    foreach (var obj in _currentDocumentObjects.Where(o => o.ClassId == row.Class.Id && o.Name == oldName))
                        obj.Name = newName;
                }
            }

            row.Class.Name = newName;
            SaveLibrary();
            Refresh();
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            var baseName = row.Class.Name + " copy";
            var name = baseName;
            int n = 2;
            while (_library.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} {n++}";

            var dup = row.Class.Clone();
            dup.Id = Guid.NewGuid().ToString("N");
            dup.Name = name;
            _library.Add(dup);
            SaveLibrary();
            Refresh();
        }

        private void ChangeColor_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            var dlg = new ColorPickerDialog(row.Class.ColorHex) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.ResultHex == null) return;

            row.Class.ColorHex = dlg.ResultHex;
            SaveLibrary();
            Refresh();
        }

        private void ChangeShape_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            var options = new[] { "Rectangle", "Square", "Polygon", "Line", "(none)" };
            var current = row.Class.PreferredShape?.ToString() ?? "(none)";
            var pick = SimpleChoiceDialog.Show(this, "Preferred shape", options, current);
            if (pick == null) return;

            row.Class.PreferredShape = pick switch
            {
                "Rectangle" => ShapeKind.Rectangle,
                "Square" => ShapeKind.Square,
                "Polygon" => ShapeKind.Polygon,
                "Line" => ShapeKind.Line,
                _ => (ShapeKind?)null
            };
            SaveLibrary();
            Refresh();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null) return;

            var message = row.UsageCount > 0
                ? $"This class is currently used by {row.UsageCount} calibration object(s) in the open document.\n\n" +
                  "Deleting the class will NOT delete those objects — they keep their current name, just without a linked class.\n\n" +
                  $"Delete \"{row.Class.Name}\"?"
                : $"Delete \"{row.Class.Name}\"?";

            if (MessageBox.Show(this, message, "Delete Class", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var obj in _currentDocumentObjects.Where(o => o.ClassId == row.Class.Id))
                obj.ClassId = null;

            _library.Remove(row.Class);
            SaveLibrary();
            Refresh();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Class Library JSON|*.json", FileName = "calibration_classes.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ClassLibraryStore.ExportTo(dlg.FileName, _library);
                MessageBox.Show(this, "Exported.", "Export Classes", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not export.\n\n{ex.Message}", "Export Classes", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Class Library JSON|*.json" };
            if (dlg.ShowDialog() != true) return;

            List<CalibrationClass> incoming;
            try
            {
                (incoming, _) = ClassLibraryStore.LoadForImport(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read this file.\n\n{ex.Message}", "Import Classes", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var conflicts = incoming.Where(inc => _library.Any(c => string.Equals(c.Name, inc.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            bool replace = false;
            if (conflicts.Count > 0)
            {
                var msg = $"{conflicts.Count} class(es) already exist:\n\n{string.Join("\n", conflicts.Select(c => c.Name))}\n\n" +
                          "Yes = Replace existing\nNo = Skip existing (keep current)\nCancel = Abort import";
                var choice = MessageBox.Show(this, msg, "Import Conflicts", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                replace = choice == MessageBoxResult.Yes;
            }

            int added = 0, updated = 0;
            foreach (var inc in incoming)
            {
                var existing = _library.FirstOrDefault(c => string.Equals(c.Name, inc.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!replace) continue;
                    existing.ColorHex = inc.ColorHex;
                    existing.PreferredShape = inc.PreferredShape;
                    existing.Description = inc.Description;
                    updated++;
                }
                else
                {
                    inc.Id = Guid.NewGuid().ToString("N");
                    _library.Add(inc);
                    added++;
                }
            }

            SaveLibrary();
            Refresh();
            MessageBox.Show(this, $"Imported {added} new, updated {updated}.", "Import Classes", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private class ClassRow
        {
            public CalibrationClass Class { get; }
            public int UsageCount { get; }
            public Brush SwatchBrush => Class.SwatchBrush;
            public string Name => Class.Name;
            public string ShapeLabel => Class.ShapeLabel;
            public string UsageCountLabel => UsageCount > 0 ? $"{UsageCount} in use" : "";

            public ClassRow(CalibrationClass c, int usageCount)
            {
                Class = c;
                UsageCount = usageCount;
            }
        }
    }
}

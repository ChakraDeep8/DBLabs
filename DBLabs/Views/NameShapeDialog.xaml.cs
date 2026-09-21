using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    public partial class NameShapeDialog : Window
    {
        public string ResultName { get; private set; } = "";

        private readonly IReadOnlyCollection<string> _existingNames;

        public NameShapeDialog(string title, string defaultName, IReadOnlyCollection<string> existingNames, bool isRename = false)
        {
            InitializeComponent();
            _existingNames = existingNames;
            PromptText.Text = title;
            NameBox.Text = defaultName;
            Title = isRename ? "Rename region" : "Name this region";
            Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
            NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Create_Click(this, new RoutedEventArgs()); };
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("Enter a name for this region.");
                return;
            }
            if (_existingNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowError($"\"{name}\" is already used by another region. Names should be unique.");
                return;
            }

            ResultName = name;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}

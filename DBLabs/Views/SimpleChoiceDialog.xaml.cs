using System.Windows;
using System.Windows.Input;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>Tiny single-choice list dialog, used where a full custom dialog would be overkill.</summary>
    public partial class SimpleChoiceDialog : Window
    {
        public string? Result { get; private set; }

        public SimpleChoiceDialog(string prompt, string[] options, string? current)
        {
            InitializeComponent();
            PromptText.Text = prompt;
            OptionsList.ItemsSource = options;
            if (current != null) OptionsList.SelectedItem = current;
        }

        public static string? Show(Window owner, string prompt, string[] options, string? current)
        {
            var dlg = new SimpleChoiceDialog(prompt, options, current) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Result = OptionsList.SelectedItem as string;
            DialogResult = Result != null;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OptionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);
    }
}

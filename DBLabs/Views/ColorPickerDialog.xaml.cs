using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Window = System.Windows.Window;

namespace DBLabs.Views
{
    /// <summary>Small WPF-only swatch-grid color picker (avoids a System.Windows.Forms dependency).</summary>
    public partial class ColorPickerDialog : Window
    {
        private static readonly string[] Palette =
        {
            "#4C8DFF", "#3ECF6B", "#F5A623", "#E5484D", "#22C3D6", "#C86DD7",
            "#E8D34C", "#FF7A9E", "#7CE38B", "#6E8CFF", "#FF9D5C", "#5CE0D8",
            "#FFFFFF", "#9A9AA5", "#000000",
        };

        public string? ResultHex { get; private set; }

        public ColorPickerDialog(string currentHex)
        {
            InitializeComponent();
            HexBox.Text = currentHex;

            foreach (var hex in Palette)
            {
                var swatch = new Border
                {
                    Width = 34, Height = 34, Margin = new Thickness(3), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(string.Equals(hex, currentHex, System.StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    BorderBrush = (Brush)FindResource("AccentBrush"),
                    Cursor = Cursors.Hand,
                    Tag = hex
                };
                try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { swatch.Background = Brushes.Gray; }
                swatch.MouseLeftButtonDown += (_, _) => { HexBox.Text = hex; };
                SwatchGrid.Items.Add(swatch);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var hex = HexBox.Text.Trim();
            try
            {
                ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                MessageBox.Show(this, "Enter a valid hex color, e.g. #4C8DFF.", "Choose Color", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ResultHex = hex;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

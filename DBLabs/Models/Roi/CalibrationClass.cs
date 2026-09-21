using System;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DBLabs.Models.Roi
{
    /// <summary>
    /// A reusable, named calibration label (e.g. "CUSTOMER_ZONE_ROI") — created once, then
    /// assigned to any number of drawn objects across any number of camera calibrations.
    /// This is the "class library" and is intentionally separate from any one calibration file:
    /// it persists on the machine and is reused across cameras/projects.
    /// </summary>
    public class CalibrationClass
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#4C8DFF";

        /// <summary>Optional — if set, selecting this class can auto-activate the matching draw tool.</summary>
        public ShapeKind? PreferredShape { get; set; }

        public string Description { get; set; } = "";
        public bool IsFavorite { get; set; }
        public DateTime? LastUsedUtc { get; set; }

        // ---- Display-only helpers for XAML binding (not persisted) ----
        [JsonIgnore]
        public Brush SwatchBrush
        {
            get
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex)); }
                catch { return Brushes.Gray; }
            }
        }

        [JsonIgnore]
        public string ShapeLabel => PreferredShape?.ToString() ?? "";

        public CalibrationClass Clone() => new()
        {
            Id = Id, Name = Name, ColorHex = ColorHex, PreferredShape = PreferredShape,
            Description = Description, IsFavorite = IsFavorite, LastUsedUtc = LastUsedUtc
        };
    }
}

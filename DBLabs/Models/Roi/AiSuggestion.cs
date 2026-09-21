using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace DBLabs.Models.Roi
{
    /// <summary>
    /// One "Magic" AI calibration candidate — a region the detector thinks is worth drawing,
    /// before the technician accepts, renames, classifies or discards it. Never touches the
    /// document directly; MagicSuggestionsDialog turns accepted ones into real
    /// CalibrationObjectBase instances via AiSuggestion.ToCalibrationObject().
    /// </summary>
    public class AiSuggestion : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>What the detector believes this is — shown to the technician, not a guarantee.</summary>
        public string DetectedLabel { get; set; } = "";

        /// <summary>Which detector produced this ("YOLOv8", "HOG fallback", "Outline", "Circle").
        /// Surfaced in the review list so it is never ambiguous which engine drew a box — a
        /// silent fallback to the classical detectors is otherwise indistinguishable from the
        /// model simply doing badly.</summary>
        public string Source { get; set; } = "";

        /// <summary>Rectangle or Square for axis-aligned boxes; Polygon for contour/circle approximations.</summary>
        public ShapeKind Kind { get; set; }

        /// <summary>Image-pixel-space bounds for Rectangle/Square suggestions.</summary>
        public Rect Bounds { get; set; }

        /// <summary>Image-pixel-space vertices for Polygon suggestions (irregular contours or circle approximations).</summary>
        public List<Point>? PolygonPoints { get; set; }

        /// <summary>0..1 heuristic confidence — used only to sort/badge, never to auto-accept.</summary>
        public double Confidence { get; set; }

        private bool _include = true;
        /// <summary>Whether this suggestion is checked in the review list.</summary>
        public bool Include { get => _include; set { _include = value; Raise(); } }

        private string _name = "";
        /// <summary>Editable name the technician can change before applying.</summary>
        public string Name { get => _name; set { _name = value; Raise(); } }

        /// <summary>Class picked from the shared library, if any; null keeps the typed Name as a one-off label.</summary>
        public CalibrationClass? SelectedClass { get; set; }

        public string ConfidenceLabel => $"{Confidence * 100:0}%";

        public CalibrationObjectBase ToCalibrationObject()
        {
            CalibrationObjectBase obj = Kind switch
            {
                ShapeKind.Square => new RectangleObject { X1 = Bounds.Left, Y1 = Bounds.Top, X2 = Bounds.Right, Y2 = Bounds.Bottom, IsSquare = true },
                ShapeKind.Rectangle => new RectangleObject { X1 = Bounds.Left, Y1 = Bounds.Top, X2 = Bounds.Right, Y2 = Bounds.Bottom },
                ShapeKind.Polygon => new PolygonObject { Points = PolygonPoints != null ? new List<Point>(PolygonPoints) : new List<Point>() },
                _ => new RectangleObject { X1 = Bounds.Left, Y1 = Bounds.Top, X2 = Bounds.Right, Y2 = Bounds.Bottom },
            };
            obj.Name = string.IsNullOrWhiteSpace(Name) ? DetectedLabel : Name;
            obj.ClassId = SelectedClass?.Id;
            return obj;
        }
    }
}

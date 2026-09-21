using System.Collections.ObjectModel;

namespace DBLabs.Models.Roi
{
    /// <summary>
    /// The frame currently being labelled and the regions drawn on it. Canvas and region list
    /// both render from this one model, so they cannot drift apart.
    ///
    /// Geometry lives in ORIGINAL image pixel coordinates throughout — crops are always cut from
    /// the full-resolution frame, never from whatever happens to be on screen.
    /// </summary>
    public class RoiDocument
    {
        public string ImageFileName { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }

        public ObservableCollection<CalibrationObjectBase> Objects { get; } = new();

        public bool HasImage => ImageWidth > 0 && ImageHeight > 0;
    }
}

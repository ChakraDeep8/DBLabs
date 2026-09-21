namespace DBLabs.Models.Roi
{
    /// <summary>How a crop of arbitrary shape is made to fit a fixed output size.</summary>
    public enum CropFit
    {
        /// <summary>Widen the crop box in the source frame until it is square, then scale. Uses
        /// real surrounding pixels instead of padding, at the cost of more background per crop.</summary>
        ExpandToSquare,

        /// <summary>Scale to fit and pad the remainder — the subject is untouched and no extra
        /// background creeps in, but part of every image is dead space.</summary>
        Letterbox,

        /// <summary>Scale to the exact size regardless of aspect. Every pixel is subject, but a
        /// person comes out visibly distorted.</summary>
        Stretch,

        /// <summary>Scale to a fixed height and let width follow the subject. Consistent scale
        /// rather than identical files.</summary>
        FixedHeight
    }

    /// <summary>
    /// Requested output size for exported crops. Null sizing anywhere means "leave the crop at
    /// whatever size it came out of the frame", which is the historical behaviour.
    /// </summary>
    public class CropSizing
    {
        /// <summary>Target edge in pixels — both sides for the square fits, the height for
        /// FixedHeight.</summary>
        public int TargetSize { get; set; } = 640;

        public CropFit Fit { get; set; } = CropFit.ExpandToSquare;
    }
}

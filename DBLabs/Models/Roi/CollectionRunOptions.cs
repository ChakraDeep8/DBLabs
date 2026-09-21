using System;

namespace DBLabs.Models.Roi
{
    /// <summary>
    /// Settings for one unattended collection run against an RTSP stream. See
    /// PersonCollectorService for how they are applied.
    /// </summary>
    public class CollectionRunOptions
    {
        public string RtspUrl { get; set; } = "";

        /// <summary>How often to sample the stream. A ceiling rather than a promise: inference
        /// takes a few hundred milliseconds, so the achievable rate tops out around 3-4 fps and
        /// the run reports what it actually managed.</summary>
        public double FramesPerSecond { get; set; } = 1.0;

        public string OutputFolder { get; set; } = "";

        /// <summary>Folder name the crops are filed under, matching how the Data Builder labels
        /// hand-drawn regions.</summary>
        public string LabelName { get; set; } = "Person";

        /// <summary>
        /// Minimum detector confidence for a crop to be written. Deliberately far above Magic's
        /// adaptive floor (which drops to 0.15 on IR footage): that floor is safe when a human
        /// reviews every suggestion, but here a weak guess becomes a permanently mislabelled
        /// training file that nobody ever looks at again.
        /// </summary>
        public float MinConfidence { get; set; } = 0.50f;

        /// <summary>Boxes shorter than this on their narrow side are dropped — a 20px figure in
        /// the distance carries no detail worth training on.</summary>
        public int MinCropSidePixels { get; set; } = 48;

        public int PaddingPixels { get; set; }

        /// <summary>Uniform output size for every crop, or null to keep each one at whatever size
        /// it came out of the frame.</summary>
        public CropSizing? CropSizing { get; set; }

        /// <summary>Mean absolute difference (0-255 scale) below which two crops in the same place
        /// count as the same shot. Higher keeps more, lower demands more variety.</summary>
        public double VarietyThreshold { get; set; } = 6.0;

        /// <summary>How long a recently-saved crop is remembered for duplicate comparison.</summary>
        public TimeSpan DuplicateMemory { get; set; } = TimeSpan.FromSeconds(60);

        // ---- Stop conditions: any combination, whichever trips first. None set = until stopped.
        public TimeSpan? MaxDuration { get; set; }
        public int? MaxCrops { get; set; }
        public DateTime? StopAtLocalTime { get; set; }

        public TimeSpan SampleInterval => TimeSpan.FromSeconds(1.0 / Math.Max(0.05, FramesPerSecond));

        public bool HasAnyStopCondition => MaxDuration.HasValue || MaxCrops.HasValue || StopAtLocalTime.HasValue;
    }
}

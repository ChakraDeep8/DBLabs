using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DBLabs.Models.Roi;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Rect = System.Windows.Rect;

namespace DBLabs.Services
{
    /// <summary>Live state of a run, pushed to the UI as it goes.</summary>
    public sealed class CollectionProgress
    {
        public int FramesSampled { get; init; }
        public int CropsSaved { get; init; }
        public int DuplicatesSkipped { get; init; }
        public int LowQualitySkipped { get; init; }
        public int Reconnects { get; init; }
        public TimeSpan Elapsed { get; init; }
        public double ActualFps { get; init; }
        public string? Message { get; init; }

        /// <summary>Frozen on the worker thread before it is handed over — a Mat never crosses
        /// threads, and a frozen BitmapSource is safe to touch from the UI.</summary>
        public BitmapSource? Preview { get; init; }

        /// <summary>Thumbnails of the crops this sample just wrote, so the window can show what
        /// is actually being collected rather than only what the camera sees. Frozen, like the
        /// preview.</summary>
        public IReadOnlyList<BitmapSource>? NewCrops { get; init; }
    }

    public sealed class CollectionSummary
    {
        public int CropsSaved { get; init; }
        public int FramesSampled { get; init; }
        public int DuplicatesSkipped { get; init; }
        public int LowQualitySkipped { get; init; }
        public int Reconnects { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string StoppedBecause { get; init; } = "";
        public string? ManifestPath { get; init; }
        public string? Error { get; init; }
        public bool Failed => Error != null;
    }

    /// <summary>
    /// Collects person crops from a live stream, unattended.
    ///
    /// The loop reads continuously and samples on a clock, rather than reading once per interval.
    /// That is not an optimisation: RTSP frames queue inside the FFMPEG buffer, so a loop that
    /// only reads when it wants a sample drifts further and further behind real time, eventually
    /// labelling footage from minutes ago. Draining the stream keeps "now" actually now.
    /// </summary>
    public static class PersonCollectorService
    {
        private const int MaxConsecutiveFailures = 10;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

        public static Task<CollectionSummary> RunAsync(
            CollectionRunOptions options,
            Func<IFrameSource> sourceFactory,
            IProgress<CollectionProgress>? progress,
            CancellationToken token) =>
            Task.Run(() => Run(options, sourceFactory, progress, token), token);

        private static CollectionSummary Run(
            CollectionRunOptions options,
            Func<IFrameSource> sourceFactory,
            IProgress<CollectionProgress>? progress,
            CancellationToken token)
        {
            var started = DateTime.Now;
            var clock = Stopwatch.StartNew();
            var duplicates = new CropSimilarityIndex(options.VarietyThreshold, options.DuplicateMemory);
            var saved = new List<CropResult>();

            int framesSampled = 0, duplicatesSkipped = 0, lowQualitySkipped = 0, reconnects = 0;
            string stoppedBecause = "stopped";

            var source = sourceFactory();
            try
            {
                if (!source.Open())
                    return Fail($"Could not open the stream at {options.RtspUrl}.");

                using var frame = new Mat();
                var nextSample = TimeSpan.Zero;
                int consecutiveFailures = 0;

                while (!token.IsCancellationRequested)
                {
                    var reason = StopReason(options, clock.Elapsed, saved.Count, DateTime.Now);
                    if (reason != null) { stoppedBecause = reason; break; }

                    if (!source.TryRead(frame))
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= MaxConsecutiveFailures)
                            return Fail($"The stream dropped and did not come back after {MaxConsecutiveFailures} attempts.");

                        reconnects++;
                        Report(progress, $"Stream dropped — reconnecting (attempt {consecutiveFailures})…");

                        var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, consecutiveFailures - 1)));
                        if (token.WaitHandle.WaitOne(backoff)) break;

                        source.Dispose();
                        source = sourceFactory();
                        source.Open();
                        continue;
                    }
                    consecutiveFailures = 0;

                    if (clock.Elapsed < nextSample) continue;

                    // Clamped to now rather than advanced by one interval: if a sample overran,
                    // stepping forward would leave the schedule in the past and fire a burst of
                    // back-to-back samples trying to catch up.
                    nextSample = clock.Elapsed + options.SampleInterval;
                    framesSampled++;

                    var sample = SampleFrame(frame, options, duplicates, framesSampled);
                    if (sample.Error != null) { stoppedBecause = "write failed"; Report(progress, sample.Error); break; }

                    saved.AddRange(sample.Written);
                    duplicatesSkipped += sample.Duplicates;
                    lowQualitySkipped += sample.LowQuality;

                    Report(progress, null, Preview(frame), sample.Thumbnails);
                }

                string? manifest = null;
                if (saved.Count > 0)
                {
                    try { manifest = DatasetManifestService.AppendBatch(options.OutputFolder, saved); }
                    catch (Exception ex) { Report(progress, $"Crops are saved, but the index could not be written: {ex.Message}"); }
                }

                return new CollectionSummary
                {
                    CropsSaved = saved.Count,
                    FramesSampled = framesSampled,
                    DuplicatesSkipped = duplicatesSkipped,
                    LowQualitySkipped = lowQualitySkipped,
                    Reconnects = reconnects,
                    Elapsed = clock.Elapsed,
                    StoppedBecause = token.IsCancellationRequested ? "stopped by you" : stoppedBecause,
                    ManifestPath = manifest
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            finally
            {
                source.Dispose();
            }

            CollectionSummary Fail(string error) => new()
            {
                CropsSaved = saved.Count,
                FramesSampled = framesSampled,
                DuplicatesSkipped = duplicatesSkipped,
                LowQualitySkipped = lowQualitySkipped,
                Reconnects = reconnects,
                Elapsed = clock.Elapsed,
                StoppedBecause = "error",
                Error = error
            };

            void Report(IProgress<CollectionProgress>? sink, string? message, BitmapSource? preview = null,
                IReadOnlyList<BitmapSource>? newCrops = null) =>
                sink?.Report(new CollectionProgress
                {
                    NewCrops = newCrops,
                    FramesSampled = framesSampled,
                    CropsSaved = saved.Count,
                    DuplicatesSkipped = duplicatesSkipped,
                    LowQualitySkipped = lowQualitySkipped,
                    Reconnects = reconnects,
                    Elapsed = clock.Elapsed,
                    ActualFps = clock.Elapsed.TotalSeconds > 0 ? framesSampled / clock.Elapsed.TotalSeconds : 0,
                    Message = message,
                    Preview = preview
                });
        }

        private sealed record SampleOutcome(
            List<CropResult> Written,
            int Duplicates,
            int LowQuality,
            string? Error,
            IReadOnlyList<BitmapSource>? Thumbnails);

        /// <summary>Detects people in one sampled frame and writes the crops worth keeping.</summary>
        private static SampleOutcome SampleFrame(
            Mat frame, CollectionRunOptions options, CropSimilarityIndex duplicates, int sampleNumber)
        {
            var written = new List<CropResult>();
            int dupes = 0, weak = 0;

            var people = YoloObjectDetector
                .Detect(frame, confidenceThreshold: options.MinConfidence)
                .Where(s => s.DetectedLabel.Equals("Person", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var keep = new List<CalibrationObjectBase>();
            var thumbnails = new List<BitmapSource>();

            foreach (var person in people)
            {
                var box = person.Bounds;
                if (Math.Min(box.Width, box.Height) < options.MinCropSidePixels) { weak++; continue; }

                var rect = ToMatRect(box, frame.Width, frame.Height);
                if (rect.Width < 2 || rect.Height < 2) { weak++; continue; }

                using var crop = new Mat(frame, rect);
                var nowUtc = DateTime.UtcNow;
                if (duplicates.IsDuplicate(crop, box, nowUtc)) { dupes++; continue; }
                duplicates.Remember(crop, box, nowUtc);

                // Built from the crop already in hand for the duplicate check, rather than
                // re-reading the file back off disk after it is written.
                var thumbnail = Thumbnail(crop);
                if (thumbnail != null) thumbnails.Add(thumbnail);

                keep.Add(new RectangleObject
                {
                    // No ClassId: CropExportService falls back to the region's own name for the
                    // folder, which is exactly the label this run is filing under.
                    Name = options.LabelName,
                    X1 = box.Left, Y1 = box.Top, X2 = box.Right, Y2 = box.Bottom
                });
            }

            if (keep.Count == 0) return new SampleOutcome(written, dupes, weak, null, null);

            try
            {
                var frameName = $"rtsp_{DateTime.Now:yyyyMMdd_HHmmss}_{sampleNumber:D5}.jpg";
                Directory.CreateDirectory(options.OutputFolder);
                written.AddRange(CropExportService
                    .ExportCrops(frame, keep, options.OutputFolder, frameName, _ => null,
                        options.PaddingPixels, options.CropSizing)
                    .Where(r => r.Saved));
            }
            catch (Exception ex)
            {
                return new SampleOutcome(written, dupes, weak, $"Could not write crops: {ex.Message}", null);
            }

            // Only show thumbnails when every kept region really was written. The alternative —
            // pairing them up by index — would quietly mislabel the strip if the export skipped
            // one, and showing a crop that isn't on disk is worse than showing none.
            var shown = written.Count == thumbnails.Count ? thumbnails : null;
            return new SampleOutcome(written, dupes, weak, null, shown);
        }

        /// <summary>A small frozen thumbnail of one crop, safe to hand to the UI thread.</summary>
        private static BitmapSource? Thumbnail(Mat crop)
        {
            try
            {
                double scale = Math.Min(1.0, 120.0 / Math.Max(crop.Width, crop.Height));
                using var small = new Mat();
                Cv2.Resize(crop, small,
                    new OpenCvSharp.Size(Math.Max(1, (int)(crop.Width * scale)), Math.Max(1, (int)(crop.Height * scale))),
                    interpolation: InterpolationFlags.Area);

                var bitmap = small.ToBitmapSource();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Which stop condition has tripped, if any — the first one that has. Takes the current
        /// time as an argument rather than reading the clock itself, so the whole rule set can be
        /// tested without waiting for real minutes to pass.
        /// </summary>
        public static string? StopReason(CollectionRunOptions options, TimeSpan elapsed, int cropsSaved, DateTime nowLocal)
        {
            if (options.MaxDuration.HasValue && elapsed >= options.MaxDuration.Value)
                return $"ran for the full {Describe(options.MaxDuration.Value)}";

            if (options.MaxCrops.HasValue && cropsSaved >= options.MaxCrops.Value)
                return $"reached {options.MaxCrops.Value} crops";

            if (options.StopAtLocalTime.HasValue && nowLocal >= options.StopAtLocalTime.Value)
                return $"reached {options.StopAtLocalTime.Value:HH:mm}";

            return null;
        }

        private static string Describe(TimeSpan span) =>
            span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hour(s)" : $"{span.TotalMinutes:0.#} minute(s)";

        private static OpenCvSharp.Rect ToMatRect(Rect box, int width, int height)
        {
            int x = Math.Clamp((int)Math.Floor(box.X), 0, Math.Max(0, width - 1));
            int y = Math.Clamp((int)Math.Floor(box.Y), 0, Math.Max(0, height - 1));
            int w = Math.Clamp((int)Math.Ceiling(box.Width), 0, width - x);
            int h = Math.Clamp((int)Math.Ceiling(box.Height), 0, height - y);
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        private static BitmapSource? Preview(Mat frame)
        {
            try
            {
                using var small = new Mat();
                double scale = Math.Min(1.0, 480.0 / Math.Max(frame.Width, frame.Height));
                Cv2.Resize(frame, small, new OpenCvSharp.Size((int)(frame.Width * scale), (int)(frame.Height * scale)),
                    interpolation: InterpolationFlags.Area);

                var bitmap = small.ToBitmapSource();
                bitmap.Freeze(); // frozen here so the UI thread can use it safely
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace DBLabs.Services
{
    /// <summary>Outcome of a sharp-frame search.</summary>
    public sealed class SharpFrameResult
    {
        /// <summary>The chosen frame, or null when nothing could be decoded at all.</summary>
        public Mat? Frame { get; init; }

        /// <summary>Laplacian variance of the chosen frame. Higher is sharper.</summary>
        public double Sharpness { get; init; }

        /// <summary>True when the frame met the sharpness threshold.</summary>
        public bool MeetsThreshold { get; init; }

        /// <summary>How many candidate frames were actually decoded and scored.</summary>
        public int CandidatesScored { get; init; }

        public string? Error { get; init; }
    }

    /// <summary>
    /// Pulls a sharp, in-focus frame out of a video file or stream.
    ///
    /// Blur is measured by Laplacian variance: convolving with a Laplacian kernel isolates
    /// high-frequency detail (edges), so a crisp frame produces a wide spread of responses and a
    /// high variance, while a blurred or motion-smeared one produces a narrow spread and a low
    /// variance. Scoring happens on a downscaled grayscale copy so the number is comparable
    /// across resolutions and cheap to compute.
    ///
    /// Candidates are decoded and scored CONCURRENTLY rather than one at a time: several workers
    /// each open their own VideoCapture (OpenCV captures are not thread-safe, so they cannot be
    /// shared) and seek to different positions spread across the video. The first frame to clear
    /// the threshold wins and cancels the rest, so the wait is roughly one seek+decode rather
    /// than the sum of all of them.
    /// </summary>
    public static class VideoFrameGrabService
    {
        /// <summary>
        /// Laplacian-variance cutoff for "sharp enough". ~100 is the widely used starting point
        /// for this metric; frames that are visibly soft or motion-smeared land well below it.
        /// </summary>
        public const double DefaultSharpnessThreshold = 100.0;

        public static async Task<SharpFrameResult> GrabSharpFrameAsync(
            string source,
            double threshold = DefaultSharpnessThreshold,
            int candidateCount = 16,
            int timeoutMs = 20000,
            CancellationToken cancellationToken = default)
        {
            int frameCount;
            try
            {
                frameCount = await Task.Run(() =>
                {
                    using var probe = new VideoCapture(source);
                    if (!probe.IsOpened()) return -1;
                    return (int)probe.Get(VideoCaptureProperties.FrameCount);
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new SharpFrameResult { Error = "Cancelled." };
            }
            catch (Exception ex)
            {
                return new SharpFrameResult { Error = ex.Message };
            }

            if (frameCount < 0)
                return new SharpFrameResult { Error = "Could not open this video. Check the path/URL and the file format." };

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            return frameCount > 1
                ? await ScanSeekableAsync(source, frameCount, threshold, candidateCount, linked)
                : await ScanSequentialAsync(source, threshold, candidateCount, linked);
        }

        /// <summary>
        /// Seekable source (a video file): spread candidate positions across the running time and
        /// score them in parallel, first past the threshold wins.
        /// </summary>
        private static async Task<SharpFrameResult> ScanSeekableAsync(
            string source, int frameCount, double threshold, int candidateCount, CancellationTokenSource cts)
        {
            // Skip the outer 5% at each end: intros, fades and trailing black frames are common
            // there and score as blurry for reasons that have nothing to do with focus.
            int first = (int)(frameCount * 0.05);
            int last = (int)(frameCount * 0.95);
            if (last <= first) { first = 0; last = Math.Max(0, frameCount - 1); }

            int count = Math.Max(1, Math.Min(candidateCount, last - first + 1));
            var positions = new int[count];
            double step = count > 1 ? (last - first) / (double)(count - 1) : 0;
            for (int i = 0; i < count; i++) positions[i] = first + (int)Math.Round(step * i);

            int workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, Math.Min(4, count)));
            var queue = new ConcurrentQueue<int>(positions);
            var scored = new ConcurrentBag<(Mat Frame, double Sharpness)>();
            var winner = new TaskCompletionSource<(Mat Frame, double Sharpness)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
            {
                VideoCapture? capture = null;
                try
                {
                    capture = new VideoCapture(source);
                    if (!capture.IsOpened()) return;

                    while (!cts.IsCancellationRequested && queue.TryDequeue(out int position))
                    {
                        capture.Set(VideoCaptureProperties.PosFrames, position);
                        var frame = new Mat();
                        if (!capture.Read(frame) || frame.Empty()) { frame.Dispose(); continue; }

                        double sharpness = MeasureSharpness(frame);
                        if (sharpness >= threshold)
                        {
                            // First acceptable frame ends the search for everyone.
                            if (winner.TrySetResult((frame, sharpness))) cts.Cancel();
                            else frame.Dispose();
                            return;
                        }

                        scored.Add((frame, sharpness));
                    }
                }
                catch (Exception) { /* a dead worker just means fewer candidates */ }
                finally { capture?.Dispose(); }
            })).ToArray();

            await Task.WhenAny(winner.Task, Task.WhenAll(workers));

            if (winner.Task.IsCompletedSuccessfully)
            {
                var hit = winner.Task.Result;
                int total = scored.Count + 1;
                DisposeAll(scored);
                return new SharpFrameResult
                {
                    Frame = hit.Frame,
                    Sharpness = hit.Sharpness,
                    MeetsThreshold = true,
                    CandidatesScored = total
                };
            }

            // Nothing cleared the bar - hand back the sharpest seen so the caller can report how
            // close it got, flagged as not meeting the threshold.
            return BestOf(scored);
        }

        /// <summary>
        /// Non-seekable source (a live stream): frames can only be read forward, so decode a run
        /// of them on one capture and score each as it arrives, stopping at the first sharp one.
        /// </summary>
        private static async Task<SharpFrameResult> ScanSequentialAsync(
            string source, double threshold, int candidateCount, CancellationTokenSource cts)
        {
            return await Task.Run(() =>
            {
                using var capture = new VideoCapture(source);
                if (!capture.IsOpened())
                    return new SharpFrameResult { Error = "Could not open this source." };

                var scored = new ConcurrentBag<(Mat Frame, double Sharpness)>();

                for (int i = 0; i < candidateCount && !cts.IsCancellationRequested; i++)
                {
                    var frame = new Mat();
                    if (!capture.Read(frame) || frame.Empty()) { frame.Dispose(); continue; }

                    double sharpness = MeasureSharpness(frame);
                    if (sharpness >= threshold)
                    {
                        int total = scored.Count + 1;
                        DisposeAll(scored);
                        return new SharpFrameResult
                        {
                            Frame = frame,
                            Sharpness = sharpness,
                            MeetsThreshold = true,
                            CandidatesScored = total
                        };
                    }

                    scored.Add((frame, sharpness));
                }

                return BestOf(scored);
            });
        }

        private static SharpFrameResult BestOf(ConcurrentBag<(Mat Frame, double Sharpness)> scored)
        {
            if (scored.IsEmpty)
                return new SharpFrameResult { Error = "No frames could be decoded from this source." };

            var ordered = scored.OrderByDescending(c => c.Sharpness).ToList();
            var best = ordered[0];
            foreach (var candidate in ordered.Skip(1)) candidate.Frame.Dispose();

            return new SharpFrameResult
            {
                Frame = best.Frame,
                Sharpness = best.Sharpness,
                MeetsThreshold = false,
                CandidatesScored = ordered.Count
            };
        }

        private static void DisposeAll(ConcurrentBag<(Mat Frame, double Sharpness)> scored)
        {
            foreach (var candidate in scored) candidate.Frame.Dispose();
        }

        /// <summary>
        /// Laplacian variance of the frame. Scored on a grayscale copy scaled to a fixed width so
        /// the value means the same thing for a 720p clip and a 4K one.
        /// </summary>
        public static double MeasureSharpness(Mat frame)
        {
            const int scoringWidth = 640;

            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            using var scaled = new Mat();
            if (gray.Width > scoringWidth)
            {
                int height = Math.Max(1, (int)(gray.Height * (scoringWidth / (double)gray.Width)));
                Cv2.Resize(gray, scaled, new OpenCvSharp.Size(scoringWidth, height), interpolation: InterpolationFlags.Area);
            }
            else
            {
                gray.CopyTo(scaled);
            }

            using var laplacian = new Mat();
            Cv2.Laplacian(scaled, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stdDev);

            return stdDev.Val0 * stdDev.Val0;
        }
    }
}

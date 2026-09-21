using System;
using System.Collections.Generic;
using OpenCvSharp;
using Rect = System.Windows.Rect;
using Size = OpenCvSharp.Size;

namespace DBLabs.Services
{
    /// <summary>
    /// Short-term memory of what has just been saved, so an unattended run collects variety
    /// rather than volume.
    ///
    /// The problem it solves: someone standing still at 2 fps produces thousands of near-identical
    /// crops that cost disk and teach a classifier nothing. The problem it must NOT cause: a
    /// genuinely different person walking into the same doorway is not a duplicate.
    ///
    /// So a crop is rejected only when BOTH gates agree — it overlaps a recent box heavily AND
    /// looks the same. Position alone would discard the second person; appearance alone would
    /// discard a matching outfit elsewhere in the frame.
    /// </summary>
    public sealed class CropSimilarityIndex
    {
        /// <summary>Thumbnails are compared at this size: small enough to be cheap and to ignore
        /// noise, large enough to tell two people apart.</summary>
        private const int SignatureSize = 32;

        /// <summary>Overlap above which two boxes count as "the same place".</summary>
        private const double SpatialGate = 0.8;

        private readonly double _appearanceThreshold;
        private readonly TimeSpan _memory;
        private readonly List<Entry> _recent = new();

        public CropSimilarityIndex(double appearanceThreshold, TimeSpan memory)
        {
            _appearanceThreshold = appearanceThreshold;
            _memory = memory;
        }

        private readonly record struct Entry(Rect Box, float[] Signature, DateTime SeenUtc);

        public int Remembered => _recent.Count;

        /// <summary>True when this crop is close enough to something just saved to be worth skipping.</summary>
        public bool IsDuplicate(Mat crop, Rect box, DateTime nowUtc)
        {
            Forget(nowUtc);
            var signature = Signature(crop);

            foreach (var entry in _recent)
            {
                if (Overlap(entry.Box, box) < SpatialGate) continue;
                if (Difference(entry.Signature, signature) <= _appearanceThreshold) return true;
            }
            return false;
        }

        public void Remember(Mat crop, Rect box, DateTime nowUtc)
        {
            Forget(nowUtc);
            _recent.Add(new Entry(box, Signature(crop), nowUtc));
        }

        private void Forget(DateTime nowUtc) =>
            _recent.RemoveAll(e => nowUtc - e.SeenUtc > _memory);

        /// <summary>
        /// A mean-subtracted 32x32 grayscale thumbnail. Subtracting each thumbnail's own mean is
        /// what stops a passing cloud or a light being switched on from reading as "a different
        /// person" — the comparison is about shape and contrast, not absolute brightness.
        /// </summary>
        private static float[] Signature(Mat crop)
        {
            using var gray = new Mat();
            if (crop.Channels() >= 3) Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
            else crop.CopyTo(gray);

            using var small = new Mat();
            Cv2.Resize(gray, small, new Size(SignatureSize, SignatureSize), interpolation: InterpolationFlags.Area);

            var values = new float[SignatureSize * SignatureSize];
            double sum = 0;
            for (int y = 0; y < SignatureSize; y++)
            {
                for (int x = 0; x < SignatureSize; x++)
                {
                    float v = small.Get<byte>(y, x);
                    values[y * SignatureSize + x] = v;
                    sum += v;
                }
            }

            float mean = (float)(sum / values.Length);
            for (int i = 0; i < values.Length; i++) values[i] -= mean;
            return values;
        }

        private static double Difference(float[] a, float[] b)
        {
            if (a.Length != b.Length) return double.MaxValue;
            double total = 0;
            for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
            return total / a.Length;
        }

        /// <summary>Intersection over union of two boxes.</summary>
        private static double Overlap(Rect a, Rect b)
        {
            var intersect = Rect.Intersect(a, b);
            if (intersect.IsEmpty) return 0;
            double intersectArea = intersect.Width * intersect.Height;
            double union = a.Width * a.Height + b.Width * b.Height - intersectArea;
            return union <= 0 ? 0 : intersectArea / union;
        }
    }
}

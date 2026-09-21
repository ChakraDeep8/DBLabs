using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DBLabs.Models.Roi;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Rect = System.Windows.Rect;
using Size = OpenCvSharp.Size;

namespace DBLabs.Services
{
    /// <summary>
    /// Person detection via a YOLOv8s ONNX model run on-device through ONNX Runtime — no network
    /// call at inference time. It recognises the 80 COCO categories by name; the collector keeps
    /// only "person".
    ///
    /// The weights are NOT distributed with this source. They are Ultralytics' and AGPL-3.0, so
    /// ModelStore fetches them on first run into the user's own app-data folder rather than
    /// bundling them here — see ModelStore for the reasoning.
    /// </summary>
    public static class YoloObjectDetector
    {
        private const int ModelSize = 640;
        private const int NumClasses = 80;

        private static readonly Lazy<InferenceSession?> Session = new(LoadSession);

        private static InferenceSession? LoadSession()
        {
            try
            {
                var path = ModelStore.ModelPath;
                return File.Exists(path) ? new InferenceSession(path) : null;
            }
            catch
            {
                // Missing or corrupt weights, unsupported hardware — callers check IsAvailable
                // and say so plainly rather than the app dying on a background thread.
                return null;
            }
        }

        public static bool IsAvailable => Session.Value != null;

        /// <summary>Runs YOLOv8 detection on one frame. Leave confidenceThreshold null to use the
        /// per-frame adaptive cutoff (see AdaptiveConfidenceThreshold); iouThreshold controls how
        /// aggressively overlapping boxes are merged.</summary>
        public static List<AiSuggestion> Detect(Mat source, float? confidenceThreshold = null, float iouThreshold = 0.55f,
            bool enhanceLowLight = true)
        {
            var session = Session.Value;
            if (session == null) return new List<AiSuggestion>();

            try
            {
                double saturation = MeanSaturation(source);
                float threshold = confidenceThreshold ?? ThresholdFor(saturation);

                // Washed-out IR/night frames are the ones the model struggles on, so they get a
                // local-contrast pass before inference (see EnhanceForInference). Ordinary
                // colour footage is handed over untouched. Either way this only ever changes the
                // pixels the model looks at — detections come back in the original frame's own
                // coordinates, so crops are still cut from the untouched frame.
                using var enhanced = enhanceLowLight ? EnhanceForInference(source, saturation) : null;
                var (tensor, scale, padX, padY) = Preprocess(enhanced ?? source);

                using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("images", tensor) });
                var output = results.First().AsEnumerable<float>().ToArray(); // [1,84,8400] flattened

                var candidates = DecodeCandidates(output, source.Width, source.Height, scale, padX, padY, threshold);
                var kept = NonMaxSuppression(candidates, iouThreshold);

                return kept
                    .OrderByDescending(c => c.Score)
                    .Take(30)
                    .Select(BuildSuggestion)
                    .ToList();
            }
            catch
            {
                // Best-effort — a decode/inference hiccup on an unusual frame just yields no
                // YOLO suggestions for that run, never a crash.
                return new List<AiSuggestion>();
            }
        }

        /// <summary>Turns one surviving detection into a plain box — crops are cut rectangular,
        /// so tracing a silhouette inside the box would be work thrown away here.</summary>
        private static AiSuggestion BuildSuggestion(Candidate c) => new()
        {
            DetectedLabel = ClassName(c.ClassId),
            Kind = ShapeKind.Rectangle,
            Bounds = c.Box,
            Confidence = c.Score,
            Source = "YOLOv8",
        };

        /// <summary>
        /// Picks a confidence cutoff from how colourful the frame is.
        ///
        /// Measured across real sample frames, brightness turned out to be the wrong signal —
        /// a dim but colourful portrait (mean brightness 62) scored Person at 0.91, while a
        /// *brighter* IR room (86) and night street (94) scored their objects at 0.20-0.51.
        /// What those hard frames share is being washed out: mean saturation 17 and 29 against
        /// 77 for the easy one. Saturation is effectively a proxy for how far the frame sits
        /// from the colour-photo distribution YOLO was trained on, so it predicts score
        /// deflation far better than brightness does, and the cutoff follows it.
        /// </summary>
        public static float AdaptiveConfidenceThreshold(Mat source) => ThresholdFor(MeanSaturation(source));

        private static float ThresholdFor(double saturation)
        {
            if (saturation >= 50) return 0.35f; // ordinary colour footage — keep it strict
            if (saturation >= 25) return 0.18f; // night / washed-out colour
            return 0.15f;                        // IR / monochrome
        }

        /// <summary>Mean HSV saturation, the "how colourful is this frame" measure both the
        /// adaptive cutoff and the low-light enhancement key off. Returns 0 (hardest case) for
        /// single-channel input, and falls back to a neutral-colour reading on error so a
        /// measurement failure can't silently loosen the threshold.</summary>
        private static double MeanSaturation(Mat source)
        {
            try
            {
                if (source.Channels() < 3) return 0; // already monochrome — treat as the hardest case

                using var hsv = new Mat();
                Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
                var channels = Cv2.Split(hsv);
                try
                {
                    return Cv2.Mean(channels[1]).Val0;
                }
                finally
                {
                    foreach (var c in channels) c.Dispose();
                }
            }
            catch
            {
                return 100; // treat as easy colour footage → strictest threshold, no enhancement
            }
        }

        /// <summary>
        /// CLAHE (contrast-limited adaptive histogram equalisation) over the luminance channel,
        /// applied only to washed-out frames. Local, tile-wise equalisation rather than a global
        /// pass: a CCTV frame with a blown-out doorway and black corners is exactly the case
        /// where global equalisation makes things worse, while CLAHE lifts detail in the dark
        /// regions without flattening the bright ones. Returns null when the frame doesn't need
        /// it, so the caller can hand the original straight through; the caller owns the result.
        /// </summary>
        private static Mat? EnhanceForInference(Mat source, double saturation)
        {
            if (saturation >= 50 || source.Channels() < 3) return null;

            try
            {
                using var lab = new Mat();
                Cv2.CvtColor(source, lab, ColorConversionCodes.BGR2Lab);
                var channels = Cv2.Split(lab);
                try
                {
                    using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
                    using var equalized = new Mat();
                    clahe.Apply(channels[0], equalized);
                    equalized.CopyTo(channels[0]);

                    using var merged = new Mat();
                    Cv2.Merge(channels, merged);
                    var dst = new Mat();
                    Cv2.CvtColor(merged, dst, ColorConversionCodes.Lab2BGR);
                    return dst;
                }
                finally
                {
                    foreach (var c in channels) c.Dispose();
                }
            }
            catch
            {
                return null; // enhancement is an optimization, never a hard dependency
            }
        }

        /// <summary>Letterbox-resizes to 640x640 (aspect-preserving, gray padding — the same
        /// preprocessing Ultralytics trains with) and packs it into a normalized [1,3,640,640]
        /// RGB CHW tensor.</summary>
        private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY) Preprocess(Mat source)
        {
            float scale = Math.Min((float)ModelSize / source.Width, (float)ModelSize / source.Height);
            int newW = Math.Max(1, (int)Math.Round(source.Width * scale));
            int newH = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var resized = new Mat();
            Cv2.Resize(source, resized, new Size(newW, newH), interpolation: InterpolationFlags.Linear);

            using var canvas = new Mat(ModelSize, ModelSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            int padX = (ModelSize - newW) / 2;
            int padY = (ModelSize - newH) / 2;
            using (var roi = new Mat(canvas, new OpenCvSharp.Rect(padX, padY, newW, newH)))
                resized.CopyTo(roi);

            using var rgb = new Mat();
            Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

            var bytes = new byte[ModelSize * ModelSize * 3];
            Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);

            var tensor = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });
            for (int y = 0; y < ModelSize; y++)
            {
                for (int x = 0; x < ModelSize; x++)
                {
                    int idx = (y * ModelSize + x) * 3;
                    tensor[0, 0, y, x] = bytes[idx] / 255f;
                    tensor[0, 1, y, x] = bytes[idx + 1] / 255f;
                    tensor[0, 2, y, x] = bytes[idx + 2] / 255f;
                }
            }

            return (tensor, scale, padX, padY);
        }

        private readonly record struct Candidate(Rect Box, int ClassId, float Score);

        private static List<Candidate> DecodeCandidates(float[] output, int imageWidth, int imageHeight,
            float scale, int padX, int padY, float confidenceThreshold)
        {
            const int numBoxes = 8400;
            var candidates = new List<Candidate>();

            for (int i = 0; i < numBoxes; i++)
            {
                // Argmax is taken over the relevant classes only, rather than over all 80 and
                // then discarding what isn't relevant. That distinction matters: an office
                // chair in IR footage scored highest as "toilet", with "chair" close behind —
                // scoring only the classes that can apply here recovers the sensible reading
                // instead of throwing the detection away with its bad label.
                float bestScore = 0;
                int bestClass = -1;
                foreach (int c in RelevantClassIds)
                {
                    float score = output[(4 + c) * numBoxes + i];
                    if (score > bestScore) { bestScore = score; bestClass = c; }
                }
                if (bestClass < 0 || bestScore < confidenceThreshold) continue;

                float cx = output[0 * numBoxes + i];
                float cy = output[1 * numBoxes + i];
                float w = output[2 * numBoxes + i];
                float h = output[3 * numBoxes + i];

                // Box is in 640x640 letterboxed space — undo the pad/scale to land back in the
                // original frame's own pixel coordinates, same as every other Magic detector.
                double x1 = (cx - w / 2 - padX) / scale;
                double y1 = (cy - h / 2 - padY) / scale;
                double x2 = (cx + w / 2 - padX) / scale;
                double y2 = (cy + h / 2 - padY) / scale;

                x1 = Math.Clamp(x1, 0, imageWidth);
                y1 = Math.Clamp(y1, 0, imageHeight);
                x2 = Math.Clamp(x2, 0, imageWidth);
                y2 = Math.Clamp(y2, 0, imageHeight);
                if (x2 - x1 < 2 || y2 - y1 < 2) continue;

                candidates.Add(new Candidate(new Rect(x1, y1, x2 - x1, y2 - y1), bestClass, bestScore));
            }
            return candidates;
        }

        /// <summary>
        /// Greedy NMS, deliberately class-AGNOSTIC. Per-class NMS is the textbook default, but
        /// it leaves one physical object stacked under several labels at once — a single car in
        /// night footage surfaced simultaneously as car 0.20, bicycle 0.18 and motorcycle 0.16,
        /// three boxes on the same vehicle. Suppressing across classes keeps only the
        /// best-scoring interpretation of each object, which is also what makes the lower
        /// adaptive cutoffs safe to run: fewer, better boxes rather than more of everything.
        /// </summary>
        private static List<Candidate> NonMaxSuppression(List<Candidate> candidates, float iouThreshold)
        {
            var kept = new List<Candidate>();
            var remaining = candidates.OrderByDescending(c => c.Score).ToList();
            while (remaining.Count > 0)
            {
                var best = remaining[0];
                kept.Add(best);
                remaining.RemoveAt(0);
                remaining.RemoveAll(c => IoU(c.Box, best.Box) > iouThreshold);
            }
            return kept;
        }

        private static double IoU(Rect a, Rect b)
        {
            var intersect = Rect.Intersect(a, b);
            if (intersect.IsEmpty) return 0;
            double intersectArea = intersect.Width * intersect.Height;
            double unionArea = a.Width * a.Height + b.Width * b.Height - intersectArea;
            return unionArea <= 0 ? 0 : intersectArea / unionArea;
        }

        private static string ClassName(int classId)
        {
            if (classId < 0 || classId >= CocoClasses.Length) return "Object";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(CocoClasses[classId]);
        }

        // Standard 80-class COCO label set, in the exact order YOLOv8 was trained on — index
        // must match the model's class output position, so don't reorder/edit this list.
        private static readonly string[] CocoClasses =
        {
            "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
            "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
            "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
            "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
            "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
            "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
            "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
            "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
            "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
            "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
            "toothbrush"
        };

        /// <summary>
        /// The COCO classes that can plausibly appear in a camera-calibration frame — people,
        /// vehicles and the fixtures of an office/retail/street scene. COCO's other ~67
        /// categories (broccoli, teddy bear, toothbrush...) are pure noise here, and leaving
        /// them scoreable is what produced misreads on real footage. Kept as names and resolved
        /// to indices once, so this list stays readable and can't silently drift out of step
        /// with CocoClasses.
        ///
        /// Declared after CocoClasses on purpose: static field initializers run in textual
        /// order, so resolving the names any earlier would read a null array.
        /// </summary>
        private static readonly string[] RelevantClassNames =
        {
            "person", "bicycle", "car", "motorcycle", "bus", "truck",
            "chair", "couch", "dining table", "bench", "potted plant", "tv", "laptop"
        };

        private static readonly int[] RelevantClassIds = RelevantClassNames
            .Select(name => Array.IndexOf(CocoClasses, name))
            .Where(id => id >= 0)
            .ToArray();
    }
}

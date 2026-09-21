using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DBLabs.Services
{
    /// <summary>
    /// Writes the dataset index that closes out a batch. The class folders alone tell a trainer
    /// what each crop is, but not where it came from — this ties every crop back to its source
    /// frame and the exact pixel rectangle it was cut from, which is what makes a set auditable
    /// and a mislabelled crop traceable.
    /// </summary>
    public static class DatasetManifestService
    {
        public const string ManifestFileName = "labels.csv";

        /// <summary>
        /// Appends the batch to labels.csv in the dataset folder, writing the header only when
        /// creating it. Appending rather than overwriting means each batch adds to the set
        /// instead of replacing the record of everything collected before it.
        /// </summary>
        public static string AppendBatch(string outputFolder, IEnumerable<CropResult> crops)
        {
            Directory.CreateDirectory(outputFolder);
            var manifestPath = Path.Combine(outputFolder, ManifestFileName);
            bool isNew = !File.Exists(manifestPath);

            var lines = new List<string>();
            if (isNew)
                lines.Add("file,class,source_image,x,y,width,height,saved_utc");

            foreach (var crop in crops.Where(c => c.Saved))
            {
                // Paths are stored relative to the dataset folder so the set stays portable if
                // it is moved or shared.
                var relative = Path.GetRelativePath(outputFolder, crop.SavedPath!).Replace('\\', '/');
                lines.Add(string.Join(",",
                    Csv(relative),
                    Csv(crop.ClassName),
                    Csv(crop.SourceImage),
                    crop.X.ToString(CultureInfo.InvariantCulture),
                    crop.Y.ToString(CultureInfo.InvariantCulture),
                    crop.Width.ToString(CultureInfo.InvariantCulture),
                    crop.Height.ToString(CultureInfo.InvariantCulture),
                    crop.SavedUtc.ToString("o", CultureInfo.InvariantCulture)));
            }

            File.AppendAllLines(manifestPath, lines);
            return manifestPath;
        }

        /// <summary>Quotes a CSV field only when it needs it.</summary>
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}

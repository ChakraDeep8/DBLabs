using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DBLabs.Services
{
    /// <summary>
    /// Finds, and if necessary downloads, the YOLOv8 weights the person detector needs.
    ///
    /// The weights are deliberately NOT committed to this repository. They are Ultralytics'
    /// and licensed AGPL-3.0, which is strong copyleft: redistributing them would place
    /// obligations on this project's own licensing. Fetching them onto the user's machine at
    /// first run keeps that decision with whoever runs the app, and keeps 43MB of binary out of
    /// git history where it can never be removed.
    ///
    /// They land in the user's app-data folder rather than next to the executable, so a
    /// per-user install without admin rights still works and an app update doesn't discard them.
    /// </summary>
    public static class ModelStore
    {
        private const string ModelFileName = "yolov8s.onnx";

        private const string DownloadUrl =
            "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolov8s.onnx";

        /// <summary>Roughly 43MB — used only to sanity-check a finished download.</summary>
        private const long MinimumPlausibleBytes = 20L * 1024 * 1024;

        public static string ModelFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBLabs", "Models");

        public static string ModelPath => Path.Combine(ModelFolder, ModelFileName);

        public static bool IsPresent => File.Exists(ModelPath) && new FileInfo(ModelPath).Length >= MinimumPlausibleBytes;

        /// <summary>
        /// Downloads the weights if they aren't already present. Reports progress as a fraction
        /// so a caller can show a bar; returns false with a reason rather than throwing, since a
        /// failed download is an ordinary thing to tell the user about, not an exception.
        /// </summary>
        public static async Task<(bool Ok, string? Error)> EnsureAsync(
            IProgress<double>? progress = null, CancellationToken token = default)
        {
            if (IsPresent) return (true, null);

            // Written beside the target then moved, so an interrupted download can never leave a
            // half-file that looks present and fails to load later.
            var partial = ModelPath + ".part";

            try
            {
                Directory.CreateDirectory(ModelFolder);
                if (File.Exists(partial)) File.Delete(partial);

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                    return (false, $"The download returned {(int)response.StatusCode} {response.ReasonPhrase}.");

                var total = response.Content.Headers.ContentLength ?? 0;

                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var target = File.Create(partial))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token);
                        written += read;
                        if (total > 0) progress?.Report(written / (double)total);
                    }
                }

                if (new FileInfo(partial).Length < MinimumPlausibleBytes)
                {
                    File.Delete(partial);
                    return (false, "The downloaded file was too small to be the model — the link may have moved.");
                }

                if (File.Exists(ModelPath)) File.Delete(ModelPath);
                File.Move(partial, ModelPath);
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                return (false, "The download was cancelled.");
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                return (false, ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* nothing useful to do */ }
        }
    }
}

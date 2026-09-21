using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DBLabs.Services
{
    /// <summary>
    /// The camera list shared with the RTSP Camera Viewer app, so a stream saved in either one
    /// shows up in the other. Read best-effort: a missing or malformed file just means no saved
    /// cameras, never a failure the user has to deal with.
    /// </summary>
    public static class SavedCamerasStore
    {
        private static readonly string RtspViewerCamerasFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RtspCameraViewer", "cameras.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Name/Url pairs from the RTSP Camera Viewer app's saved list, if present.</summary>
        public static System.Collections.Generic.List<(string Name, string Url)> LoadRtspViewerCameras()
        {
            var result = new System.Collections.Generic.List<(string, string)>();
            try
            {
                if (!File.Exists(RtspViewerCamerasFile)) return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(RtspViewerCamerasFile));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var url = el.TryGetProperty("Url", out var u) ? u.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url))
                        result.Add((name, url));
                }
            }
            catch { /* best-effort */ }
            return result;
        }

        /// <summary>
        /// Adds (or updates, by matching URL) a camera in the same saved-camera list the RTSP
        /// Camera Viewer app reads from — so a camera typed once in "Grab Frame from RTSP" shows
        /// up in the saved list next time, in either app. Returns false if name/url are invalid.
        /// </summary>
        public static bool SaveRtspViewerCamera(string name, string url)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return false;

            System.Text.Json.Nodes.JsonArray root;
            try
            {
                root = File.Exists(RtspViewerCamerasFile)
                    ? (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(RtspViewerCamerasFile)) as System.Text.Json.Nodes.JsonArray)
                      ?? new System.Text.Json.Nodes.JsonArray()
                    : new System.Text.Json.Nodes.JsonArray();
            }
            catch
            {
                root = new System.Text.Json.Nodes.JsonArray();
            }

            var existing = root.FirstOrDefault(n => string.Equals((string?)n?["Url"], url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing["Name"] = name;
            }
            else
            {
                root.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["Id"] = Guid.NewGuid().ToString("N"),
                    ["Name"] = name,
                    ["Url"] = url,
                    ["Username"] = null,
                    ["Password"] = null
                });
            }

            var dir = Path.GetDirectoryName(RtspViewerCamerasFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(RtspViewerCamerasFile, root.ToJsonString(JsonOptions));
            return true;
        }
    }
}

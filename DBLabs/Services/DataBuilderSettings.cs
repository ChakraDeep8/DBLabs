using System;
using System.IO;

namespace DBLabs.Services
{
    /// <summary>
    /// Remembers the Data Builder's output folder between sessions. Retyping a dataset path
    /// every launch is the kind of friction that makes people stop using a tool.
    /// </summary>
    public static class DataBuilderSettings
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBLabs");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "databuilder-output.txt");

        /// <summary>The stream a collection run last used — the same friction argument as the
        /// output folder, and an RTSP URL is considerably more tedious to retype.</summary>
        private static readonly string CollectUrlFile = Path.Combine(SettingsFolder, "collect-rtsp-url.txt");

        public static string LoadOutputFolder() => Read(SettingsFile);
        public static void SaveOutputFolder(string folder) => Write(SettingsFile, folder);

        public static string LoadCollectUrl() => Read(CollectUrlFile);
        public static void SaveCollectUrl(string url) => Write(CollectUrlFile, url);

        private static string Read(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file).Trim() : ""; }
            catch { return ""; }
        }

        private static void Write(string file, string value)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllText(file, value ?? "");
            }
            catch { /* a settings write failing must never interrupt the workflow */ }
        }
    }
}

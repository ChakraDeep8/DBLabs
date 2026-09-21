using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBLabs.Models.Roi;

namespace DBLabs.Services
{
    /// <summary>
    /// Persists the reusable calibration class library to
    /// %AppData%\DBLabs\classes.json — separate from any one calibration file
    /// and separate from the packaged executable's directory (which may be read-only/protected).
    /// </summary>
    public static class ClassLibraryStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBLabs");

        private static readonly string StoreFile = Path.Combine(StoreDir, "classes.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static ObservableCollection<CalibrationClass> Load()
        {
            try
            {
                if (!File.Exists(StoreFile)) return new ObservableCollection<CalibrationClass>();
                var doc = JsonSerializer.Deserialize<ClassLibraryFile>(File.ReadAllText(StoreFile), JsonOptions);
                return new ObservableCollection<CalibrationClass>(doc?.Classes ?? new List<CalibrationClass>());
            }
            catch
            {
                return new ObservableCollection<CalibrationClass>();
            }
        }

        public static void Save(IEnumerable<CalibrationClass> classes)
        {
            Directory.CreateDirectory(StoreDir);
            var doc = new ClassLibraryFile { Version = 1, Classes = classes.ToList() };
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(doc, JsonOptions));
        }

        /// <summary>Imports classes from an exported library file. Returns (added, skippedExisting).</summary>
        public static (List<CalibrationClass> Imported, List<CalibrationClass> Conflicts) LoadForImport(string path)
        {
            var doc = JsonSerializer.Deserialize<ClassLibraryFile>(File.ReadAllText(path), JsonOptions);
            var incoming = doc?.Classes ?? new List<CalibrationClass>();
            return (incoming, new List<CalibrationClass>());
        }

        public static void ExportTo(string path, IEnumerable<CalibrationClass> classes)
        {
            var doc = new ClassLibraryFile { Version = 1, Classes = classes.ToList() };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        }

        private class ClassLibraryFile
        {
            public int Version { get; set; } = 1;
            public List<CalibrationClass> Classes { get; set; } = new();
        }
    }
}

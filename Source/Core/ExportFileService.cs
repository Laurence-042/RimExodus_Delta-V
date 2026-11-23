using System;
using System.IO;
using Verse;

namespace DeltaV
{
    /// <summary>Single boundary for user-facing file exports and their success audit log.</summary>
    internal static class ExportFileService
    {
        internal static string Write(string artifact, string path, Action<string> writer, string context = null)
        {
            if (artifact.NullOrEmpty()) throw new ArgumentException("Export artifact name is required.", nameof(artifact));
            if (path.NullOrEmpty()) throw new ArgumentException("Export path is required.", nameof(path));
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!directory.NullOrEmpty()) Directory.CreateDirectory(directory);
            writer(fullPath);

            string source = context.NullOrEmpty() ? "" : $" from {context}";
            Log.Message($"[DeltaV] Exported {artifact}{source} to {fullPath}");
            return fullPath;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Verse;

namespace DeltaV
{
    [DataContract]
    internal sealed class SectorGenerationDocument
    {
        [DataMember(Order = 0)] public int version = 1;
        [DataMember(Order = 1)] public SectorGenerationPreset densityPreset = SectorGenerationPreset.Standard;
        [DataMember(Order = 2)] public List<SectorGenerationNode> roots = new List<SectorGenerationNode>();
    }

    internal static class SectorGenerationJson
    {
        private const long MaxBytes = 1024 * 1024;
        internal static string DirectoryPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "DeltaV", "SectorPresets");

        internal static List<string> Files()
        {
            if (!Directory.Exists(DirectoryPath)) return new List<string>();
            return Directory.GetFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string Export(DeltaVSettings settings)
        {
            settings.EnsureSectorGenerationTable();
            string path = Path.Combine(DirectoryPath,
                "sector-generation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            var document = new SectorGenerationDocument
            {
                densityPreset = settings.sectorGenerationPreset,
                roots = settings.sectorGenerationRoots.Select(root => root.Copy()).ToList()
            };
            return ExportFileService.Write("sector generation preset", path,
                exportedPath => Write(exportedPath, document));
        }

        internal static bool TryRead(string path, out SectorGenerationDocument document, out string error)
        {
            document = null; error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException(SectorGenerationTable.Message(
                    "DeltaV_SectorJsonMissing", "Preset file does not exist: {0}", path), path);
                if (info.Length > MaxBytes) throw new InvalidDataException(SectorGenerationTable.Message(
                    "DeltaV_SectorJsonTooLarge", "Preset exceeds 1 MiB."));
                var serializer = Serializer();
                using (FileStream stream = File.OpenRead(path))
                    document = serializer.ReadObject(stream) as SectorGenerationDocument;
                if (document == null || document.version != 1)
                    throw new InvalidDataException(SectorGenerationTable.Message(
                        "DeltaV_SectorJsonUnsupportedVersion", "Unsupported sector preset version."));
                if (!Enum.IsDefined(typeof(SectorGenerationPreset), document.densityPreset))
                    throw new InvalidDataException(SectorGenerationTable.Message(
                        "DeltaV_SectorJsonUnknownDensity", "Unknown density preset."));
                if (!SectorGenerationTable.Validate(document.roots, out string validation))
                    throw new InvalidDataException(validation);
                document.roots = document.roots.Select(root => root.Copy()).ToList();
                return true;
            }
            catch (Exception ex) { error = ex.Message; document = null; return false; }
        }

        private static void Write(string path, SectorGenerationDocument document)
        {
            var serializer = Serializer();
            using var memory = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(memory, Encoding.UTF8, false, true, "  "))
                serializer.WriteObject(writer, document);
            if (memory.Length > MaxBytes) throw new InvalidDataException(SectorGenerationTable.Message(
                "DeltaV_SectorJsonTooLarge", "Preset exceeds 1 MiB."));
            File.WriteAllBytes(path, memory.ToArray());
        }

        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(
            typeof(SectorGenerationDocument), new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = 8192,
                UseSimpleDictionaryFormat = true
            });
    }
}

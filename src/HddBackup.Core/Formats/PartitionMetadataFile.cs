using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Formats
{
    /// <summary>Contents of the <c>.partition.json</c> sidecar of a partition image.</summary>
    [DataContract]
    public sealed class PartitionMetadata
    {
        public const int CurrentFormatVersion = 1;

        [DataMember(Name = "formatVersion", Order = 0)]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        [DataMember(Name = "tableStyle", Order = 1)]
        public string TableStyle { get; set; }

        [DataMember(Name = "partitionNumber", Order = 2)]
        public int PartitionNumber { get; set; }

        [DataMember(Name = "offset", Order = 3)]
        public long Offset { get; set; }

        [DataMember(Name = "length", Order = 4)]
        public long Length { get; set; }

        [DataMember(Name = "sectorSize", Order = 5)]
        public int SectorSize { get; set; }

        [DataMember(Name = "mbrType", Order = 6)]
        public int? MbrType { get; set; }

        [DataMember(Name = "gptType", Order = 7)]
        public string GptType { get; set; }

        [DataMember(Name = "gptId", Order = 8)]
        public string GptId { get; set; }

        [DataMember(Name = "name", Order = 9)]
        public string Name { get; set; }

        [DataMember(Name = "fileSystem", Order = 10)]
        public string FileSystem { get; set; }

        [DataMember(Name = "sourceModel", Order = 11)]
        public string SourceModel { get; set; }

        [DataMember(Name = "sourceSize", Order = 12)]
        public long SourceSize { get; set; }

        [DataMember(Name = "sha256", Order = 13)]
        public string Sha256 { get; set; }

        [DataMember(Name = "createdUtc", Order = 14)]
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public static class PartitionMetadataFile
    {
        public const string Suffix = ".partition.json";

        public static string PathFor(string imagePath) => imagePath + Suffix;

        /// <summary>Writes indented UTF-8 JSON (no BOM) and flushes it to storage.</summary>
        public static void Write(string path, PartitionMetadata metadata)
        {
            var serializer = new DataContractJsonSerializer(typeof(PartitionMetadata));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  "))
                {
                    serializer.WriteObject(writer, metadata);
                    writer.Flush();
                }

                stream.Flush(true);
            }
        }

        public static PartitionMetadata Read(string path)
        {
            PartitionMetadata metadata;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PartitionMetadata));
                using (var stream = File.OpenRead(path))
                {
                    metadata = (PartitionMetadata)serializer.ReadObject(stream);
                }
            }
            catch (SerializationException ex)
            {
                throw new ImagingException(ErrorCode.UnsupportedMetadata, "Partition metadata is not valid JSON: " + ex.Message);
            }

            if (metadata == null || metadata.FormatVersion < 1 || metadata.FormatVersion > PartitionMetadata.CurrentFormatVersion)
            {
                throw new ImagingException(ErrorCode.UnsupportedMetadata, "Unsupported partition metadata format version.");
            }

            if (metadata.Length <= 0)
            {
                throw new ImagingException(ErrorCode.UnsupportedMetadata, "Partition metadata has no length.");
            }

            return metadata;
        }
    }
}

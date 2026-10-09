using System.IO.Compression;
using EZConverter.Compression.Models;

namespace EZConverter.Compression.Services.Compression;

public static class CompressionLevelMapper
{
    public static CompressionLevel ToSystemCompressionLevel(ArchiveCompressionLevel level) =>
        level switch
        {
            ArchiveCompressionLevel.Store => CompressionLevel.NoCompression,
            ArchiveCompressionLevel.Fast => CompressionLevel.Fastest,
            ArchiveCompressionLevel.Normal => CompressionLevel.Optimal,
            ArchiveCompressionLevel.Maximum => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };
}

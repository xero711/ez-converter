namespace EZConverter.Compression.Models;

public sealed record ArchiveCreateRequest(
    ArchiveSourceManifest SourceManifest,
    string OutputArchivePath,
    ArchiveCompressionLevel CompressionLevel,
    GpuMode GpuMode,
    bool OverwriteExisting,
    string SelectedEngine = "",
    bool UsedFallback = false,
    string FallbackReason = "");

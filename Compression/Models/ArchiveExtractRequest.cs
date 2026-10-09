namespace EZConverter.Compression.Models;

public sealed record ArchiveExtractRequest(
    string ArchivePath,
    string DestinationDirectory,
    GpuMode GpuMode,
    bool OverwriteExisting,
    string SelectedEngine = "",
    bool UsedFallback = false,
    string FallbackReason = "");

namespace EZConverter.Compression.Services.Compression;

public sealed record BackendAvailability(
    bool IsAvailable,
    bool SupportsZipCompatibleDeflate,
    string Detail);

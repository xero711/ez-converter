namespace EZConverter.Compression.Services.Compression;

public sealed record CompressionBackendResult(
    ICompressionBackend Backend,
    string EngineLabel,
    bool UsedFallback,
    FallbackReason FallbackReason,
    string Reason,
    BackendAvailability Availability);

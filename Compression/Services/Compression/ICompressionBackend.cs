using System.IO;

namespace EZConverter.Compression.Services.Compression;

public interface ICompressionBackend
{
    string Name { get; }

    BackendAvailability Availability { get; }

    bool IsAvailable { get; }

    bool SupportsZipDeflate { get; }

    Task CompressAsync(
        Stream input,
        Stream output,
        CompressionJobOptions options,
        CancellationToken cancellationToken);

    Task DecompressAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken);
}

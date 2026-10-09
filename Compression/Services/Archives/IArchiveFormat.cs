using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;

namespace EZConverter.Compression.Services.Archives;

public interface IArchiveFormat
{
    string Name { get; }

    IReadOnlyCollection<string> Extensions { get; }

    bool CanRead(string path);

    bool CanWrite(string path);

    Task<ArchiveOperationResult> CreateArchiveAsync(
        ArchiveCreateRequest request,
        ICompressionBackend backend,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        CancellationToken cancellationToken);

    Task<ArchiveOperationResult> ExtractArchiveAsync(
        ArchiveExtractRequest request,
        ICompressionBackend backend,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        CancellationToken cancellationToken);
}

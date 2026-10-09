using System.IO;
using System.IO.Compression;

namespace EZConverter.Compression.Services.Compression;

public sealed class CpuCompressionBackend : ICompressionBackend
{
    public string Name => "CPU";

    public BackendAvailability Availability { get; } =
        new(true, true, "CPUバックエンドは常に使用できます。");

    public bool IsAvailable => true;

    public bool SupportsZipDeflate => true;

    public async Task CompressAsync(
        Stream input,
        Stream output,
        CompressionJobOptions options,
        CancellationToken cancellationToken)
    {
        using var deflateStream = new DeflateStream(
            output,
            CompressionLevelMapper.ToSystemCompressionLevel(options.Level),
            leaveOpen: true);

        await input.CopyToAsync(deflateStream, cancellationToken).ConfigureAwait(false);
    }

    public async Task DecompressAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var deflateStream = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
        await deflateStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}

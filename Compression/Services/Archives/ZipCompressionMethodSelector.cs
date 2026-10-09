using System.Buffers;
using System.IO.Compression;
using EZConverter.Compression.Models;

namespace EZConverter.Compression.Services.Archives;

internal static class ZipCompressionMethodSelector
{
    private const long AdaptiveStoreMinimumBytes = 64L * 1024 * 1024;
    private const int SampleBytes = 64 * 1024;
    private const double IncompressibleRatioThreshold = 1.0;

    public static bool ShouldStoreForSpeed(ArchiveSourceFile sourceFile, ArchiveCompressionLevel requestedLevel)
    {
        if (requestedLevel == ArchiveCompressionLevel.Store || sourceFile.Length == 0)
        {
            return true;
        }

        if (sourceFile.Length < AdaptiveStoreMinimumBytes)
        {
            return false;
        }

        var sampleRatio = EstimateSampleDeflateRatio(sourceFile);
        return sampleRatio >= IncompressibleRatioThreshold;
    }

    private static double EstimateSampleDeflateRatio(ArchiveSourceFile sourceFile)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SampleBytes);
        try
        {
            using var source = new FileStream(
                sourceFile.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                SampleBytes,
                FileOptions.SequentialScan);

            var read = source.Read(buffer, 0, Math.Min(buffer.Length, (int)Math.Min(sourceFile.Length, SampleBytes)));
            if (read <= 0)
            {
                return 0;
            }

            using var memory = new MemoryStream();
            using (var deflate = new DeflateStream(memory, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(buffer, 0, read);
            }

            return (double)memory.Length / read;
        }
        catch
        {
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

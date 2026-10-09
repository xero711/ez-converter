namespace EZConverter.Compression.Models;

public sealed record ArchiveOperationResult(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string InputPath,
    string OutputPath,
    string Format,
    string Engine,
    int TotalFiles,
    long InputBytes,
    long OutputBytes,
    bool UsedFallback,
    string FallbackReason)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public double CompressionRatioPercent
    {
        get
        {
            if (InputBytes <= 0 || OutputBytes < 0)
            {
                return 0;
            }

            return (1 - (double)OutputBytes / InputBytes) * 100;
        }
    }
}

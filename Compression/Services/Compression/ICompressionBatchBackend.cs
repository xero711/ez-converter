namespace EZConverter.Compression.Services.Compression;

public interface ICompressionBatchBackend : ICompressionBackend
{
    bool SupportsBatchCompression { get; }

    long MaxBatchItemBytes { get; }

    long MaxBatchInputBytes { get; }

    long MinimumUsefulBatchBytes { get; }

    bool RequiresCpuPerformanceValidation { get; }

    double MinimumSpeedupOverCpu { get; }

    Task<IReadOnlyList<CompressionBatchResult>> CompressBatchAsync(
        IReadOnlyList<CompressionBatchItem> items,
        CompressionJobOptions options,
        CancellationToken cancellationToken);
}

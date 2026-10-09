using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Archives;

namespace EZConverter.Compression.Services.Compression;

public sealed class CompressionBackendSelector
{
    private const long GpuAutoThresholdBytes = 256L * 1024 * 1024;

    private readonly ICompressionBackend _cpuBackend;
    private readonly ICompressionBackend _gpuBackend;

    public CompressionBackendSelector(ICompressionBackend cpuBackend, ICompressionBackend gpuBackend)
    {
        _cpuBackend = cpuBackend;
        _gpuBackend = gpuBackend;
    }

    public CompressionBackendResult Select(GpuMode mode, long totalBytes, bool requiresZipCompatibleDeflate)
    {
        if (mode == GpuMode.CpuOnly)
        {
            return new CompressionBackendResult(
                _cpuBackend,
                "CPU",
                false,
                FallbackReason.UserSelectedCpuOnly,
                "ユーザーがCPUのみを選択しました。",
                _cpuBackend.Availability);
        }

        if (mode == GpuMode.GpuPreferred)
        {
            if (CanUseGpu(requiresZipCompatibleDeflate, out var fallbackReason, out var reason))
            {
                return new CompressionBackendResult(
                    _gpuBackend,
                    "GPU",
                    false,
                    FallbackReason.None,
                    "ユーザーがGPU優先を選択し、GPUバックエンドを使用できます。",
                    _gpuBackend.Availability);
            }

            return new CompressionBackendResult(
                _cpuBackend,
                "自動フォールバック",
                true,
                fallbackReason,
                reason,
                _gpuBackend.Availability);
        }

        var canUseGpu = CanUseGpu(requiresZipCompatibleDeflate, out var autoFallbackReason, out var autoReason);
        if (totalBytes >= GpuAutoThresholdBytes && canUseGpu)
        {
            return new CompressionBackendResult(
                _gpuBackend,
                "GPU",
                false,
                FallbackReason.None,
                "入力サイズが大きいため、AutoでGPUを選択しました。",
                _gpuBackend.Availability);
        }

        if (totalBytes < GpuAutoThresholdBytes)
        {
            return new CompressionBackendResult(
                _cpuBackend,
                "CPU",
                false,
                FallbackReason.FileTooSmallForGpuAcceleration,
                "GPU転送コストに対して入力サイズが小さいため、CPUを選択しました。",
                _cpuBackend.Availability);
        }

        return new CompressionBackendResult(
            _cpuBackend,
            "自動フォールバック",
            true,
            autoFallbackReason,
            autoReason,
            _gpuBackend.Availability);
    }

    public CompressionBackendResult SelectForArchiveCreate(
        GpuMode mode,
        ArchiveSourceManifest manifest,
        ArchiveCompressionLevel compressionLevel,
        bool requiresZipCompatibleDeflate)
    {
        if (mode != GpuMode.Auto)
        {
            return Select(mode, manifest.TotalBytes, requiresZipCompatibleDeflate);
        }

        var canUseGpu = CanUseGpu(requiresZipCompatibleDeflate, out var autoFallbackReason, out var autoReason);
        var batchReason = string.Empty;
        if (canUseGpu &&
            TryDescribeUsefulBatchWorkload(manifest, compressionLevel, out batchReason))
        {
            return new CompressionBackendResult(
                _gpuBackend,
                "GPU",
                false,
                FallbackReason.None,
                batchReason,
                _gpuBackend.Availability);
        }

        if (manifest.TotalBytes >= GpuAutoThresholdBytes && canUseGpu)
        {
            if (IsBatchOnlyGpuUnsuitableFor(manifest, compressionLevel, out var unsuitableReason))
            {
                return new CompressionBackendResult(
                    _cpuBackend,
                    "CPU",
                    false,
                    FallbackReason.InputNotSuitableForGpuAcceleration,
                    unsuitableReason,
                    _cpuBackend.Availability);
            }

            return new CompressionBackendResult(
                _gpuBackend,
                "GPU",
                false,
                FallbackReason.None,
                "入力サイズが大きいため、AutoでGPUを選択しました。",
                _gpuBackend.Availability);
        }

        if (manifest.TotalBytes < GpuAutoThresholdBytes)
        {
            return new CompressionBackendResult(
                _cpuBackend,
                "CPU",
                false,
                FallbackReason.FileTooSmallForGpuAcceleration,
                canUseGpu && !string.IsNullOrWhiteSpace(batchReason)
                    ? batchReason
                    : "GPU転送コストに対して入力サイズが小さいため、CPUを選択しました。",
                _cpuBackend.Availability);
        }

        return new CompressionBackendResult(
            _cpuBackend,
            "自動フォールバック",
            true,
            autoFallbackReason,
            autoReason,
            _gpuBackend.Availability);
    }

    private bool CanUseGpu(bool requiresZipCompatibleDeflate, out FallbackReason fallbackReason, out string reason)
    {
        var availability = _gpuBackend.Availability;
        if (!availability.IsAvailable)
        {
            fallbackReason = ClassifyUnavailableGpu(availability.Detail);
            reason = availability.Detail;
            return false;
        }

        if (requiresZipCompatibleDeflate && !_gpuBackend.SupportsZipDeflate)
        {
            fallbackReason = FallbackReason.ZipCompatibleDeflateNotSupported;
            reason = "GPUバックエンドはZIP互換DEFLATEをまだサポートしていません。";
            return false;
        }

        fallbackReason = FallbackReason.None;
        reason = string.Empty;
        return true;
    }

    private bool TryDescribeUsefulBatchWorkload(
        ArchiveSourceManifest manifest,
        ArchiveCompressionLevel compressionLevel,
        out string reason)
    {
        reason = string.Empty;
        if (_gpuBackend is not ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend ||
            compressionLevel == ArchiveCompressionLevel.Store)
        {
            return false;
        }

        var summary = SummarizeBatchWorkload(manifest, compressionLevel, batchBackend);
        if (summary.UsefulBatchCount == 0)
        {
            reason = summary.EligibleFileCount == 0
                ? $"GPU batchの対象となる入力が、アイテム上限 {FormatBytes(summary.MaxBatchItemBytes)} 以内にありません。"
                : $"GPU batch対象は {summary.EligibleFileCount:N0} 件 / {FormatBytes(summary.EligibleBytes)} ですが、最大batch {FormatBytes(summary.MaxBatchInputBytes)} で分割すると、有効しきい値 {FormatBytes(summary.MinimumUsefulBatchBytes)} に達するbatchがありません。";
            return false;
        }

        reason = $"AutoでGPU batchを選択しました: 有効batch {summary.UsefulBatchCount:N0} 件、ファイル {summary.UsefulFileCount:N0} 件、有効入力 {FormatBytes(summary.UsefulBytes)}、アイテム上限 {FormatBytes(summary.MaxBatchItemBytes)}。";
        return true;
    }

    private bool IsBatchOnlyGpuUnsuitableFor(
        ArchiveSourceManifest manifest,
        ArchiveCompressionLevel compressionLevel,
        out string reason)
    {
        reason = string.Empty;
        if (_gpuBackend is not ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend)
        {
            return false;
        }

        if (compressionLevel == ArchiveCompressionLevel.Store)
        {
            reason = "StoreモードにはGPUへオフロードできる圧縮処理がありません。";
            return true;
        }

        var summary = SummarizeBatchWorkload(manifest, compressionLevel, batchBackend);
        if (summary.UsefulBatchCount > 0)
        {
            return false;
        }

        reason = summary.EligibleFileCount == 0
            ? $"入力にGPU batchのアイテム上限 {FormatBytes(summary.MaxBatchItemBytes)} 以内のファイルがないため、GPU転送・フォールバックの負荷を避けてCPUを選択しました。"
            : $"GPU対象は {summary.EligibleFileCount:N0} 件 / {FormatBytes(summary.EligibleBytes)} ですが、最大batchのどのグループも有効しきい値 {FormatBytes(summary.MinimumUsefulBatchBytes)} に達しないため、GPU転送・フォールバックの負荷を避けてCPUを選択しました。";
        return true;
    }

    private static BatchWorkloadSummary SummarizeBatchWorkload(
        ArchiveSourceManifest manifest,
        ArchiveCompressionLevel compressionLevel,
        ICompressionBatchBackend batchBackend)
    {
        var maxBatchItemBytes = batchBackend.MaxBatchItemBytes <= 0
            ? long.MaxValue
            : batchBackend.MaxBatchItemBytes;
        var maxBatchInputBytes = batchBackend.MaxBatchInputBytes <= 0
            ? 512L * 1024 * 1024
            : batchBackend.MaxBatchInputBytes;
        var minimumUsefulBatchBytes = Math.Max(1, batchBackend.MinimumUsefulBatchBytes);

        var eligibleFileCount = 0;
        var eligibleBytes = 0L;
        var usefulBatchCount = 0;
        var usefulFileCount = 0;
        var usefulBytes = 0L;
        var pendingFileCount = 0;
        var pendingBytes = 0L;

        foreach (var file in manifest.Files)
        {
            if (!IsGpuBatchCandidate(file, compressionLevel, maxBatchItemBytes))
            {
                FlushPending();
                continue;
            }

            eligibleFileCount++;
            eligibleBytes += file.Length;

            if (pendingBytes > 0 && pendingBytes + file.Length > maxBatchInputBytes)
            {
                FlushPending();
            }

            pendingFileCount++;
            pendingBytes += file.Length;
        }

        FlushPending();

        return new BatchWorkloadSummary(
            maxBatchItemBytes,
            maxBatchInputBytes,
            minimumUsefulBatchBytes,
            eligibleFileCount,
            eligibleBytes,
            usefulBatchCount,
            usefulFileCount,
            usefulBytes);

        void FlushPending()
        {
            if (pendingFileCount >= 2 && pendingBytes >= minimumUsefulBatchBytes)
            {
                usefulBatchCount++;
                usefulFileCount += pendingFileCount;
                usefulBytes += pendingBytes;
            }

            pendingFileCount = 0;
            pendingBytes = 0;
        }
    }

    private static bool IsGpuBatchCandidate(
        ArchiveSourceFile file,
        ArchiveCompressionLevel compressionLevel,
        long maxBatchItemBytes) =>
        file.Length > 0 &&
        file.Length <= maxBatchItemBytes &&
        !ZipCompressionMethodSelector.ShouldStoreForSpeed(file, compressionLevel);

    private static FallbackReason ClassifyUnavailableGpu(string detail)
    {
        if (detail.Contains("NVIDIA GPU", StringComparison.OrdinalIgnoreCase))
        {
            return FallbackReason.NoNvidiaGpu;
        }

        if (detail.Contains("CUDA driver", StringComparison.OrdinalIgnoreCase))
        {
            return FallbackReason.CudaDriverNotFound;
        }

        if (detail.Contains("CUDA runtime", StringComparison.OrdinalIgnoreCase))
        {
            return FallbackReason.CudaRuntimeNotFound;
        }

        if (detail.Contains("nvCOMP", StringComparison.OrdinalIgnoreCase))
        {
            return FallbackReason.NvcompLibraryNotFound;
        }

        if (detail.Contains("EZConverter.NativeGpu", StringComparison.OrdinalIgnoreCase))
        {
            return FallbackReason.NativeGpuBridgeNotFound;
        }

        return FallbackReason.GpuOperationFailed;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private sealed record BatchWorkloadSummary(
        long MaxBatchItemBytes,
        long MaxBatchInputBytes,
        long MinimumUsefulBatchBytes,
        int EligibleFileCount,
        long EligibleBytes,
        int UsefulBatchCount,
        int UsefulFileCount,
        long UsefulBytes);
}

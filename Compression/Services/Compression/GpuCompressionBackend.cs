using System.Buffers;
using System.IO;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Archives;

namespace EZConverter.Compression.Services.Compression;

public sealed class GpuCompressionBackend : ICompressionBatchBackend
{
    private const int StreamCopyBufferSize = 1024 * 1024;
    private const long DefaultMinimumUsefulBatchBytes = 8L * 1024 * 1024;
    private const long ManagedBatchInputLimitBytes = 128L * 1024 * 1024;

    private readonly GpuDetectionResult _detection;
    private readonly NativeGpuCompressionBridge _nativeBridge;
    private readonly bool _requiresCpuPerformanceValidation;
    private readonly double _minimumSpeedupOverCpu;
    private readonly object _selfTestLock = new();
    private bool _selfTestCompleted;
    private string? _selfTestError;
    private long _compressionBatches, _decompressionBatches;
    public long CompressionBatches => Interlocked.Read(ref _compressionBatches);
    public long DecompressionBatches => Interlocked.Read(ref _decompressionBatches);
    public bool SupportsDecompression => IsAvailable && _nativeBridge.SupportsDecompression;

    public (ReadOnlyMemory<byte>[] Bytes, bool UsedGpu, string Reason) DecodeBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> compressed, IReadOnlyList<int> sizes, bool useGpu, CancellationToken ct)
    {
        if (compressed.Count != sizes.Count) throw new ArgumentException("展開batchの件数が不正です。");
        var validated = new ReadOnlyMemory<byte>[sizes.Count];
        for (int i = 0; i < sizes.Count; i++) validated[i] = VerifiedDeflate.Decode(compressed[i], sizes[i], ct);
        if (!useGpu) return (validated, false, "");
        if (!SupportsDecompression || sizes.Any(n => n <= 0 || n > 65536))
            return (validated, false, "GPU非対応のチャンクはCPUで展開しました。");
        ct.ThrowIfCancellationRequested();
        if (!_nativeBridge.TryDecompressBatch(compressed, sizes, out var gpuBytes, out var error))
            return (validated, false, error ?? "GPU展開に失敗したためCPUへ切り替えました。");
        ct.ThrowIfCancellationRequested();
        for (int i = 0; i < gpuBytes.Length; i++)
            if (!gpuBytes[i].Span.SequenceEqual(validated[i].Span))
                return (validated, false, "GPU展開の照合に失敗したためCPUへ切り替えました。");
        Interlocked.Increment(ref _decompressionBatches);
        return (gpuBytes, true, "");
    }

    public GpuCompressionBackend(
        GpuDetectionResult detection,
        NativeGpuCompressionBridge? nativeBridge = null,
        bool requiresCpuPerformanceValidation = true,
        double minimumSpeedupOverCpu = 1.10)
    {
        _detection = detection;
        _nativeBridge = nativeBridge ?? NativeGpuCompressionBridge.TryLoadDefault();
        _requiresCpuPerformanceValidation = requiresCpuPerformanceValidation;
        _minimumSpeedupOverCpu = minimumSpeedupOverCpu <= 0 ? 1.0 : minimumSpeedupOverCpu;
    }

    public string Name => "NVIDIA CUDA/nvCOMP";

    public BackendAvailability Availability
    {
        get
        {
            if (!_detection.HasNvidiaGpu)
            {
                return new(false, false, "NVIDIA GPU は検出されませんでした。");
            }

            if (!_detection.CudaDriverAvailable)
            {
                return new(false, false, "CUDA driver が見つかりません。");
            }

            var supportsZipDeflate =
                _nativeBridge.IsLoaded &&
                (_nativeBridge.SupportsZipCompatibleDeflate ||
                 _nativeBridge.SupportsBatchZipCompatibleDeflate);

            if (!_detection.CudaRuntimeAvailable && !supportsZipDeflate)
            {
                return new(false, false, CreateDependencyMissingDetail("CUDA runtime が見つかりません。"));
            }

            if (!_detection.NvcompAvailable && !supportsZipDeflate)
            {
                return new(false, false, CreateDependencyMissingDetail("nvCOMP native library が見つかりません。"));
            }

            if (!_nativeBridge.IsLoaded)
            {
                return new(false, false, _nativeBridge.LoadError ?? "ZiperネイティブGPUブリッジが見つかりません。");
            }

            return new(
                true,
                supportsZipDeflate,
                supportsZipDeflate
                    ? "ネイティブGPUブリッジはZIP互換DEFLATEをサポートしています。"
                    : "ネイティブGPUブリッジは読み込まれましたが、ZIP互換DEFLATEサポートは使用できません。");
        }
    }

    private string CreateDependencyMissingDetail(string fallbackDetail)
    {
        if (_nativeBridge.IsLoaded && !string.IsNullOrWhiteSpace(_nativeBridge.LoadError))
        {
            return _nativeBridge.LoadError;
        }

        return fallbackDetail;
    }

    public bool IsAvailable => Availability.IsAvailable;

    public bool SupportsZipDeflate => Availability.SupportsZipCompatibleDeflate;

    public bool SupportsBatchCompression =>
        _nativeBridge.SupportsBatchZipCompatibleDeflate;

    public long MaxBatchItemBytes =>
        _nativeBridge.MaxRawDeflateInputBytes == 0
            ? long.MaxValue
            : checked((long)_nativeBridge.MaxRawDeflateInputBytes);

    public long MaxBatchInputBytes =>
        _nativeBridge.MaxRawDeflateBatchTotalInputBytes == 0
            ? ManagedBatchInputLimitBytes
            : Math.Min(ManagedBatchInputLimitBytes, checked((long)_nativeBridge.MaxRawDeflateBatchTotalInputBytes));

    public long MinimumUsefulBatchBytes =>
        Math.Min(DefaultMinimumUsefulBatchBytes, MaxBatchInputBytes);

    public bool RequiresCpuPerformanceValidation => _requiresCpuPerformanceValidation;

    public double MinimumSpeedupOverCpu => _minimumSpeedupOverCpu;

    public Task CompressAsync(
        Stream input,
        Stream output,
        CompressionJobOptions options,
        CancellationToken cancellationToken)
    {
        if (!SupportsZipDeflate)
        {
            throw new NotSupportedException(Availability.Detail);
        }

        EnsureNativeDeflateSelfTestPassed((int)options.Level);
        return CompressWithNativeBridgeAsync(input, output, options, cancellationToken);
    }

    public Task DecompressAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("ZIP互換DEFLATEに接続されたGPU展開バックエンドはまだ有効化されていません。");

    public Task<IReadOnlyList<CompressionBatchResult>> CompressBatchAsync(
        IReadOnlyList<CompressionBatchItem> items,
        CompressionJobOptions options,
        CancellationToken cancellationToken)
    {
        if (!SupportsBatchCompression)
        {
            throw new NotSupportedException(Availability.Detail);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureNativeDeflateSelfTestPassed((int)options.Level);
        var inputs = items.Select(item => item.Input).ToArray();
        if (!_nativeBridge.TryCompressRawDeflateBatch(
                inputs,
                (int)options.Level,
                out var compressedItems,
                out var error))
        {
            throw new InvalidOperationException(error ?? "GPU batch圧縮に失敗しました。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _compressionBatches);
        return Task.FromResult<IReadOnlyList<CompressionBatchResult>>(
            compressedItems.Select(memory => new CompressionBatchResult(memory)).ToArray());
    }

    private async Task CompressWithNativeBridgeAsync(
        Stream input,
        Stream output,
        CompressionJobOptions options,
        CancellationToken cancellationToken)
    {
        var nativeInputLimit = _nativeBridge.MaxRawDeflateInputBytes == 0
            ? int.MaxValue
            : Math.Min((long)_nativeBridge.MaxRawDeflateInputBytes, int.MaxValue);
        var managedInputLimit = Math.Min(nativeInputLimit, ArchiveResourceLimits.GetBatchInputBytesLimit());
        var knownInputLength = TryGetRemainingLength(input);
        if (knownInputLength is > 0 && knownInputLength > managedInputLimit)
        {
            throw new InvalidOperationException(
                $"GPU単一入力の安全上限 {managedInputLimit:N0} bytes を超えています。CPUへフォールバックします。");
        }

        var initialCapacity = knownInputLength is >= 0 and <= int.MaxValue
            ? checked((int)Math.Min(knownInputLength.Value, 16L * 1024 * 1024))
            : 0;
        using var memory = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamCopyBufferSize);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (memory.Length > managedInputLimit - read)
                {
                    throw new InvalidOperationException(
                        $"GPU単一入力の安全上限 {managedInputLimit:N0} bytes を超えています。CPUへフォールバックします。");
                }

                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!memory.TryGetBuffer(out var segment) || segment.Array is null)
        {
            throw new InvalidOperationException("GPU入力バッファーを取得できません。");
        }

        if (!_nativeBridge.TryCompressRawDeflate(
                segment.Array.AsSpan(segment.Offset, checked((int)memory.Length)),
                (int)options.Level,
                out var compressed,
                out var error))
        {
            throw new InvalidOperationException(error ?? "GPU圧縮に失敗しました。");
        }

        await output.WriteAsync(compressed, cancellationToken).ConfigureAwait(false);
    }

    private static long? TryGetRemainingLength(Stream input)
    {
        try
        {
            var remaining = input.Length - input.Position;
            return remaining >= 0 ? remaining : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void EnsureNativeDeflateSelfTestPassed(int compressionLevel)
    {
        if (_selfTestCompleted)
        {
            if (_selfTestError is not null)
            {
                throw new InvalidOperationException(_selfTestError);
            }

            return;
        }

        lock (_selfTestLock)
        {
            if (_selfTestCompleted)
            {
                if (_selfTestError is not null)
                {
                    throw new InvalidOperationException(_selfTestError);
                }

                return;
            }

            _selfTestError = RunNativeDeflateSelfTest(compressionLevel);
            _selfTestCompleted = true;
            if (_selfTestError is not null)
            {
                throw new InvalidOperationException(_selfTestError);
            }
        }
    }

    private string? RunNativeDeflateSelfTest(int compressionLevel)
    {
        var validationInputs = GpuDeflateSelfTest.CreateValidationInputs();
        if (_nativeBridge.SupportsBatchZipCompatibleDeflate)
        {
            var batchInputs = validationInputs.Select(input => (ReadOnlyMemory<byte>)input).ToArray();
            if (!_nativeBridge.TryCompressRawDeflateBatch(batchInputs, compressionLevel, out var compressedItems, out var error))
            {
                return $"ネイティブGPU ZIP互換DEFLATEの自己検証に失敗しました: {error}";
            }

            return GpuDeflateSelfTest.ValidateRawDeflateBatch(batchInputs, compressedItems, out var validationError)
                ? null
                : $"ネイティブGPU ZIP互換DEFLATEの自己検証に失敗しました: {validationError}";
        }

        if (_nativeBridge.SupportsZipCompatibleDeflate)
        {
            var compressedItems = new List<ReadOnlyMemory<byte>>(validationInputs.Count);
            foreach (var input in validationInputs)
            {
                if (!_nativeBridge.TryCompressRawDeflate(input, compressionLevel, out var compressed, out var error))
                {
                    return $"ネイティブGPU ZIP互換DEFLATEの自己検証に失敗しました: {error}";
                }

                compressedItems.Add(compressed);
            }

            var singleInputs = validationInputs.Select(input => (ReadOnlyMemory<byte>)input).ToArray();
            return GpuDeflateSelfTest.ValidateRawDeflateBatch(singleInputs, compressedItems, out var validationError)
                ? null
                : $"ネイティブGPU ZIP互換DEFLATEの自己検証に失敗しました: {validationError}";
        }

        return "ネイティブGPU ZIP互換DEFLATEの自己検証に失敗しました: バックエンドがraw DEFLATEサポートを報告していません。";
    }
}

using System.Runtime.InteropServices;

namespace EZConverter.Compression.Services.Compression;

public sealed class NativeGpuCompressionBridge
{
    private const long MinimumUsefulRawDeflateInputBytes = 16L * 1024 * 1024;
    private const int RawDeflateOutputSlackBytes = 64 * 1024;
    private const int MaxManagedArrayLength = 0x7FFFFFC7;

    private static readonly string[] CandidateLibraryNames =
    {
        "EZConverter.NativeGpu.dll",
        "ziper_native_gpu.dll"
    };

    private static readonly Lazy<NativeGpuCompressionBridge> DefaultBridge = new(LoadDefault);

    private readonly nint _libraryHandle;

    public bool SupportsDecompression => IsLoaded && NativeLibrary.TryGetExport(_libraryHandle, "ez_gpu_decompress_deflate_batch", out _);

    public bool TryDecompressBatch(IReadOnlyList<ReadOnlyMemory<byte>> inputs, IReadOnlyList<int> sizes,
        out ReadOnlyMemory<byte>[] outputs, out string? error)
    {
        outputs = Array.Empty<ReadOnlyMemory<byte>>();
        error = null;
        if (!SupportsDecompression) { error = "GPU展開エンジンを使用できません。"; return false; }
        if (inputs.Count != sizes.Count || inputs.Count > 2048) throw new ArgumentException("展開batchが不正です。");
        if (inputs.Count == 0) return true;
        var offsets = new nuint[inputs.Count];
        var lengths = new nuint[inputs.Count];
        var expected = new nuint[inputs.Count];
        int inputTotal = 0, outputTotal = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            if (sizes[i] <= 0 || sizes[i] > 65536 || inputs[i].Length <= 0 || inputs[i].Length > 131072)
                throw new InvalidDataException("GPU展開チャンクが上限を超えています。");
            offsets[i] = (nuint)inputTotal;
            lengths[i] = (nuint)inputs[i].Length;
            expected[i] = (nuint)sizes[i];
            inputTotal = checked(inputTotal + inputs[i].Length);
            outputTotal = checked(outputTotal + ((sizes[i] + 255) & ~255));
        }
        if (inputTotal > 64 * 1024 * 1024 || outputTotal > 64 * 1024 * 1024) throw new InvalidDataException("GPU展開batchが大きすぎます。");
        var input = new byte[inputTotal];
        for (int i = 0; i < inputs.Count; i++) inputs[i].CopyTo(input.AsMemory((int)offsets[i]));
        var output = new byte[outputTotal];
        NativeLibrary.TryGetExport(_libraryHandle, "ez_gpu_decompress_deflate_batch", out var address);
        var decode = Marshal.GetDelegateForFunctionPointer<DecompressBatchDelegate>(address);
        int status = decode(input, (nuint)input.Length, offsets, lengths, expected, (nuint)inputs.Count, output, (nuint)output.Length);
        if (status != 0) { error = TryReadLastError() ?? $"GPU展開に失敗しました: {status}"; return false; }
        outputs = new ReadOnlyMemory<byte>[inputs.Count];
        int position = 0;
        for (int i = 0; i < inputs.Count; i++) { outputs[i] = output.AsMemory(position, sizes[i]); position += (sizes[i] + 255) & ~255; }
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecompressBatchDelegate(byte[] input, nuint capacity, nuint[] offsets, nuint[] lengths,
        nuint[] expected, nuint count, byte[] output, nuint outputCapacity);

    private readonly GpuCompressRawDeflateDelegate? _compressRawDeflate;
    private readonly GpuCompressRawDeflateBatchDelegate? _compressRawDeflateBatch;
    private readonly GpuCompressRawDeflateBatchWithOffsetDelegate? _compressRawDeflateBatchWithOffset;
    private readonly GpuGetRawDeflateBatchOutputCapacityDelegate? _getRawDeflateBatchOutputCapacity;
    private readonly GpuGetLastErrorDelegate? _getLastError;

    private NativeGpuCompressionBridge(
        bool isLoaded,
        nint libraryHandle,
        string? loadError,
        bool supportsZipCompatibleDeflate,
        bool supportsBatchZipCompatibleDeflate,
        nuint maxRawDeflateInputBytes,
        nuint maxRawDeflateBatchTotalInputBytes,
        GpuCompressRawDeflateDelegate? compressRawDeflate,
        GpuCompressRawDeflateBatchDelegate? compressRawDeflateBatch,
        GpuCompressRawDeflateBatchWithOffsetDelegate? compressRawDeflateBatchWithOffset,
        GpuGetRawDeflateBatchOutputCapacityDelegate? getRawDeflateBatchOutputCapacity,
        GpuGetLastErrorDelegate? getLastError)
    {
        IsLoaded = isLoaded;
        _libraryHandle = libraryHandle;
        LoadError = loadError;
        SupportsZipCompatibleDeflate = supportsZipCompatibleDeflate;
        SupportsBatchZipCompatibleDeflate = supportsBatchZipCompatibleDeflate;
        MaxRawDeflateInputBytes = maxRawDeflateInputBytes;
        MaxRawDeflateBatchTotalInputBytes = maxRawDeflateBatchTotalInputBytes;
        _compressRawDeflate = compressRawDeflate;
        _compressRawDeflateBatch = compressRawDeflateBatch;
        _compressRawDeflateBatchWithOffset = compressRawDeflateBatchWithOffset;
        _getRawDeflateBatchOutputCapacity = getRawDeflateBatchOutputCapacity;
        _getLastError = getLastError;
    }

    public bool IsLoaded { get; }

    public string? LoadError { get; }

    public bool SupportsZipCompatibleDeflate { get; }

    public bool SupportsBatchZipCompatibleDeflate { get; }

    public nuint MaxRawDeflateInputBytes { get; }

    public nuint MaxRawDeflateBatchTotalInputBytes { get; }

    public static NativeGpuCompressionBridge TryLoadFromPath(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new NativeGpuCompressionBridge(false, 0, "ネイティブGPUライブラリのパスが空です。", false, false, 0, 0, null, null, null, null, null);
        }

        return TryLoadLibrary(Path.GetFullPath(libraryPath));
    }

    public static NativeGpuCompressionBridge TryLoadDefault() => DefaultBridge.Value;

    private static NativeGpuCompressionBridge LoadDefault()
    {
        if (!Environment.Is64BitProcess)
        {
            return new NativeGpuCompressionBridge(false, 0, "Ziper は32bit版を廃止しました。ネイティブGPUブリッジは64bitプロセスでのみ読み込みます。", false, false, 0, 0, null, null, null, null, null);
        }

        foreach (var libraryPath in GetCandidateLibraryPaths())
        {
            var bridge = TryLoadLibrary(libraryPath);
            if (bridge.IsLoaded)
            {
                return bridge;
            }
        }

        return new NativeGpuCompressionBridge(false, 0, "EZConverter.NativeGpu.dll が見つかりません。", false, false, 0, 0, null, null, null, null, null);
    }

    public bool TryCompressRawDeflate(
        ReadOnlySpan<byte> input,
        int compressionLevel,
        out byte[] compressed,
        out string? error)
    {
        compressed = Array.Empty<byte>();
        error = null;

        if (!SupportsZipCompatibleDeflate || _compressRawDeflate is null)
        {
            error = LoadError ?? "ネイティブGPUのraw DEFLATE exportが使用できません。";
            return false;
        }

        if (MaxRawDeflateInputBytes > 0 && (nuint)input.Length > MaxRawDeflateInputBytes)
        {
            error = $"ネイティブGPUのraw DEFLATE最大入力は {MaxRawDeflateInputBytes:N0} bytes です。";
            return false;
        }

        var outputCapacity = CalculateRawDeflateOutputCapacity(input.Length);
        var output = new byte[outputCapacity];
        var inputArray = input.ToArray();
        var status = _compressRawDeflate(
            inputArray,
            (nuint)inputArray.Length,
            compressionLevel,
            output,
            (nuint)output.Length,
            out var outputLength);

        if (status != 0)
        {
            var nativeError = TryReadLastError();
            error = string.IsNullOrWhiteSpace(nativeError)
                ? $"ネイティブGPU圧縮が失敗しました。status={status}"
                : $"ネイティブGPU圧縮が失敗しました。status={status}: {nativeError}";
            return false;
        }

        compressed = output.AsSpan(0, checked((int)outputLength)).ToArray();
        return true;
    }

    public bool TryCompressRawDeflateBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> inputs,
        int compressionLevel,
        out ReadOnlyMemory<byte>[] compressed,
        out string? error)
    {
        compressed = Array.Empty<ReadOnlyMemory<byte>>();
        error = null;

        if (!SupportsBatchZipCompatibleDeflate ||
            (_compressRawDeflateBatch is null && _compressRawDeflateBatchWithOffset is null))
        {
            error = LoadError ?? "ネイティブGPUのbatch raw DEFLATE exportが使用できません。";
            return false;
        }

        if (inputs.Count == 0)
        {
            compressed = Array.Empty<ReadOnlyMemory<byte>>();
            return true;
        }

        var totalInputBytes = checked(inputs.Sum(input => input.Length));
        if (MaxRawDeflateBatchTotalInputBytes > 0 && (nuint)totalInputBytes > MaxRawDeflateBatchTotalInputBytes)
        {
            error = $"ネイティブGPUのbatch raw DEFLATE最大合計入力は {MaxRawDeflateBatchTotalInputBytes:N0} bytes です。";
            return false;
        }

        var inputLayout = CreateBatchInputLayout(
            inputs,
            totalInputBytes,
            allowNonZeroBufferOffset: _compressRawDeflateBatchWithOffset is not null);
        var outputOffsets = new nuint[inputs.Count];
        var outputCapacities = new nuint[inputs.Count];
        var outputLengths = new nuint[inputs.Count];
        var batchOutputCapacity = CalculateRawDeflateBatchOutputCapacity(inputs, compressionLevel);

        var outputOffset = 0L;
        for (var index = 0; index < inputs.Count; index++)
        {
            outputOffsets[index] = (nuint)outputOffset;
            outputCapacities[index] = (nuint)batchOutputCapacity;
            outputOffset = checked(outputOffset + batchOutputCapacity);
        }

        if (outputOffset > MaxManagedArrayLength)
        {
            error = $"ネイティブGPU batch出力ステージングバッファが {outputOffset:N0} bytes になります。batchを小さく分割してください。";
            return false;
        }

        var outputBuffer = new byte[checked((int)outputOffset)];
        var status = _compressRawDeflateBatchWithOffset is not null
            ? _compressRawDeflateBatchWithOffset(
                inputLayout.Buffer,
                inputLayout.BufferOffset,
                inputLayout.InputOffsets,
                inputLayout.InputLengths,
                (nuint)inputs.Count,
                compressionLevel,
                outputBuffer,
                outputOffsets,
                outputCapacities,
                outputLengths)
            : _compressRawDeflateBatch!(
                inputLayout.Buffer,
                inputLayout.InputOffsets,
                inputLayout.InputLengths,
                (nuint)inputs.Count,
                compressionLevel,
                outputBuffer,
                outputOffsets,
                outputCapacities,
                outputLengths);

        if (status != 0)
        {
            var nativeError = TryReadLastError();
            error = string.IsNullOrWhiteSpace(nativeError)
                ? $"ネイティブGPU batch圧縮が失敗しました。status={status}"
                : $"ネイティブGPU batch圧縮が失敗しました。status={status}: {nativeError}";
            return false;
        }

        compressed = new ReadOnlyMemory<byte>[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var length = checked((int)outputLengths[index]);
            compressed[index] = outputBuffer.AsMemory(checked((int)outputOffsets[index]), length);
        }

        return true;
    }

    private int CalculateRawDeflateBatchOutputCapacity(
        IReadOnlyList<ReadOnlyMemory<byte>> inputs,
        int compressionLevel)
    {
        var maxInputLength = inputs.Max(input => input.Length);
        if (_getRawDeflateBatchOutputCapacity is not null)
        {
            try
            {
                var nativeCapacity = _getRawDeflateBatchOutputCapacity((nuint)maxInputLength, compressionLevel);
                if (nativeCapacity > 0 && nativeCapacity <= MaxManagedArrayLength)
                {
                    return checked((int)nativeCapacity);
                }
            }
            catch
            {
                // native側で取得できない場合はmanaged側の保守的な上限を使う。
            }
        }

        return CalculateRawDeflateOutputCapacity(maxInputLength);
    }

    private static BatchInputLayout CreateBatchInputLayout(
        IReadOnlyList<ReadOnlyMemory<byte>> inputs,
        int totalInputBytes,
        bool allowNonZeroBufferOffset)
    {
        if (TryCreateContiguousBatchInputLayout(
                inputs,
                totalInputBytes,
                allowNonZeroBufferOffset,
                out var contiguousLayout))
        {
            return contiguousLayout;
        }

        var inputBuffer = new byte[totalInputBytes];
        var inputOffsets = new nuint[inputs.Count];
        var inputLengths = new nuint[inputs.Count];
        var inputOffset = 0;
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            inputOffsets[index] = (nuint)inputOffset;
            inputLengths[index] = (nuint)input.Length;
            input.CopyTo(inputBuffer.AsMemory(inputOffset, input.Length));
            inputOffset += input.Length;
        }

        return new BatchInputLayout(inputBuffer, 0, inputOffsets, inputLengths);
    }

    private static bool TryCreateContiguousBatchInputLayout(
        IReadOnlyList<ReadOnlyMemory<byte>> inputs,
        int totalInputBytes,
        bool allowNonZeroBufferOffset,
        out BatchInputLayout layout)
    {
        layout = BatchInputLayout.Empty;
        if (inputs.Count == 0 ||
            !MemoryMarshal.TryGetArray(inputs[0], out var firstSegment) ||
            firstSegment.Array is null)
        {
            return false;
        }

        if (!allowNonZeroBufferOffset && firstSegment.Offset != 0)
        {
            return false;
        }

        var sharedArray = firstSegment.Array;
        var baseOffset = firstSegment.Offset;
        var expectedOffset = firstSegment.Offset;
        var inputOffsets = new nuint[inputs.Count];
        var inputLengths = new nuint[inputs.Count];

        for (var index = 0; index < inputs.Count; index++)
        {
            if (!MemoryMarshal.TryGetArray(inputs[index], out var segment) ||
                !ReferenceEquals(segment.Array, sharedArray) ||
                segment.Offset != expectedOffset)
            {
                return false;
            }

            inputOffsets[index] = (nuint)(segment.Offset - baseOffset);
            inputLengths[index] = (nuint)segment.Count;
            expectedOffset = checked(segment.Offset + segment.Count);
        }

        if (expectedOffset - baseOffset != totalInputBytes)
        {
            return false;
        }

        layout = new BatchInputLayout(sharedArray, (nuint)baseOffset, inputOffsets, inputLengths);
        return true;
    }

    private static int CalculateRawDeflateOutputCapacity(int inputLength)
    {
        if (inputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength), inputLength, "入力サイズに負の値は指定できません。");
        }

        // DEFLATEの最悪膨張は小さい。余裕分を足しつつ、32-64KiB項目ごとに
        // 1MiBのステージング領域を確保しないようにする。
        var capacity = checked((long)inputLength + Math.Max(RawDeflateOutputSlackBytes, inputLength / 8L) + 1024);
        if (capacity > MaxManagedArrayLength)
        {
            throw new InvalidOperationException($"raw DEFLATE出力容量がmanaged配列の上限を超えます: {capacity:N0} bytes");
        }

        return (int)capacity;
    }

    private static IEnumerable<string> GetCandidateLibraryPaths()
    {
        if (!Environment.Is64BitProcess)
        {
            yield break;
        }

        var baseDirectory = AppContext.BaseDirectory;
        foreach (var libraryName in CandidateLibraryNames)
        {
            yield return Path.Combine(baseDirectory, libraryName);
            yield return Path.Combine(baseDirectory, "runtimes", "win-x64", "native", libraryName);
            yield return Path.Combine(baseDirectory, "Tools", "GpuCompression", libraryName);
        }
    }

    private static NativeGpuCompressionBridge TryLoadLibrary(string libraryPath)
    {
        try
        {
            if (!NativeLibrary.TryLoad(libraryPath, out var handle))
            {
                return new NativeGpuCompressionBridge(false, 0, $"{libraryPath} が見つからないか、読み込めません。", false, false, 0, 0, null, null, null, null, null);
            }

            var supportsZipDeflate = TryReadZipDeflateCapability(handle);
            var supportsBatchZipDeflate = TryReadBatchZipDeflateCapability(handle);
            var compressRawDeflate = TryGetCompressRawDeflate(handle);
            var compressRawDeflateBatch = TryGetCompressRawDeflateBatch(handle);
            var compressRawDeflateBatchWithOffset = TryGetCompressRawDeflateBatchWithOffset(handle);
            var getRawDeflateBatchOutputCapacity = TryGetRawDeflateBatchOutputCapacity(handle);
            var maxRawDeflateInputBytes = TryGetMaxRawDeflateInputBytes(handle);
            var maxRawDeflateBatchTotalInputBytes = TryGetMaxRawDeflateBatchTotalInputBytes(handle);
            var getLastError = TryGetLastError(handle);
            var nativeLoadDetail = TryReadLastError(getLastError);
            supportsZipDeflate = supportsZipDeflate &&
                                 compressRawDeflate is not null &&
                                 IsUsefulForZipWorkloads(maxRawDeflateInputBytes);
            supportsBatchZipDeflate = supportsBatchZipDeflate &&
                                      (compressRawDeflateBatch is not null || compressRawDeflateBatchWithOffset is not null) &&
                                      IsUsefulForZipWorkloads(maxRawDeflateBatchTotalInputBytes);

            var loadError = supportsZipDeflate || supportsBatchZipDeflate
                ? null
                : CreateIncompleteBridgeReason(libraryPath, maxRawDeflateInputBytes, compressRawDeflate is not null, nativeLoadDetail);

            return new NativeGpuCompressionBridge(
                true,
                handle,
                loadError,
                supportsZipDeflate,
                supportsBatchZipDeflate,
                maxRawDeflateInputBytes,
                maxRawDeflateBatchTotalInputBytes,
                compressRawDeflate,
                compressRawDeflateBatch,
                compressRawDeflateBatchWithOffset,
                getRawDeflateBatchOutputCapacity,
                getLastError);
        }
        catch (Exception ex)
        {
            return new NativeGpuCompressionBridge(false, 0, ex.Message, false, false, 0, 0, null, null, null, null, null);
        }
    }

    private static bool IsUsefulForZipWorkloads(nuint maxRawDeflateInputBytes) =>
        maxRawDeflateInputBytes == 0 || maxRawDeflateInputBytes >= (nuint)MinimumUsefulRawDeflateInputBytes;

    private static string CreateIncompleteBridgeReason(
        string libraryPath,
        nuint maxRawDeflateInputBytes,
        bool hasCompressExport,
        string? nativeLoadDetail)
    {
        if (!string.IsNullOrWhiteSpace(nativeLoadDetail))
        {
            return $"{libraryPath} は読み込まれましたが、ネイティブGPUバックエンドは使用できません: {nativeLoadDetail}";
        }

        if (!hasCompressExport)
        {
            return $"{libraryPath} は読み込まれましたが、ZIP互換raw DEFLATE exportが不足しています。";
        }

        if (!IsUsefulForZipWorkloads(maxRawDeflateInputBytes))
        {
            return $"{libraryPath} は読み込まれましたが、native raw DEFLATEの最大入力が {maxRawDeflateInputBytes:N0} bytes しかありません。";
        }

        return $"{libraryPath} は読み込まれましたが、ZIP互換raw DEFLATEサポートは無効です。";
    }

    private static string? TryReadLastError(GpuGetLastErrorDelegate? getLastError)
    {
        if (getLastError is null)
        {
            return null;
        }

        try
        {
            var pointer = getLastError();
            return pointer == 0 ? null : Marshal.PtrToStringAnsi(pointer);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadZipDeflateCapability(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_supports_zip_deflate", out var export))
            {
                return false;
            }

            var function = Marshal.GetDelegateForFunctionPointer<SupportsZipDeflateDelegate>(export);
            return function() == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBatchZipDeflateCapability(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_supports_zip_deflate_batch", out var export))
            {
                return false;
            }

            var function = Marshal.GetDelegateForFunctionPointer<SupportsZipDeflateDelegate>(export);
            return function() == 1;
        }
        catch
        {
            return false;
        }
    }

    private static GpuCompressRawDeflateDelegate? TryGetCompressRawDeflate(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_compress_raw_deflate", out var export))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<GpuCompressRawDeflateDelegate>(export);
        }
        catch
        {
            return null;
        }
    }

    private static GpuCompressRawDeflateBatchDelegate? TryGetCompressRawDeflateBatch(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_compress_raw_deflate_batch", out var export))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<GpuCompressRawDeflateBatchDelegate>(export);
        }
        catch
        {
            return null;
        }
    }

    private static GpuCompressRawDeflateBatchWithOffsetDelegate? TryGetCompressRawDeflateBatchWithOffset(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_compress_raw_deflate_batch_v2", out var export))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<GpuCompressRawDeflateBatchWithOffsetDelegate>(export);
        }
        catch
        {
            return null;
        }
    }

    private static GpuGetRawDeflateBatchOutputCapacityDelegate? TryGetRawDeflateBatchOutputCapacity(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_get_raw_deflate_batch_output_capacity", out var export))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<GpuGetRawDeflateBatchOutputCapacityDelegate>(export);
        }
        catch
        {
            return null;
        }
    }

    private static nuint TryGetMaxRawDeflateInputBytes(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_get_max_raw_deflate_input_size", out var export))
            {
                return 0;
            }

            var function = Marshal.GetDelegateForFunctionPointer<GpuGetMaxRawDeflateInputSizeDelegate>(export);
            return function();
        }
        catch
        {
            return 0;
        }
    }

    private static nuint TryGetMaxRawDeflateBatchTotalInputBytes(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_get_max_raw_deflate_batch_total_input_size", out var export))
            {
                return 0;
            }

            var function = Marshal.GetDelegateForFunctionPointer<GpuGetMaxRawDeflateInputSizeDelegate>(export);
            return function();
        }
        catch
        {
            return 0;
        }
    }

    private static GpuGetLastErrorDelegate? TryGetLastError(nint handle)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ziper_gpu_get_last_error", out var export))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<GpuGetLastErrorDelegate>(export);
        }
        catch
        {
            return null;
        }
    }

    private string? TryReadLastError()
    {
        if (_getLastError is null)
        {
            return null;
        }

        try
        {
            var pointer = _getLastError();
            return pointer == 0 ? null : Marshal.PtrToStringAnsi(pointer);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SupportsZipDeflateDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint GpuGetMaxRawDeflateInputSizeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuCompressRawDeflateDelegate(
        [In]
        byte[] input,
        nuint inputLength,
        int compressionLevel,
        [Out]
        byte[] output,
        nuint outputCapacity,
        out nuint outputLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuCompressRawDeflateBatchDelegate(
        [In]
        byte[] inputBuffer,
        [In]
        nuint[] inputOffsets,
        [In]
        nuint[] inputLengths,
        nuint itemCount,
        int compressionLevel,
        [Out]
        byte[] outputBuffer,
        [In]
        nuint[] outputOffsets,
        [In]
        nuint[] outputCapacities,
        [Out]
        nuint[] outputLengths);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GpuCompressRawDeflateBatchWithOffsetDelegate(
        [In]
        byte[] inputBuffer,
        nuint inputBufferOffset,
        [In]
        nuint[] inputOffsets,
        [In]
        nuint[] inputLengths,
        nuint itemCount,
        int compressionLevel,
        [Out]
        byte[] outputBuffer,
        [In]
        nuint[] outputOffsets,
        [In]
        nuint[] outputCapacities,
        [Out]
        nuint[] outputLengths);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint GpuGetRawDeflateBatchOutputCapacityDelegate(
        nuint maxInputLength,
        int compressionLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GpuGetLastErrorDelegate();

    private sealed record BatchInputLayout(
        byte[] Buffer,
        nuint BufferOffset,
        nuint[] InputOffsets,
        nuint[] InputLengths)
    {
        public static BatchInputLayout Empty { get; } =
            new(Array.Empty<byte>(), 0, Array.Empty<nuint>(), Array.Empty<nuint>());
    }
}

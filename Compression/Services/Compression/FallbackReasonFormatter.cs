namespace EZConverter.Compression.Services.Compression;

public static class FallbackReasonFormatter
{
    public static string Format(FallbackReason reason) => reason switch
    {
        FallbackReason.None => "なし",
        FallbackReason.UserSelectedCpuOnly => "ユーザーがCPUのみを選択",
        FallbackReason.FileTooSmallForGpuAcceleration => "入力サイズがGPUアクセラレーションに不向き",
        FallbackReason.NoNvidiaGpu => "NVIDIA GPUが見つからない",
        FallbackReason.CudaDriverNotFound => "CUDAドライバーが見つからない",
        FallbackReason.CudaRuntimeNotFound => "CUDA Runtimeが見つからない",
        FallbackReason.NvcompLibraryNotFound => "nvCOMPライブラリが見つからない",
        FallbackReason.NativeGpuBridgeNotFound => "ネイティブGPUブリッジが見つからない",
        FallbackReason.ZipCompatibleDeflateNotSupported => "ZIP互換DEFLATEをサポートしていない",
        FallbackReason.InputNotSuitableForGpuAcceleration => "入力がGPUアクセラレーションに不向き",
        FallbackReason.GpuOperationFailed => "GPU処理に失敗",
        _ => reason.ToString()
    };

    public static string Format(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "なし"
            : Enum.TryParse<FallbackReason>(reason, out var parsed)
                ? Format(parsed)
                : reason;
}

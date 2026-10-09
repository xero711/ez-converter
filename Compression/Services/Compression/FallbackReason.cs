namespace EZConverter.Compression.Services.Compression;

public enum FallbackReason
{
    None,
    UserSelectedCpuOnly,
    FileTooSmallForGpuAcceleration,
    NoNvidiaGpu,
    CudaDriverNotFound,
    CudaRuntimeNotFound,
    NvcompLibraryNotFound,
    NativeGpuBridgeNotFound,
    ZipCompatibleDeflateNotSupported,
    InputNotSuitableForGpuAcceleration,
    GpuOperationFailed
}

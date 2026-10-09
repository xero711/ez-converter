namespace EZConverter.Compression.Models;

public sealed record GpuDetectionResult(
    bool HasNvidiaGpu,
    bool CudaDriverAvailable,
    bool CudaRuntimeAvailable,
    bool NvcompAvailable,
    string? GpuName,
    long? VramBytes,
    IReadOnlyList<string> AvailableBackends,
    string Notes)
{
    public static GpuDetectionResult CpuOnly(string notes) =>
        new(false, false, false, false, null, null, new[] { "CPU" }, notes);
}

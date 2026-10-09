namespace EZConverter.Compression.Services.Archives;

internal static class ArchiveResourceLimits
{
    private const long Mebibyte = 1024L * 1024;
    private const long MinimumRetainedCompressionBytes = 16L * Mebibyte;
    private const long MaximumRetainedCompressionBytes = 64L * Mebibyte;
    private const long MinimumBatchInputBytes = 8L * Mebibyte;
    private const long MaximumBatchInputBytes = 64L * Mebibyte;

    public static long GetRetainedCompressionBytesLimit() =>
        CalculateMemoryLimit(
            MinimumRetainedCompressionBytes,
            MaximumRetainedCompressionBytes,
            totalMemoryDivisor: 32,
            headroomDivisor: 8);

    public static long GetBatchInputBytesLimit() =>
        CalculateMemoryLimit(
            MinimumBatchInputBytes,
            MaximumBatchInputBytes,
            totalMemoryDivisor: 32,
            headroomDivisor: 8);

    public static int GetResponsiveParallelism(int workItemCount)
    {
        if (workItemCount <= 1)
        {
            return Math.Max(1, workItemCount);
        }

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var workerLimit = processorCount <= 2 ? 1 : processorCount - 1;
        return Math.Clamp(Math.Min(workItemCount, workerLimit), 1, 8);
    }

    private static long CalculateMemoryLimit(
        long minimumBytes,
        long maximumBytes,
        int totalMemoryDivisor,
        int headroomDivisor)
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            var byTotalMemory = memoryInfo.TotalAvailableMemoryBytes > 0
                ? memoryInfo.TotalAvailableMemoryBytes / totalMemoryDivisor
                : maximumBytes;
            var headroom = memoryInfo.HighMemoryLoadThresholdBytes > memoryInfo.MemoryLoadBytes
                ? memoryInfo.HighMemoryLoadThresholdBytes - memoryInfo.MemoryLoadBytes
                : 0;
            var byCurrentHeadroom = headroom > 0
                ? headroom / headroomDivisor
                : minimumBytes;
            var calculated = Math.Min(byTotalMemory, byCurrentHeadroom);
            var clamped = Math.Clamp(calculated, minimumBytes, maximumBytes);
            return Math.Max(minimumBytes, clamped / Mebibyte * Mebibyte);
        }
        catch
        {
            return minimumBytes;
        }
    }
}

internal sealed class RetainedMemoryBudget
{
    private long _reservedBytes;
    private long _peakReservedBytes;

    public RetainedMemoryBudget(long limitBytes)
    {
        LimitBytes = Math.Max(0, limitBytes);
    }

    public long LimitBytes { get; }

    public long PeakReservedBytes => Volatile.Read(ref _peakReservedBytes);

    public bool TryReserve(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _reservedBytes);
            if (bytes > LimitBytes - current)
            {
                return false;
            }

            var updated = current + bytes;
            if (Interlocked.CompareExchange(ref _reservedBytes, updated, current) != current)
            {
                continue;
            }

            UpdatePeak(updated);
            return true;
        }
    }

    public void Release(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var remaining = Interlocked.Add(ref _reservedBytes, -bytes);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _reservedBytes, 0);
        }
    }

    private void UpdatePeak(long value)
    {
        while (true)
        {
            var currentPeak = Volatile.Read(ref _peakReservedBytes);
            if (value <= currentPeak ||
                Interlocked.CompareExchange(ref _peakReservedBytes, value, currentPeak) == currentPeak)
            {
                return;
            }
        }
    }
}

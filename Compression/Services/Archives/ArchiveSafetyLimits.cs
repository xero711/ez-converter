namespace EZConverter.Compression.Services.Archives;

public static class ArchiveSafetyLimits
{
    public const int MaxEntries = 100_000;
    public const long MaxFileBytes = 64L * 1024 * 1024 * 1024;
    public const long MaxTotalBytes = 128L * 1024 * 1024 * 1024;
}

namespace EZConverter.Compression.Models;

public sealed record ArchiveOperationReporter(
    IProgress<ArchiveProgress> Progress,
    IProgress<string> Log);

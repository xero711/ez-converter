namespace EZConverter.Compression.Models;

public sealed record ArchiveSourceFile(
    string FullPath,
    string EntryName,
    long Length,
    DateTimeOffset LastWriteTime);

public sealed record ArchiveSourceManifest(
    IReadOnlyList<string> InputPaths,
    IReadOnlyList<ArchiveSourceFile> Files,
    IReadOnlyList<string> DirectoryEntries,
    long TotalBytes)
{
    public int TotalFiles => Files.Count;
}

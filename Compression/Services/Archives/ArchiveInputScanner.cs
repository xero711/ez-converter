using System.IO;
using EZConverter.Compression.Models;

namespace EZConverter.Compression.Services.Archives;

public sealed class ArchiveInputScanner
{
    public ArchiveSourceManifest BuildManifest(IEnumerable<string> inputPaths, string? excludedOutputPath = null, CancellationToken cancellationToken = default)
    {
        var inputs = inputPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (inputs.Length == 0)
        {
            throw new InvalidOperationException("入力ファイルまたはフォルダを指定してください。");
        }

        var excludedFullPath = string.IsNullOrWhiteSpace(excludedOutputPath)
            ? null
            : Path.GetFullPath(excludedOutputPath);

        var files = new List<ArchiveSourceFile>();
        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivePathGuard.RejectLinks(input);
            if (File.Exists(input))
            {
                AddFile(input, Path.GetFileName(input), excludedFullPath, files, entryNames);
                continue;
            }

            if (Directory.Exists(input))
            {
                AddDirectory(input, excludedFullPath, files, directories, entryNames, cancellationToken);
                continue;
            }

            throw new FileNotFoundException("入力ファイルまたはフォルダが存在しません。", input);
        }

        return new ArchiveSourceManifest(
            inputs,
            files,
            directories.ToArray(),
            files.Sum(file => file.Length));
    }

    private static void AddDirectory(
        string directoryPath,
        string? excludedFullPath,
        List<ArchiveSourceFile> files,
        SortedSet<string> directories,
        HashSet<string> entryNames, CancellationToken cancellationToken)
    {
        var trimmedDirectory = TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var parent = Directory.GetParent(trimmedDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("ドライブのルートは直接圧縮対象にできません。フォルダを選択してください。");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };

        directories.Add(ToZipEntryName(Path.GetRelativePath(parent, trimmedDirectory), true));

        var pending = new Stack<string>();
        pending.Push(trimmedDirectory);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchivePathGuard.RejectLinks(child);
                if (Directory.Exists(child))
                {
                    directories.Add(ToZipEntryName(Path.GetRelativePath(parent, child), true));
                    pending.Push(child);
                }
                else AddFile(child, ToZipEntryName(Path.GetRelativePath(parent, child), false), excludedFullPath, files, entryNames);
                if (directories.Count + files.Count > ArchiveSafetyLimits.MaxEntries)
                    throw new InvalidDataException("入力件数が安全上限を超えています。");
            }
        }
    }

    private static void AddFile(
        string fullPath,
        string entryName,
        string? excludedFullPath,
        List<ArchiveSourceFile> files,
        HashSet<string> entryNames)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        if (excludedFullPath is not null && string.Equals(normalizedPath, excludedFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedEntryName = ToZipEntryName(entryName, false);
        if (!entryNames.Add(normalizedEntryName))
        {
            throw new InvalidOperationException($"ZIP内パスが重複しています: {normalizedEntryName}");
        }

        var info = new FileInfo(normalizedPath);
        ArchivePathGuard.RejectLinks(normalizedPath);
        if (info.Length > ArchiveSafetyLimits.MaxFileBytes || files.Count >= ArchiveSafetyLimits.MaxEntries)
            throw new InvalidDataException("入力ファイルが安全上限を超えています。");
        files.Add(new ArchiveSourceFile(
            normalizedPath,
            normalizedEntryName,
            info.Length,
            info.LastWriteTime));
    }

    private static string ToZipEntryName(string path, bool isDirectory)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("../", StringComparison.Ordinal) || normalized == "..")
        {
            throw new InvalidOperationException($"ZIP内に安全でないパスは作成できません: {path}");
        }

        return isDirectory && !normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized + "/"
            : normalized;
    }

    private static string TrimEndingDirectorySeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

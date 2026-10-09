using EZConverter.Compression.Services.Security;

namespace EZConverter.Compression.Services.Archives;

internal static class ArchivePathGuard
{
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveSecurityException("リンク・ジャンクションを含むパスは扱えません。");
        }
    }

    public static void ValidateNames(IEnumerable<(string Name, bool Directory)> entries, string root)
    {
        var validator = new PathSafetyValidator();
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!validator.TryGetSafeDestinationPath(root, entry.Name, out var path, out var reason))
                throw new ArchiveSecurityException(reason);
            if (!names.TryAdd(path, entry.Directory)) throw new InvalidDataException("アーカイブ内の名前が重複しています。");
        }
        foreach (var path in names.Keys)
        {
            for (string? ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (names.TryGetValue(ancestor, out bool directory) && !directory)
                    throw new InvalidDataException("ファイルとフォルダの名前が衝突しています。");
        }
    }
}

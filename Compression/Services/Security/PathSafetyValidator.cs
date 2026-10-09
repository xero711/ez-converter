using System.IO;

namespace EZConverter.Compression.Services.Security;

public sealed class PathSafetyValidator
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public bool TryGetSafeDestinationPath(
        string destinationRoot,
        string entryName,
        out string destinationPath,
        out string reason)
    {
        destinationPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            reason = "展開先フォルダが指定されていません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entryName))
        {
            reason = "空のZIPエントリ名は展開できません。";
            return false;
        }

        if (entryName.IndexOf('\0') >= 0)
        {
            reason = "NULL文字を含むZIPエントリ名は展開できません。";
            return false;
        }

        var normalizedEntry = entryName.Replace('\\', '/');
        if (normalizedEntry.StartsWith("/", StringComparison.Ordinal) ||
            normalizedEntry.StartsWith("\\", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(normalizedEntry) ||
            HasDrivePrefix(normalizedEntry))
        {
            reason = "絶対パスのZIPエントリは展開できません。";
            return false;
        }

        var rawSegments = normalizedEntry.Split('/', StringSplitOptions.None);
        var segments = rawSegments
            .Where((segment, index) => !(index == rawSegments.Length - 1 && segment.Length == 0))
            .ToArray();

        if (segments.Length == 0)
        {
            reason = "空のZIPエントリ名は展開できません。";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!IsSafeSegment(segment, out reason))
            {
                return false;
            }
        }

        var rootFullPath = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
        var combined = segments.Aggregate(rootFullPath, Path.Combine);
        var fullDestination = Path.GetFullPath(combined);

        if (!fullDestination.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = "展開先フォルダ外への書き込みは拒否されました。";
            return false;
        }

        destinationPath = fullDestination;
        return true;
    }

    private static bool IsSafeSegment(string segment, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(segment))
        {
            reason = "空のパス要素を含むZIPエントリは展開できません。";
            return false;
        }

        if (segment is "." or "..")
        {
            reason = "親ディレクトリ移動を含むZIPエントリは展開できません。";
            return false;
        }

        if (segment.IndexOfAny(InvalidFileNameChars) >= 0 || segment.Contains(':', StringComparison.Ordinal))
        {
            reason = $"不正なファイル名を含むZIPエントリは展開できません: {segment}";
            return false;
        }

        if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
        {
            reason = $"末尾がドットまたは空白のファイル名は展開できません: {segment}";
            return false;
        }

        var baseName = segment.Split('.')[0].TrimEnd(' ', '.');
        if (ReservedDeviceNames.Contains(baseName))
        {
            reason = $"Windows予約名への展開は拒否されました: {segment}";
            return false;
        }

        return true;
    }

    private static bool HasDrivePrefix(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}

using System.Net;
using System.Security.Cryptography;

namespace EZConverter.Sharing;

public sealed record TransferFile(string Id, string RelativePath, long Length, string Sha256);
public sealed record LocalFile(string SourcePath, TransferFile File, long LastWriteTicks = 0, long LastAccessTicks = 0);
public sealed record DeviceInfo(string Protocol, string Name, string Fingerprint, int Port);
public sealed record PeerDevice(
    string Name,
    string Address,
    int Port,
    string Fingerprint,
    DateTimeOffset SeenAt,
    string Protocol = "ez-share/1",
    string TransportProtocol = "https",
    string? DeviceType = null)
{
    public bool IsLocalSend => Protocol == "localsend/2";
    public string DisplayName => IsLocalSend
        ? $"{Name} · {Address} · LocalSend"
        : $"{Name} · {Address} · {Fingerprint[..Math.Min(12, Fingerprint.Length)]}";
    public string ConnectionUrl => IsLocalSend
        ? $"{TransportProtocol}://{TransferServer.FormatUriHost(Address)}:{Port}/{(TransportProtocol == "https" ? "#" + Fingerprint : string.Empty)}"
        : $"https://{TransferServer.FormatUriHost(Address)}:{Port}/#{Fingerprint}";
}
public sealed record OfferRequest(string Sender, List<TransferFile> Files);
public sealed record OfferReceipt(string Id, string Secret);
public sealed record OfferStatus(string State, string? Error, Dictionary<string, long> Offsets);
public sealed record TransferProgress(string Id, string Direction, string Name, long Completed, long Total, string State, string? Detail = null)
{
    public double Percent
    {
        get
        {
            if (Total == 0)
            {
                return State == "完了" ? 100 : 0;
            }

            return Math.Clamp(100d * Completed / Total, 0, 100);
        }
    }
}
public sealed record ShareManifest(string Name, DateTimeOffset ExpiresAt, List<TransferFile> Files);
public sealed record LinkInfo(string Token, DateTimeOffset ExpiresAt, string Name, int FileCount, long TotalBytes, bool PasswordRequired, bool IsInvitation);

public sealed class IncomingTransfer
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id { get; }
    public string Sender { get; }
    public string Address { get; }
    public IReadOnlyList<TransferFile> Files { get; }
    public long TotalBytes => Files.Sum(f => f.Length);
    internal Task<bool> Decision => _decision.Task;
    internal IncomingTransfer(string id, string sender, string address, IReadOnlyList<TransferFile> files) =>
        (Id, Sender, Address, Files) = (id, sender, address, files);
    public void Accept() => _decision.TrySetResult(true);
    public void Reject() => _decision.TrySetResult(false);
}

public sealed class TransferServerOptions
{
    public string DeviceName { get; init; } = Environment.MachineName;
    public required string ReceiveDirectory { get; init; }
    public required string StateDirectory { get; init; }
    public int HttpPort { get; init; }
    public int HttpsPort { get; init; }
    public int SignalPort { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public string? LocalSendReceivePin { get; init; }
    public TimeSpan OfferLifetime { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan TerminalReceiveRetention { get; init; } = TimeSpan.FromMinutes(2);
    public long MaxReceiveBytes { get; init; } = 100L * 1024 * 1024 * 1024;
    public bool EnableTurnRelay { get; init; }
    public string TurnServerUrl { get; init; } = string.Empty;
    public string TurnSharedSecret { get; init; } = string.Empty;
    public const int ChunkSize = 4 * 1024 * 1024;
}

public static class TransferFiles
{
    public static async Task<List<LocalFile>> CollectAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new List<LocalFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selected in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            await CollectSelectedPathAsync(selected, result, seen, names, ct);
        }

        if (result.Count == 0) throw new IOException("送信できるファイルがありません。");

        return result;
    }

    private static async Task CollectSelectedPathAsync(
        string selected,
        List<LocalFile> result,
        HashSet<string> seen,
        HashSet<string> names,
        CancellationToken ct)
    {
        var full = Path.GetFullPath(selected);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンクされたファイル・フォルダーは共有できません。");

        var isDirectory = Directory.Exists(full);
        var files = isDirectory
            ? Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            : [full];
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            await AddFileAsync(path, full, isDirectory, result, seen, names, ct);
        }
    }

    private static async Task AddFileAsync(
        string path,
        string root,
        bool isDirectory,
        List<LocalFile> result,
        HashSet<string> seen,
        HashSet<string> names,
        CancellationToken ct)
    {
        if (!seen.Add(path)) return;

        var relative = isDirectory
            ? Path.GetFileName(root) + "/" + Path.GetRelativePath(root, path).Replace('\\', '/')
            : Path.GetFileName(path);
        ValidateRelativePath(relative);
        if (!names.Add(relative)) throw new IOException($"同じ名前のファイルがあります: {relative}");
        if (result.Count >= 2000) throw new IOException("1回に共有できるファイルは2,000件までです。");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var length = stream.Length;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                result.Add(new(path, new(Guid.NewGuid().ToString("N"), relative, length, sha),
                    File.GetLastWriteTimeUtc(path).Ticks, File.GetLastAccessTimeUtc(path).Ticks));
    }

    public static void ValidateManifest(IReadOnlyList<TransferFile> files, long maxBytes)
    {
        if (files.Count is < 1 or > 2000) throw new InvalidDataException("ファイル数が不正です。");
        long total = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ValidateManifestFile(file, maxBytes, ids, names, ref total);
        }

        ValidateNoFileDirectoryConflicts(names);
    }

    private static void ValidateManifestFile(
        TransferFile file,
        long maxBytes,
        HashSet<string> ids,
        HashSet<string> names,
        ref long total)
    {
        if (file is null) throw new InvalidDataException("ファイル情報がありません。");
        ValidateRelativePath(file.RelativePath);
        if (string.IsNullOrEmpty(file.Id) || file.Id.Length != 32 || !file.Id.All(Uri.IsHexDigit) || !ids.Add(file.Id) || !names.Add(file.RelativePath))
            throw new InvalidDataException("ファイル識別子・名前が不正または重複しています。");
        if (file.Length < 0 || file.Length > maxBytes || total > maxBytes - file.Length) throw new InvalidDataException("受信サイズの上限を超えています。");
        total += file.Length;
        if (file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("SHA-256が不正です。");
    }

    private static void ValidateNoFileDirectoryConflicts(HashSet<string> names)
    {
        foreach (var name in names)
        {
            for (var i = name.IndexOf('/'); i >= 0; i = name.IndexOf('/', i + 1))
            {
                if (names.Contains(name[..i])) throw new InvalidDataException("ファイル名とフォルダー名が競合しています。");
            }
        }
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 200 || path.Contains('\\')) throw new InvalidDataException("ファイル名が不正です。");
        foreach (var segment in path.Split('/'))
        {
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("安全でないファイル名が含まれています。");
        }
    }

    public static string UnderDirectory(string root, string relative)
    {
        ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("保存先が不正です。");
        return path;
    }
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Size(long size)
    {
        if (size >= 1L << 30) return $"{size / (double)(1L << 30):0.##} GB";
        if (size >= 1L << 20) return $"{size / (double)(1L << 20):0.##} MB";
        if (size >= 1024) return $"{size / 1024d:0.##} KB";

        return $"{size} B";
    }
}

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MediaConverter.Services;

public sealed record YtDlpUpdateResult(bool Updated, bool Skipped, string Version, string Detail);

/// <summary>
/// Keeps yt-dlp in a per-user tools directory so the executable can update
/// independently from the application installation directory.
/// </summary>
public sealed class YtDlpUpdateService
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    private readonly string _installDirectory;
    private readonly string _stateFilePath;

    public YtDlpUpdateService(string? installDirectoryOverride = null)
    {
        _installDirectory = installDirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EZConverter", "Tools", "yt-dlp");
        _stateFilePath = Path.Combine(_installDirectory, "update-state.json");
    }

    public string ExecutablePath => Path.Combine(_installDirectory, "yt-dlp.exe");

    public async Task<YtDlpUpdateResult> UpdateIfNeededAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_installDirectory);
            var previousState = await ReadStateAsync(cancellationToken);
            if (!force && File.Exists(ExecutablePath) && previousState is not null &&
                DateTimeOffset.UtcNow - previousState.CheckedAtUtc < CheckInterval)
            {
                return new YtDlpUpdateResult(
                    Updated: false,
                    Skipped: true,
                    previousState.Version,
                    $"前回の確認から12時間以内のため確認を省略しました ({previousState.Version})。");
            }

            using var releaseRequest = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            GitHubReleaseRequests.Configure(releaseRequest, "EZConverter/1.0 (yt-dlp updater)");
            using var releaseResponse = await HttpClient.SendAsync(
                releaseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();

            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var releaseDocument = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
            var release = releaseDocument.RootElement;
            var version = GetRequiredString(release, "tag_name");
            var asset = FindWindowsAsset(release, version);

            var currentHash = File.Exists(ExecutablePath)
                ? await GetSha256Async(ExecutablePath, cancellationToken)
                : null;
            if (string.Equals(currentHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version), cancellationToken);
                return new YtDlpUpdateResult(false, false, version, $"最新のNightly版です ({version})。");
            }

            var temporaryPath = Path.Combine(_installDirectory, $"yt-dlp-{Guid.NewGuid():N}.download");
            try
            {
                using var assetRequest = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
                assetRequest.Headers.UserAgent.ParseAdd("EZConverter/1.0 (yt-dlp updater)");
                using var assetResponse = await HttpClient.SendAsync(
                    assetRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                assetResponse.EnsureSuccessStatusCode();

                if (assetResponse.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
                {
                    throw new InvalidDataException("yt-dlpのダウンロードサイズが公式リリース情報と一致しません。");
                }

                await using (var destination = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await assetResponse.Content.CopyToAsync(destination, cancellationToken);
                }

                var downloadedInfo = new FileInfo(temporaryPath);
                if (downloadedInfo.Length != asset.Size || downloadedInfo.Length is < 1_000_000 or > 100_000_000)
                {
                    throw new InvalidDataException("yt-dlpのダウンロードファイルのサイズが不正です。");
                }

                await using (var file = File.OpenRead(temporaryPath))
                {
                    var signature = new byte[2];
                    var read = await file.ReadAsync(signature, cancellationToken);
                    if (read != 2 || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
                    {
                        throw new InvalidDataException("ダウンロードしたyt-dlpはWindows実行ファイルではありません。");
                    }
                }

                var downloadedHash = await GetSha256Async(temporaryPath, cancellationToken);
                if (!string.Equals(downloadedHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("yt-dlpのSHA-256検証に失敗しました。元のファイルは変更していません。");
                }

                ReplaceExecutable(temporaryPath, ExecutablePath);
                await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version), cancellationToken);
                return new YtDlpUpdateResult(true, false, version, $"公式Nightly版を更新しました ({version})。");
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    private static ReleaseAsset FindWindowsAsset(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("yt-dlpの公式リリースにファイル一覧がありません。");
        }

        foreach (var candidate in assets.EnumerateArray())
        {
            if (!candidate.TryGetProperty("name", out var name) || name.GetString() != "yt-dlp.exe")
            {
                continue;
            }

            var downloadUrl = new Uri(GetRequiredString(candidate, "browser_download_url"));
            if (downloadUrl.Scheme != Uri.UriSchemeHttps ||
                !downloadUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUrl.AbsolutePath.StartsWith(
                    "/yt-dlp/yt-dlp-nightly-builds/releases/download/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("yt-dlpの配布元が公式Nightlyリポジトリではありません。");
            }

            var digest = GetRequiredString(candidate, "digest");
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != prefix.Length + 64)
            {
                throw new InvalidDataException("公式リリースに有効なSHA-256情報がありません。");
            }

            if (!long.TryParse(GetRequiredString(candidate, "size"), out var size) || size is < 1_000_000 or > 100_000_000)
            {
                throw new InvalidDataException("公式リリースのyt-dlpファイルサイズが不正です。");
            }

            return new ReleaseAsset(downloadUrl, digest[prefix.Length..], size, version);
        }

        throw new InvalidDataException("yt-dlpの公式NightlyリリースにWindows版が見つかりません。");
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"yt-dlpの更新情報に{propertyName}がありません。");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? throw new InvalidDataException($"{propertyName}が空です。"),
            JsonValueKind.Number => value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException($"{propertyName}の形式が不正です。")
        };
    }

    private static async Task<string> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private async Task<UpdateState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await JsonSerializer.DeserializeAsync<UpdateState>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteStateAsync(UpdateState state, CancellationToken cancellationToken)
    {
        var temporaryPath = _stateFilePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, _stateFilePath, overwrite: true);
    }

    private static void ReplaceExecutable(string temporaryPath, string executablePath)
    {
        var backupPath = executablePath + ".backup";
        TryDelete(backupPath);
        if (File.Exists(executablePath))
        {
            File.Move(executablePath, backupPath);
        }

        try
        {
            File.Move(temporaryPath, executablePath);
            TryDelete(backupPath);
        }
        catch
        {
            if (!File.Exists(executablePath) && File.Exists(backupPath))
            {
                File.Move(backupPath, executablePath);
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary and obsolete update files can be cleaned up on a later run.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the usable executable even if Windows temporarily locks a backup file.
        }
    }

    private sealed record ReleaseAsset(Uri DownloadUri, string Sha256, long Size, string Version);
    private sealed record UpdateState(DateTimeOffset CheckedAtUtc, string Version);
}

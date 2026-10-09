using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaConverter.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    Uri ArchiveUri,
    Uri? ChecksumUri,
    string? ExpectedSha256,
    long? ArchiveSize);

/// <summary>
/// Checks GitHub Releases and hands an already verified archive to the separate
/// update agent. The running application is never overwritten by this class.
/// </summary>
public sealed class AppUpdateService
{
    private const string GitHubApiBaseUrl = "https://api.github.com/repos";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly GitHubUpdateOptions _options;
    private readonly string _baseDirectory;
    private readonly HttpClient _httpClient;
    private readonly string _updateRoot;

    public AppUpdateService(string? baseDirectoryOverride = null)
        : this(baseDirectoryOverride, SharedHttpClient, updateRootOverride: null)
    {
    }

    internal AppUpdateService(
        string? baseDirectoryOverride,
        HttpClient httpClient,
        string? updateRootOverride,
        Version? currentVersionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _baseDirectory = baseDirectoryOverride ?? AppContext.BaseDirectory;
        _options = GitHubUpdateOptions.Load(_baseDirectory);
        _httpClient = httpClient;
        _updateRoot = updateRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZConverter", "Updates");
        CurrentVersion = currentVersionOverride is null
            ? GetCurrentVersion()
            : NormalizeVersion(currentVersionOverride);
    }

    public Version CurrentVersion { get; }

    public bool IsConfigured => _options.IsConfigured;

    public string ConfigurationDetail => _options.Detail;

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var endpoint = $"{GitHubApiBaseUrl}/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repository)}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("EZConverter-AppUpdater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var release = document.RootElement;
        if (GetOptionalBoolean(release, "draft"))
        {
            return null;
        }

        var tagName = GetRequiredString(release, "tag_name");
        var releaseVersion = ParseReleaseVersion(tagName);
        if (releaseVersion <= CurrentVersion)
        {
            return null;
        }

        var archive = FindAsset(release, _options.ReleaseAssetName)
            ?? throw new InvalidDataException($"GitHub Releasesに更新ファイル {_options.ReleaseAssetName} がありません。");
        var checksum = FindAsset(release, _options.ChecksumAssetName)
            ?? FindAsset(release, _options.ReleaseAssetName + ".sha256");
        var digest = NormalizeSha256(GetOptionalString(archive, "digest"));
        if (digest is null && checksum is null)
        {
            throw new InvalidDataException("GitHub Releasesに更新ファイルのSHA-256情報がありません。");
        }

        return new AppUpdateInfo(
            releaseVersion,
            tagName,
            GetOptionalString(release, "name") ?? tagName,
            LimitReleaseNotes(GetOptionalString(release, "body") ?? string.Empty),
            GetVerifiedGitHubAssetUri(archive, _options.Owner, _options.Repository),
            checksum is null ? null : GetVerifiedGitHubAssetUri(checksum.Value, _options.Owner, _options.Repository),
            digest,
            GetOptionalInt64(archive, "size"));
    }

    public async Task<string> DownloadAndVerifyAsync(
        AppUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(_updateRoot, $"{update.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(updateDirectory);

        var archivePath = Path.Combine(updateDirectory, _options.ReleaseAssetName);
        try
        {
            await DownloadFileAsync(update.ArchiveUri, archivePath, update.ArchiveSize, cancellationToken);
            var expectedHash = update.ExpectedSha256;
            if (expectedHash is null && update.ChecksumUri is not null)
            {
                var checksumPath = Path.Combine(updateDirectory, "checksum.txt");
                await DownloadFileAsync(update.ChecksumUri, checksumPath, null, cancellationToken);
                expectedHash = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken), _options.ReleaseAssetName);
            }

            if (expectedHash is null)
            {
                throw new InvalidDataException("更新ファイルのSHA-256を確定できませんでした。");
            }

            var actualHash = await GetSha256Async(archivePath, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新ファイルのSHA-256検証に失敗しました。インストールは行いません。");
            }

            return archivePath;
        }
        catch
        {
            TryDeleteDirectory(updateDirectory);
            throw;
        }
    }

    public Process StartUpdateAgent(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("検証済みの更新ファイルが見つかりません。", archivePath);
        }

        var agentSourcePath = Path.Combine(_baseDirectory, _options.UpdateAgentName);
        if (!File.Exists(agentSourcePath))
        {
            throw new FileNotFoundException(
                "更新ヘルパーが見つかりません。リリース配布物に更新ヘルパーを含めてください。",
                agentSourcePath);
        }

        var agentDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EZConverter", "Updates", "agents");
        Directory.CreateDirectory(agentDirectory);
        var agentPath = Path.Combine(agentDirectory, $"{Path.GetFileNameWithoutExtension(_options.UpdateAgentName)}-{Guid.NewGuid():N}.exe");
        File.Copy(agentSourcePath, agentPath, overwrite: false);

        var applicationPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            throw new InvalidOperationException("現在のアプリケーションパスを取得できません。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            WorkingDirectory = agentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--archive");
        startInfo.ArgumentList.Add(Path.GetFullPath(archivePath));
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(Path.GetFullPath(_baseDirectory));
        startInfo.ArgumentList.Add("--app");
        startInfo.ArgumentList.Add(Path.GetFullPath(applicationPath));

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("更新ヘルパーを起動できませんでした。");
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    private static Version GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? typeof(AppUpdateService).Assembly.GetName().Version;
        return version is null ? new Version(0, 0, 0, 0) : NormalizeVersion(version);
    }

    private static Version ParseReleaseVersion(string tagName)
    {
        var match = Regex.Match(tagName.Trim(), @"^[vV]?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:[-+].*)?$");
        if (!match.Success)
        {
            throw new InvalidDataException($"リリースタグのバージョン形式が不正です: {tagName}");
        }

        return new Version(
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            match.Groups[4].Success
                ? int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    private static JsonElement? FindAsset(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub Releasesにファイル一覧がありません。");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(GetOptionalString(asset, "name"), assetName, StringComparison.Ordinal))
            {
                return asset;
            }
        }

        return default;
    }

    private static Uri GetVerifiedGitHubAssetUri(JsonElement asset, string owner, string repository)
    {
        var uri = new Uri(GetRequiredString(asset, "browser_download_url"));
        var expectedPrefix = $"/{owner}/{repository}/releases/download/";
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub Releasesの配布URLが許可された形式ではありません。");
        }

        return uri;
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string prefix = "sha256:";
        var hash = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
        return Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$") ? hash : null;
    }

    private static string ParseChecksum(string content, string archiveName)
    {
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), @"^(?<hash>[0-9a-fA-F]{64})(?:\s+\*?)(?<name>.*)$");
            if (match.Success && (string.IsNullOrWhiteSpace(match.Groups[2].Value)
                || string.Equals(Path.GetFileName(match.Groups[2].Value.Trim()), archiveName, StringComparison.OrdinalIgnoreCase)))
            {
                return match.Groups[1].Value;
            }
        }

        throw new InvalidDataException("チェックサムファイルに更新アーカイブのSHA-256がありません。");
    }

    private async Task DownloadFileAsync(Uri uri, string destinationPath, long? expectedSize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("EZConverter-AppUpdater/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (expectedSize is long size && response.Content.Headers.ContentLength is long contentLength && contentLength != size)
        {
            throw new InvalidDataException("GitHub Releasesのファイルサイズが一致しません。");
        }

        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<string> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string LimitReleaseNotes(string text)
    {
        text = text.Trim();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return GetOptionalString(element, propertyName)
            ?? throw new InvalidDataException($"GitHub Releasesの{propertyName}がありません。");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static bool GetOptionalBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A later update run can clean up abandoned files.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not hide the original update error.
        }
    }

    private sealed record GitHubUpdateOptions(
        string Owner,
        string Repository,
        string ReleaseAssetName,
        string ChecksumAssetName,
        string UpdateAgentName,
        string Detail)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repository);

        public static GitHubUpdateOptions Load(string baseDirectory)
        {
            var defaultOptions = new GitHubUpdateOptions(
                string.Empty,
                string.Empty,
                "MediaConverter-win-x64.zip",
                "MediaConverter-win-x64.zip.sha256",
                "MediaConverter.UpdateAgent.exe",
                "GitHubリポジトリが未設定です。");
            var path = Path.Combine(baseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return defaultOptions with { Detail = "appsettings.jsonが見つかりません。" };
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("GitHub", out var github))
                {
                    return defaultOptions with { Detail = "appsettings.jsonにGitHub設定がありません。" };
                }

                return new GitHubUpdateOptions(
                    GetOptionalString(github, "Owner") ?? string.Empty,
                    GetOptionalString(github, "Repository") ?? string.Empty,
                    GetOptionalString(github, "ReleaseAssetName") ?? defaultOptions.ReleaseAssetName,
                    GetOptionalString(github, "ChecksumAssetName") ?? defaultOptions.ChecksumAssetName,
                    GetOptionalString(github, "UpdateAgentName") ?? defaultOptions.UpdateAgentName,
                    "GitHub Releasesを使用します。");
            }
            catch (JsonException exception)
            {
                return defaultOptions with { Detail = $"appsettings.jsonを読み込めません: {exception.Message}" };
            }
            catch (IOException exception)
            {
                return defaultOptions with { Detail = $"appsettings.jsonを読み込めません: {exception.Message}" };
            }
        }
    }
}

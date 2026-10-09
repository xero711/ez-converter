using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaConverter.Services;

public sealed record DenoUpdateResult(bool Updated, bool Skipped, string Version, string Detail);

/// <summary>
/// Keeps the JavaScript runtime required by current yt-dlp extractors in the
/// per-user tools directory and verifies official release assets before install.
/// </summary>
public sealed class DenoUpdateService
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/denoland/deno/releases/latest";
    private const string RepositoryPath = "/denoland/deno/releases/download/";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    private readonly string _installDirectory;
    private readonly string _stateFilePath;

    public DenoUpdateService(string? installDirectoryOverride = null)
    {
        _installDirectory = installDirectoryOverride ?? Path.Combine(ToolLocator.ManagedToolsRoot, "deno");
        _stateFilePath = Path.Combine(_installDirectory, "update-state.json");
    }

    public string ExecutablePath => Path.Combine(_installDirectory, "deno.exe");

    public async Task<DenoUpdateResult> UpdateIfNeededAsync(
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
                var currentHash = await GetSha256Async(ExecutablePath, cancellationToken);
                if (string.Equals(currentHash, previousState.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new DenoUpdateResult(
                        Updated: false,
                        Skipped: true,
                        previousState.Version,
                        $"前回の確認から12時間以内のためDeno確認を省略しました ({previousState.Version})。");
                }
            }

            using var releaseRequest = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            releaseRequest.Headers.UserAgent.ParseAdd("EZConverter/1.0 (Deno updater)");
            releaseRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var releaseResponse = await HttpClient.SendAsync(
                releaseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();

            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var releaseDocument = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
            var release = releaseDocument.RootElement;
            var version = ParseVersion(GetRequiredString(release, "tag_name"));
            var asset = FindWindowsAsset(release, version);

            if (File.Exists(ExecutablePath) && previousState?.Version == version)
            {
                var existingHash = await GetSha256Async(ExecutablePath, cancellationToken);
                if (string.Equals(existingHash, previousState.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version, existingHash), cancellationToken);
                    return new DenoUpdateResult(false, false, version, $"最新のDenoです ({version})。");
                }
            }

            var archivePath = Path.Combine(_installDirectory, $"deno-{Guid.NewGuid():N}.download");
            var stagingDirectory = Path.Combine(_installDirectory, $"deno-staging-{Guid.NewGuid():N}");
            try
            {
                using (var assetRequest = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri))
                {
                    assetRequest.Headers.UserAgent.ParseAdd("EZConverter/1.0 (Deno updater)");
                    using var assetResponse = await HttpClient.SendAsync(
                        assetRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    assetResponse.EnsureSuccessStatusCode();
                    if (assetResponse.Content.Headers.ContentLength is long length && length != asset.Size)
                    {
                        throw new InvalidDataException("Denoのダウンロードサイズが公式リリース情報と一致しません。");
                    }

                    await using var destination = new FileStream(
                        archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                    await assetResponse.Content.CopyToAsync(destination, cancellationToken);
                }

                var archiveInfo = new FileInfo(archivePath);
                if (archiveInfo.Length != asset.Size || archiveInfo.Length is < 5_000_000 or > 100_000_000)
                {
                    throw new InvalidDataException("ダウンロードしたDenoアーカイブのサイズが不正です。");
                }

                var archiveHash = await GetSha256Async(archivePath, cancellationToken);
                if (!string.Equals(archiveHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("DenoのSHA-256検証に失敗しました。現在のファイルは変更していません。");
                }

                var stagingExecutablePath = Path.Combine(stagingDirectory, "deno.exe");
                await ExtractExecutableAsync(archivePath, stagingExecutablePath, cancellationToken);
                var versionCheck = await ExternalToolRunner.RunAsync(
                    stagingExecutablePath, ["--version"], cancellationToken, stagingDirectory);
                if (versionCheck.ExitCode != 0 || !HasExpectedVersion(versionCheck.StandardOutput, version))
                {
                    throw new InvalidDataException("検証後のDeno実行ファイルが公式配布バージョンと一致しません。");
                }

                var executableHash = await GetSha256Async(stagingExecutablePath, cancellationToken);
                ReplaceExecutable(stagingExecutablePath);
                await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version, executableHash), cancellationToken);
                return new DenoUpdateResult(true, false, version, $"Denoを更新しました ({version})。");
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static ReleaseAsset FindWindowsAsset(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Denoの公式リリースにファイル一覧がありません。");
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException("動画URL取得用DenoはWindows x64/ARM64版のみ対応しています。")
        };
        var expectedName = $"deno-{architecture}-pc-windows-msvc.zip";
        foreach (var candidate in assets.EnumerateArray())
        {
            if (!candidate.TryGetProperty("name", out var name) || name.GetString() != expectedName)
            {
                continue;
            }

            var downloadUrl = new Uri(GetRequiredString(candidate, "browser_download_url"));
            if (downloadUrl.Scheme != Uri.UriSchemeHttps ||
                !downloadUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUrl.AbsolutePath.StartsWith(RepositoryPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Denoの配布URLが公式GitHubリリースではありません。");
            }

            var digest = GetRequiredString(candidate, "digest");
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != prefix.Length + 64)
            {
                throw new InvalidDataException("Deno公式リリースに有効なSHA-256情報がありません。");
            }

            if (!long.TryParse(GetRequiredString(candidate, "size"), out var size) || size is < 5_000_000 or > 100_000_000)
            {
                throw new InvalidDataException("Deno公式リリースのアーカイブサイズが不正です。");
            }

            return new ReleaseAsset(downloadUrl, digest[prefix.Length..], size, version);
        }

        throw new InvalidDataException($"Deno公式リリースに{architecture}版が見つかりません。");
    }

    private static async Task ExtractExecutableAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.FullName.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1 || entries[0].Length is < 1_000_000 or > 150_000_000)
        {
            throw new InvalidDataException("Denoアーカイブから有効な実行ファイルを一意に特定できませんでした。");
        }

        await using (var source = entries[0].Open())
        await using (var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        await using var executable = File.OpenRead(destinationPath);
        var signature = new byte[2];
        var read = await executable.ReadAsync(signature, cancellationToken);
        if (read != 2 || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
        {
            throw new InvalidDataException("Denoアーカイブ内のファイルはWindows実行ファイルではありません。");
        }
    }

    private void ReplaceExecutable(string stagedPath)
    {
        var backupPath = Path.Combine(_installDirectory, $"deno-{Guid.NewGuid():N}.backup");
        var movedOldExecutable = false;
        try
        {
            if (File.Exists(ExecutablePath))
            {
                File.Move(ExecutablePath, backupPath);
                movedOldExecutable = true;
            }

            File.Move(stagedPath, ExecutablePath);
            if (movedOldExecutable)
            {
                TryDeleteFile(backupPath);
            }
        }
        catch
        {
            if (!File.Exists(ExecutablePath) && movedOldExecutable && File.Exists(backupPath))
            {
                File.Move(backupPath, ExecutablePath);
            }

            throw;
        }
    }

    private static string ParseVersion(string value)
    {
        var version = value.StartsWith('v') ? value[1..] : value;
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Deno公式リリースから有効なバージョン情報を取得できませんでした。");
        }

        return version;
    }

    private static bool HasExpectedVersion(string output, string expectedVersion)
    {
        return Regex.IsMatch(
            output,
            $@"(?m)^deno\s+{Regex.Escape(expectedVersion)}(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"Deno更新情報に{propertyName}がありません。");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? throw new InvalidDataException($"{propertyName}が空です。"),
            JsonValueKind.Number => value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException($"{propertyName}の形式が不正です。")
        };
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
        Directory.CreateDirectory(_installDirectory);
        var temporaryPath = _stateFilePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, _stateFilePath, overwrite: true);
    }

    private static async Task<string> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Keep an old runtime backup if Windows still has an open handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the verified runtime if cleanup is temporarily blocked.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Retry cleanup on a later run if Windows still has an open handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve files that could not be safely removed.
        }
    }

    private sealed record ReleaseAsset(Uri DownloadUri, string Sha256, long Size, string Version);
    private sealed record UpdateState(DateTimeOffset CheckedAtUtc, string Version, string ExecutableSha256);
}
